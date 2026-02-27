# Architecture Patterns

**Domain:** Roslyn-based C# code analysis CLI with LLM enrichment
**Researched:** 2026-02-25

## Recommended Architecture

Transform the monolithic `Program.cs` into a **pipeline-of-analyzers** architecture with dependency injection, where a thin CLI shell orchestrates a configurable sequence of analysis passes that feed into composable emitters.

The design principle: **each component does one thing, data flows in one direction, and LLM enrichment is a decorator on top of static analysis -- never a prerequisite.**

### Architecture Diagram

```
CLI Shell (argument parsing, DI container setup)
    |
    v
SolutionLoader (MSBuild registration, workspace, compilation)
    |
    v
AnalysisContext (solution, compilations, project assemblies -- shared read-only state)
    |
    +---> IAnalyzer[] (registered analysis passes, run sequentially)
    |       |-- MethodAnalyzer          (extract methods, signatures, docstrings)
    |       |-- CallGraphAnalyzer       (calls-out, called-by edges)
    |       |-- ClassAnalyzer           (classes, inheritance, interfaces)
    |       |-- PatternDetector         (repository, actor, controller, middleware)
    |       |-- DangerAnalyzer          (high fan-in, hot paths, complexity)
    |       |-- MetricsAnalyzer         (cyclomatic complexity, parameter counts)
    |       v
    AnalysisResult (unified model: methods, classes, graphs, patterns, metrics)
    |
    +---> IEnricher[] (optional enrichment passes)
    |       |-- LlmEnricher            (summaries via IChatClient)
    |       v
    EnrichedResult (AnalysisResult + LLM summaries, enhanced descriptions)
    |
    +---> IEmitter (output generation)
    |       |-- ObsidianEmitter        (markdown with frontmatter, wikilinks, tags)
    |       v
    Output files on disk
    |
    +---> StateTracker (content hashes, last-run metadata for incremental mode)
```

### Component Boundaries

| Component | Responsibility | Communicates With | Owns |
|-----------|---------------|-------------------|------|
| **CLI Shell** | Parse args, build DI container, orchestrate pipeline | SolutionLoader, Pipeline, StateTracker | `Options` record, exit codes |
| **SolutionLoader** | MSBuild registration, open solution, compile projects | Roslyn APIs | `AnalysisContext` creation |
| **AnalysisContext** | Immutable container of compiled solution state | Read by all analyzers | Solution, Compilations, ProjectAssemblies |
| **IAnalyzer pipeline** | Extract specific facts from code | AnalysisContext (read), AnalysisResult (write) | Individual analysis data |
| **AnalysisResult** | Unified model of all discovered facts | Built by analyzers, read by enrichers/emitters | Methods, Classes, Graphs, Patterns, Metrics |
| **IEnricher pipeline** | Add LLM-generated content to analysis results | AnalysisResult (read), IChatClient, EnrichedResult (write) | LLM summaries, enhanced descriptions |
| **IEmitter** | Generate output files from enriched results | EnrichedResult (read), filesystem (write) | Markdown formatting, file layout |
| **StateTracker** | Track content hashes for incremental mode | Filesystem (state file), git (diff detection) | `.code2obsidian-state.json` |
| **Configuration** | Load and merge config from file + CLI overrides | Read by CLI Shell, LLM providers | `code2obsidian.json` settings |

### Data Flow

```
[.sln file on disk]
        |
        | SolutionLoader.LoadAsync()
        v
[AnalysisContext]  <-- immutable, shared by all analyzers
        |
        | foreach analyzer in IAnalyzer[]
        |   analyzer.AnalyzeAsync(context, resultBuilder)
        v
[AnalysisResult]   <-- complete static analysis model
        |
        | if --enrich: foreach enricher in IEnricher[]
        |   enricher.EnrichAsync(result, enrichedBuilder)
        v
[EnrichedResult]   <-- analysis + LLM content (or just analysis if no --enrich)
        |
        | emitter.EmitAsync(enrichedResult, outputDir)
        v
[Markdown files on disk]
        |
        | stateTracker.SaveState(hashes)
        v
[.code2obsidian-state.json]
```

**Key data flow rules:**

