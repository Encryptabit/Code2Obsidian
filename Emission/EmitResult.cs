namespace Code2Obsidian.Emission;

/// <summary>
/// Result of the emission stage, reporting how many notes were written and any warnings.
/// </summary>
public sealed record EmitResult(int NotesWritten, IReadOnlyList<string> Warnings);
