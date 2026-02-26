# Phase 5: LLM Enrichment - Research

**Researched:** 2026-02-26
**Domain:** LLM integration via Microsoft.Extensions.AI, provider-agnostic chat completions, content-hash caching, cost estimation
**Confidence:** HIGH

## Summary

Phase 5 adds opt-in LLM-generated plain-English summaries to the existing pipeline. The codebase already has stub `IEnricher`/`EnrichedResult` types and a pipeline that calls enrichers between analysis and emission. The primary technology decision -- Microsoft.Extensions.AI (MEAI) -- is locked and well-supported: the `IChatClient` interface is GA (since May 2025), targets .NET 8, and has first-party provider packages for OpenAI, OllamaSharp, and the official Anthropic SDK. All three providers expose `.AsIChatClient()` extension methods, making provider switching a config-level concern.

The key implementation challenges are: (1) designing a content hash that captures structural changes without over-invalidating cached summaries, (2) building a token estimation strategy that works across providers for cost display, (3) integrating with the existing SQLite state store via a schema migration, and (4) handling the interactive setup flow when no config exists. The MEAI `ChatResponse.Usage` property provides `InputTokenCount`/`OutputTokenCount` after each call, enabling live token tracking. For pre-run estimation, a character-based heuristic (~1 token per 4 characters) is sufficient since exact tokenization varies by provider.

**Primary recommendation:** Implement a single `LlmEnricher : IEnricher` that receives an `IChatClient` and a summary cache, iterates methods/types, checks cache by content hash, batches uncached items to the LLM with `SemaphoreSlim`-based concurrency control, and stores results. The emitter reads summaries from `EnrichedResult` at render time. Config loading, provider factory, and interactive setup are separate concerns in an `Enrichment/Config/` namespace.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- Audience: experienced developers -- assume domain knowledge, reference implementation details
- Length: Claude's discretion based on complexity -- simple methods get one-liners, complex methods get short paragraphs
- Class summaries: responsibility-focused -- describe the class's role and purpose, not its member list
- Placement: dedicated `## Summary` section in each note, alongside existing structural sections
- Config approach: JSON config file for defaults (`code2obsidian.llm.json`), CLI flags override -- works for both local dev and CI
- Provider abstraction: Microsoft.Extensions.AI (MEAI) -- any MEAI-compatible provider works automatically, no hardcoded provider list
- API keys: config file can reference env vars (`"apiKey": "$ANTHROPIC_API_KEY"`) with environment variable fallback
- Missing provider: if `--enrich` is passed with no LLM configured, interactively walk user through provider setup and save config for next time
- Before enrichment on a large codebase, display estimated API cost and prompt for confirmation (LLM-05)
- Cost estimation should be provider-aware (use model pricing metadata from config or MEAI if available)
- Additionally show live token progress counter during enrichment (tokens used vs estimated) -- user preference for token-centric UX
- Cache storage: extend existing `.code2obsidian.db` SQLite with a summaries table -- one store for everything
- Cache key: content hash covering signature, body, dependencies, and callers -- any structural change triggers re-enrichment
- Incremental interaction: `--incremental --enrich` only sends changed/new methods to LLM -- cached summaries reused for unchanged code
- Running `--enrich` twice on unchanged code makes zero API calls (LLM-03)

### Claude's Discretion
- Summary length calibration per method complexity
- Exact prompt design for LLM calls
- MEAI adapter registration pattern
- Token estimation algorithm
- Interactive setup flow UX details
- Error handling for LLM API failures (retries, timeouts, partial results)
- Confirmation threshold for cost prompt (what counts as "large")

