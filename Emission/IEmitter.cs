using Code2Obsidian.Enrichment;

namespace Code2Obsidian.Emission;

/// <summary>
/// Interface for emission pipeline stages.
/// Emitters generate output files (e.g., Obsidian markdown notes) from enriched analysis data.
/// </summary>
public interface IEmitter
{
    /// <summary>
    /// Emits output files to the specified directory.
    /// </summary>
    Task<EmitResult> EmitAsync(EnrichedResult result, string outputDirectory, CancellationToken ct);
}
