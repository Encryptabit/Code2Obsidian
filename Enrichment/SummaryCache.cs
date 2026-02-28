using Code2Obsidian.Incremental;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Code2Obsidian.Enrichment;

/// <summary>
/// Reads and writes LLM-generated structured enrichment data in the SQLite summaries table.
/// Follows IncrementalState's open-per-operation pattern (NOT IDisposable).
/// Cache entries are keyed by entity_id + content_hash; a stale hash means cache miss.
/// </summary>
public sealed class SummaryCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private const int BusyTimeoutSeconds = 30;

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock;
    private readonly Channel<QueueItem> _writeQueue;
    private readonly Task _queueProcessorTask;
    private Exception? _queueFailure;

    public SummaryCache(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = BusyTimeoutSeconds
        }.ToString();
        _writeLock = WriteLocks.GetOrAdd(_dbPath, _ => new SemaphoreSlim(1, 1));
        _writeQueue = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _queueProcessorTask = Task.Run(ProcessQueueAsync);

        // Ensure schema exists up front so the queue processor can focus on batched writes.
        using var connection = OpenConnection();
    }

    /// <summary>
    /// Attempts to retrieve a cached enrichment response for the given entity and content hash.
    /// Returns null on cache miss (no entry or stale hash) or on database error (graceful corruption).
    /// Tags are stored as comma-separated string; on read, split by comma and trim each element.
    /// </summary>
    public EnrichmentResponse? TryGet(string entityId, string currentContentHash)
    {
        try
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT summary, purpose, tags, improvements
                FROM summaries
                WHERE entity_id = @id AND content_hash = @hash
                """;
            cmd.Parameters.AddWithValue("@id", entityId);
            cmd.Parameters.AddWithValue("@hash", currentContentHash);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var summary = reader.GetString(0);
            var purpose = reader.GetString(1);
            var tagsRaw = reader.GetString(2);
            var improvements = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var tags = string.IsNullOrWhiteSpace(tagsRaw)
                ? Array.Empty<string>()
                : tagsRaw.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

            return new EnrichmentResponse(summary, purpose, tags, improvements);
        }
        catch (SqliteException)
        {
            // Graceful corruption: treat as cache miss
            return null;
        }
    }

    /// <summary>
    /// Stores or updates an enrichment response for the given entity with its content hash and model ID.
    /// Enqueues writes and lets a single background queue processor persist batches.
    /// </summary>
    public void Put(
        string entityId,
        string contentHash,
        EnrichmentResponse response,
        string modelId,
        bool updateSummary = true,
        bool updateSuggestions = false)
    {
        ThrowIfQueueFailed();

        var item = new WriteItem(
            entityId,
            contentHash,
            response,
            modelId,
            DateTime.UtcNow,
            updateSummary,
            updateSuggestions);
        if (!_writeQueue.Writer.TryWrite(item))
        {
            ThrowIfQueueFailed();
            throw new InvalidOperationException("Summary cache write queue is not accepting entries.");
        }
    }

    /// <summary>
    /// Waits until all queued writes up to this call are persisted to SQLite.
    /// </summary>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfQueueFailed();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeQueue.Writer.TryWrite(new FlushItem(completion)))
        {
            ThrowIfQueueFailed();
            throw new InvalidOperationException("Summary cache flush failed because the write queue is unavailable.");
        }

        return completion.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Counts how many of the given entities already have a matching cache entry.
    /// Used by cost estimator to determine how many entities need LLM calls.
    /// </summary>
    public int CountCached(IEnumerable<(string entityId, string contentHash)> entities)
    {
        try
        {
            using var connection = OpenConnection();
            int count = 0;

            foreach (var (entityId, contentHash) in entities)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM summaries WHERE entity_id = @id AND content_hash = @hash";
                cmd.Parameters.AddWithValue("@id", entityId);
                cmd.Parameters.AddWithValue("@hash", contentHash);

                if (cmd.ExecuteScalar() is not null)
                    count++;
            }

            return count;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutSeconds * 1000}";
            cmd.ExecuteNonQuery();
        }

        StateSchema.EnsureSchema(connection);
        return connection;
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<WriteItem>();
        var pendingFlushes = new List<TaskCompletionSource<bool>>();
        var reader = _writeQueue.Reader;

        try
        {
            while (await reader.WaitToReadAsync())
            {
                batch.Clear();
                pendingFlushes.Clear();

                while (reader.TryRead(out var item))
                {
                    switch (item)
                    {
                        case WriteItem write:
                            batch.Add(write);
                            break;
                        case FlushItem flush:
                            pendingFlushes.Add(flush.Completion);
                            break;
                    }
                }

                if (batch.Count > 0)
                {
                    WriteBatch(batch);
                }

                foreach (var flush in pendingFlushes)
                {
                    flush.TrySetResult(true);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var flush in pendingFlushes)
            {
                flush.TrySetException(ex);
            }

            FailQueue(ex);
        }
    }

    private void WriteBatch(IReadOnlyList<WriteItem> batch)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var selectCmd = connection.CreateCommand();
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = """
                SELECT content_hash, summary, purpose, tags, model_id, created_at,
                       improvements, improvements_model_id, improvements_created_at
                FROM summaries
                WHERE entity_id = @id
                """;
            var selectIdParam = selectCmd.Parameters.Add("@id", SqliteType.Text);

            using var upsertCmd = connection.CreateCommand();
            upsertCmd.Transaction = transaction;
            upsertCmd.CommandText = """
                INSERT OR REPLACE INTO summaries (
                    entity_id, content_hash, summary, purpose, tags, model_id, created_at,
                    improvements, improvements_model_id, improvements_created_at)
                VALUES (@id, @hash, @summary, @purpose, @tags, @model, @time, @improvements, @improvementsModel, @improvementsTime)
                """;

            var idParam = upsertCmd.Parameters.Add("@id", SqliteType.Text);
            var hashParam = upsertCmd.Parameters.Add("@hash", SqliteType.Text);
            var summaryParam = upsertCmd.Parameters.Add("@summary", SqliteType.Text);
            var purposeParam = upsertCmd.Parameters.Add("@purpose", SqliteType.Text);
            var tagsParam = upsertCmd.Parameters.Add("@tags", SqliteType.Text);
            var modelParam = upsertCmd.Parameters.Add("@model", SqliteType.Text);
            var timeParam = upsertCmd.Parameters.Add("@time", SqliteType.Text);
            var improvementsParam = upsertCmd.Parameters.Add("@improvements", SqliteType.Text);
            var improvementsModelParam = upsertCmd.Parameters.Add("@improvementsModel", SqliteType.Text);
            var improvementsTimeParam = upsertCmd.Parameters.Add("@improvementsTime", SqliteType.Text);

            foreach (var item in batch)
            {
                selectIdParam.Value = item.EntityId;
                CachedRow? existing = null;
                using (var reader = selectCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        existing = new CachedRow(
                            ContentHash: reader.GetString(0),
                            Summary: reader.GetString(1),
                            Purpose: reader.GetString(2),
                            Tags: reader.GetString(3),
                            ModelId: reader.GetString(4),
                            CreatedAt: reader.GetString(5),
                            Improvements: reader.IsDBNull(6) ? "" : reader.GetString(6),
                            ImprovementsModelId: reader.IsDBNull(7) ? null : reader.GetString(7),
                            ImprovementsCreatedAt: reader.IsDBNull(8) ? null : reader.GetString(8));
                    }
                }

                var merged = MergeRow(existing, item);

                idParam.Value = item.EntityId;
                hashParam.Value = item.ContentHash;
                summaryParam.Value = merged.Summary;
                purposeParam.Value = merged.Purpose;
                tagsParam.Value = merged.Tags;
                modelParam.Value = merged.ModelId;
                timeParam.Value = merged.CreatedAt;
                improvementsParam.Value = merged.Improvements;
                improvementsModelParam.Value = merged.ImprovementsModelId ?? (object)DBNull.Value;
                improvementsTimeParam.Value = merged.ImprovementsCreatedAt ?? (object)DBNull.Value;
                upsertCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void ThrowIfQueueFailed()
    {
        if (Volatile.Read(ref _queueFailure) is Exception ex)
            throw new InvalidOperationException("Summary cache write queue failed.", ex);

        if (_queueProcessorTask.IsCompletedSuccessfully)
            throw new InvalidOperationException("Summary cache write queue stopped unexpectedly.");
    }

    private void FailQueue(Exception ex)
    {
        if (Interlocked.CompareExchange(ref _queueFailure, ex, null) is not null)
            return;

        _writeQueue.Writer.TryComplete(ex);
        while (_writeQueue.Reader.TryRead(out var queuedItem))
        {
            if (queuedItem is FlushItem flush)
                flush.Completion.TrySetException(ex);
        }
    }

    private abstract record QueueItem;
    private sealed record WriteItem(
        string EntityId,
        string ContentHash,
        EnrichmentResponse Response,
        string ModelId,
        DateTime TimestampUtc,
        bool UpdateSummary,
        bool UpdateSuggestions) : QueueItem;
    private sealed record FlushItem(TaskCompletionSource<bool> Completion) : QueueItem;

    private static MergedRow MergeRow(CachedRow? existing, WriteItem item)
    {
        var timestamp = item.TimestampUtc.ToString("o");
        var sameHash = existing is not null &&
            string.Equals(existing.ContentHash, item.ContentHash, StringComparison.Ordinal);

        string summary;
        string purpose;
        string tags;
        string summaryModelId;
        string summaryCreatedAt;

        if (item.UpdateSummary)
        {
            summary = item.Response.Summary;
            purpose = item.Response.Purpose;
            tags = string.Join(", ", item.Response.Tags);
            summaryModelId = item.ModelId;
            summaryCreatedAt = timestamp;
        }
        else if (sameHash && existing is not null)
        {
            summary = existing.Summary;
            purpose = existing.Purpose;
            tags = existing.Tags;
            summaryModelId = existing.ModelId;
            summaryCreatedAt = existing.CreatedAt;
        }
        else
        {
            summary = "";
            purpose = "";
            tags = "";
            summaryModelId = item.ModelId;
            summaryCreatedAt = timestamp;
        }

        string improvements;
        string? improvementsModelId;
        string? improvementsCreatedAt;

        if (item.UpdateSuggestions)
        {
            improvements = item.Response.Improvements;
            improvementsModelId = item.ModelId;
            improvementsCreatedAt = timestamp;
        }
        else if (sameHash && existing is not null)
        {
            improvements = existing.Improvements;
            improvementsModelId = existing.ImprovementsModelId;
            improvementsCreatedAt = existing.ImprovementsCreatedAt;
        }
        else
        {
            improvements = "";
            improvementsModelId = null;
            improvementsCreatedAt = null;
        }

        return new MergedRow(
            Summary: summary,
            Purpose: purpose,
            Tags: tags,
            ModelId: summaryModelId,
            CreatedAt: summaryCreatedAt,
            Improvements: improvements,
            ImprovementsModelId: improvementsModelId,
            ImprovementsCreatedAt: improvementsCreatedAt);
    }

    private sealed record CachedRow(
        string ContentHash,
        string Summary,
        string Purpose,
        string Tags,
        string ModelId,
        string CreatedAt,
        string Improvements,
        string? ImprovementsModelId,
        string? ImprovementsCreatedAt);

    private sealed record MergedRow(
        string Summary,
        string Purpose,
        string Tags,
        string ModelId,
        string CreatedAt,
        string Improvements,
        string? ImprovementsModelId,
        string? ImprovementsCreatedAt);
}