1. Data flows strictly left-to-right (load -> analyze -> enrich -> emit -> save state)
2. AnalysisContext is immutable after creation -- analyzers never modify it
3. Analyzers write to a shared AnalysisResultBuilder, but each analyzer owns its own section (no cross-analyzer mutation)
4. Enrichers produce a new EnrichedResult wrapping AnalysisResult -- they do not mutate the original
5. The emitter is the only component that writes to disk (besides StateTracker)
6. StateTracker is invoked at both ends: at start (to determine what changed) and at finish (to persist new state)

## Core Data Models

### AnalysisContext (immutable, shared)

```csharp
public sealed class AnalysisContext
{
    public Solution Solution { get; }
    public IReadOnlyDictionary<ProjectId, Compilation> Compilations { get; }
    public IReadOnlySet<IAssemblySymbol> ProjectAssemblies { get; }
    public IReadOnlySet<string> ChangedFilePaths { get; }  // empty = full run
}
```

### AnalysisResult (built incrementally by analyzers)

```csharp
public sealed class AnalysisResult
{
    public IReadOnlyDictionary<MethodId, MethodInfo> Methods { get; }
    public IReadOnlyDictionary<TypeId, ClassInfo> Classes { get; }
    public CallGraph CallGraph { get; }
    public IReadOnlyList<PatternMatch> Patterns { get; }
    public IReadOnlyDictionary<MethodId, DangerInfo> Dangers { get; }
    public IReadOnlyDictionary<MethodId, MetricsInfo> Metrics { get; }
}
```

### EnrichedResult (analysis + LLM content)

```csharp
public sealed class EnrichedResult
{
    public AnalysisResult Analysis { get; }
    public IReadOnlyDictionary<MethodId, string> MethodSummaries { get; }
    public IReadOnlyDictionary<TypeId, string> ClassSummaries { get; }
}
```

### Key design choice: stable IDs, not Roslyn symbols

Analyzers should produce results keyed by **stable string IDs** (e.g., `"Namespace.ClassName.MethodName(ParamType1,ParamType2)"`) rather than `IMethodSymbol` references. Reasons:

- Roslyn symbols are not serializable -- you cannot cache or persist them
- State tracking for incremental mode needs stable keys across runs
- The emitter and enricher layers should not depend on Roslyn at all
- String IDs enable future cross-run comparison and diffing

The `MethodId` and `TypeId` types should be strongly-typed wrappers around these stable strings, generated during analysis from `ISymbol.ToDisplayString()` with a consistent format.

## Patterns to Follow

### Pattern 1: Analyzer Interface with Shared Context

**What:** Each analysis concern implements `IAnalyzer` and receives the same immutable context. Analyzers are registered in DI and executed in sequence.

**When:** Always. This is the primary extension point for adding new analysis capabilities.

**Example:**

```csharp
public interface IAnalyzer
{
    string Name { get; }
    Task AnalyzeAsync(
        AnalysisContext context,
        AnalysisResultBuilder builder,
        CancellationToken ct);
}

public sealed class MethodAnalyzer : IAnalyzer
{
    public string Name => "Methods";

    public async Task AnalyzeAsync(
        AnalysisContext context,
        AnalysisResultBuilder builder,
        CancellationToken ct)
    {
        foreach (var (projectId, compilation) in context.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(ct);
                // ... extract methods, populate builder.Methods
            }
        }
    }
}
```

**Confidence:** HIGH -- this is standard .NET extensibility via interfaces + DI, well-supported by Microsoft.Extensions.DependencyInjection.

### Pattern 2: Microsoft.Extensions.AI for LLM Abstraction

**What:** Use the `IChatClient` interface from `Microsoft.Extensions.AI.Abstractions` to abstract over LLM providers. Register the concrete provider in DI based on configuration.

**When:** For all LLM enrichment. The enricher layer depends only on `IChatClient`, never on provider-specific types.

**Example:**

```csharp
// In DI setup, based on config:
services.AddChatClient(config.Provider switch
{
    "openai" => new OpenAIClient(config.ApiKey)
        .AsChatClient(config.Model),
    "ollama" => new OllamaChatClient(
        new Uri(config.Endpoint), config.Model),
    "anthropic" => new AnthropicClient(config.ApiKey)
        .AsChatClient(config.Model),
    _ => throw new InvalidOperationException(
        $"Unknown LLM provider: {config.Provider}")
});

// In LlmEnricher -- provider-agnostic:
public sealed class LlmEnricher : IEnricher
{
    private readonly IChatClient _chat;

    public LlmEnricher(IChatClient chat) => _chat = chat;

    public async Task EnrichAsync(
        AnalysisResult result,
        EnrichedResultBuilder builder,
        CancellationToken ct)
    {
        foreach (var (id, method) in result.Methods)
        {
            var prompt = BuildPrompt(method);
            var response = await _chat.GetResponseAsync(prompt, ct);
            builder.AddMethodSummary(id, response.Text);
        }
    }
}
```

