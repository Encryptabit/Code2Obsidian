using Microsoft.CodeAnalysis;

namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Strongly-typed wrapper for a stable method identifier string.
/// Format: "Namespace.ClassName.MethodName(ParamType1, ParamType2)"
/// </summary>
public readonly record struct MethodId(string Value)
{
    /// <summary>
    /// Creates a stable MethodId from a Roslyn IMethodSymbol.
    /// This is the ONLY place where Roslyn symbols cross into domain models.
    /// </summary>
    public static MethodId FromSymbol(IMethodSymbol symbol)
    {
        var containingType = symbol.ContainingType?.ToDisplayString() ?? "global";
        var parameters = string.Join(", ",
            symbol.Parameters.Select(p => p.Type.ToDisplayString()));
        return new MethodId($"{containingType}.{symbol.Name}({parameters})");
    }

    public override string ToString() => Value;
}
