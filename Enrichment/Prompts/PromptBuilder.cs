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
    /// Default system prompt for summary-only enrichment (backward compatible).
    /// </summary>
    public static string SystemPrompt { get; } = BuildSystemPrompt(includeSummary: true, includeSuggestions: false);

    /// <summary>
    /// Builds a system prompt tailored to summary and/or suggestions mode.
    /// </summary>
    public static string BuildSystemPrompt(bool includeSummary, bool includeSuggestions)
    {
        if (!includeSummary && !includeSuggestions)
            throw new ArgumentException("At least one enrichment mode must be enabled.");

        var lines = new List<string>
        {
            "Analyze the code entity, then respond ONLY with these XML tags:",
            "Use tools as needed to inspect source code details.",
            "Start from the provided Source file and stay within the provided Analysis root.",
            "Avoid broad repository scans when targeted lookups are sufficient."
        };

        if (includeSummary)
        {
            lines.Add("<summary>1-4 sentence technical summary for experienced developers. Reference implementation details.</summary>");
            lines.Add("<purpose>Single sentence describing what this code does.</purpose>");
            lines.Add("<tags>comma-separated: entry-point, data-access, async, utility, factory, DI, validation, error-handling</tags>");
        }

        if (includeSuggestions)
        {
            lines.Add("<improvements>2-5 concise, concrete optimization/refactor suggestions as markdown bullets (each line starts with '- ').</improvements>");
        }

        lines.Add("No preamble or text outside these tags.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a user prompt for summarizing a C# method.
    /// Includes signature, doc comment, complexity, callees, callers, and containing type.
    /// </summary>
    public static string BuildMethodPrompt(MethodInfo method, AnalysisResult analysis)
    {
        return BuildMethodPrompt(
            method,
            analysis,
            includeSummary: true,
            includeSuggestions: false,
            existingWhatItDoes: null,
            analysisRoot: null);
    }

    /// <summary>
    /// Builds a mode-aware user prompt for a C# method.
    /// </summary>
    public static string BuildMethodPrompt(
        MethodInfo method,
        AnalysisResult analysis,
        bool includeSummary,
        bool includeSuggestions,
        string? existingWhatItDoes,
        string? analysisRoot = null)
    {
        if (!includeSummary && !includeSuggestions)
            throw new ArgumentException("At least one enrichment mode must be enabled.");

        var intent = includeSummary && includeSuggestions
            ? "Summarize this C# method and suggest concrete improvements:"
            : includeSuggestions
                ? "Suggest concrete improvements for this C# method:"
                : "Summarize this C# method:";

        var lines = new List<string>
        {
            intent,
            ""
        };

        AddLocationContext(
            lines,
            method.FilePath,
            method.Id.Value,
            method.ProjectName,
            analysisRoot);

        lines.AddRange(
        [
            $"Signature: {method.DisplaySignature}",
            $"Class: {method.ContainingTypeName}",
            $"Complexity: {method.CyclomaticComplexity}"
        ]);

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

        if (includeSuggestions && !string.IsNullOrWhiteSpace(existingWhatItDoes))
        {
            lines.Add("");
            lines.Add("Existing \"What it does\" context:");
            lines.Add(existingWhatItDoes.Trim());
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a user prompt for summarizing a C# type (class, interface, record, struct).
    /// Includes kind, inheritance, interfaces, DI dependencies, and member counts.
    /// </summary>
    public static string BuildTypePrompt(TypeInfo type, AnalysisResult analysis)
    {
        return BuildTypePrompt(
            type,
            analysis,
            includeSummary: true,
            includeSuggestions: false,
            existingWhatItDoes: null,
            analysisRoot: null);
    }

    /// <summary>
    /// Builds a mode-aware user prompt for a C# type.
    /// </summary>
    public static string BuildTypePrompt(
        TypeInfo type,
        AnalysisResult analysis,
        bool includeSummary,
        bool includeSuggestions,
        string? existingWhatItDoes,
        string? analysisRoot = null)
    {
        if (!includeSummary && !includeSuggestions)
            throw new ArgumentException("At least one enrichment mode must be enabled.");

        var kindName = type.Kind switch
        {
            TypeKindInfo.Class => "class",
            TypeKindInfo.Interface => "interface",
            TypeKindInfo.Record => "record",
            TypeKindInfo.Struct => "struct",
            _ => "type"
        };

        var intent = includeSummary && includeSuggestions
            ? $"Summarize the role of this C# {kindName} and suggest concrete improvements:"
            : includeSuggestions
                ? $"Suggest concrete improvements for this C# {kindName}:"
                : $"Summarize the role and responsibility of this C# {kindName}:";

        var lines = new List<string>
        {
            intent,
            ""
        };

        AddLocationContext(
            lines,
            type.FilePath,
            type.Id.Value,
            type.ProjectName,
            analysisRoot);

        lines.AddRange(
        [
            $"Type: {type.FullName}"
        ]);

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

        if (includeSuggestions && !string.IsNullOrWhiteSpace(existingWhatItDoes))
        {
            lines.Add("");
            lines.Add("Existing \"What it does\" context:");
            lines.Add(existingWhatItDoes.Trim());
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

    private static void AddLocationContext(
        List<string> lines,
        string sourceFile,
        string entityId,
        string project,
        string? analysisRoot)
    {
        lines.Add("Location context:");
        if (!string.IsNullOrWhiteSpace(analysisRoot))
            lines.Add($"Analysis root: {analysisRoot}");
        lines.Add($"Source file: {sourceFile}");
        lines.Add($"Entity id: {entityId}");
        lines.Add($"Project: {project}");
        lines.Add("");
    }
}
