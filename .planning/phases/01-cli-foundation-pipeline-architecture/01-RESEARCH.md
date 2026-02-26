# Phase 1: CLI Foundation & Pipeline Architecture - Research

**Researched:** 2026-02-25
**Domain:** .NET CLI refactoring -- System.CommandLine, Spectre.Console progress UX, pipeline architecture
**Confidence:** HIGH

## Summary

Phase 1 transforms a 493-line monolithic `Program.cs` into a clean pipeline architecture with proper CLI parsing and progress reporting. The existing codebase is a single-file .NET 8 console app that hand-rolls argument parsing, has no progress feedback, and mixes solution loading, Roslyn analysis, call graph construction, and markdown emission in one giant `Main` method.

The core libraries are System.CommandLine 2.0.3 (stable, Microsoft-backed) for CLI parsing with auto-generated help, and Spectre.Console 0.54.0 for progress bars. Both are well-documented, production-ready, and confirmed compatible with .NET 8. The pipeline architecture decomposes into three interfaces (IAnalyzer, IEnricher, IEmitter) with a Pipeline orchestrator, using the data models and project structure defined in the domain architecture research.

**Primary recommendation:** Install System.CommandLine 2.0.3 and Spectre.Console 0.54.0, extract the monolith into Loading/, Analysis/, Enrichment/ (stub), and Emission/ folders following the architecture research, wire progress reporting through the pipeline via `IProgress<T>` or direct Spectre.Console context passing, and keep Program.cs as a thin shell that only parses arguments and calls `Pipeline.RunAsync()`.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- System.CommandLine for argument parsing with auto-generated help (per INFR-01)
- Accept both .sln file paths and directory paths as input -- auto-detect which was given
- Default output to a `./vault/` folder adjacent to the input path, overridable with `-o`/`--output`
- Default verbosity is informational: progress bars plus key milestones ("Analyzing ProjectX...", "Emitting 42 notes...")
- Silent overwrite of existing output vault on re-run -- always overwrite regardless of path, no --force required
- Spectre.Console for progress bars (per INFR-02)
- Phased progress bars: separate bars per pipeline stage (Analyzing... -> Enriching... -> Emitting...)
- Detailed end-of-run summary: per-project counts, per-stage timing, total notes generated
- Auto-detect non-interactive terminal (piped output) and suppress progress bars automatically
- File parse failures: skip the file with a warning, continue analyzing the rest of the solution
- Bad input path: error message with suggestion ("Did you mean path/to/similar.sln?")
- Inline warnings during run AND a collected error summary at the end listing all skipped/failed files with reasons
- Granular exit codes: 0 = clean success, 1 = success with warnings (skipped files), 2 = fatal error
- Validate output path is writable before starting analysis -- fail fast on permissions/disk issues
- Roadmap success criterion #4 (backward compatibility) removed -- output format is unconstrained during refactor
- Silent overwrite behavior confirmed for all output paths (default and custom)

### Claude's Discretion
- Pipeline stage granularity and internal architecture (INFR-06)
- Exact error message wording and suggestion algorithm
- Internal logging strategy
- Progress bar visual styling within Spectre.Console
- End-of-run summary formatting

### Deferred Ideas (OUT OF SCOPE)
None -- discussion stayed within phase scope
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| INFR-01 | CLI uses System.CommandLine for argument parsing with auto-generated help | System.CommandLine 2.0.3 stable: RootCommand, Argument, Option, SetAction, auto-generated help/version. Complete API patterns documented below. |
| INFR-02 | Progress bars shown during analysis and enrichment of large solutions (Spectre.Console) | Spectre.Console 0.54.0: AnsiConsole.Progress() with multi-task support, async via StartAsync, auto-detection of non-interactive terminals via Profile.Capabilities.Interactive. Patterns documented below. |
| INFR-06 | Monolithic Program.cs refactored into pipeline architecture (IAnalyzer -> IEnricher -> IEmitter) | Domain architecture research provides complete interface definitions, project structure, data flow patterns, and build order. Phase 1 ports existing logic into this structure. |
</phase_requirements>