### Deferred Ideas (OUT OF SCOPE)
- `--enrich-all` flag to bypass cache and force full re-enrichment (useful after model upgrade) -- not in current requirements
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| LLM-01 | `--enrich` flag triggers LLM-generated plain-English summaries for methods and classes | MEAI IChatClient.GetResponseAsync() with system+user prompts; LlmEnricher implements existing IEnricher interface; summaries stored in EnrichedResult dictionary keyed by MethodId/TypeId |
| LLM-02 | LLM provider is configurable via JSON config file (Anthropic, OpenAI, Ollama) | `code2obsidian.llm.json` with provider/model/apiKey fields; factory creates IChatClient via Anthropic.AsIChatClient(), OpenAI ChatClient.AsIChatClient(), OllamaApiClient constructor; all return same IChatClient |
| LLM-03 | LLM summaries are cached by source content hash to avoid redundant API calls | SQLite `summaries` table with entity_id + content_hash + summary_text; schema migration V1->V2; content hash = SHA256(signature + body + callees + callers) |
| LLM-04 | Tool works fully without any LLM configured (summaries are additive, not required) | Pipeline already passes empty enrichers list; emitter renders `## Summary` section only when EnrichedResult contains a summary for that entity; no enricher = no summary section |
| LLM-05 | Cost estimation displayed before LLM enrichment begins on large codebases | Pre-scan counts uncached entities, estimates tokens via chars/4 heuristic, multiplies by per-token pricing from config; Spectre.Console prompt for confirmation above threshold |
</phase_requirements>

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.Extensions.AI | 10.3.0 | IChatClient abstraction, middleware pipeline | Official .NET AI abstraction layer; GA since May 2025; required by Anthropic SDK 12.x |
| Microsoft.Extensions.AI.Abstractions | 10.3.0 | IChatClient/ChatMessage/ChatResponse types | Transitive via MEAI; defines the provider contract |
| Anthropic | 12.8.0 | Anthropic Claude provider | Official SDK; implements IChatClient via `.AsIChatClient("model")` |
| Microsoft.Extensions.AI.OpenAI | 10.3.0 | OpenAI provider adapter | Wraps OpenAI SDK; `.AsIChatClient()` extension |
| OllamaSharp | 5.4.16 | Ollama local LLM provider | MS-recommended; implements IChatClient natively |
| Microsoft.Data.Sqlite | 8.0.11 | Summary cache storage | Already in project; extend existing schema |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Text.Json | (framework) | Config file parsing | Parse `code2obsidian.llm.json` |
| Spectre.Console | 0.54.0 (existing) | Interactive setup, cost confirmation prompts, token progress | Already in project; use for all UX |
| Microsoft.ML.Tokenizers | latest | Accurate token counting | OPTIONAL: only if character heuristic proves too inaccurate; adds complexity |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Character heuristic for token estimation | Microsoft.ML.Tokenizers (TiktokenTokenizer) | Accurate but adds ~1MB dependency and model-specific tokenizer files; heuristic is +-20% which is fine for cost estimates |
| SemaphoreSlim for concurrency | System.Threading.RateLimiting | More features but heavier; SemaphoreSlim is sufficient for simple concurrent-request limiting |
| Custom JSON config | Microsoft.Extensions.Configuration | Overkill for a single config file; System.Text.Json deserialization is simpler |

**Installation:**
```bash
dotnet add package Microsoft.Extensions.AI --version 10.3.0
dotnet add package Anthropic --version 12.8.0
dotnet add package Microsoft.Extensions.AI.OpenAI --version 10.3.0
dotnet add package OllamaSharp --version 5.4.16
```

**Version note:** MEAI 10.x is required because Anthropic 12.8.0 depends on `Microsoft.Extensions.AI.Abstractions >= 10.2.0`. Both MEAI 10.x and Anthropic 12.x target .NET 8, so they are compatible with the project's `net8.0` target framework. The 10.x versions of `System.Text.Json` (transitive dependency) also target .NET 8.

## Architecture Patterns

### Recommended Project Structure
```
Enrichment/
  IEnricher.cs                    # (existing) Pipeline interface
  EnrichedResult.cs               # (existing, extended) Add summary dictionaries
  LlmEnricher.cs                  # Core enricher: iterates entities, checks cache, calls LLM
  SummaryCache.cs                 # SQLite read/write for summary table
  Config/
    LlmConfig.cs                  # Strongly-typed config model
    LlmConfigLoader.cs            # JSON parsing with env var expansion
    ChatClientFactory.cs          # Creates IChatClient from config
    InteractiveSetup.cs           # First-run provider setup wizard
  Prompts/
    PromptBuilder.cs              # Builds system + user prompts for methods/classes
  CostEstimator.cs                # Token estimation and cost calculation
```

