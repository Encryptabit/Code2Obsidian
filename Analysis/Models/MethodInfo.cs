namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Pure domain model representing a method extracted from source code.
/// Contains NO references to Microsoft.CodeAnalysis types.
/// </summary>
public sealed record MethodInfo(
    MethodId Id,
    string Name,
    string ContainingTypeName,
    TypeId ContainingTypeId,
    string FilePath,
    string DisplaySignature,
    string? DocComment,
    string Namespace,
    string ProjectName,
    string AccessModifier,
    int CyclomaticComplexity,
    string? BodySource = null
);