## Standard Stack

### Core (Phase 1 additions)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.CommandLine | 2.0.3 | CLI argument parsing, help generation, version flag | Stable release (Dec 2025). Microsoft-backed, heading toward BCL inclusion. Provides typed options, auto-help, shell completions. Replaces the fragile hand-rolled parser at lines 281-344 of current Program.cs. |
| Spectre.Console | 0.54.0 | Progress bars, styled console output, end-of-run tables | De facto standard for rich .NET CLI output. Provides AnsiConsole.Progress() for multi-task progress, auto-detects terminal capabilities, falls back gracefully in non-interactive mode. |

### Existing (no changes in Phase 1)

| Library | Version | Purpose | Status |
|---------|---------|---------|--------|
| Microsoft.Build.Locator | 1.9.1 | MSBuild instance discovery | Keep at 1.9.1 for Phase 1. Domain research recommends upgrade to 1.11.2 but that is a separate concern. |
| Microsoft.CodeAnalysis.CSharp.Workspaces | 4.14.0 | Roslyn analysis | No change |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 4.14.0 | Solution/project loading | No change |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| System.CommandLine 2.0.3 | Spectre.Console.Cli 0.53.1 | Spectre.Console.Cli is being split into separate repo for 1.0 -- version churn risk. System.CommandLine is the Microsoft-blessed approach with NativeAOT support. |
| System.CommandLine 2.0.3 | CommandLineParser 2.9.1 | No active development. System.CommandLine is the modern replacement. |
| Spectre.Console 0.54.0 | Raw Console.WriteLine | No progress feedback, no color, no structured output. Unacceptable UX for multi-project solutions. |

**Installation:**
```bash
dotnet add package System.CommandLine --version 2.0.3
dotnet add package Spectre.Console --version 0.54.0
```

**Updated .csproj (Phase 1 only):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Build.Locator" Version="1.9.1" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.14.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="4.14.0" />
    <PackageReference Include="System.CommandLine" Version="2.0.3" />
    <PackageReference Include="Spectre.Console" Version="0.54.0" />
  </ItemGroup>
</Project>
```

## Architecture Patterns

### Recommended Project Structure (Phase 1)

```
Code2Obsidian/
    Code2Obsidian.csproj
    Program.cs                          # Thin: parse args, call Pipeline.RunAsync()

    Cli/
        CliOptions.cs                   # Strongly-typed record for parsed CLI options

    Loading/
        SolutionLoader.cs               # MSBuild registration + workspace management
        AnalysisContext.cs               # Immutable container of loaded solution state

    Analysis/
        IAnalyzer.cs                    # Interface for analysis passes
        AnalysisResult.cs               # Immutable result model
        AnalysisResultBuilder.cs        # Builder for AnalysisResult
        Models/
            MethodId.cs                 # Strongly-typed wrapper for stable method ID string
            TypeId.cs                   # Strongly-typed wrapper for stable type ID string
            MethodInfo.cs               # Method data (stable ID, signature, docstring, calls)
            CallGraph.cs                # Directed graph with stable string IDs
        Analyzers/
            MethodAnalyzer.cs           # Port existing method extraction logic
            CallGraphAnalyzer.cs        # Port existing call graph logic

    Enrichment/
        IEnricher.cs                    # Interface for enrichment passes (stub for Phase 1)
        EnrichedResult.cs               # Analysis + enrichment content (passthrough for Phase 1)

    Emission/
        IEmitter.cs                     # Interface for output generation
        ObsidianEmitter.cs              # Port existing markdown generation

    Pipeline/
        Pipeline.cs                     # Orchestrates: load -> analyze -> enrich -> emit
        PipelineResult.cs               # Tracks per-stage timing, warning counts, note counts