### Pattern 1: Provider Factory via Config
**What:** A factory that reads `code2obsidian.llm.json` and creates the appropriate `IChatClient` without hardcoding providers.
**When to use:** At pipeline startup, when `--enrich` flag is present.
**Example:**
```csharp
// Source: MEAI official docs + Anthropic/OpenAI/Ollama NuGet READMEs
public static IChatClient CreateFromConfig(LlmConfig config)
{
    // Resolve API key (supports "$ENV_VAR" syntax)
    var apiKey = ResolveApiKey(config.ApiKey);

    return config.Provider.ToLowerInvariant() switch
    {
        "anthropic" => new AnthropicClient(new() { ApiKey = apiKey })
            .AsIChatClient(config.Model),

        "openai" => new OpenAIClient(apiKey)
            .GetChatClient(config.Model)
            .AsIChatClient(),

        "ollama" => new OllamaApiClient(
            new Uri(config.Endpoint ?? "http://localhost:11434"),
            config.Model),

        _ => throw new InvalidOperationException(
            $"Unknown provider '{config.Provider}'. Supported: anthropic, openai, ollama")
    };
}
```

### Pattern 2: Content Hash for Cache Invalidation
**What:** SHA256 hash covering method signature, body source, callees, and callers. Any structural change triggers re-enrichment.
**When to use:** Before each LLM call to check the cache.
**Example:**
```csharp
// Content hash for a method summary
public static string ComputeMethodHash(MethodInfo method, CallGraph callGraph)
{
    using var sha = SHA256.Create();
    var sb = new StringBuilder();
    sb.AppendLine(method.DisplaySignature);
    sb.AppendLine(method.DocComment ?? "");
    // Sorted callees for deterministic hash
    foreach (var callee in callGraph.GetCallees(method.Id).OrderBy(c => c.Value))
        sb.AppendLine($"calls:{callee.Value}");
    foreach (var caller in callGraph.GetCallers(method.Id).OrderBy(c => c.Value))
        sb.AppendLine($"calledby:{caller.Value}");
    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
    return Convert.ToHexString(hash);
}
```

### Pattern 3: Enricher with SemaphoreSlim Concurrency
**What:** LlmEnricher processes methods/types in parallel with bounded concurrency to avoid rate limiting.
**When to use:** During the enrichment pipeline stage.
**Example:**
```csharp
// Source: .NET SemaphoreSlim pattern for async I/O throttling
private readonly SemaphoreSlim _throttle = new(maxConcurrency: 3);

private async Task<string> GetSummaryAsync(
    IChatClient client, string prompt, CancellationToken ct)
{
    await _throttle.WaitAsync(ct);
    try
    {
        var response = await client.GetResponseAsync(
            [
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, prompt)
            ],
            new ChatOptions { MaxOutputTokens = 300 },
            ct);

        // Track token usage
        var usage = response.Usage;
        if (usage is not null)
        {
            Interlocked.Add(ref _inputTokens, usage.InputTokenCount ?? 0);
            Interlocked.Add(ref _outputTokens, usage.OutputTokenCount ?? 0);
        }

        return response.Text ?? "";
    }
    finally
    {
        _throttle.Release();
    }
}
```

### Pattern 4: EnrichedResult Extension
**What:** Extend existing `EnrichedResult` to carry summary dictionaries.
**When to use:** Between enrichment and emission stages.
**Example:**
```csharp
public sealed class EnrichedResult
{
    public AnalysisResult Analysis { get; }

    // NEW: LLM summaries keyed by entity ID
    public Dictionary<string, string> MethodSummaries { get; } = new();
    public Dictionary<string, string> TypeSummaries { get; } = new();

    public EnrichedResult(AnalysisResult analysis) => Analysis = analysis;
}
```

### Pattern 5: Emitter Summary Section Injection
**What:** ObsidianEmitter checks `EnrichedResult` for summaries and injects a `## Summary` section.
**When to use:** During note rendering.
**Example:**
```csharp
// In RenderClassNote, after purpose summary:
if (enrichedResult.TypeSummaries.TryGetValue(typeInfo.Id.Value, out var summary))
{
    sb.AppendLine("## Summary");
    sb.AppendLine(summary);
    sb.AppendLine();
}
```

