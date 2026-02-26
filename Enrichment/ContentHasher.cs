using System.Security.Cryptography;
using System.Text;
using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Enrichment;

/// <summary>
/// Computes deterministic SHA256 content hashes for methods and types.
/// Hash covers structural data so that any change to signature, body, dependencies,
/// callers, or complexity triggers cache invalidation and re-enrichment.
/// </summary>
public static class ContentHasher
{
    /// <summary>
    /// Computes a SHA256 hash covering the method's signature, body source, doc comment,
    /// sorted callee IDs, sorted caller IDs, and cyclomatic complexity.
    /// Per locked decision: cache key covers signature, body, dependencies, and callers.
    /// </summary>
    public static string ComputeMethodHash(MethodInfo method, CallGraph callGraph)
    {
        var sb = new StringBuilder();

        sb.AppendLine(method.DisplaySignature);
        sb.AppendLine(method.BodySource ?? "");
        sb.AppendLine(method.DocComment ?? "");

        // Sorted callee IDs
        var callees = callGraph.GetCallees(method.Id)
            .Select(id => id.Value)
            .OrderBy(id => id, StringComparer.Ordinal);
        foreach (var callee in callees)
        {
            sb.Append("calls:");
            sb.AppendLine(callee);
        }

        // Sorted caller IDs
        var callers = callGraph.GetCallers(method.Id)
            .Select(id => id.Value)
            .OrderBy(id => id, StringComparer.Ordinal);
        foreach (var caller in callers)
        {
            sb.Append("calledby:");
            sb.AppendLine(caller);
        }

        sb.AppendLine(method.CyclomaticComplexity.ToString());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes a SHA256 hash covering the type's structural data:
    /// full name, doc comment, base class, interfaces, method count, and property count.
    /// Captures structural changes that should trigger re-summarization.
    /// </summary>
    public static string ComputeTypeHash(TypeInfo type)
    {
        var sb = new StringBuilder();

        sb.AppendLine(type.FullName);
        sb.AppendLine(type.DocComment ?? "");
        sb.AppendLine(type.BaseClassFullName ?? "");

        // Sorted interface names
        var interfaces = type.InterfaceFullNames
            .OrderBy(name => name, StringComparer.Ordinal);
        foreach (var iface in interfaces)
        {
            sb.AppendLine(iface);
        }

        sb.AppendLine(type.MethodIds.Count.ToString());
        sb.AppendLine(type.Properties.Count.ToString());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