**Confidence:** HIGH -- `Microsoft.Extensions.AI` reached stable release in 2025. Official Anthropic SDK, OpenAI SDK, and OllamaSharp all implement `IChatClient`. This is the Microsoft-endorsed approach.

**Sources:**
- [Microsoft.Extensions.AI docs](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) (updated 2026-01-24)
- [Anthropic.SDK NuGet](https://www.nuget.org/packages/Anthropic.SDK) -- implements IChatClient
- [OllamaSharp](https://github.com/awaescher/OllamaSharp) -- first IChatClient implementation

### Pattern 3: DI-Based Pipeline Composition

**What:** Use `Microsoft.Extensions.DependencyInjection` to register analyzers, enrichers, and emitters. The pipeline orchestrator resolves them from the container and runs them in order.

**When:** Always. This enables testability (mock any component), configurability (swap implementations), and clean separation.

**Example:**

```csharp
// Registration
services.AddSingleton<IAnalyzer, MethodAnalyzer>();
services.AddSingleton<IAnalyzer, CallGraphAnalyzer>();
services.AddSingleton<IAnalyzer, ClassAnalyzer>();
services.AddSingleton<IAnalyzer, PatternDetector>();
services.AddSingleton<IAnalyzer, DangerAnalyzer>();

if (options.Enrich)
{
    services.AddSingleton<IEnricher, LlmEnricher>();
}

services.AddSingleton<IEmitter, ObsidianEmitter>();

// Orchestration
public sealed class Pipeline
{
    private readonly IEnumerable<IAnalyzer> _analyzers;
    private readonly IEnumerable<IEnricher> _enrichers;
    private readonly IEmitter _emitter;

    public async Task<int> RunAsync(
        AnalysisContext context, CancellationToken ct)
    {
        var builder = new AnalysisResultBuilder();
        foreach (var analyzer in _analyzers)
        {
            await analyzer.AnalyzeAsync(context, builder, ct);
        }

        var result = builder.Build();
        var enrichedBuilder = new EnrichedResultBuilder(result);

        foreach (var enricher in _enrichers)
        {
            await enricher.EnrichAsync(result, enrichedBuilder, ct);
        }

        var enriched = enrichedBuilder.Build();
        await _emitter.EmitAsync(enriched, ct);
        return 0;
    }
}
```

**Confidence:** HIGH -- standard .NET pattern, used extensively in ASP.NET Core and .NET CLI tools.

### Pattern 4: Incremental Processing via Content Hashing

**What:** Before analysis, compute SHA-256 hashes of source files. Compare against stored hashes from previous run. Only analyze changed files. After successful run, persist new hashes.

**When:** When `--incremental` flag is set. Falls back to full analysis when no state file exists.

**Example:**

```csharp
public sealed class StateTracker
{
    private readonly string _stateFilePath;

    public async Task<IReadOnlySet<string>> GetChangedFilesAsync(
        Solution solution, CancellationToken ct)
    {
        var previousState = await LoadStateAsync(ct);
        if (previousState is null)
            return new HashSet<string>(); // empty = process all

        var changed = new HashSet<string>();
        foreach (var doc in solution.Projects
            .SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null) continue;
            var hash = ComputeHash(doc.FilePath);
            if (!previousState.FileHashes
                .TryGetValue(doc.FilePath, out var prev)
                || prev != hash)
            {
                changed.Add(doc.FilePath);
            }
        }
        return changed;
    }
}
```

**Confidence:** MEDIUM -- the pattern is straightforward, but the interaction between content hashing and Roslyn's compilation model needs care. A changed file may affect symbols in unchanged files (e.g., changing a base class signature). The initial implementation should use content hashing for "which files to re-emit" but still load the full compilation for semantic accuracy.

### Pattern 5: Configuration Layering

**What:** Support a `code2obsidian.json` config file in the target solution directory, with CLI flags as overrides. Use `Microsoft.Extensions.Configuration` for layered config.

**When:** For LLM provider settings (API keys, model names, endpoints) and analysis options (which analyzers to run, tag preferences). CLI flags like `--enrich`, `--incremental`, `--per-file` override file config.

**Confidence:** HIGH -- standard .NET configuration pattern via `Microsoft.Extensions.Configuration.Json`.

## Anti-Patterns to Avoid

### Anti-Pattern 1: Roslyn Symbols as Long-Lived Keys

**What:** Using `IMethodSymbol` or `ITypeSymbol` as dictionary keys beyond the scope of a single analysis run, or attempting to serialize/persist them.

**Why bad:** Roslyn symbols are tied to a specific `Compilation` instance. They cannot be serialized, persisted, or compared across compilations. Using `SymbolEqualityComparer` works within one run but breaks for incremental/cross-run scenarios. The current codebase already does this correctly within a single run, but the architecture must not extend this pattern to state tracking.

**Instead:** Convert symbols to stable string IDs via `ToDisplayString()` as early as possible in the pipeline. All downstream components (enrichers, emitters, state tracker) work with string IDs only.

### Anti-Pattern 2: Enricher Depends on Analysis Order

**What:** Designing enrichers that assume specific analyzers have run, or that reach back into the AnalysisContext to do their own Roslyn queries.

**Why bad:** Creates hidden coupling between enrichment and analysis. Makes it impossible to test enrichers without a full Roslyn compilation. Breaks the clean data flow.

**Instead:** Enrichers receive only the fully-built `AnalysisResult`. If an enricher needs data that no analyzer provides, add a new analyzer -- do not have the enricher do its own Roslyn work.

### Anti-Pattern 3: God Object AnalysisResult

**What:** Putting every piece of data into a single flat dictionary or making AnalysisResult mutable and passed through the entire pipeline.

**Why bad:** Makes it unclear which analyzer owns which data. Makes testing individual analyzers require asserting against a massive object. Leads to accidental coupling between analyzers.

**Instead:** Use the builder pattern. Each analyzer writes to its own section of `AnalysisResultBuilder`. The builder produces an immutable `AnalysisResult` only after all analyzers complete. Each section is a strongly-typed collection (Methods, Classes, CallGraph, Patterns, etc.).

### Anti-Pattern 4: Emitter Knows About Roslyn

**What:** Having the markdown emitter import `Microsoft.CodeAnalysis` types.

**Why bad:** Couples output generation to the analysis framework. Makes it impossible to test emitters without Roslyn. Prevents future output formats (JSON, HTML) from being added cleanly.

**Instead:** The emitter works exclusively with the domain model (`AnalysisResult` / `EnrichedResult`). All Roslyn-to-domain-model conversion happens in the analyzer layer.

### Anti-Pattern 5: LLM Calls in the Hot Path

**What:** Making LLM API calls synchronously within the analysis loop, or making LLM enrichment a prerequisite for emitting output.

**Why bad:** LLM calls are slow (100ms-10s each), unreliable (rate limits, network), and expensive. A solution with 500 methods would need 500 API calls minimum. If enrichment fails mid-run, you lose all work.

**Instead:** Static analysis completes fully first. LLM enrichment is a separate pass that can be interrupted and resumed. Emit output even if enrichment fails (with placeholders). Consider batching and rate limiting within the enricher.

## Project Structure

```
Code2Obsidian/
    Code2Obsidian.csproj
    Program.cs                          # Thin: parse args, build DI, call Pipeline.RunAsync

    Configuration/
        Code2ObsidianOptions.cs         # Strongly-typed options (replaces Options record)
        LlmProviderOptions.cs           # Provider, ApiKey, Model, Endpoint
        ConfigurationLoader.cs          # JSON + CLI override merging

    Loading/
        SolutionLoader.cs               # MSBuild registration + workspace management
        AnalysisContext.cs               # Immutable container of loaded solution state

    Analysis/
        IAnalyzer.cs                    # Interface for analysis passes
        AnalysisResult.cs               # Immutable result model
        AnalysisResultBuilder.cs        # Builder for AnalysisResult
        Models/
            MethodInfo.cs               # Method data (stable ID, signature, docstring, etc.)
            ClassInfo.cs                # Class data (members, inheritance, interfaces)
            CallGraph.cs                # Directed graph with stable string IDs
            PatternMatch.cs             # Detected pattern (type, location, confidence)
            DangerInfo.cs               # Danger annotation (reason, severity)
            MetricsInfo.cs              # Computed metrics (complexity, fan-in/out)
        Analyzers/
            MethodAnalyzer.cs           # Extract methods, signatures, docstrings
            CallGraphAnalyzer.cs        # Build call graph edges
            ClassAnalyzer.cs            # Classes, inheritance chains, interfaces
            PatternDetector.cs          # Repository, actor, controller patterns
            DangerAnalyzer.cs           # High fan-in, hot paths, complexity flags
            MetricsAnalyzer.cs          # Cyclomatic complexity, parameter counts

    Enrichment/
        IEnricher.cs                    # Interface for enrichment passes
        EnrichedResult.cs               # Analysis + LLM content
        EnrichedResultBuilder.cs        # Builder for EnrichedResult
        LlmEnricher.cs                  # IChatClient-based enrichment
        PromptTemplates.cs              # Prompt construction for methods/classes

    Emission/
        IEmitter.cs                     # Interface for output generation
        ObsidianEmitter.cs              # Markdown + frontmatter + wikilinks
        MarkdownRenderers/
            MethodRenderer.cs           # Render method notes
            ClassRenderer.cs            # Render class notes
            IndexRenderer.cs            # Render index/overview pages

    State/
        StateTracker.cs                 # Content hash tracking for incremental mode
        StateFile.cs                    # Serialization model for state JSON

    Pipeline.cs                         # Orchestrates: load -> analyze -> enrich -> emit -> save
```

**Why this structure:**

- **Flat namespace, not deep nesting.** The project is a CLI tool, not a framework. One level of folders is enough. Resist the urge to over-namespace.
- **Analysis/ contains both the interface and implementations.** No need for separate `Contracts` or `Abstractions` projects -- this is a single deployable CLI.
- **Models/ is under Analysis/ because those are the analysis domain models.** The emitter and enricher consume them but do not define them.
- **Enrichment/ is separate from Analysis/ because it has fundamentally different dependencies** (IChatClient vs Roslyn). This boundary is load-bearing.
- **State/ is separate because it crosses run boundaries.** It reads/writes its own file format and does not depend on Roslyn.

## Scalability Considerations

| Concern | At 100 files | At 1K files | At 10K files |
|---------|-------------|-------------|--------------|
| **Solution loading** | Fast (<10s) | Moderate (30-60s) | Slow (2-5min). MSBuildWorkspace loads all projects. Cannot parallelize. |
| **Analysis passes** | Trivial | Moderate. Run analyzers per-project to bound memory. | Memory pressure. Consider processing projects sequentially, releasing compilations after each. |
| **LLM enrichment** | 100 API calls, ~2 min at 1/s | 1K calls, ~17 min. Need batching + progress. | 10K calls = hours. Must support resume. Likely needs selective enrichment (only public API, only changed). |
| **Markdown emission** | Trivial | Fast. May hit filesystem limits in flat dir. | Thousands of files in one dir. Obsidian may slow. Consider namespace subdirectories with flatten-for-graph option. |
| **Incremental state** | Trivial JSON file | Small JSON (~100KB) | Larger state file (~1MB). Still manageable. |
| **Memory** | <500MB | 1-2GB for full compilation | 4-8GB. May need to process projects individually and merge results. |

**Key scaling strategy:** Process projects one at a time for very large solutions. Load project, compile, run all analyzers on it, convert symbols to stable IDs, release the compilation. This bounds memory to the largest single project rather than the entire solution.

## Suggested Build Order

Build order follows the dependency graph -- each component only depends on things built before it.

### Phase 1: Foundation (no new features, just structure)

1. **Configuration/Code2ObsidianOptions.cs** -- Replace the Options record with strongly-typed config
2. **Loading/SolutionLoader.cs** -- Extract MSBuild registration and solution loading from Program.cs
3. **Loading/AnalysisContext.cs** -- Immutable container wrapping loaded solution state
4. **Analysis/IAnalyzer.cs + AnalysisResult.cs + AnalysisResultBuilder.cs** -- Core abstractions
5. **Analysis/Models/** -- Domain model types (MethodInfo, ClassInfo, CallGraph with stable string IDs)
6. **Analysis/Analyzers/MethodAnalyzer.cs** -- Port existing method extraction logic
7. **Analysis/Analyzers/CallGraphAnalyzer.cs** -- Port existing call graph logic
8. **Emission/IEmitter.cs + ObsidianEmitter.cs** -- Port existing markdown generation
9. **Pipeline.cs** -- Orchestrate the above
10. **Program.cs** -- Thin shell: parse args, register DI, run pipeline

*Goal: exact same output as current tool, but decomposed into testable components.*

### Phase 2: Extended Static Analysis

11. **Analysis/Analyzers/ClassAnalyzer.cs** -- New analyzer: classes, inheritance, interfaces
12. **Analysis/Analyzers/PatternDetector.cs** -- New analyzer: detect repository, actor, controller patterns
13. **Analysis/Analyzers/DangerAnalyzer.cs** -- New analyzer: high fan-in, hot paths
14. **Analysis/Analyzers/MetricsAnalyzer.cs** -- New analyzer: cyclomatic complexity
15. **Emission/MarkdownRenderers/** -- Class rendering, enhanced method rendering, index pages

*Goal: richer static analysis output, still no LLM dependency.*

### Phase 3: LLM Enrichment

16. **Configuration/LlmProviderOptions.cs** -- Provider config model
17. **Configuration/ConfigurationLoader.cs** -- JSON config file + CLI override merging
18. **Enrichment/IEnricher.cs + EnrichedResult.cs** -- Enrichment abstractions
19. **Enrichment/PromptTemplates.cs** -- Prompt engineering for code summaries
20. **Enrichment/LlmEnricher.cs** -- IChatClient-based enrichment with rate limiting
21. Wire `--enrich` flag through Pipeline

*Goal: opt-in LLM summaries on top of static analysis.*

### Phase 4: Incremental Processing

22. **State/StateFile.cs** -- State serialization model
23. **State/StateTracker.cs** -- Content hashing, change detection, state persistence
24. Wire `--incremental` flag through Pipeline, filter AnalysisContext.ChangedFilePaths

*Goal: only re-process changed files on subsequent runs.*

**Why this order:**

- Phase 1 must come first because every subsequent phase depends on the component boundaries it establishes. You cannot add analyzers to a monolith.
- Phase 2 before Phase 3 because static analysis is the foundation that LLM enrichment decorates. Building LLM enrichment first would create pressure to couple it with analysis.
- Phase 3 before Phase 4 because incremental processing is an optimization. It is harder to add correctly (symbol cross-references across files) and provides less user value than LLM enrichment.
- Within each phase, the order follows the data flow: models before analyzers, analyzers before emitters, abstractions before implementations.

## Sources

- [Roslyn Performance considerations for large solutions](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Performance-considerations-for-large-solutions.md) -- Official Roslyn docs on memory/perf
- [Using MSBuildWorkspace](https://gist.github.com/DustinCampbell/32cd69d04ea1c08a16ae5c4cd21dd3a3) -- Dustin Campbell (Roslyn team) guide on MSBuildWorkspace usage
- [Microsoft.Extensions.AI libraries](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) -- Official docs, updated 2026-01-24
- [Generative AI with LLMs in .NET](https://devblogs.microsoft.com/dotnet/generative-ai-with-large-language-models-in-dotnet-and-csharp/) -- .NET Blog, MEAI overview
- [Pipeline Pattern in C# .NET](https://michaelscodingspot.com/pipeline-pattern-implementations-csharp/) -- Pipeline pattern implementations
- [Pipes and Filters pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/pipes-and-filters) -- Microsoft Architecture Center
- [System.CommandLine beta5 announcement](https://github.com/dotnet/command-line-api/issues/2576) -- Stable release targeting .NET 10 (Nov 2025)
- [Spectre.Console.Cli](https://spectreconsole.net/cli) -- CLI parsing with DI support
- [DI in .NET console apps](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/usage) -- Official Microsoft docs
- [Anthropic.SDK NuGet](https://www.nuget.org/packages/Anthropic.SDK) -- IChatClient implementation for Claude
- [OllamaSharp](https://github.com/awaescher/OllamaSharp) -- IChatClient implementation for Ollama

---

*Architecture research: 2026-02-25*
