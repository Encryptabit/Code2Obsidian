namespace Code2Obsidian.Incremental;

/// <summary>
/// Describes how a source file changed between the prior run and the current state.
/// </summary>
public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed
}

/// <summary>
/// A single file change detected between incremental runs.
/// </summary>
/// <param name="Path">Relative path of the file (new path for renames).</param>
/// <param name="OldPath">Previous path when <see cref="Kind"/> is <see cref="FileChangeKind.Renamed"/>; null otherwise.</param>
/// <param name="Kind">The type of change detected.</param>
public sealed record FileChange(string Path, string? OldPath, FileChangeKind Kind);

/// <summary>
/// The result of change detection: a set of file changes and associated metadata.
/// Produced by <see cref="IChangeDetector"/> implementations.
/// </summary>
/// <param name="Changes">All detected file changes.</param>
/// <param name="CommitSha">HEAD commit SHA at detection time; null for hash-only detection.</param>
/// <param name="IsFullRebuild">True when no prior state exists (first run or corrupted state).</param>
public sealed record ChangeSet(
    IReadOnlyList<FileChange> Changes,
    string? CommitSha,
    bool IsFullRebuild)
{
    /// <summary>
    /// All non-deleted file paths (Added, Modified, Renamed) as a case-insensitive set.
    /// This is the set used for pipeline file filtering.
    /// </summary>
    public IReadOnlySet<string> ChangedFilePaths { get; } =
        new HashSet<string>(
            Changes
                .Where(c => c.Kind != FileChangeKind.Deleted)
                .Select(c => c.Path),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// File paths of deleted files as a case-insensitive set.
    /// </summary>
    public IReadOnlySet<string> DeletedFilePaths { get; } =
        new HashSet<string>(
            Changes
                .Where(c => c.Kind == FileChangeKind.Deleted)
                .Select(c => c.Path),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps OldPath to NewPath for renamed files.
    /// </summary>
    public IReadOnlyDictionary<string, string> RenamedPaths { get; } =
        Changes
            .Where(c => c.Kind == FileChangeKind.Renamed && c.OldPath is not null)
            .ToDictionary(c => c.OldPath!, c => c.Path, StringComparer.OrdinalIgnoreCase);
}
