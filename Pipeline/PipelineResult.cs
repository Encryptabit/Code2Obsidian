using Code2Obsidian.Analysis;
using Code2Obsidian.Emission;

namespace Code2Obsidian.Pipeline;

/// <summary>
/// Captures per-stage timing, counts, and warnings from a pipeline execution.
/// Mutable during pipeline execution, read-only after completion.
/// </summary>
public sealed class PipelineResult
{
    public TimeSpan AnalysisDuration { get; set; }
    public TimeSpan EnrichmentDuration { get; set; }
    public TimeSpan EmissionDuration { get; set; }

    /// <summary>
    /// Total duration across all three pipeline stages.
    /// </summary>
    public TimeSpan TotalDuration => AnalysisDuration + EnrichmentDuration + EmissionDuration;

    public int ProjectsAnalyzed { get; set; }
    public int FilesAnalyzed { get; set; }
    public int NotesGenerated { get; set; }
    public int EnrichersRun { get; set; }

    /// <summary>
    /// Number of files skipped due to being unchanged (incremental mode).
    /// </summary>
    public int FilesSkipped { get; set; }

    /// <summary>
    /// Number of stale notes removed from the vault (incremental mode).
    /// </summary>
    public int NotesDeleted { get; set; }

    /// <summary>
    /// Whether this run used incremental mode.
    /// </summary>
    public bool WasIncremental { get; set; }

    /// <summary>
    /// The analysis result, exposed for state saving in incremental mode.
    /// </summary>
    public AnalysisResult? AnalysisResult { get; set; }

    /// <summary>
    /// The emission result, exposed for state saving in incremental mode.
    /// </summary>
    public EmitResult? EmitResult { get; set; }

    /// <summary>
    /// Warnings accumulated from all pipeline stages (skipped files, write failures, etc.).
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Whether a fatal error occurred during pipeline execution.
    /// </summary>
    public bool HasFatalError { get; set; }

    /// <summary>
    /// Exit code: 0 = clean success, 1 = success with warnings, 2 = fatal error.
    /// </summary>
    public int ExitCode => HasFatalError ? 2 : Warnings.Count > 0 ? 1 : 0;
}
