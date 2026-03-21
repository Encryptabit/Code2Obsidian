using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Enrichment.Prompts;

/// <summary>
/// Builds system and user prompts for LLM method/type summarization.
/// Prompts are optimized for experienced developers: concise, technical, no basic concept explanations.
/// </summary>
public static class PromptBuilder
{
    private const int DefaultFanThreshold = 10;

    /// <summary>
    /// Default system prompt for summary-only enrichment (backward compatible).
    /// </summary>
    public static string SystemPrompt { get; } = BuildSystemPrompt(includeSummary: true, includeSuggestions: false);

    /// <summary>
    /// Builds a system prompt tailored to summary and/or suggestions mode.
    /// </summary>
    public static string BuildSystemPrompt(bool includeSummary, bool includeSuggestions, bool preferSerenaSymbolLookup = false)
    {
        if (!includeSummary && !includeSuggestions)
            throw new ArgumentException("At least one enrichment mode must be enabled.");

        var lines = new List<string>
        {
            "Analyze the code entity, then respond ONLY with these XML tags:",
            "Use tools only to inspect the target symbol and the supporting evidence needed for the answer.",
            "Start from the provided Source file and stay within the provided Analysis root.",
            "Treat prompt-provided analysis facts as authoritative."
        };

        if (preferSerenaSymbolLookup)
        {
            lines.Add("Serena workflow rules:");
            lines.Add("1. Complexity, callers, callees, fan-in, fan-out, threshold labels, and other whole-solution facts come from precomputed analysis in the prompt.");
            lines.Add("2. Do not recompute repository-wide metrics with Serena.");
            lines.Add("3. Use Serena for symbol navigation, body inspection, immediate structure, and supporting evidence only.");
            lines.Add("4. Method workflow: if Serena reports that no project is active, activate the provided Analysis root; call the Serena get_symbols_overview tool on the Source file; call the Serena find_symbol tool for the containing type; call the Serena find_symbol tool for the target method with include_body=true when body details matter.");
            lines.Add("5. Type workflow: call the Serena get_symbols_overview tool on the Source file; call the Serena find_symbol tool for the target type with include_body=false and depth=1 for immediate structure; fetch member bodies selectively with the Serena find_symbol tool and include_body=true only when needed.");
            lines.Add("6. Use the Serena find_referencing_symbols tool only when real usage evidence is needed for the summary or suggestions.");
            lines.Add("7. Use the Serena find_file tool only when path normalization is needed.");
            lines.Add("8. Use the Serena search_for_pattern tool only for non-symbol content such as literals, config, SQL, or comments.");
            lines.Add("9. Avoid the Serena read_file tool unless Serena cannot resolve the symbol.");
            lines.Add("10. Never use shell grep/rg or other shell text search for symbol lookup.");
        }

        if (includeSummary)
        {
            lines.Add("<summary>1-4 sentence technical summary for experienced developers. Reference implementation details.</summary>");
            lines.Add("<purpose>Single sentence describing what this code does.</purpose>");
            lines.Add("<tags>comma-separated: entry-point, data-access, async, utility, factory, DI, validation, error-handling</tags>");
        }

        if (includeSuggestions)
        {
            lines.Add("<improvements>2-5 markdown bullets (each line starts with '- '). Each suggestion must:");
            lines.Add("  - Be specific to THIS code, not generic advice (no boilerplate null checks, no 'add unit tests' unless a concrete gap exists).");
            lines.Add("  - Identify a real problem: correctness bug, performance issue, maintainability concern, or missed simplification.");
            lines.Add("  - State what to change and why it matters (e.g., 'Replace GroupBy+First with a single pass — current code is O(n log n) for a task that is O(n)').");
            lines.Add("  - Skip suggestions the code already handles correctly. If the code is clean, return fewer bullets rather than padding with low-value advice.");
            lines.Add("</improvements>");
        }

        lines.Add("No preamble or text outside these tags.");
        return string.Join("\n", lines);
    }

    public static string SerenaReadinessSystemPrompt { get; } = """
        You are preparing Serena for headless batch code analysis.
        Use Serena MCP tools only. Do not use shell commands.
        Respond ONLY with:
        <ready>true|false</ready>
        <reason>single-sentence explanation</reason>
        """;

    public static string BuildSerenaReadinessPrompt(string? analysisRoot)
    {
        var lines = new List<string>
        {
            "Verify that Serena is ready for headless symbol lookup in this project.",
            ""
        };

        if (!string.IsNullOrWhiteSpace(analysisRoot))
            lines.Add($"Analysis root: {analysisRoot}");

        lines.AddRange(
        [
            "Steps:",
            "1. Ensure the project is active in Serena. Only if Serena reports that no project is active, activate the analysis root.",
            "2. Call Serena's onboarding-check tool (the tool named check_onboarding_performed, even if exposed under a server prefix).",
            "3. If onboarding is incomplete, inactive, or not yet applied for this project, call Serena's onboarding tool and wait for it to finish.",
            "4. Call Serena's onboarding-check tool again to confirm onboarding is now active.",
            "5. Call Serena's initial-instructions tool (the tool named initial_instructions, even if exposed under a server prefix).",
            "6. If project activation fails, onboarding cannot be completed, onboarding still remains inactive afterward, or initial instructions cannot be retrieved, return <ready>false</ready> with the reason.",
            "7. If Serena is ready for headless use, return <ready>true</ready>."
        ]);

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
        string? analysisRoot = null,
        int fanInThreshold = DefaultFanThreshold,
        int fanOutThreshold = DefaultFanThreshold)
    {
        if (!includeSummary && !includeSuggestions)
            throw new ArgumentException("At least one enrichment mode must be enabled.");

        var intent = includeSummary && includeSuggestions
            ? "Summarize this C# method and suggest concrete improvements:"
            : includeSuggestions
                ? "Suggest concrete improvements for this C# method:"
                : "Summarize this C# method:";
        var fanIn = analysis.CallGraph.GetCallers(method.Id).Count;
        var fanOut = analysis.CallGraph.GetCallees(method.Id).Count;

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
            $"Complexity: {method.CyclomaticComplexity}",
            $"Fan-in: {fanIn}",
            $"Fan-out: {fanOut}",
            $"Threshold labels: {FormatThresholdLabels(fanIn, fanOut, fanInThreshold, fanOutThreshold)}"
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

    private static string FormatThresholdLabels(int fanIn, int fanOut, int fanInThreshold, int fanOutThreshold)
    {
        var normalizedFanInThreshold = Math.Max(1, fanInThreshold);
        var normalizedFanOutThreshold = Math.Max(1, fanOutThreshold);
        var labels = new List<string>();

        if (fanIn >= normalizedFanInThreshold)
            labels.Add("high fan-in");

        if (fanOut >= normalizedFanOutThreshold)
            labels.Add("high fan-out");

        return labels.Count == 0 ? "none" : string.Join(", ", labels);
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