```

**Key structural decisions for Phase 1:**
- The `Cli/` folder holds only the options record -- System.CommandLine setup stays in Program.cs (it is 30-40 lines, not worth a separate file)
- `Enrichment/` exists with stub interfaces so the pipeline has all three stages even though enrichment is a no-op passthrough in Phase 1
- `Pipeline/PipelineResult.cs` is the data structure that feeds the end-of-run summary
- No DI container in Phase 1 -- manual composition in Program.cs is sufficient since there are only 2 analyzers and 1 emitter. DI can be added in Phase 2 when the analyzer count grows.

### Pattern 1: System.CommandLine Setup with Positional Argument and Options

**What:** Define a RootCommand with a positional `Argument<string>` for the input path, plus options for output and verbosity. Use `SetAction` with async handler.

**When:** Program.cs entry point.

**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/dotnet/standard/commandline/get-started-tutorial
// Adapted for Code2Obsidian

using System.CommandLine;

var inputArgument = new Argument<string>("input")
{
    Description = "Path to a .sln file or directory containing one"
};

var outputOption = new Option<string>("--output", "-o")
{
    Description = "Output directory for the Obsidian vault",
    DefaultValueFactory = _ => null  // null = compute from input path
};

var rootCommand = new RootCommand("Analyze a C# solution and generate an Obsidian vault")
{
    inputArgument,
    outputOption
};

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var input = parseResult.GetValue(inputArgument);
    var output = parseResult.GetValue(outputOption);

    // Resolve input: auto-detect .sln vs directory
    // Resolve output: default to ./vault/ adjacent to input
    // Run pipeline

    var pipeline = new Pipeline(/* ... */);
    return await pipeline.RunAsync(resolvedInput, resolvedOutput, cancellationToken);
});

var result = rootCommand.Parse(args);
return await result.InvokeAsync();
```

**Critical API notes (from official docs, verified 2025-12-05):**
- `RootCommand` auto-generates `--help`/`-h`/`-?` and `--version` flags
- `SetAction` with `(ParseResult, CancellationToken) => Task<int>` is the async overload
- Must use `InvokeAsync()` (not `Invoke()`) when action is async
- `ParseResult.GetValue(option)` retrieves typed values
- Do NOT mix sync and async actions in the same app

### Pattern 2: Input Path Auto-Detection

**What:** Accept both `.sln` file paths and directory paths. If a directory is given, find the `.sln` file inside it.

**When:** After CLI parsing, before pipeline starts.

**Example:**
```csharp
// Source: Custom pattern for Code2Obsidian

static string ResolveSolutionPath(string input)
{
    if (File.Exists(input) && input.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        return Path.GetFullPath(input);

    if (Directory.Exists(input))
    {
        var slnFiles = Directory.GetFiles(input, "*.sln");
        return slnFiles.Length switch
        {
            1 => Path.GetFullPath(slnFiles[0]),
            0 => throw new InvalidOperationException(
                $"No .sln file found in '{input}'. Specify a .sln file directly."),
            _ => throw new InvalidOperationException(
                $"Multiple .sln files found in '{input}': {string.Join(", ", slnFiles.Select(Path.GetFileName))}. Specify one directly.")
        };
    }

    // Input does not exist -- generate suggestion
    throw new FileNotFoundException(BuildSuggestionMessage(input));
}

static string ResolveOutputPath(string? output, string solutionPath)
{
    if (output is not null)
        return Path.GetFullPath(output);

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    return Path.Combine(solutionDir, "vault");
}
```

### Pattern 3: Spectre.Console Progress with Pipeline Stages

**What:** Phased progress bars showing separate progress per pipeline stage. Use `AnsiConsole.Progress().StartAsync()` with multiple tasks.

**When:** During pipeline execution.

