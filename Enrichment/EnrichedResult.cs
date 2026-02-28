using Code2Obsidian.Analysis;

using System.Collections.Concurrent;
namespace Code2Obsidian.Enrichment;

/// <summary>
/// Structured LLM response parsed from XML output.
/// Summary is the detailed technical description, Purpose is a one-liner,
/// Tags are comma-separated architectural/behavioral labels.
/// Improvements contains actionable optimization suggestions for this entity.
/// </summary>
public sealed record EnrichmentResponse(string Summary, string Purpose, string[] Tags, string Improvements = "");

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

    /// <summary>
    /// True when this run requested LLM "what it does" summary generation.
    /// Emitters use this to decide whether to render summary blocks.
    /// </summary>
    public bool IncludeSummary { get; set; }

    /// <summary>
    /// True when this run requested LLM improvement suggestions.
    /// Emitters use this to decide whether to render improvements blocks.
    /// </summary>
    public bool IncludeSuggestions { get; set; }

    /// <summary>
    /// LLM-generated enrichment responses for methods, keyed by MethodId.Value.
    /// Empty when --enrich is not used; emitter renders no Summary section in that case.
    /// Uses ConcurrentDictionary for thread-safe writes from parallel enrichment tasks.
    /// </summary>
    public ConcurrentDictionary<string, EnrichmentResponse> MethodSummaries { get; } = new();

    /// <summary>
    /// LLM-generated enrichment responses for types, keyed by TypeId.Value.
    /// Empty when --enrich is not used; emitter renders no Summary section in that case.
    /// Uses ConcurrentDictionary for thread-safe writes from parallel enrichment tasks.
    /// </summary>
    public ConcurrentDictionary<string, EnrichmentResponse> TypeSummaries { get; } = new();

    public EnrichedResult(AnalysisResult analysis)
    {
        Analysis = analysis;
    }
}
