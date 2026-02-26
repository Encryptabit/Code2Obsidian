namespace Code2Obsidian.Incremental;

/// <summary>
/// Manages SQLite-backed incremental state persistence.
/// Connections are opened and closed per operation (not held open during pipeline).
/// </summary>
public sealed class IncrementalState
{
    /// <summary>
    /// Absolute path to the .code2obsidian-state.db file.
    /// </summary>
    public string DbPath { get; }

    /// <summary>
    /// The commit SHA from the last successful run, set by <see cref="TryLoad"/>.
    /// </summary>
    public string? CommitSha { get; private set; }

    public IncrementalState(string dbPath)
    {
        DbPath = dbPath;
    }

    /// <summary>
    /// Returns stored file_path to content_hash mapping.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetFileHashes()
    {
        return new Dictionary<string, string>();
    }
}