**Example:**
```csharp
// Source: https://spectreconsole.net/live/progress (verified 2026-02-25)

await AnsiConsole.Progress()
    .Columns(
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new SpinnerColumn(),
        new ElapsedTimeColumn())
    .StartAsync(async ctx =>
    {
        // Stage 1: Analysis
        var analysisTask = ctx.AddTask("Analyzing solution...", maxValue: totalFiles);
        foreach (var project in solution.Projects)
        {
            analysisTask.Description = $"Analyzing {project.Name}...";
            foreach (var document in project.Documents)
            {
                // ... analyze document ...
                analysisTask.Increment(1);
            }
        }

        // Stage 2: Enrichment (no-op in Phase 1)
        var enrichTask = ctx.AddTask("Enriching...", maxValue: 1);
        enrichTask.Increment(1); // Instant completion for Phase 1

        // Stage 3: Emission
        var emitTask = ctx.AddTask("Emitting vault...", maxValue: totalNotes);
        foreach (var note in notes)
        {
            // ... write markdown file ...
            emitTask.Increment(1);
        }
    });
```

**Spectre.Console non-interactive detection:**
Spectre.Console automatically detects non-interactive terminals (piped output, CI systems). When `AnsiConsole.Profile.Capabilities.Interactive` is `false`, progress bars are displayed in a simplified, non-animated format. No custom code needed for the auto-detect requirement -- Spectre handles this out of the box. For testing, you can override: `console.Profile.Capabilities.Interactive = false`.

### Pattern 4: Pipeline Orchestrator

**What:** The Pipeline class receives analyzer, enricher, and emitter instances and executes them in sequence, tracking timing and results.

**When:** Core orchestration, called from the CLI action handler.

**Example:**
```csharp
// Source: Architecture research (ARCHITECTURE.md)

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
        CancellationToken ct)
    {
        var result = new PipelineResult();
        var sw = Stopwatch.StartNew();

        // Stage 1: Analysis
        var builder = new AnalysisResultBuilder();
        foreach (var analyzer in _analyzers)
        {
            await analyzer.AnalyzeAsync(context, builder, ct);
        }
        var analysisResult = builder.Build();
        result.AnalysisDuration = sw.Elapsed;
        sw.Restart();

        // Stage 2: Enrichment (passthrough in Phase 1)
        var enrichedResult = new EnrichedResult(analysisResult);
        foreach (var enricher in _enrichers)
        {
            await enricher.EnrichAsync(analysisResult, enrichedResult, ct);
        }
        result.EnrichmentDuration = sw.Elapsed;
        sw.Restart();

        // Stage 3: Emission
        var emitResult = await _emitter.EmitAsync(enrichedResult, outputDir, ct);
        result.EmissionDuration = sw.Elapsed;
        result.NotesGenerated = emitResult.NotesWritten;

        return result;
    }
}
```

### Pattern 5: End-of-Run Summary Table

**What:** After pipeline completes, display a formatted summary using Spectre.Console tables or markup.

**When:** After pipeline returns, before exit.

**Example:**
```csharp
// Source: Spectre.Console docs

var table = new Table()
    .Border(TableBorder.Rounded)
    .AddColumn("Stage")
    .AddColumn("Duration")
    .AddColumn("Items");

table.AddRow("Analysis", result.AnalysisDuration.ToString(@"mm\:ss\.ff"),
    $"{result.ProjectsAnalyzed} projects, {result.FilesAnalyzed} files");
table.AddRow("Enrichment", result.EnrichmentDuration.ToString(@"mm\:ss\.ff"),
    result.EnrichersRun == 0 ? "skipped" : $"{result.ItemsEnriched} items");
table.AddRow("Emission", result.EmissionDuration.ToString(@"mm\:ss\.ff"),
    $"{result.NotesGenerated} notes");
table.AddRow("[bold]Total[/]", result.TotalDuration.ToString(@"mm\:ss\.ff"),
    $"{result.NotesGenerated} notes generated");

AnsiConsole.Write(table);

if (result.Warnings.Count > 0)
{
    AnsiConsole.MarkupLine($"\n[yellow]{result.Warnings.Count} warning(s):[/]");
    foreach (var warning in result.Warnings)
        AnsiConsole.MarkupLine($"  [yellow]![/] {warning}");
}
```

