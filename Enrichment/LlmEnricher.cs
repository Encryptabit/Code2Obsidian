using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    private const int ProgressEntityStride = 10;
    private const long ProgressMinIntervalTicks = TimeSpan.TicksPerSecond;

    private readonly IChatClient _client;
    private readonly SummaryCache _cache;
    private readonly LlmConfig _config;
    private readonly IProgress<PipelineProgress>? _progress;
    private readonly Func<int, int, int, decimal, bool>? _confirmEnrichment;
    private readonly IReadOnlySet<string>? _dirtyFiles;

    private int _inputTokensUsed;
    private int _outputTokensUsed;
    private int _entitiesEnriched;
    private int _entitiesCached;
    private int _entitiesFailed;
    private int _lastReportedCompleted = -1;
    private long _lastProgressTicks;

    public LlmEnricher(
        IChatClient client,
        SummaryCache cache,
        LlmConfig config,
        IProgress<PipelineProgress>? progress = null,
        Func<int, int, int, decimal, bool>? confirmEnrichment = null,
        IReadOnlySet<string>? dirtyFiles = null)
    {
        _client = client;
        _cache = cache;
        _config = config;
        _progress = progress;
        _confirmEnrichment = confirmEnrichment;
        _dirtyFiles = dirtyFiles;
    }

    // Read-only accessors for reconstructing with a different IProgress
    internal IChatClient Client => _client;
    internal SummaryCache Cache => _cache;
    internal LlmConfig Config => _config;
    internal Func<int, int, int, decimal, bool>? ConfirmEnrichment => _confirmEnrichment;
    internal IReadOnlySet<string>? DirtyFiles => _dirtyFiles;

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
    /// When dirtyFiles is set (incremental mode), only entities in dirty files are candidates for LLM calls;
    /// unchanged entities are served from cache only and skipped if uncached.
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
            else if (_dirtyFiles is null || _dirtyFiles.Contains(method.FilePath))
            {
                // Only enrich uncached entities in dirty files (or all if no dirty filter)
                uncachedMethods.Add((method, hash));
            }
            // else: unchanged file, uncached — skip (don't send to LLM in incremental mode)
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
            else if (_dirtyFiles is null || _dirtyFiles.Contains(type.FilePath))
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
                ReportProgress("Enrichment skipped by user", EntitiesCached, totalEntities, force: true);
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
            totalEntities, totalEntities, force: true);
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
            var rawText = response.Text;

            // Track token usage via Interlocked for thread safety
            if (response.Usage?.InputTokenCount is long inputTokens)
                Interlocked.Add(ref _inputTokensUsed, (int)inputTokens);
            if (response.Usage?.OutputTokenCount is long outputTokens)
                Interlocked.Add(ref _outputTokensUsed, (int)outputTokens);

            var enrichmentResponse = ParseXmlResponse(rawText);
            if (enrichmentResponse is null)
            {
                Interlocked.Increment(ref _entitiesFailed);
                ReportEntityProgress($"Failed method: {method.Name}", totalEntities);
                return;
            }

            _cache.Put(method.Id.Value, hash, enrichmentResponse, _config.Model);
            enriched.MethodSummaries[method.Id.Value] = enrichmentResponse;
            Interlocked.Increment(ref _entitiesEnriched);
            ReportEntityProgress($"Enriched method: {method.Name}", totalEntities);
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
            ReportEntityProgress($"Failed method: {method.Name}", totalEntities);
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
            var rawText = response.Text;

            // Track token usage
            if (response.Usage?.InputTokenCount is long inputTokens)
                Interlocked.Add(ref _inputTokensUsed, (int)inputTokens);
            if (response.Usage?.OutputTokenCount is long outputTokens)
                Interlocked.Add(ref _outputTokensUsed, (int)outputTokens);

            var enrichmentResponse = ParseXmlResponse(rawText);
            if (enrichmentResponse is null)
            {
                Interlocked.Increment(ref _entitiesFailed);
                ReportEntityProgress($"Failed type: {type.Name}", totalEntities);
                return;
            }

            _cache.Put(type.Id.Value, hash, enrichmentResponse, _config.Model);
            enriched.TypeSummaries[type.Id.Value] = enrichmentResponse;
            Interlocked.Increment(ref _entitiesEnriched);
            ReportEntityProgress($"Enriched type: {type.Name}", totalEntities);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmEnricher] Warning: Failed to enrich type '{type.Name}': {ex.Message}");
            Interlocked.Increment(ref _entitiesFailed);
            ReportEntityProgress($"Failed type: {type.Name}", totalEntities);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Parses an LLM response string into a structured EnrichmentResponse.
    /// Uses triple fallback: XDocument parse -> regex extraction -> raw text wrapping.
    /// Returns null if the input is null or whitespace.
    /// </summary>
    internal static EnrichmentResponse? ParseXmlResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string? summary = null;
        string? purpose = null;
        string? tagsRaw = null;

        // Attempt 1: XDocument parse
        try
        {
            var doc = XDocument.Parse($"<root>{raw}</root>");
            summary = doc.Root?.Element("summary")?.Value;
            purpose = doc.Root?.Element("purpose")?.Value;
            tagsRaw = doc.Root?.Element("tags")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            // Fall through to regex
        }

        // Attempt 2: Regex fallback (if XDocument didn't find content)
        if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(purpose))
        {
            var summaryMatch = Regex.Match(raw, @"<summary>(.*?)</summary>", RegexOptions.Singleline);
            var purposeMatch = Regex.Match(raw, @"<purpose>(.*?)</purpose>", RegexOptions.Singleline);
            var tagsMatch = Regex.Match(raw, @"<tags>(.*?)</tags>", RegexOptions.Singleline);

            if (summaryMatch.Success) summary = summaryMatch.Groups[1].Value;
            if (purposeMatch.Success) purpose = purposeMatch.Groups[1].Value;
            if (tagsMatch.Success) tagsRaw = tagsMatch.Groups[1].Value;
        }

        // Apply HtmlDecode to extracted values
        if (summary is not null) summary = WebUtility.HtmlDecode(summary);
        if (purpose is not null) purpose = WebUtility.HtmlDecode(purpose);
        if (tagsRaw is not null) tagsRaw = WebUtility.HtmlDecode(tagsRaw);

        // Double fallback: if neither method found content, wrap raw text
        if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(purpose))
        {
            return new EnrichmentResponse(raw.Trim(), raw.Trim(), Array.Empty<string>());
        }

        // Parse tags
        var tags = string.IsNullOrWhiteSpace(tagsRaw)
            ? Array.Empty<string>()
            : tagsRaw.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

        return new EnrichmentResponse(
            summary?.Trim() ?? "",
            purpose?.Trim() ?? "",
            tags);
    }

    private void ReportEntityProgress(string description, int total)
    {
        var completed = EntitiesCached + EntitiesEnriched + EntitiesFailed;
        ReportProgress(description, completed, total);
    }

    private void ReportProgress(string description, int current, int total, bool force = false)
    {
        if (!force && current < total)
        {
            var lastCompleted = Volatile.Read(ref _lastReportedCompleted);
            var lastTicks = Volatile.Read(ref _lastProgressTicks);
            var nowTicks = DateTime.UtcNow.Ticks;

            var strideMet = current - lastCompleted >= ProgressEntityStride;
            var intervalMet = nowTicks - lastTicks >= ProgressMinIntervalTicks;
            if (!strideMet && !intervalMet)
                return;

            if (Interlocked.CompareExchange(ref _lastReportedCompleted, current, lastCompleted) != lastCompleted)
                return;

            Interlocked.Exchange(ref _lastProgressTicks, nowTicks);
        }
        else
        {
            Interlocked.Exchange(ref _lastReportedCompleted, current);
            Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
        }

        _progress?.Report(new PipelineProgress(PipelineStage.Enriching, description, current, total));
    }
}