### Anti-Patterns to Avoid
- **Hardcoded provider list in enricher:** The enricher should only know about `IChatClient`. Provider-specific logic lives entirely in `ChatClientFactory`.
- **Storing raw LLM responses without the content hash:** Without the hash, there's no way to know if the cached summary is stale. Always store hash alongside summary.
- **Calling LLM for every entity without checking cache first:** This is the #1 performance/cost pitfall. Cache lookup MUST precede every LLM call.
- **Blocking the pipeline on LLM failures:** A single API error should not abort the entire run. Log warning, skip that entity, continue with others.
- **Making the emitter depend on IChatClient:** The emitter reads from `EnrichedResult` only. It never calls the LLM directly.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Chat completion protocol | HTTP client for LLM APIs | MEAI `IChatClient.GetResponseAsync()` | Handles auth, retries, serialization, streaming; provider-agnostic |
| Provider switching | If/else chain per provider | MEAI `AsIChatClient()` pattern | Each provider SDK exposes the same interface |
| Token counting (exact) | Custom BPE tokenizer | `Microsoft.ML.Tokenizers` or char/4 heuristic | Tokenization is model-specific and complex |
| JSON config parsing | Manual string parsing | `System.Text.Json` `JsonSerializer.Deserialize<T>` | Handles escaping, nullability, validation |
| SQLite schema migration | Ad-hoc ALTER TABLE | Extend existing `StateSchema.EnsureSchema()` with `PRAGMA user_version` check | Already established pattern in Phase 4 |
| Interactive prompts | Raw `Console.ReadLine()` | `Spectre.Console` `SelectionPrompt`/`TextPrompt` | Already in the project; handles arrow keys, validation |
| Concurrent request limiting | Manual Task.WhenAll batching | `SemaphoreSlim` | Standard .NET pattern for bounding async concurrency |

**Key insight:** The MEAI abstraction eliminates the need for any provider-specific code in the enrichment pipeline. The `ChatClientFactory` is the ONLY place that knows about Anthropic/OpenAI/Ollama. Everything else works through `IChatClient`.

## Common Pitfalls

### Pitfall 1: MEAI Version Mismatch
**What goes wrong:** Anthropic 12.8.0 requires `Microsoft.Extensions.AI.Abstractions >= 10.2.0`. Using MEAI 9.x with Anthropic 12.x causes assembly load failures.
**Why it happens:** MEAI went from preview to GA with breaking changes between 9.x and 10.x. Provider packages pin to specific MEAI major versions.
**How to avoid:** Pin all MEAI packages to 10.3.0. Pin Anthropic to 12.8.0. Run `dotnet restore` and verify no version conflicts.
**Warning signs:** `TypeLoadException` or `MissingMethodException` at runtime mentioning `IChatClient`.

### Pitfall 2: System.Text.Json Version Conflict
**What goes wrong:** Anthropic 12.8.0 and MEAI 10.3.0 both depend on `System.Text.Json >= 10.0.2`. On a .NET 8 project, this pulls in a newer System.Text.Json than the framework provides.
**Why it happens:** .NET 8 ships System.Text.Json 8.x; the NuGet packages need 10.x features.
**How to avoid:** NuGet resolves this automatically by downloading the higher version. This is safe -- System.Text.Json 10.x is backward compatible with .NET 8 runtime. Just ensure no explicit `<PackageReference>` pins it to 8.x.
**Warning signs:** Compile errors about missing `JsonSerializer` overloads.

### Pitfall 3: Over-Invalidating the Cache
**What goes wrong:** Including too much in the content hash (e.g., formatting changes, comment whitespace) causes cache misses on trivial changes, wasting API calls.
**Why it happens:** Temptation to hash "everything" for correctness.
**How to avoid:** Hash only structural content: display signature, body complexity/length indicator, sorted callee/caller IDs. NOT raw source text (whitespace-sensitive).
**Warning signs:** Running `--enrich` after reformatting code triggers full re-enrichment.

