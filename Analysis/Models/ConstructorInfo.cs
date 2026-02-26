namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Pure domain model representing a constructor with its parameters.
/// Contains NO references to Microsoft.CodeAnalysis types.
/// </summary>
public sealed record ConstructorInfo(
    string DisplaySignature,
    string AccessModifier,
    IReadOnlyList<ParameterInfo> Parameters
);
