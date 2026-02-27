namespace Code2Obsidian.Incremental;

/// <summary>
/// Abstraction for detecting source file changes between incremental runs.
/// Implementations compare current file state against a stored <see cref="IncrementalState"/>.
/// </summary>
public interface IChangeDetector
{
    /// <summary>
    /// Detects which .cs files have changed since the prior run.
    /// </summary>
    /// <param name="repoOrProjectPath">
    /// Path to the git repository root or project directory.
    /// </param>
    /// <param name="priorState">
    /// The stored incremental state from the previous run.
    /// When null, returns a <see cref="ChangeSet"/> with <see cref="ChangeSet.IsFullRebuild"/> = true
    /// (first run / INFR-05).
    /// </param>
    /// <returns>
    /// A <see cref="ChangeSet"/> describing all changed .cs files, or null if
    /// detection failed (e.g., not a git repository for <see cref="GitChangeDetector"/>).
    /// </returns>
    ChangeSet? DetectChanges(string repoOrProjectPath, IncrementalState? priorState);
}
