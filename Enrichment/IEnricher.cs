using Code2Obsidian.Analysis;

namespace Code2Obsidian.Enrichment;

/// <summary>
/// Interface for enrichment pipeline stages.
/// Enrichers add supplemental data (e.g., LLM summaries) to the analysis result.
/// Stub interface for Phase 1 -- no enrichers are implemented yet.
/// </summary>
public interface IEnricher
{
    /// <summary>
    /// Human-readable name for this enricher (used in progress reporting).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Enriches the analysis result with additional data.
    /// </summary>
    Task EnrichAsync(AnalysisResult analysis, EnrichedResult enriched, CancellationToken ct);
}
