namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Pure domain model representing a property or field extracted from source code.
/// Contains NO references to Microsoft.CodeAnalysis types.
/// </summary>
public sealed record PropertyFieldInfo(
    string Name,
    string TypeName,
    string AccessModifier,
    bool IsStatic
);
