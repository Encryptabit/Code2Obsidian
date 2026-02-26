using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Enrichment.Prompts;

/// <summary>
/// Builds system and user prompts for LLM method/type summarization.
/// Prompts are optimized for experienced developers: concise, technical, no basic concept explanations.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// System prompt instructing the LLM to respond in structured XML format.
    /// </summary>
    public static string SystemPrompt { get; } =
        "Analyze the code entity, then respond ONLY with these XML tags:\n" +
        "<summary>1-4 sentence technical summary for experienced developers. Reference implementation details.</summary>\n" +
        "<purpose>Single sentence describing what this code does.</purpose>\n" +
        "<tags>comma-separated: entry-point, data-access, async, utility, factory, DI, validation, error-handling</tags>\n" +
        "No preamble or text outside these tags.";

    /// <summary>
    /// Builds a user prompt for summarizing a C# method.
    /// Includes signature, doc comment, complexity, callees, callers, and containing type.
    /// </summary>
    public static string BuildMethodPrompt(MethodInfo method, AnalysisResult analysis)
    {
        var lines = new List<string>
        {
            "Summarize this C# method:",
            "",
            $"Signature: {method.DisplaySignature}",
            $"Class: {method.ContainingTypeName}",
            $"Complexity: {method.CyclomaticComplexity}"
        };

        if (!string.IsNullOrWhiteSpace(method.DocComment))
        {
            lines.Add($"Doc comment: {method.DocComment}");
        }

        // Callees (up to 20, sorted)
        var calleeNames = ResolveMethodNames(
            analysis.CallGraph.GetCallees(method.Id), analysis, limit: 20);
        if (calleeNames.Count > 0)
        {
            lines.Add($"Calls: {string.Join(", ", calleeNames)}");
        }

        // Callers (up to 20, sorted)
        var callerNames = ResolveMethodNames(
            analysis.CallGraph.GetCallers(method.Id), analysis, limit: 20);
        if (callerNames.Count > 0)
        {
            lines.Add($"Called by: {string.Join(", ", callerNames)}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a user prompt for summarizing a C# type (class, interface, record, struct).
    /// Includes kind, inheritance, interfaces, DI dependencies, and member counts.
    /// </summary>
    public static string BuildTypePrompt(TypeInfo type, AnalysisResult analysis)
    {
        var kindName = type.Kind switch
        {
            TypeKindInfo.Class => "class",
            TypeKindInfo.Interface => "interface",
            TypeKindInfo.Record => "record",
            TypeKindInfo.Struct => "struct",
            _ => "type"
        };

        var lines = new List<string>
        {
            $"Summarize the role and responsibility of this C# {kindName}:",
            "",
            $"Type: {type.FullName}"
        };

        if (!string.IsNullOrWhiteSpace(type.BaseClassFullName))
        {
            lines.Add($"Inherits: {type.BaseClassFullName}");
        }

        if (type.InterfaceFullNames.Count > 0)
        {
            lines.Add($"Implements: {string.Join(", ", type.InterfaceFullNames)}");
        }

        // DI dependencies: extract parameter type names from all constructors, deduplicated
        var diDeps = type.Constructors
            .SelectMany(c => c.Parameters)
            .Select(p => p.TypeName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (diDeps.Count > 0)
        {
            lines.Add($"Dependencies: {string.Join(", ", diDeps)}");
        }

        var propFieldCount = type.Properties.Count + type.Fields.Count;
        lines.Add($"Members: {type.MethodIds.Count} methods, {propFieldCount} properties/fields");

        if (!string.IsNullOrWhiteSpace(type.DocComment))
        {
            lines.Add($"Doc comment: {type.DocComment}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Estimates the combined token count for a system + user prompt pair using the chars/4 heuristic.
    /// </summary>
    public static int EstimatePromptTokens(string systemPrompt, string userPrompt)
    {
        return CostEstimator.EstimateTokens(systemPrompt) + CostEstimator.EstimateTokens(userPrompt);
    }

    /// <summary>
    /// Resolves a set of MethodIds to human-readable method names.
    /// Uses the analysis methods dictionary for lookup; falls back to MethodId.Value.
    /// </summary>
    private static List<string> ResolveMethodNames(
        IReadOnlySet<MethodId> methodIds, AnalysisResult analysis, int limit)
    {
        return methodIds
            .Select(id => analysis.Methods.TryGetValue(id, out var m) ? m.Name : id.Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }
}