### Anti-Patterns to Avoid

- **Analysis logic in Program.cs:** Program.cs must be a thin shell. No Roslyn imports, no markdown generation. It parses args and calls Pipeline.
- **Roslyn symbols leaking to emitter:** The emitter must work with domain model types (MethodInfo, CallGraph with string IDs), NOT IMethodSymbol. All Roslyn-to-domain conversion happens in analyzers. (See architecture research anti-pattern #4.)
- **Direct Console.WriteLine in pipeline stages:** All console output must go through Spectre.Console's AnsiConsole or be suppressed. Raw Console.Write breaks progress bar rendering.
- **Mixing sync and async in System.CommandLine:** Use the async overload of SetAction everywhere. Use InvokeAsync(), not Invoke(). The Roslyn APIs are inherently async.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| CLI argument parsing | Queue-based string parser (current lines 281-344) | System.CommandLine 2.0.3 RootCommand + Argument + Option | Current parser has no help generation, no tab completion, no type validation, and will collapse as flags grow. |
| Progress reporting | Console.WriteLine status messages | Spectre.Console AnsiConsole.Progress() | Proper multi-task progress bars with elapsed time, percentages, auto-refresh, and non-interactive fallback. |
| Help text generation | PrintUsage() method (current lines 346-358) | System.CommandLine auto-generated help | System.CommandLine generates help from option descriptions, types, and default values. Always in sync with actual options. |
| Non-interactive terminal detection | Manual Console.IsOutputRedirected checks | Spectre.Console Profile.Capabilities.Interactive | Spectre.Console handles terminal capability detection, ANSI support detection, and fallback rendering automatically. |
| Exit code handling for parse errors | Manual error printing (current lines 33-38) | System.CommandLine ParseResult.Invoke() | Invoke() handles parse errors, prints them to stderr, shows help, and returns appropriate exit codes. |

**Key insight:** The existing codebase has 64 lines of hand-rolled CLI parsing and help text that System.CommandLine replaces with ~15 lines of declarative option/argument definitions plus automatic help, validation, and tab completion.

## Common Pitfalls

### Pitfall 1: System.CommandLine Sync/Async Mismatch

**What goes wrong:** Mixing synchronous `SetAction(Func<ParseResult, int>)` with async Roslyn calls inside the handler. This leads to `.Result` / `.GetAwaiter().GetResult()` deadlocks or `Invoke()` vs `InvokeAsync()` confusion.
**Why it happens:** System.CommandLine has both sync and async overloads. Developers start with the sync example from tutorials and then realize Roslyn is async.
**How to avoid:** Use the async overload from the start: `SetAction(async (ParseResult parseResult, CancellationToken ct) => { ... return exitCode; })`. Always call `InvokeAsync()` at the end. Make `Main` return `Task<int>`.
**Warning signs:** `.Result` or `.GetAwaiter().GetResult()` calls in the action handler. Using `Invoke()` instead of `InvokeAsync()`.

### Pitfall 2: Spectre.Console Progress + Direct Console Output

**What goes wrong:** Writing to `Console.WriteLine` or `AnsiConsole.MarkupLine` while a Progress context is active corrupts the progress bar rendering. Output gets interleaved with progress bar control sequences.
**Why it happens:** Spectre.Console's progress display uses ANSI escape sequences to overwrite previous lines. Any direct console output breaks this mechanism. The Spectre docs explicitly warn: "Progress display is not thread safe, and using it together with other interactive components is not supported."
**How to avoid:** During progress bar display, communicate status ONLY through `task.Description` updates. Collect warnings in a list and display them after the progress bar completes. Never call `Console.Write`, `Console.Error.Write`, or `AnsiConsole.MarkupLine` inside a `Progress().Start()` lambda.
**Warning signs:** Garbled terminal output, progress bars that "jump" or leave artifacts.

### Pitfall 3: Silent MSBuild/Compilation Failures

**What goes wrong:** `MSBuildWorkspace.OpenSolutionAsync()` succeeds but the compilations have broken references. All `GetSymbolInfo()` calls return null. The tool produces empty output but reports success.
**Why it happens:** MSBuild workspace does NOT throw on NuGet restore failures or missing references. It reports failures only through the `WorkspaceFailed` event and the `Diagnostics` property.
**How to avoid:** Subscribe to `workspace.WorkspaceFailed` and log/collect all diagnostics. After loading, validate compilation health by checking error diagnostic counts. If error count is high relative to solution size, warn the user.
**Warning signs:** Methods dictionary has far fewer entries than expected. CallsOut/CallsIn dictionaries are empty.

### Pitfall 4: Output Path Validation Race Condition

**What goes wrong:** Validating that the output path is writable by creating a test file, then another process or permission change makes it unwritable by the time emission starts.
**Why it happens:** Time-of-check-to-time-of-use (TOCTOU) gap between validation and actual writing.
**How to avoid:** Validate output directory by actually creating it (`Directory.CreateDirectory`) and writing a small test file, then deleting the test file. This proves current writeability. Accept that extremely rare TOCTOU failures can still occur -- handle write failures during emission gracefully by catching IOException and adding to the warning list.
**Warning signs:** Test passes but emission fails with permission errors.

### Pitfall 5: Incorrect Default Output Path Computation

**What goes wrong:** When the user passes a directory (not a .sln file), the "adjacent to the input path" default of `./vault/` is ambiguous. Is it adjacent to the directory they passed, or adjacent to the .sln file found inside that directory?
**Why it happens:** The requirement says "adjacent to the input path" but input could be a directory or a .sln file.
**How to avoid:** Always compute the default output path relative to the resolved .sln file's parent directory, regardless of whether the user passed a .sln or a directory. Document this: "Default output: `<solution-dir>/vault/`".

## Code Examples

### Complete Program.cs Entry Point (Phase 1)

```csharp
// Source: System.CommandLine official docs + Spectre.Console docs, adapted for Code2Obsidian

using System.CommandLine;
using Spectre.Console;

namespace Code2Obsidian;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .sln file or directory containing one"
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for the Obsidian vault (default: <solution-dir>/vault/)"
        };

        var rootCommand = new RootCommand(
            "Analyze a C# solution and generate an Obsidian vault")
        {
            inputArgument,
            outputOption
        };

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            return await RunPipelineAsync(input, output, cancellationToken);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> RunPipelineAsync(
        string input, string? output, CancellationToken ct)
    {
        // 1. Resolve input path
        // 2. Resolve output path
        // 3. Validate output writability
        // 4. Create pipeline components
        // 5. Run pipeline with progress
        // 6. Display summary
        // 7. Return exit code
        // ... (see architecture patterns above)
    }
}
```

### IAnalyzer Interface

```csharp
// Source: Architecture research (ARCHITECTURE.md)

public interface IAnalyzer
{
    string Name { get; }
    Task AnalyzeAsync(
        AnalysisContext context,
        AnalysisResultBuilder builder,
        CancellationToken ct);
}
```

### IEnricher Interface (Stub for Phase 1)

```csharp
public interface IEnricher
{
    string Name { get; }
    Task EnrichAsync(
        AnalysisResult analysis,
        EnrichedResult enriched,
        CancellationToken ct);
}
```

### IEmitter Interface

```csharp
public interface IEmitter
{
    Task<EmitResult> EmitAsync(
        EnrichedResult result,
        string outputDirectory,
        CancellationToken ct);
}

public sealed record EmitResult(int NotesWritten, IReadOnlyList<string> Warnings);
```

### Writable Path Validation

```csharp
static void ValidateOutputPath(string outputDir)
{
    try
    {
        Directory.CreateDirectory(outputDir);
        var testFile = Path.Combine(outputDir, ".code2obsidian-write-test");
        File.WriteAllText(testFile, "test");
        File.Delete(testFile);
    }
    catch (UnauthorizedAccessException)
    {
        throw new InvalidOperationException(
            $"Output directory '{outputDir}' is not writable. Check permissions.");
    }
    catch (IOException ex)
    {
        throw new InvalidOperationException(
            $"Cannot write to output directory '{outputDir}': {ex.Message}");
    }
}
```

### Exit Code Strategy

```csharp
// After pipeline completes:
static int DetermineExitCode(PipelineResult result)
{
    if (result.HasFatalError)
        return 2;  // Fatal error
    if (result.Warnings.Count > 0)
        return 1;  // Success with warnings (skipped files)
    return 0;      // Clean success
}
```

### "Did you mean?" Suggestion for Bad Paths

```csharp
static string BuildSuggestionMessage(string input)
{
    var dir = Path.GetDirectoryName(input) ?? ".";
    if (!Directory.Exists(dir))
        return $"Path not found: '{input}'";

    var fileName = Path.GetFileName(input);
    var candidates = Directory.GetFiles(dir, "*.sln")
        .Select(Path.GetFileName)
        .Where(f => f is not null)
        .OrderBy(f => LevenshteinDistance(fileName, f!))
        .Take(3)
        .ToList();

    if (candidates.Count == 0)
        return $"File not found: '{input}'. No .sln files in '{dir}'.";

    var suggestions = string.Join("\n  ", candidates.Select(c => Path.Combine(dir, c!)));
    return $"File not found: '{input}'. Did you mean:\n  {suggestions}";
}
```

## State of the Art

| Old Approach (Current Code) | New Approach (Phase 1) | Impact |
|---------------------------|------------------------|--------|
| Hand-rolled arg parsing (64 lines) | System.CommandLine 2.0.3 declarative setup (~15 lines) | Auto-generated help, version, tab completion, type validation |
| Console.WriteLine for status | Spectre.Console progress bars | Real-time progress, elapsed time, non-interactive fallback |
| PrintUsage() manual help text | System.CommandLine auto-help | Help always matches actual options. No drift. |
| Monolithic Main (167 lines of logic) | Pipeline orchestrator + stage interfaces | Testable stages, clear boundaries, future-proof for Phases 2-5 |
| Options record (4 fields) | CliOptions record (input, output) + System.CommandLine parsing | Extensible: --enrich, --incremental, --verbose flags add without refactor |
| Raw exit code (0 or 2) | Granular exit codes (0/1/2) with warning collection | CI-friendly: scripts can distinguish clean success from partial failures |

**Deprecated/outdated:**
- `--per-file` / `--per-method` flags from existing code are being removed. Phase 1 emits in whatever format the ObsidianEmitter uses (which can change freely per user decision).
- The `_obsidian` default output directory is replaced by `vault` adjacent to the solution.

## Open Questions

1. **Progress bar architecture: single context vs separate per stage**
   - What we know: Spectre.Console supports adding multiple tasks to one Progress context, or running separate Progress contexts sequentially
   - What's unclear: Whether "separate bars per pipeline stage" (from user decision) means one Progress context with three tasks, or three separate Progress contexts that appear sequentially
   - Recommendation: Use a single `Progress().StartAsync()` context with three named tasks. This avoids flickering between contexts and gives a unified elapsed-time view. The task descriptions update to show the current stage. This matches the user intent of "phased progress bars."

2. **Progress context and pipeline boundary**
   - What we know: The Pipeline orchestrator owns the analysis/enrichment/emission loop. Spectre.Console progress context must wrap that loop.
   - What's unclear: Whether the Pipeline class should receive the progress context (making it aware of Spectre.Console) or report progress through an abstraction.
   - Recommendation: Use a simple `IProgress<PipelineProgress>` or a callback interface that the pipeline calls. The Program.cs layer adapts this to Spectre.Console. This keeps the Pipeline class testable without Spectre.Console dependency. The adapter in Program.cs updates `ProgressTask` descriptions and increments based on pipeline progress events.

3. **Levenshtein distance for path suggestions**
   - What we know: User wants "Did you mean?" suggestions for bad input paths
   - What's unclear: Whether a full Levenshtein implementation is justified or a simpler approach suffices
   - Recommendation: For .sln file suggestions, simple directory listing and filename prefix matching is likely sufficient. Only implement Levenshtein if there are multiple close candidates. Keep it simple in Phase 1 -- even listing available .sln files in the directory is a good UX improvement over "file not found."

## Sources

### Primary (HIGH confidence)
- [System.CommandLine 2.0.3 NuGet](https://www.nuget.org/packages/System.CommandLine) - Version, framework targets
- [System.CommandLine tutorial (Microsoft Learn, updated 2025-12-05)](https://learn.microsoft.com/en-us/dotnet/standard/commandline/get-started-tutorial) - RootCommand, Options, Arguments, SetAction API
- [System.CommandLine parse and invoke (Microsoft Learn, updated 2025-06-19)](https://learn.microsoft.com/en-us/dotnet/standard/commandline/how-to-parse-and-invoke) - Async actions, exit codes, CancellationToken
- [Spectre.Console 0.54.0 NuGet](https://www.nuget.org/packages/Spectre.Console) - Version, .NET 8 support
- [Spectre.Console Progress docs](https://spectreconsole.net/live/progress) - AddTask, Increment, Columns, StartAsync
- [Spectre.Console non-interactive detection](https://spectreconsole.net/) - Profile.Capabilities.Interactive auto-detection
- [Code2Obsidian domain architecture research](.planning/research/ARCHITECTURE.md) - Pipeline pattern, interface definitions, project structure
- [Code2Obsidian domain stack research](.planning/research/STACK.md) - Library versions, rationale
- [Code2Obsidian domain pitfalls research](.planning/research/PITFALLS.md) - MSBuild failures, symbol identity, memory

### Secondary (MEDIUM confidence)
- [System.CommandLine + Spectre.Console integration (anthonysimmon.com)](https://anthonysimmon.com/beautiful-interactive-console-apps-with-system-commandline-and-spectre-console/) - IAnsiConsole DI pattern, UTF-8 encoding
- [Pipeline Pattern in C# (michaelscodingspot.com)](https://michaelscodingspot.com/pipeline-pattern-implementations-csharp/) - Pipeline stage patterns
- [MSBuildWorkspace usage guide (Dustin Campbell)](https://gist.github.com/DustinCampbell/32cd69d04ea1c08a16ae5c4cd21dd3a3) - IProgress<ProjectLoadProgress> for progress during solution load

### Tertiary (LOW confidence)
- None. All findings verified against primary or secondary sources.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - System.CommandLine 2.0.3 and Spectre.Console 0.54.0 are verified against NuGet and official docs
- Architecture: HIGH - Pipeline pattern is well-understood; project structure follows domain research which was thoroughly vetted
- CLI API patterns: HIGH - All code examples verified against Microsoft Learn docs (updated Dec 2025)
- Progress bar patterns: HIGH - Spectre.Console API verified against official docs, non-interactive detection confirmed
- Pitfalls: HIGH - MSBuild failures, symbol identity, and output corruption are documented in domain research with issue links

**Research date:** 2026-02-25
**Valid until:** 2026-04-25 (60 days -- stable libraries, low churn expected)
