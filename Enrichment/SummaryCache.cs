using Code2Obsidian.Incremental;
using Microsoft.Data.Sqlite;

namespace Code2Obsidian.Enrichment;

/// <summary>
/// Reads and writes LLM-generated summaries in the SQLite summaries table.
/// Follows IncrementalState's open-per-operation pattern (NOT IDisposable).
/// Cache entries are keyed by entity_id + content_hash; a stale hash means cache miss.
/// </summary>
public sealed class SummaryCache
{
    private readonly string _dbPath;

    public SummaryCache(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// Attempts to retrieve a cached summary for the given entity and content hash.
    /// Returns null on cache miss (no entry or stale hash) or on database error (graceful corruption).
    /// </summary>
    public string? TryGet(string entityId, string currentContentHash)
    {
        try
        {
            using var connection = OpenConnection();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT summary_text FROM summaries WHERE entity_id = @id AND content_hash = @hash";
            cmd.Parameters.AddWithValue("@id", entityId);
            cmd.Parameters.AddWithValue("@hash", currentContentHash);

            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            // Graceful corruption: treat as cache miss
            return null;
        }
    }

    /// <summary>
    /// Stores or updates a summary for the given entity with its content hash and model ID.
    /// Uses INSERT OR REPLACE to handle both new entries and hash changes.
    /// </summary>
    public void Put(string entityId, string contentHash, string summaryText, string modelId)
    {
        using var connection = OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO summaries (entity_id, content_hash, summary_text, model_id, created_at)
            VALUES (@id, @hash, @text, @model, @time)
            """;
        cmd.Parameters.AddWithValue("@id", entityId);
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@text", summaryText);
        cmd.Parameters.AddWithValue("@model", modelId);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));

        cmd.ExecuteNonQuery();
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
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        StateSchema.EnsureSchema(connection);
        return connection;
    }
}
