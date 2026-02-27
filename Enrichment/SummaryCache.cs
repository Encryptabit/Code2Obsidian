using Code2Obsidian.Incremental;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace Code2Obsidian.Enrichment;

/// <summary>
/// Reads and writes LLM-generated structured enrichment data in the SQLite summaries table.
/// Follows IncrementalState's open-per-operation pattern (NOT IDisposable).
/// Cache entries are keyed by entity_id + content_hash; a stale hash means cache miss.
/// </summary>
public sealed class SummaryCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan[] BusyRetryBackoff =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500)
    ];
    private const int BusyTimeoutSeconds = 30;

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock;

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
            return ExecuteWithBusyRetry(() =>
            {
                using var connection = OpenConnection();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT summary, purpose, tags FROM summaries WHERE entity_id = @id AND content_hash = @hash";
                cmd.Parameters.AddWithValue("@id", entityId);
                cmd.Parameters.AddWithValue("@hash", currentContentHash);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return null;

                var summary = reader.GetString(0);
                var purpose = reader.GetString(1);
                var tagsRaw = reader.GetString(2);
                var tags = string.IsNullOrWhiteSpace(tagsRaw)
                    ? Array.Empty<string>()
                    : tagsRaw.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

                return new EnrichmentResponse(summary, purpose, tags);
            });
        }
        catch (SqliteException)
        {
            // Graceful corruption: treat as cache miss
            return null;
        }
    }

    /// <summary>
    /// Stores or updates an enrichment response for the given entity with its content hash and model ID.
    /// Uses INSERT OR REPLACE to handle both new entries and hash changes.
    /// Tags are stored as a comma-separated string.
    /// </summary>
    public void Put(string entityId, string contentHash, EnrichmentResponse response, string modelId)
    {
        _writeLock.Wait();
        try
        {
            ExecuteWithBusyRetry(() =>
            {
                using var connection = OpenConnection();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO summaries (entity_id, content_hash, summary, purpose, tags, model_id, created_at)
                    VALUES (@id, @hash, @summary, @purpose, @tags, @model, @time)
                    """;
                cmd.Parameters.AddWithValue("@id", entityId);
                cmd.Parameters.AddWithValue("@hash", contentHash);
                cmd.Parameters.AddWithValue("@summary", response.Summary);
                cmd.Parameters.AddWithValue("@purpose", response.Purpose);
                cmd.Parameters.AddWithValue("@tags", string.Join(", ", response.Tags));
                cmd.Parameters.AddWithValue("@model", modelId);
                cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));

                cmd.ExecuteNonQuery();
                return 0;
            });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Counts how many of the given entities already have a matching cache entry.
    /// Used by cost estimator to determine how many entities need LLM calls.
    /// </summary>
    public int CountCached(IEnumerable<(string entityId, string contentHash)> entities)
    {
        try
        {
            return ExecuteWithBusyRetry(() =>
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
            });
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

    private static T ExecuteWithBusyRetry<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < BusyRetryBackoff.Length)
            {
                Thread.Sleep(BusyRetryBackoff[attempt]);
            }
        }
    }

    private static bool IsBusyOrLocked(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;
}
