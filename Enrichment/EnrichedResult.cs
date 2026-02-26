using Code2Obsidian.Analysis;

using System.Collections.Concurrent;
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

    /// <summary>
    /// LLM-generated summaries for methods, keyed by MethodId.Value.
    /// Empty when --enrich is not used; emitter renders no Summary section in that case.
    /// Uses ConcurrentDictionary for thread-safe writes from parallel enrichment tasks.
    /// </summary>
    public ConcurrentDictionary<string, string> MethodSummaries { get; } = new();

    /// <summary>
    /// LLM-generated summaries for types, keyed by TypeId.Value.
    /// Empty when --enrich is not used; emitter renders no Summary section in that case.
    /// Uses ConcurrentDictionary for thread-safe writes from parallel enrichment tasks.
    /// </summary>
    public ConcurrentDictionary<string, string> TypeSummaries { get; } = new();

    public EnrichedResult(AnalysisResult analysis)
    {
        Analysis = analysis;
    }
}
