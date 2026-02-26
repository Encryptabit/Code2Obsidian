namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Categorization of type kind for domain model usage.
/// </summary>
public enum TypeKindInfo
{
    Class,
    Interface,
    Record,
    Struct
}

/// <summary>
/// Pure domain model representing a class, interface, record, or struct extracted from source code.
/// Contains NO references to Microsoft.CodeAnalysis types.
/// </summary>
public sealed record TypeInfo(
    TypeId Id,
    string Name,
    string FullName,
    string Namespace,
    TypeKindInfo Kind,
    string FilePath,
    string? BaseClassFullName,
    string? BaseClassName,
    IReadOnlyList<string> InterfaceFullNames,
    IReadOnlyList<string> InterfaceNames,
    IReadOnlyList<PropertyFieldInfo> Properties,
    IReadOnlyList<PropertyFieldInfo> Fields,
    IReadOnlyList<ConstructorInfo> Constructors,
    IReadOnlyList<MethodId> MethodIds,
    string? DocComment,
    string ProjectName,
    string AccessModifier
);
