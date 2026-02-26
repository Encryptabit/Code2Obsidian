using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Models;
using Code2Obsidian.Enrichment.Config;
using Code2Obsidian.Enrichment.Prompts;
using Code2Obsidian.Pipeline;
using Microsoft.Extensions.AI;

namespace Code2Obsidian.Enrichment;

/// <summary>
/// Core enricher that connects the pipeline to LLM providers via IChatClient.
/// Implements cache-first lookup, SemaphoreSlim-bounded concurrency, per-entity error resilience,
/// and Interlocked token tracking for thread-safe accumulation.
/// </summary>
public sealed class LlmEnricher : IEnricher
{
    private readonly IChatClient _client;
    private readonly SummaryCache _cache;
    private readonly LlmConfig _config;
    private readonly IProgress<PipelineProgress>? _progress;
    private readonly Func<int, int, int, decimal, bool>? _confirmEnrichment;

    private int _inputTokensUsed;
    private int _outputTokensUsed;
    private int _entitiesEnriched;
    private int _entitiesCached;
    private int _entitiesFailed;

    public LlmEnricher(
        IChatClient client,
        SummaryCache cache,
        LlmConfig config,
        IProgress<PipelineProgress>? progress = null,
        Func<int, int, int, decimal, bool>? confirmEnrichment = null)
    {
        _client = client;
        _cache = cache;
        _config = config;
        _progress = progress;
        _confirmEnrichment = confirmEnrichment;
    }

    // Read-only accessors for reconstructing with a different IProgress
    internal IChatClient Client => _client;
    internal SummaryCache Cache => _cache;
    internal LlmConfig Config => _config;
    internal Func<int, int, int, decimal, bool>? ConfirmEnrichment => _confirmEnrichment;

    /// <summary>Human-readable name for pipeline progress display.</summary>
    public string Name => "LLM Enrichment";

    /// <summary>Cumulative input tokens consumed across all LLM calls (thread-safe).</summary>
    public int InputTokensUsed => Volatile.Read(ref _inputTokensUsed);

    /// <summary>Cumulative output tokens consumed across all LLM calls (thread-safe).</summary>
    public int OutputTokensUsed => Volatile.Read(ref _outputTokensUsed);

    /// <summary>Count of entities that received LLM-generated summaries.</summary>
    public int EntitiesEnriched => Volatile.Read(ref _entitiesEnriched);

    /// <summary>Count of entities served from the summary cache (zero API calls).</summary>
    public int EntitiesCached => Volatile.Read(ref _entitiesCached);

    /// <summary>Count of entities where the LLM call failed (logged and skipped).</summary>
    public int EntitiesFailed => Volatile.Read(ref _entitiesFailed);

    /// <summary>
    /// Enriches the analysis result with LLM-generated summaries for methods and types.
    /// Cache-first: checks SummaryCache before each LLM call. Running twice on unchanged code makes zero API calls.
    /// </summary>
    public async Task EnrichAsync(AnalysisResult analysis, EnrichedResult enriched, CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(_config.MaxConcurrency);

        // Phase 1: Partition methods into cached vs uncached
        var uncachedMethods = new List<(MethodInfo method, string hash)>();
        foreach (var (methodId, method) in analysis.Methods)
        {
            ct.ThrowIfCancellationRequested();
            var hash = ContentHasher.ComputeMethodHash(method, analysis.CallGraph);
            var cached = _cache.TryGet(methodId.Value, hash);
            if (cached is not null)
            {
                enriched.MethodSummaries[methodId.Value] = cached;
                Interlocked.Increment(ref _entitiesCached);
            }
            else
            {
                uncachedMethods.Add((method, hash));
            }
        }

        // Phase 2: Partition types into cached vs uncached
        var uncachedTypes = new List<(TypeInfo type, string hash)>();
        foreach (var (typeId, type) in analysis.Types)
        {
            ct.ThrowIfCancellationRequested();
            var hash = ContentHasher.ComputeTypeHash(type);
            var cached = _cache.TryGet(typeId.Value, hash);
            if (cached is not null)
            {
                enriched.TypeSummaries[typeId.Value] = cached;
                Interlocked.Increment(ref _entitiesCached);
            }
            else
            {
                uncachedTypes.Add((type, hash));
            }
        }

        var totalEntities = analysis.Methods.Count + analysis.Types.Count;
        var uncachedCount = uncachedMethods.Count + uncachedTypes.Count;

        ReportProgress(
            $"Found {totalEntities} entities: {EntitiesCached} cached, {uncachedCount} to enrich",
            EntitiesCached, totalEntities);

        // Cost estimation and confirmation before LLM calls
        if (uncachedCount > 0 && CostEstimator.ShouldConfirm(uncachedCount) && _confirmEnrichment is not null)
        {
            // Sample first few methods for average prompt size estimation
            var sampleCount = Math.Min(5, uncachedMethods.Count);
            var avgInputTokens = 0;
            if (sampleCount > 0)
            {
                var totalSampleTokens = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    var prompt = PromptBuilder.BuildMethodPrompt(uncachedMethods[i].method, analysis);
                    totalSampleTokens += CostEstimator.EstimateTokens(PromptBuilder.SystemPrompt + prompt);
                }
                avgInputTokens = totalSampleTokens / sampleCount;
            }
            else
            {
                avgInputTokens = 500; // fallback estimate
            }

            var (estInputTokens, estOutputTokens, estCost) = CostEstimator.EstimateCost(
                uncachedCount, avgInputTokens, _config.MaxOutputTokens,
                _config.CostPerInputToken, _config.CostPerOutputToken);

            if (!_confirmEnrichment(uncachedCount, estInputTokens, estOutputTokens, estCost))
            {
                ReportProgress("Enrichment skipped by user", EntitiesCached, totalEntities);
                return;
            }
        }