### Pitfall 4: Rate Limiting / 429 Errors
**What goes wrong:** Sending too many concurrent requests to the LLM API triggers rate limiting, causing failures or extreme slowdowns.
**Why it happens:** No concurrency control on LLM calls, especially with hundreds of methods.
**How to avoid:** Use `SemaphoreSlim` to limit concurrent calls (3-5 max). Implement exponential backoff on 429 responses.
**Warning signs:** `AnthropicRateLimitException`, HTTP 429 responses, or OpenAI `TooManyRequestsError`.

### Pitfall 5: Blocking Pipeline on LLM Failure
**What goes wrong:** A single LLM API error (network timeout, invalid response) crashes the entire enrichment run, losing all progress.
**Why it happens:** Not catching exceptions per-entity.
**How to avoid:** Wrap each LLM call in try/catch. On failure: log warning, skip entity, continue. Report skipped count in summary.
**Warning signs:** Enrichment run aborts after processing 50 of 200 methods due to a transient API error.

### Pitfall 6: Interactive Prompts in CI/Non-Interactive Environments
**What goes wrong:** Interactive setup wizard blocks indefinitely when running in CI.
**Why it happens:** `Spectre.Console` prompts wait for user input that never comes.
**How to avoid:** Detect non-interactive terminal (`!Console.IsInputRedirected` or `Environment.UserInteractive`). In non-interactive mode: if config is missing, emit clear error message and exit with non-zero code. Never prompt.
**Warning signs:** CI pipeline hangs forever at "Select your LLM provider" prompt.

### Pitfall 7: Emitter Coupling to Enrichment
**What goes wrong:** Emitter tries to render `## Summary` section even when enrichment was not requested, producing empty sections or null reference errors.
**Why it happens:** Not checking whether summaries exist before rendering.
**How to avoid:** Emitter conditionally renders `## Summary` only when `EnrichedResult.MethodSummaries` contains the entity's key. When `--enrich` is not used, the dictionaries are empty, and no summary section appears.
**Warning signs:** Notes contain empty `## Summary` headers with no content.

## Code Examples

### IChatClient Usage (MEAI Official Pattern)
```csharp
// Source: https://learn.microsoft.com/en-us/dotnet/ai/ichatclient
using Microsoft.Extensions.AI;

IChatClient client = /* from factory */;

// Simple completion
ChatResponse response = await client.GetResponseAsync(
    [
        new(ChatRole.System, "You are a code documentation assistant."),
        new(ChatRole.User, "Summarize this method: public void Foo() { ... }")
    ],
    new ChatOptions { MaxOutputTokens = 200, Temperature = 0.3f },
    cancellationToken);

string summary = response.Text ?? "";

// Token usage from response
UsageDetails? usage = response.Usage;
int inputTokens = usage?.InputTokenCount ?? 0;
int outputTokens = usage?.OutputTokenCount ?? 0;
```

### Anthropic Provider Setup
```csharp
// Source: https://github.com/anthropics/anthropic-sdk-csharp
using Anthropic;
using Microsoft.Extensions.AI;

// Environment variable auto-detected
IChatClient client = new AnthropicClient()
    .AsIChatClient("claude-haiku-4-5");

// Or explicit API key
IChatClient client = new AnthropicClient(new() { ApiKey = "sk-..." })
    .AsIChatClient("claude-haiku-4-5");
```

### OpenAI Provider Setup
```csharp
// Source: https://learn.microsoft.com/en-us/dotnet/ai/ichatclient
using Microsoft.Extensions.AI;
using OpenAI;

IChatClient client = new OpenAIClient("sk-...")
    .GetChatClient("gpt-4o-mini")
    .AsIChatClient();
```

### Ollama Provider Setup
```csharp
// Source: https://github.com/awaescher/OllamaSharp
using OllamaSharp;

// OllamaApiClient directly implements IChatClient
IChatClient client = new OllamaApiClient(
    new Uri("http://localhost:11434"),
    "llama3.1");
```

### JSON Config File Format
```json
{
  "provider": "anthropic",
  "model": "claude-haiku-4-5",
  "apiKey": "$ANTHROPIC_API_KEY",
  "endpoint": null,
  "maxConcurrency": 3,
  "maxOutputTokens": 300,
  "costPerInputToken": 0.00000025,
  "costPerOutputToken": 0.00000125
}
```

