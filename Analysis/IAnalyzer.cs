using Code2Obsidian.Loading;
using Code2Obsidian.Pipeline;

namespace Code2Obsidian.Analysis;

/// <summary>
/// Interface for analysis pipeline stages.
/// Analyzers inspect the loaded solution and populate the result builder with findings.
/// </summary>
public interface IAnalyzer
{
    /// <summary>
    /// Human-readable name for this analyzer (used in progress reporting).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Analyzes the solution context and adds findings to the builder.
    /// Progress is reported at project/file granularity through the optional progress parameter.
    /// </summary>
    Task AnalyzeAsync(AnalysisContext context, AnalysisResultBuilder builder, IProgress<PipelineProgress>? progress, CancellationToken ct);
}
