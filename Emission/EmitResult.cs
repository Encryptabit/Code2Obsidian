namespace Code2Obsidian.Emission;

/// <summary>
/// Result of the emission stage, reporting how many notes were written, any warnings,
/// and the list of emitted notes for incremental state storage.
/// </summary>
public sealed record EmitResult(
    int NotesWritten,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<(string NotePath, string SourceFile, string EntityId)> EmittedNotes);
