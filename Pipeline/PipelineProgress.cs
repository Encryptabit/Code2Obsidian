namespace Code2Obsidian.Pipeline;

/// <summary>
/// Pipeline execution stage for progress reporting.
/// </summary>
public enum PipelineStage
{
    Loading,
    Analyzing,
    Enriching,
    Emitting
}

/// <summary>
/// Progress report from the pipeline to the UI layer.
/// Decouples pipeline execution from Spectre.Console so Pipeline remains testable.
/// </summary>
public sealed record PipelineProgress(
    PipelineStage Stage,
    string Description,
    int Current,
    int Total
);
