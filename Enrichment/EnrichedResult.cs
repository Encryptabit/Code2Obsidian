using Code2Obsidian.Analysis;

namespace Code2Obsidian.Enrichment;

/// <summary>
/// Wraps AnalysisResult with optional enrichment data.
/// In Phase 1 this is a passthrough -- future phases add LLM summaries, complexity scores, etc.
/// </summary>
public sealed class EnrichedResult
{
    /// <summary>
    /// The underlying analysis result.
    /// </summary>
    public AnalysisResult Analysis { get; }

    public EnrichedResult(AnalysisResult analysis)
    {
        Analysis = analysis;
    }
}