### SQLite Schema Migration (V1 -> V2)
```csharp
// Extend existing StateSchema.EnsureSchema()
if (version < 2)
{
    MigrateToV2(connection);
}

private static void MigrateToV2(SqliteConnection connection)
{
    using var transaction = connection.BeginTransaction();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS summaries (
            entity_id TEXT PRIMARY KEY,
            content_hash TEXT NOT NULL,
            summary_text TEXT NOT NULL,
            model_id TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_summaries_hash
            ON summaries(content_hash);

        PRAGMA user_version = 2;
        """;
    cmd.ExecuteNonQuery();
    transaction.Commit();
}
```

### Token Estimation (Character Heuristic)
```csharp
// ~1 token per 4 characters (English text / code)
// Source: widely documented heuristic across OpenAI/Anthropic docs
public static int EstimateTokens(string text)
    => (int)Math.Ceiling(text.Length / 4.0);

public static int EstimateMethodPromptTokens(MethodInfo method, string systemPrompt)
{
    var userPrompt = BuildMethodPrompt(method); // signature + context
    return EstimateTokens(systemPrompt) + EstimateTokens(userPrompt);
}
```

### Cost Estimation and Confirmation
```csharp
// Pre-enrichment cost check
int uncachedCount = entities.Count(e => !cache.HasValidEntry(e.Id, e.ContentHash));
int estimatedInputTokens = uncachedCount * avgTokensPerPrompt;
int estimatedOutputTokens = uncachedCount * config.MaxOutputTokens;
decimal estimatedCost =
    estimatedInputTokens * config.CostPerInputToken +
    estimatedOutputTokens * config.CostPerOutputToken;

if (uncachedCount > threshold)
{
    AnsiConsole.MarkupLine(
        $"[yellow]Enrichment will process {uncachedCount} entities[/]");
    AnsiConsole.MarkupLine(
        $"[yellow]Estimated: ~{estimatedInputTokens + estimatedOutputTokens:N0} tokens, ~${estimatedCost:F4}[/]");

    if (!AnsiConsole.Confirm("Proceed?"))
        return; // skip enrichment, continue with structural vault
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `IChatClient.CompleteAsync()` | `IChatClient.GetResponseAsync()` | MEAI GA (May 2025) | Method renamed; old preview code won't compile |
| `ChatCompletion` return type | `ChatResponse` return type | MEAI GA (May 2025) | All response handling code uses `ChatResponse` |
| `Microsoft.Extensions.AI.Ollama` package | `OllamaSharp` package | Mid-2025 | MS deprecated their Ollama adapter; OllamaSharp is recommended |
| Community Anthropic.SDK | Official `Anthropic` package (v10+) | 2025 | Official SDK from Anthropic; implements IChatClient natively |
| SharpToken/TiktokenSharp for tokenization | Microsoft.ML.Tokenizers | 2025 | Official MS tokenizer; migration recommended |
| Separate MEAI preview packages per provider | Unified GA packages | May 2025 | Stable API surface; no more preview churn |

**Deprecated/outdated:**
- `Microsoft.Extensions.AI.Ollama`: Deprecated in favor of OllamaSharp
- `ChatCompletion`/`CompleteAsync`: Renamed to `ChatResponse`/`GetResponseAsync` in GA
- `Anthropic.SDK` (tryAGI): Community package superseded by official `Anthropic` package from Anthropic PBC

## Open Questions

1. **Body text for content hash -- should we hash raw source or a normalized form?**
   - What we know: The current MethodInfo does not store method body source text, only `DisplaySignature` and `DocComment`. The call graph (callees/callers) captures behavioral dependencies.
   - What's unclear: Whether signature + callees + callers is sufficient for meaningful cache invalidation, or if we need to add body source text to MethodInfo during analysis.
   - Recommendation: Hash `DisplaySignature + DocComment + sorted callees + sorted callers + CyclomaticComplexity`. If a method's signature, doc comment, complexity, or call relationships change, it should be re-summarized. Body text extraction can be added later if the hash proves too coarse. This avoids modifying the analysis phase.

2. **Prompt design -- single prompt per entity or batched?**
   - What we know: MEAI's IChatClient handles one conversation at a time. Batching multiple methods in one prompt risks exceeding context windows and produces less focused summaries.
   - What's unclear: Whether Claude/GPT can produce good summaries from just a signature + doc comment + call context (without the actual method body).
   - Recommendation: One prompt per entity. Include signature, doc comment, callee names, caller names, and complexity score. For classes, include member list and inheritance chain. If summaries prove too shallow, add body extraction in a follow-up.

3. **Method body source text -- available or needed?**
   - What we know: Current `MethodInfo` has `DisplaySignature` (e.g., `public async Task<int> RunAsync(...)`) and `DocComment`, but NOT the full method body source.
   - What's unclear: Whether this is enough context for useful LLM summaries.
   - Recommendation: Start without body text. The signature + doc comment + call graph context provides reasonable signal. If summaries are too generic, adding body extraction from Roslyn `SyntaxNode.GetText()` during analysis is straightforward but increases prompt tokens significantly.

## Sources

### Primary (HIGH confidence)
- [Microsoft.Extensions.AI official docs](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) - Package architecture, IChatClient interface, GA status
- [IChatClient interface docs](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient) - GetResponseAsync, ChatMessage, ChatOptions, ChatResponse, middleware patterns, DI registration
- [Anthropic C# SDK GitHub](https://github.com/anthropics/anthropic-sdk-csharp) - AsIChatClient usage, AnthropicClient configuration, error types, MEAI integration
- [NuGet: Microsoft.Extensions.AI 10.3.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/) - Version, .NET 8 target, dependencies
- [NuGet: Microsoft.Extensions.AI.Abstractions 10.3.0](https://www.nuget.org/packages/Microsoft.Extensions.AI.Abstractions/) - Version, .NET 8 target
- [NuGet: Anthropic 12.8.0](https://www.nuget.org/packages/Anthropic/) - Version, MEAI >= 10.2.0 dependency, .NET 8 target
- [NuGet: Microsoft.Extensions.AI.OpenAI 10.3.0](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/) - Version, dependencies
- [NuGet: OllamaSharp 5.4.16](https://www.nuget.org/packages/OllamaSharp/) - Version, IChatClient implementation, .NET 8 target
- [Microsoft.ML.Tokenizers docs](https://learn.microsoft.com/en-us/dotnet/ai/how-to/use-tokenizers) - Token counting API, TiktokenTokenizer

### Secondary (MEDIUM confidence)
- [Rick Strahl: Configuring MEAI with multiple providers](https://weblog.west-wind.com/posts/2025/May/30/Configuring-MicrosoftAIExtension-with-multiple-providers) - Provider factory patterns, API key management
- [Mark Heath: Tracking Token Usage](https://markheath.net/post/tracking-token-usage-microsoft-extensions-ai) - UsageDetails class, InputTokenCount/OutputTokenCount properties
- [MEAI GA announcement](https://devblogs.microsoft.com/dotnet/ai-vector-data-dotnet-extensions-ga/) - GA date confirmation (May 2025)
- [David Puplava: Migrate MEAI.Ollama to OllamaSharp](https://www.davidpuplava.com/migrate-microsoft_extensions_ai_ollama-to-ollamasharp) - OllamaSharp migration context

### Tertiary (LOW confidence)
- Token heuristic (~4 chars per token) - widely cited but accuracy varies by model and language; adequate for cost estimation, not for exact context window management

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All packages verified on NuGet with .NET 8 targets; version compatibility confirmed (Anthropic 12.8.0 -> MEAI 10.2.0+)
- Architecture: HIGH - Codebase examined thoroughly; IEnricher/EnrichedResult/Pipeline stubs already exist; SQLite schema migration pattern established
- Pitfalls: HIGH - Version conflicts, rate limiting, and CI concerns are well-documented in MEAI ecosystem
- Token estimation: MEDIUM - Character heuristic is approximate; adequate for cost display but not exact
- Prompt effectiveness: MEDIUM - Without method body source text, summaries may be shallow; mitigated by doc comments and call context

**Research date:** 2026-02-26
**Valid until:** 2026-03-28 (stable -- MEAI is GA, provider packages are mature)
