using Microsoft.Data.Sqlite;

namespace Code2Obsidian.Incremental;

/// <summary>
/// Manages the SQLite schema for incremental state persistence.
/// Uses PRAGMA user_version for schema versioning and migration.
/// </summary>
public static class StateSchema
{
    /// <summary>
    /// Ensures the database has the correct schema version.
    /// Enables WAL mode, then checks PRAGMA user_version and runs any needed migrations.
    /// </summary>
    public static void EnsureSchema(SqliteConnection connection)
    {
        // Enable WAL mode for better concurrent read performance.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = 'wal'";
            cmd.ExecuteNonQuery();
        }

        // Check current schema version.
        int version;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(cmd.ExecuteScalar());
        }

        if (version < 1)
        {
            MigrateToV1(connection);
        }

        if (version < 2)
        {
            MigrateToV2(connection);
        }
    }

    /// <summary>
    /// Adds the summaries table for LLM enrichment caching with structured columns.
    /// Existing V1 tables are untouched; IF NOT EXISTS ensures idempotency.
    /// </summary>
    private static void MigrateToV2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS summaries (
                entity_id TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL,
                summary TEXT NOT NULL,
                purpose TEXT NOT NULL,
                tags TEXT NOT NULL,
                model_id TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_summaries_hash ON summaries(content_hash);

            PRAGMA user_version = 2;
            """;

        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Creates the initial 9-table schema in a single transaction.
    /// 6 base tables + 3 metadata tables for merger/structural detection.
    /// </summary>
    private static void MigrateToV1(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            -- Base tables (6)
            CREATE TABLE IF NOT EXISTS run_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                commit_sha TEXT,
                run_timestamp TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS file_hashes (
                file_path TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS call_edges (
                caller_id TEXT NOT NULL,
                callee_id TEXT NOT NULL,
                caller_file TEXT NOT NULL,
                callee_file TEXT NOT NULL,
                PRIMARY KEY (caller_id, callee_id)
            );

            CREATE TABLE IF NOT EXISTS type_references (
                type_id TEXT NOT NULL,
                file_path TEXT NOT NULL,
                PRIMARY KEY (type_id, file_path)
            );

            CREATE TABLE IF NOT EXISTS type_files (
                type_id TEXT NOT NULL,
                file_path TEXT NOT NULL,
                PRIMARY KEY (type_id, file_path)
            );

            CREATE TABLE IF NOT EXISTS emitted_notes (
                note_path TEXT PRIMARY KEY,
                source_file TEXT NOT NULL,
                entity_id TEXT NOT NULL
            );

            -- Metadata tables (3) for RippleCalculator and AnalysisResultMerger
            CREATE TABLE IF NOT EXISTS type_metadata (
                type_id TEXT PRIMARY KEY,
                base_class TEXT,
                interfaces TEXT,
                namespace TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS method_index (
                method_id TEXT PRIMARY KEY,
                containing_type TEXT NOT NULL,
                file_path TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS type_index (
                type_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                full_name TEXT NOT NULL,
                file_path TEXT NOT NULL,
                kind TEXT NOT NULL
            );

            -- Indexes for base tables
            CREATE INDEX IF NOT EXISTS idx_call_edges_callee ON call_edges(callee_id);
            CREATE INDEX IF NOT EXISTS idx_call_edges_caller_file ON call_edges(caller_file);
            CREATE INDEX IF NOT EXISTS idx_call_edges_callee_file ON call_edges(callee_file);
            CREATE INDEX IF NOT EXISTS idx_type_references_file ON type_references(file_path);
            CREATE INDEX IF NOT EXISTS idx_type_files_file ON type_files(file_path);
            CREATE INDEX IF NOT EXISTS idx_emitted_notes_source ON emitted_notes(source_file);
            CREATE INDEX IF NOT EXISTS idx_emitted_notes_entity ON emitted_notes(entity_id);

            -- Indexes for metadata tables
            CREATE INDEX IF NOT EXISTS idx_type_metadata_namespace ON type_metadata(namespace);
            CREATE INDEX IF NOT EXISTS idx_method_index_file ON method_index(file_path);
            CREATE INDEX IF NOT EXISTS idx_type_index_file ON type_index(file_path);

            PRAGMA user_version = 1;
            """;

        cmd.ExecuteNonQuery();
        transaction.Commit();
    }
}
