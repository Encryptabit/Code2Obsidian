namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Pure domain model representing a method or constructor parameter.
/// Contains NO references to Microsoft.CodeAnalysis types.
/// </summary>
public sealed record ParameterInfo(
    string Name,
    string TypeName,
    string? TypeNoteFullName
);
