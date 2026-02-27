namespace Code2Obsidian.Incremental;

/// <summary>
/// Identifies notes in the vault that should be deleted because their source entities
/// no longer exist. Only considers notes from reanalyzed files (unchanged files are
/// assumed to still be valid).
/// </summary>
public sealed class StaleNoteDetector
{
    /// <summary>
    /// Finds vault notes that should be deleted.
    /// A stored note is stale if:
    ///   (a) Its source_file was reanalyzed (we have fresh data for it), AND
    ///   (b) Its entity_id is NOT in the current emission set (entity was deleted or renamed).
    /// Notes whose source_file was NOT reanalyzed are left alone (those files are unchanged).
    /// </summary>
    /// <param name="storedNotes">note_path to (source_file, entity_id) from previous run state.</param>
    /// <param name="currentEntityIds">Entity IDs present in the current (merged) analysis result.</param>
    /// <param name="reanalyzedFiles">Files that were reanalyzed in this incremental run.</param>
    /// <returns>List of note file paths (absolute vault paths) that should be deleted.</returns>
    public static IReadOnlyList<string> FindStaleNotes(
        IReadOnlyDictionary<string, (string SourceFile, string EntityId)> storedNotes,
        IReadOnlySet<string> currentEntityIds,
        IReadOnlySet<string> reanalyzedFiles)
    {
        var staleNotes = new List<string>();

        foreach (var (notePath, (sourceFile, entityId)) in storedNotes)
        {
            // Only consider notes from files that were reanalyzed
            if (!reanalyzedFiles.Contains(sourceFile))
                continue;

            // If the entity no longer exists in the current analysis, it is stale
            if (!currentEntityIds.Contains(entityId))
                staleNotes.Add(notePath);
        }

        return staleNotes;
    }
}