        // Phase 3: Process uncached methods in parallel with SemaphoreSlim gating
        var methodTasks = uncachedMethods.Select(item => ProcessMethodAsync(
            item.method, item.hash, analysis, enriched, semaphore, totalEntities, ct));
        await Task.WhenAll(methodTasks);

        // Phase 4: Process uncached types in parallel with SemaphoreSlim gating
        var typeTasks = uncachedTypes.Select(item => ProcessTypeAsync(
            item.type, item.hash, analysis, enriched, semaphore, totalEntities, ct));
        await Task.WhenAll(typeTasks);

        // Final progress report
        ReportProgress(
            $"Enrichment complete: {EntitiesEnriched} enriched, {EntitiesCached} cached, {EntitiesFailed} failed " +
            $"({InputTokensUsed} input tokens, {OutputTokensUsed} output tokens)",
            totalEntities, totalEntities);
    }

    private async Task ProcessMethodAsync(
        MethodInfo method, string hash, AnalysisResult analysis,
        EnrichedResult enriched, SemaphoreSlim semaphore, int totalEntities,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var userPrompt = PromptBuilder.BuildMethodPrompt(method, analysis);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, PromptBuilder.SystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var options = new ChatOptions
            {
                MaxOutputTokens = _config.MaxOutputTokens,
                Temperature = 0.3f
            };

            var response = await _client.GetResponseAsync(messages, options, ct);
            var summary = response.Text;

            if (string.IsNullOrWhiteSpace(summary))
                return;

            // Track token usage via Interlocked for thread safety
            if (response.Usage?.InputTokenCount is long inputTokens)
                Interlocked.Add(ref _inputTokensUsed, (int)inputTokens);
            if (response.Usage?.OutputTokenCount is long outputTokens)
                Interlocked.Add(ref _outputTokensUsed, (int)outputTokens);

            _cache.Put(method.Id.Value, hash, summary, _config.Model);
            enriched.MethodSummaries[method.Id.Value] = summary;
            Interlocked.Increment(ref _entitiesEnriched);

            var completed = EntitiesCached + EntitiesEnriched + EntitiesFailed;
            ReportProgress($"Enriched method: {method.Name}", completed, totalEntities);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            // Per Pitfall 5: single entity failure does not abort the run
            Console.Error.WriteLine($"[LlmEnricher] Warning: Failed to enrich method '{method.Name}': {ex.Message}");
            Interlocked.Increment(ref _entitiesFailed);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ProcessTypeAsync(
        TypeInfo type, string hash, AnalysisResult analysis,
        EnrichedResult enriched, SemaphoreSlim semaphore, int totalEntities,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var userPrompt = PromptBuilder.BuildTypePrompt(type, analysis);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, PromptBuilder.SystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var options = new ChatOptions
            {
                MaxOutputTokens = _config.MaxOutputTokens,
                Temperature = 0.3f
            };

            var response = await _client.GetResponseAsync(messages, options, ct);
            var summary = response.Text;

            if (string.IsNullOrWhiteSpace(summary))
                return;

            // Track token usage
            if (response.Usage?.InputTokenCount is long inputTokens)
                Interlocked.Add(ref _inputTokensUsed, (int)inputTokens);
            if (response.Usage?.OutputTokenCount is long outputTokens)
                Interlocked.Add(ref _outputTokensUsed, (int)outputTokens);

            _cache.Put(type.Id.Value, hash, summary, _config.Model);
            enriched.TypeSummaries[type.Id.Value] = summary;
            Interlocked.Increment(ref _entitiesEnriched);

            var completed = EntitiesCached + EntitiesEnriched + EntitiesFailed;
            ReportProgress($"Enriched type: {type.Name}", completed, totalEntities);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmEnricher] Warning: Failed to enrich type '{type.Name}': {ex.Message}");
            Interlocked.Increment(ref _entitiesFailed);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void ReportProgress(string description, int current, int total)
    {
        _progress?.Report(new PipelineProgress(PipelineStage.Enriching, description, current, total));
    }
}
