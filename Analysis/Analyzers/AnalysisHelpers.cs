using Microsoft.CodeAnalysis;

namespace Code2Obsidian.Analysis.Analyzers;

/// <summary>
/// Shared utility methods for analysis operations.
/// </summary>
internal static class AnalysisHelpers
{
    /// <summary>
    /// Determines if a method symbol is "user code" (defined in source within the solution).
    /// Ported from Program.cs IsUserMethod() (lines 397-417), adapted to use
    /// assembly name strings instead of IAssemblySymbol equality.
    /// </summary>
    public static bool IsUserMethod(IMethodSymbol? method, IReadOnlySet<string> projectAssemblyNames)
    {
        if (method is null) return false;

        // Must come from one of our project assemblies (by name comparison)
        if (method.ContainingAssembly is null ||
            !projectAssemblyNames.Contains(method.ContainingAssembly.Name))
            return false;

        // Must have source (exclude metadata-only)
        if (method.Locations.All(l => !l.IsInSource)) return false;

        // Skip generated/implicit bits
        if (method.IsImplicitlyDeclared) return false;

        // Skip property/event accessors and operators
        if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
            or MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator)
            return false;

        return true;
    }

    /// <summary>
    /// Extracts and formats XML doc comments for a method.
    /// Ported from Program.cs GetMethodDocstring() (lines 420-472).
    /// </summary>
    public static string? GetMethodDocstring(IMethodSymbol method)
    {
        try
        {
            var xml = method.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: default);
            if (string.IsNullOrWhiteSpace(xml)) return null;

            // Wrap to ensure single root for parsing
            var root = System.Xml.Linq.XElement.Parse($"<root>{xml}</root>");

            var sb = new System.Text.StringBuilder();

            var summary = root.Element("summary")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(summary))
                sb.AppendLine(NormalizeSpaces(summary));

            var parameters = root.Elements("param").ToList();
            if (parameters.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Parameters:");
                foreach (var p in parameters)
                {
                    var name = p.Attribute("name")?.Value ?? "";
                    var text = NormalizeSpaces(p.Value?.Trim() ?? "");
                    sb.AppendLine($"- {name}: {text}");
                }
            }

            var returns = root.Element("returns")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(returns))
            {
                sb.AppendLine();
                sb.AppendLine($"Returns: {NormalizeSpaces(returns)}");
            }

            var remarks = root.Element("remarks")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(remarks))
            {
                sb.AppendLine();
                sb.AppendLine(NormalizeSpaces(remarks));
            }

            var result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? xml.Trim() : result;
        }
        catch
        {
            // If the XML isn't well-formed, just return the raw text
            var fallback = method.GetDocumentationCommentXml(expandIncludes: false, cancellationToken: default);
            return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
        }
    }

    /// <summary>
    /// Normalizes whitespace in a string (collapses runs of whitespace to single space).
    /// Ported from Program.cs NormalizeSpaces() (lines 474-492).
    /// </summary>
    public static string NormalizeSpaces(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var b = new System.Text.StringBuilder(s.Length);
        bool ws = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!ws) { b.Append(' '); ws = true; }
            }
            else
            {
                b.Append(ch);
                ws = false;
            }
        }
        return b.ToString().Trim();
    }
}
