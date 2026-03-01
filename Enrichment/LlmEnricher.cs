using System.Net;
using System.Collections.Concurrent;
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
    private readonly bool _includeSummary;
    private readonly bool _includeSuggestions;

    private int _inputTokensUsed;
    private int _outputTokensUsed;
    private int _entitiesEnriched;
    private int _entitiesCached;
    private int _entitiesFailed;
    private int _entitiesInFlight;
    private int _lastReportedCompleted = -1;
    private long _lastProgressTicks;
    private readonly ConcurrentQueue<string> _failureWarnings = new();

    public LlmEnricher(
        IChatClient client,
        SummaryCache cache,
        LlmConfig config,
        IProgress<PipelineProgress>? progress = null,
        Func<int, int, int, decimal, bool>? confirmEnrichment = null,
        IReadOnlySet<string>? dirtyFiles = null,
        bool includeSummary = true,
        bool includeSuggestions = false)
    {
        if (!includeSummary && !includeSuggestions)
            throw new ArgumentException("LlmEnricher requires at least one mode: summary and/or suggestions.");

        _client = client;
        _cache = cache;
        _config = config;
        _progress = progress;
        _confirmEnrichment = confirmEnrichment;
        _dirtyFiles = dirtyFiles;
        _includeSummary = includeSummary;
        _includeSuggestions = includeSuggestions;
    }

    // Read-only accessors for reconstructing with a different IProgress
    internal IChatClient Client => _client;
    internal SummaryCache Cache => _cache;
    internal LlmConfig Config => _config;
    internal Func<int, int, int, decimal, bool>? ConfirmEnrichment => _confirmEnrichment;
    internal IReadOnlySet<string>? DirtyFiles => _dirtyFiles;
    internal bool IncludeSummary => _includeSummary;
    internal bool IncludeSuggestions => _includeSuggestions;

    /// <summary>Human-readable name for pipeline progress display.</summary>
    public string Name => _includeSummary && _includeSuggestions
        ? "LLM Enrichment + Suggestions"
        : _includeSummary
            ? "LLM Enrichment"
            : "LLM Suggestions";

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

    /// <summary>Warnings for failed entities during this enricher run.</summary>
    internal IReadOnlyCollection<string> FailureWarnings => _failureWarnings.ToArray();

    /// <summary>
    /// Enriches the analysis result with LLM-generated summaries for methods and types.
    /// Cache-first: checks SummaryCache before each LLM call. Running twice on unchanged code makes zero API calls.
    /// When dirtyFiles is set (incremental mode), only entities in dirty files are candidates for LLM calls;
    /// unchanged entities are served from cache only and skipped if uncached.
    /// </summary>
    public async Task EnrichAsync(AnalysisResult analysis, EnrichedResult enriched, CancellationToken ct)
    {
        enriched.IncludeSummary |= _includeSummary;
        enriched.IncludeSuggestions |= _includeSuggestions;

        using var semaphore = new SemaphoreSlim(_config.MaxConcurrency);
        var systemPrompt = PromptBuilder.BuildSystemPrompt(_includeSummary, _includeSuggestions);
        var modeLabel = _includeSummary && _includeSuggestions
            ? "enrich/suggest"
            : _includeSummary
                ? "enrich"
                : "suggest";

        // Phase 1: Partition methods into cached vs uncached
        var uncachedMethods = new List<(MethodInfo method, string hash, string? existingWhatItDoes)>();
        foreach (var (methodId, method) in analysis.Methods)
        {
            ct.ThrowIfCancellationRequested();
            var hash = ContentHasher.ComputeMethodHash(method, analysis.CallGraph);
            var cached = _cache.TryGet(methodId.Value, hash);
            if (cached is null)
            {
                // Backward compatibility: previous versions keyed method cache with
                // call-graph-sensitive hashes. Reuse those entries once and migrate.
                var legacyHash = ContentHasher.ComputeLegacyMethodHash(method, analysis.CallGraph);
                if (!string.Equals(legacyHash, hash, StringComparison.Ordinal))
                {
                    cached = _cache.TryGet(methodId.Value, legacyHash);
                    if (cached is not null)
                    {
                        _cache.Put(methodId.Value, hash, cached, _config.Model, updateSummary: true, updateSuggestions: HasSuggestions(cached));
                    }
                }
            }

            if (cached is not null && (HasSummary(cached) || HasSuggestions(cached)))
            {
                enriched.MethodSummaries[methodId.Value] = cached;
            }

            if (cached is not null && IsCacheHitForRequestedModes(cached))
            {
                Interlocked.Increment(ref _entitiesCached);
            }
            else if (_dirtyFiles is null || _dirtyFiles.Contains(method.FilePath))
            {
                // Only enrich uncached entities in dirty files (or all if no dirty filter)
                uncachedMethods.Add((method, hash, BuildWhatItDoesContext(cached)));
            }
            // else: unchanged file, uncached — skip (don't send to LLM in incremental mode)
        }

        // Phase 2: Partition types into cached vs uncached
        var uncachedTypes = new List<(TypeInfo type, string hash, string? existingWhatItDoes)>();
        foreach (var (typeId, type) in analysis.Types)
        {
            ct.ThrowIfCancellationRequested();
            var hash = ContentHasher.ComputeTypeHash(type);
            var cached = _cache.TryGet(typeId.Value, hash);

            if (cached is not null && (HasSummary(cached) || HasSuggestions(cached)))
            {
                enriched.TypeSummaries[typeId.Value] = cached;
            }

            if (cached is not null && IsCacheHitForRequestedModes(cached))
            {
                Interlocked.Increment(ref _entitiesCached);
            }
            else if (_dirtyFiles is null || _dirtyFiles.Contains(type.FilePath))
            {
                uncachedTypes.Add((type, hash, BuildWhatItDoesContext(cached)));
            }
        }

        var totalEntities = analysis.Methods.Count + analysis.Types.Count;
        var uncachedCount = uncachedMethods.Count + uncachedTypes.Count;

        ReportProgress(
            $"Found {totalEntities} entities: {EntitiesCached} cached, {uncachedCount} to {modeLabel}",
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
                    var prompt = PromptBuilder.BuildMethodPrompt(
                        uncachedMethods[i].method,
                        analysis,
                        _includeSummary,
                        _includeSuggestions,
                        uncachedMethods[i].existingWhatItDoes);
                    totalSampleTokens += CostEstimator.EstimateTokens(systemPrompt + prompt);
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
                await _cache.FlushAsync(ct);
                ReportProgress("Enrichment skipped by user", EntitiesCached, totalEntities, force: true);
                return;
            }
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunProgressHeartbeatAsync(totalEntities, modeLabel, heartbeatCts.Token);
        try
        {
            // Phase 3: Process uncached methods in parallel with SemaphoreSlim gating
            var methodTasks = uncachedMethods.Select(item => ProcessMethodAsync(
                item.method, item.hash, item.existingWhatItDoes, systemPrompt,
                analysis, enriched, semaphore, totalEntities, ct));
            await Task.WhenAll(methodTasks);

            // Phase 4: Process uncached types in parallel with SemaphoreSlim gating
            var typeTasks = uncachedTypes.Select(item => ProcessTypeAsync(
                item.type, item.hash, item.existingWhatItDoes, systemPrompt,
                analysis, enriched, semaphore, totalEntities, ct));
            await Task.WhenAll(typeTasks);

            // Ensure all queued cache writes are committed before stage completion.
            await _cache.FlushAsync(ct);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        // Final progress report
        ReportProgress(
            $"LLM {modeLabel} complete: {EntitiesEnriched} enriched, {EntitiesCached} cached, {EntitiesFailed} failed " +
            $"({InputTokensUsed} input tokens, {OutputTokensUsed} output tokens)",
            totalEntities, totalEntities, force: true);
    }

    private async Task ProcessMethodAsync(
        MethodInfo method, string hash, string? existingWhatItDoes, string systemPrompt, AnalysisResult analysis,
        EnrichedResult enriched, SemaphoreSlim semaphore, int totalEntities,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        Interlocked.Increment(ref _entitiesInFlight);
        try
        {
            var userPrompt = PromptBuilder.BuildMethodPrompt(
                method,
                analysis,
                _includeSummary,
                _includeSuggestions,
                existingWhatItDoes);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
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

            var enrichmentResponse = ParseXmlResponse(rawText, _includeSummary, _includeSuggestions);
            if (enrichmentResponse is null)
            {
                Interlocked.Increment(ref _entitiesFailed);
                _failureWarnings.Enqueue($"Failed method '{method.Name}': unable to parse response.");
                ReportEntityProgress($"Failed method: {method.Name}", totalEntities);
                return;
            }

            var updateSummary = _includeSummary && HasSummary(enrichmentResponse);
            var updateSuggestions = _includeSuggestions && HasSuggestions(enrichmentResponse);
            if (!updateSummary && !updateSuggestions)
            {
                Interlocked.Increment(ref _entitiesFailed);
                _failureWarnings.Enqueue($"Failed method '{method.Name}': response did not include requested enrichment fields.");
                ReportEntityProgress($"Failed method: {method.Name}", totalEntities);
                return;
            }

            _cache.Put(
                method.Id.Value,
                hash,
                enrichmentResponse,
                _config.Model,
                updateSummary: updateSummary,
                updateSuggestions: updateSuggestions);
            enriched.MethodSummaries[method.Id.Value] = enrichmentResponse;
            Interlocked.Increment(ref _entitiesEnriched);
            ReportEntityProgress($"Processed method: {method.Name}", totalEntities);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            // Per Pitfall 5: single entity failure does not abort the run
            Interlocked.Increment(ref _entitiesFailed);
            _failureWarnings.Enqueue($"Failed to enrich method '{method.Name}': {ex.Message}");
            ReportEntityProgress($"Failed method: {method.Name}", totalEntities);
        }
        finally
        {
            Interlocked.Decrement(ref _entitiesInFlight);
            semaphore.Release();
        }
    }

    private async Task ProcessTypeAsync(
        TypeInfo type, string hash, string? existingWhatItDoes, string systemPrompt, AnalysisResult analysis,
        EnrichedResult enriched, SemaphoreSlim semaphore, int totalEntities,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        Interlocked.Increment(ref _entitiesInFlight);
        try
        {
            var userPrompt = PromptBuilder.BuildTypePrompt(
                type,
                analysis,
                _includeSummary,
                _includeSuggestions,
                existingWhatItDoes);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
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

            var enrichmentResponse = ParseXmlResponse(rawText, _includeSummary, _includeSuggestions);
            if (enrichmentResponse is null)
            {
                Interlocked.Increment(ref _entitiesFailed);
                _failureWarnings.Enqueue($"Failed type '{type.Name}': unable to parse response.");
                ReportEntityProgress($"Failed type: {type.Name}", totalEntities);
                return;
            }

            var updateSummary = _includeSummary && HasSummary(enrichmentResponse);
            var updateSuggestions = _includeSuggestions && HasSuggestions(enrichmentResponse);
            if (!updateSummary && !updateSuggestions)
            {
                Interlocked.Increment(ref _entitiesFailed);
                _failureWarnings.Enqueue($"Failed type '{type.Name}': response did not include requested enrichment fields.");
                ReportEntityProgress($"Failed type: {type.Name}", totalEntities);
                return;
            }

            _cache.Put(
                type.Id.Value,
                hash,
                enrichmentResponse,
                _config.Model,
                updateSummary: updateSummary,
                updateSuggestions: updateSuggestions);
            enriched.TypeSummaries[type.Id.Value] = enrichmentResponse;
            Interlocked.Increment(ref _entitiesEnriched);
            ReportEntityProgress($"Processed type: {type.Name}", totalEntities);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _entitiesFailed);
            _failureWarnings.Enqueue($"Failed to enrich type '{type.Name}': {ex.Message}");
            ReportEntityProgress($"Failed type: {type.Name}", totalEntities);
        }
        finally
        {
            Interlocked.Decrement(ref _entitiesInFlight);
            semaphore.Release();
        }
    }

    /// <summary>
    /// Parses an LLM response string into a structured EnrichmentResponse.
    /// Uses triple fallback: XDocument parse -> regex extraction -> raw text wrapping.
    /// Returns null if the input is null or whitespace.
    /// </summary>
    internal static EnrichmentResponse? ParseXmlResponse(string raw, bool includeSummary, bool includeSuggestions)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string? summary = null;
        string? purpose = null;
        string? tagsRaw = null;
        string? improvements = null;

        // Attempt 1: XDocument parse
        try
        {
            var doc = XDocument.Parse($"<root>{raw}</root>");
            summary = doc.Root?.Element("summary")?.Value;
            purpose = doc.Root?.Element("purpose")?.Value;
            tagsRaw = doc.Root?.Element("tags")?.Value;
            improvements = doc.Root?.Element("improvements")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            // Fall through to regex
        }

        // Attempt 2: Regex fallback (if XDocument didn't find content)
        if ((string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(purpose)) || string.IsNullOrWhiteSpace(improvements))
        {
            var summaryMatch = Regex.Match(raw, @"<summary>(.*?)</summary>", RegexOptions.Singleline);
            var purposeMatch = Regex.Match(raw, @"<purpose>(.*?)</purpose>", RegexOptions.Singleline);
            var tagsMatch = Regex.Match(raw, @"<tags>(.*?)</tags>", RegexOptions.Singleline);
            var improvementsMatch = Regex.Match(raw, @"<improvements>(.*?)</improvements>", RegexOptions.Singleline);

            if (summaryMatch.Success) summary = summaryMatch.Groups[1].Value;
            if (purposeMatch.Success) purpose = purposeMatch.Groups[1].Value;
            if (tagsMatch.Success) tagsRaw = tagsMatch.Groups[1].Value;
            if (improvementsMatch.Success) improvements = improvementsMatch.Groups[1].Value;
        }

        // Apply HtmlDecode to extracted values
        if (summary is not null) summary = WebUtility.HtmlDecode(summary);
        if (purpose is not null) purpose = WebUtility.HtmlDecode(purpose);
        if (tagsRaw is not null) tagsRaw = WebUtility.HtmlDecode(tagsRaw);
        if (improvements is not null) improvements = WebUtility.HtmlDecode(improvements);

        // Mode-aware fallback for malformed non-XML output.
        if (includeSummary && string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(purpose))
        {
            summary = raw.Trim();
            purpose = raw.Trim();
        }

        if (includeSuggestions && string.IsNullOrWhiteSpace(improvements))
        {
            if (!includeSummary || (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(purpose)))
            {
                improvements = raw.Trim();
            }
        }

        // Parse tags
        var tags = string.IsNullOrWhiteSpace(tagsRaw)
            ? Array.Empty<string>()
            : tagsRaw.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

        return new EnrichmentResponse(
            summary?.Trim() ?? "",
            purpose?.Trim() ?? "",
            tags,
            improvements?.Trim() ?? "");
    }

    private bool IsCacheHitForRequestedModes(EnrichmentResponse cached)
    {
        var hasSummary = !_includeSummary || HasSummary(cached);
        var hasSuggestions = !_includeSuggestions || HasSuggestions(cached);
        return hasSummary && hasSuggestions;
    }

    private static bool HasSummary(EnrichmentResponse response) =>
        !string.IsNullOrWhiteSpace(response.Summary) || !string.IsNullOrWhiteSpace(response.Purpose);

    private static bool HasSuggestions(EnrichmentResponse response) =>
        !string.IsNullOrWhiteSpace(response.Improvements);

    private static string? BuildWhatItDoesContext(EnrichmentResponse? response)
    {
        if (response is null || !HasSummary(response))
            return null;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(response.Purpose))
            parts.Add(response.Purpose.Trim());
        if (!string.IsNullOrWhiteSpace(response.Summary))
            parts.Add(response.Summary.Trim());

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private void ReportEntityProgress(string description, int total)
    {
        var completed = EntitiesCached + EntitiesEnriched + EntitiesFailed;
        ReportProgress(description, completed, total);
    }

    private async Task RunProgressHeartbeatAsync(int total, string modeLabel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var completed = EntitiesCached + EntitiesEnriched + EntitiesFailed;
            if (completed >= total)
                return;

            var pending = Math.Max(0, total - completed);
            var inFlight = Volatile.Read(ref _entitiesInFlight);
            ReportProgress(
                $"LLM {modeLabel}: {completed}/{total} complete, {pending} pending, {inFlight} in-flight",
                completed,
                total);

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
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
