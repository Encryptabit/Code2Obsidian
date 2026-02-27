using System.Diagnostics;
using Code2Obsidian.Analysis;
using Code2Obsidian.Emission;
using Code2Obsidian.Enrichment;
using Code2Obsidian.Loading;

namespace Code2Obsidian.Pipeline;

/// <summary>
/// Orchestrates the analysis -> enrichment -> emission pipeline.
/// Reports progress through IProgress&lt;PipelineProgress&gt; so the UI layer
/// (Spectre.Console) is not a dependency of this class.
/// </summary>
public sealed class Pipeline
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers;
    private readonly IReadOnlyList<IEnricher> _enrichers;
    private readonly IEmitter _emitter;

    public Pipeline(
        IReadOnlyList<IAnalyzer> analyzers,
        IReadOnlyList<IEnricher> enrichers,
        IEmitter emitter)
    {
        _analyzers = analyzers;
        _enrichers = enrichers;
        _emitter = emitter;
    }

    public async Task<PipelineResult> RunAsync(
        AnalysisContext context,
        string outputDir,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        var result = new PipelineResult();
        var sw = Stopwatch.StartNew();

        // Stage 1: Analysis
        var builder = new AnalysisResultBuilder();
        for (int i = 0; i < _analyzers.Count; i++)
        {
            var analyzer = _analyzers[i];
            progress?.Report(new PipelineProgress(
                PipelineStage.Analyzing,
                $"Running {analyzer.Name}...",
                i,
                _analyzers.Count));

            await analyzer.AnalyzeAsync(context, builder, progress, ct);
        }

        var analysisResult = builder.Build();
        result.AnalysisDuration = sw.Elapsed;
        result.ProjectsAnalyzed = analysisResult.ProjectCount;
        result.FilesAnalyzed = analysisResult.FileCount;

        progress?.Report(new PipelineProgress(
            PipelineStage.Analyzing,
            "Analysis complete",
            _analyzers.Count,
            _analyzers.Count));

        sw.Restart();

        // Stage 2: Enrichment
        var enrichedResult = new EnrichedResult(analysisResult);
        for (int i = 0; i < _enrichers.Count; i++)
        {
            var enricher = _enrichers[i];
            progress?.Report(new PipelineProgress(
                PipelineStage.Enriching,
                $"Running {enricher.Name}...",
                i,
                _enrichers.Count));

            await enricher.EnrichAsync(analysisResult, enrichedResult, ct);
            result.EnrichersRun++;
        }

        if (_enrichers.Count == 0)
        {
            progress?.Report(new PipelineProgress(
                PipelineStage.Enriching,
                "Enrichment skipped (Phase 1)",
                1,
                1));
        }

        result.EnrichmentDuration = sw.Elapsed;
        sw.Restart();

        // Stage 3: Emission
        progress?.Report(new PipelineProgress(
            PipelineStage.Emitting,
            "Emitting vault...",
            0,
            analysisResult.Methods.Count));

        var emitResult = await _emitter.EmitAsync(enrichedResult, outputDir, ct);

        result.EmissionDuration = sw.Elapsed;
        result.NotesGenerated = emitResult.NotesWritten;
        result.Warnings.AddRange(emitResult.Warnings);

        // Expose analysis and emission results for state saving in incremental mode
        result.AnalysisResult = analysisResult;
        result.EmitResult = emitResult;

        progress?.Report(new PipelineProgress(
            PipelineStage.Emitting,
            "Emission complete",
            emitResult.NotesWritten,
            emitResult.NotesWritten));

        return result;
    }
}
