using Microsoft.CodeAnalysis;

namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Strongly-typed wrapper for a stable type identifier string.
/// Format: "Namespace.ClassName"
/// </summary>
public readonly record struct TypeId(string Value)
{
    /// <summary>
    /// Creates a stable TypeId from a Roslyn INamedTypeSymbol.
    /// This is the ONLY place where Roslyn symbols cross into domain models.
    /// </summary>
    public static TypeId FromSymbol(INamedTypeSymbol symbol)
    {
        return new TypeId(symbol.ToDisplayString());
    }

    public override string ToString() => Value;
}
