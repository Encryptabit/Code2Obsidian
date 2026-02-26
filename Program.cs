using System.CommandLine;
using System.CommandLine.Parsing;
using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Analyzers;
using Code2Obsidian.Emission;
using Code2Obsidian.Enrichment;
using Code2Obsidian.Enrichment.Config;
using Code2Obsidian.Incremental;
using Code2Obsidian.Loading;
using Code2Obsidian.Pipeline;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace Code2Obsidian;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .sln file or directory containing one"
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for the Obsidian vault (default: <solution-dir>/vault/)"
        };

        var fanInThresholdOption = new Option<int>("--fan-in-threshold")
        {
            Description = "Fan-in threshold for danger tagging (default: 10)",
            DefaultValueFactory = _ => 10
        };

        var complexityThresholdOption = new Option<int>("--complexity-threshold")
        {
            Description = "Cyclomatic complexity threshold for danger tagging (default: 15)",
            DefaultValueFactory = _ => 15
        };

        var incrementalOption = new Option<bool>("--incremental")
        {
            Description = "Only regenerate notes for files changed since last run"
        };

        var fullRebuildOption = new Option<bool>("--full-rebuild")
        {
            Description = "Force full analysis even when incremental state exists"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be regenerated without writing files"
        };

        var enrichOption = new Option<bool>("--enrich")
        {
            Description = "Generate LLM-powered plain-English summaries for methods and classes"
        };

        var llmProviderOption = new Option<string?>("--llm-provider")
        {
            Description = "LLM provider (anthropic, openai, ollama, or custom with --llm-endpoint)"
        };

        var llmModelOption = new Option<string?>("--llm-model")
        {
            Description = "LLM model name (overrides config file)"
        };

        var llmApiKeyOption = new Option<string?>("--llm-api-key")
        {
            Description = "LLM API key or $ENV_VAR reference (overrides config file)"
        };

        var llmEndpointOption = new Option<string?>("--llm-endpoint")
        {
            Description = "LLM endpoint URL (overrides config file, required for custom providers)"
        };

        var rootCommand = new RootCommand("Analyze a C# solution and generate an Obsidian vault")
        {
            inputArgument,
            outputOption,
            fanInThresholdOption,
            complexityThresholdOption,
            incrementalOption,
            fullRebuildOption,
            dryRunOption,
            enrichOption,
            llmProviderOption,
            llmModelOption,
            llmApiKeyOption,
            llmEndpointOption
        };

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var fanInThreshold = parseResult.GetValue(fanInThresholdOption);
            var complexityThreshold = parseResult.GetValue(complexityThresholdOption);
            var incremental = parseResult.GetValue(incrementalOption);
            var fullRebuild = parseResult.GetValue(fullRebuildOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var enrich = parseResult.GetValue(enrichOption);
            var llmProvider = parseResult.GetValue(llmProviderOption);
            var llmModel = parseResult.GetValue(llmModelOption);
            var llmApiKey = parseResult.GetValue(llmApiKeyOption);
            var llmEndpoint = parseResult.GetValue(llmEndpointOption);

            return await RunPipelineAsync(input, output, fanInThreshold, complexityThreshold,
                incremental, fullRebuild, dryRun, enrich, llmProvider, llmModel, llmApiKey, llmEndpoint,
                cancellationToken);
        });

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    private static async Task<int> RunPipelineAsync(
        string input, string? output, int fanInThreshold, int complexityThreshold,
        bool incremental, bool fullRebuild, bool dryRun,
        bool enrich, string? llmProvider, string? llmModel, string? llmApiKey, string? llmEndpoint,
        CancellationToken ct)
    {
        try
        {
            // Resolve input path
            var solutionPath = ResolveSolutionPath(input);

            // Resolve output path
            var outputDir = ResolveOutputPath(output, solutionPath);

            // Validate output path is writable
            ValidateOutputPath(outputDir);

            // Load solution
            var loader = new SolutionLoader();
            AnsiConsole.MarkupLine($"[bold]Solution:[/] {solutionPath}");
            AnsiConsole.MarkupLine($"[bold]Output:[/]   {outputDir}");
            AnsiConsole.WriteLine();

            using var context = await loader.LoadAsync(solutionPath, ct);

            // Set up enrichment if --enrich is passed
            var enrichers = new List<IEnricher>();
            LlmEnricher? llmEnricher = null;

            if (enrich)
            {
                var configPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "code2obsidian.llm.json");
                var config = LlmConfigLoader.TryLoad(configPath);

                // Expand env vars in CLI API key (same pattern as LlmConfigLoader)
                if (llmApiKey is not null && llmApiKey.StartsWith('$'))
                {
                    var envValue = Environment.GetEnvironmentVariable(llmApiKey.Substring(1));
                    if (!string.IsNullOrEmpty(envValue))
                        llmApiKey = envValue;
                    // If env var not set, leave as-is; ChatClientFactory will report the error
                }

                // Apply CLI overrides (JSON config for defaults, CLI flags override)
                if (llmProvider is not null || llmModel is not null || llmApiKey is not null || llmEndpoint is not null)
                {
                    if (config is not null)
                    {
                        config = config with
                        {
                            Provider = llmProvider ?? config.Provider,
                            Model = llmModel ?? config.Model,
                            ApiKey = llmApiKey ?? config.ApiKey,
                            Endpoint = llmEndpoint ?? config.Endpoint
                        };
                    }
                    else if (llmProvider is not null && llmModel is not null)
                    {
                        // All required fields provided via CLI -- no config file needed
                        config = new LlmConfig(
                            Provider: llmProvider,
                            Model: llmModel,
                            ApiKey: llmApiKey,
                            Endpoint: llmEndpoint);
                    }
                }

                // If config is still null, run interactive setup
                if (config is null)
                {
                    config = InteractiveSetup.RunSetup(configPath);
                    if (config is null)
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] No LLM configuration found and interactive setup unavailable (non-interactive terminal).");
                        AnsiConsole.MarkupLine($"[yellow]Create a config file at:[/] {Markup.Escape(configPath)}");
                        AnsiConsole.MarkupLine("[yellow]Or provide --llm-provider and --llm-model on the command line.[/]");
                        return 1;
                    }
                }

                // Create IChatClient
                IChatClient client;
                try
                {
                    client = ChatClientFactory.CreateFromConfig(config);
                }
                catch (InvalidOperationException ex)
                {
                    AnsiConsole.MarkupLine($"[red]LLM setup error:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                // Create SummaryCache
                var stateDbPath = Path.Combine(outputDir, ".code2obsidian-state.db");
                var cache = new SummaryCache(stateDbPath);

                // Create confirmation lambda for cost estimation
                Func<int, int, int, decimal, bool> confirmEnrichment = (count, inTokens, outTokens, cost) =>
                {
                    AnsiConsole.MarkupLine($"[yellow]Enrichment will process {count} entities[/]");
                    AnsiConsole.MarkupLine($"[yellow]Estimated: ~{inTokens + outTokens:N0} tokens (~${cost:F4})[/]");
                    return AnsiConsole.Confirm("Proceed with LLM enrichment?", defaultValue: true);
                };

                // Create LlmEnricher (progress wired per execution case)
                llmEnricher = new LlmEnricher(client, cache, config, progress: null, confirmEnrichment: confirmEnrichment);
                enrichers.Add(llmEnricher);

                AnsiConsole.MarkupLine($"[green]LLM enrichment enabled:[/] {config.Provider}/{config.Model}");
                AnsiConsole.WriteLine();
            }

            PipelineResult result;

            if (incremental || fullRebuild || dryRun)
            {
                // Incremental mode: delegate ALL orchestration to IncrementalPipeline (INFR-06)
                var stateDbPath = Path.Combine(outputDir, ".code2obsidian-state.db");
                var state = new IncrementalState(stateDbPath);

                if (fullRebuild)
                {
                    // Case D: --full-rebuild -> wipe state, run full analysis with state save
                    state.DeleteState();
                    AnsiConsole.MarkupLine("[yellow]State wiped. Performing full analysis.[/]");
                    LlmEnricher? liveLlm = null;
                    result = await RunWithProgress(ctx =>
                    {
                        var progress = CreateProgress(ctx);
                        var (incEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
                        liveLlm = llm;
                        var incPipeline = new IncrementalPipeline(
                            context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold,
                            enrichers: incEnrichers);
                        return incPipeline.RunFullWithStateSaveAsync(ct);
                    });
                    CopyEnrichmentMetrics(result, liveLlm);
                    result.WasIncremental = false;
                }
                else if (dryRun)
                {
                    // Case E: --dry-run -> show what would change without writing
                    if (!incremental)
                    {
                        AnsiConsole.MarkupLine("[yellow]--dry-run without --incremental: showing full analysis preview.[/]");
                    }

                    if (!state.TryLoad(out _))
                    {
                        AnsiConsole.MarkupLine("[yellow]No prior state found. Dry run requires a previous incremental run.[/]");
                        AnsiConsole.MarkupLine("[yellow]Run with --incremental first to establish state.[/]");
                        return 1;
                    }

                    result = await RunWithProgress(ctx =>
                    {
                        var progress = CreateProgress(ctx);
                        var incPipeline = new IncrementalPipeline(
                            context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold);
                        return incPipeline.RunDryRunAsync(state, ct);
                    });
                    result.WasIncremental = true;
                }
                else
                {
                    // --incremental flag
                    if (!state.TryLoad(out _))
                    {
                        // Case B: no prior state -> full analysis with state save (INFR-05)
                        AnsiConsole.MarkupLine("[yellow]No prior state found. Performing full analysis and saving state.[/]");
                        LlmEnricher? liveLlmB = null;
                        result = await RunWithProgress(ctx =>
                        {
                            var progress = CreateProgress(ctx);
                            var (incEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
                            liveLlmB = llm;
                            var incPipeline = new IncrementalPipeline(
                                context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold,
                                enrichers: incEnrichers);
                            return incPipeline.RunFullWithStateSaveAsync(ct);
                        });
                        CopyEnrichmentMetrics(result, liveLlmB);
                        result.WasIncremental = true;
                    }
                    else
                    {
                        // Case C: prior state exists -> run incremental two-pass flow (INFR-03)
                        LlmEnricher? liveLlmC = null;
                        result = await RunWithProgress(ctx =>
                        {
                            var progress = CreateProgress(ctx);
                            var (incEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
                            liveLlmC = llm;
                            var incPipeline = new IncrementalPipeline(
                                context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold,
                                enrichers: incEnrichers);
                            return incPipeline.RunIncrementalAsync(state, ct);
                        });
                        CopyEnrichmentMetrics(result, liveLlmC);
                        result.WasIncremental = true;
                    }
                }

                IncrementalPipeline.EnsureGitignore(outputDir);
            }
            else
            {
                // Case A: no incremental flags -> full analysis via existing Pipeline (no state)
                result = await RunFullPipeline(context, outputDir, fanInThreshold, complexityThreshold, enrichers, ct);
            }

            // Add loader diagnostics as warnings BEFORE rendering
            if (loader.Diagnostics.Count > 0)
            {
                result.Warnings.AddRange(loader.Diagnostics);
            }

            // Display end-of-run summary (after progress context closes)
            RenderSummary(result);

            // Display warnings after summary
            if (result.Warnings.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[yellow]{result.Warnings.Count} warning(s):[/]");
                foreach (var warning in result.Warnings)
                    AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(warning)}");
            }

            return result.ExitCode;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Operation cancelled.[/]");
            return 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
    }

    /// <summary>
    /// Rebuilds the enrichers list with progress wired for Spectre.Console context.
    /// LlmEnricher needs the IProgress from the live progress context, so we recreate
    /// it with the correct progress reporter when inside the progress lambda.
    /// Returns the new list and a reference to the new LlmEnricher (if any) for metrics collection.
    /// </summary>
    private static (IReadOnlyList<IEnricher> enrichers, LlmEnricher? llmEnricher) BuildEnrichersWithProgress(
        List<IEnricher> originalEnrichers, IProgress<PipelineProgress> progress)
    {
        var result = new List<IEnricher>();
        LlmEnricher? newLlm = null;
        foreach (var enricher in originalEnrichers)
        {
            if (enricher is LlmEnricher llm)
            {
                // Reconstruct with the live progress context (dirtyFiles set later by IncrementalPipeline if needed)
                newLlm = new LlmEnricher(
                    llm.Client, llm.Cache, llm.Config, progress, llm.ConfirmEnrichment, llm.DirtyFiles);
                result.Add(newLlm);
            }
            else
            {
                result.Add(enricher);
            }
        }
        return (result, newLlm);
    }

    /// <summary>
    /// Copies enrichment metrics from a LlmEnricher to the PipelineResult.
    /// </summary>
    private static void CopyEnrichmentMetrics(PipelineResult result, LlmEnricher? llm)
    {
        if (llm is null) return;
        result.EntitiesEnriched = llm.EntitiesEnriched;
        result.EntitiesCached = llm.EntitiesCached;
        result.EntitiesFailed = llm.EntitiesFailed;
        result.InputTokensUsed = llm.InputTokensUsed;
        result.OutputTokensUsed = llm.OutputTokensUsed;
    }

    /// <summary>
    /// Runs the full pipeline (non-incremental Case A) with Spectre.Console progress.
    /// </summary>
    private static async Task<PipelineResult> RunFullPipeline(
        AnalysisContext context, string outputDir, int fanInThreshold, int complexityThreshold,
        List<IEnricher> enrichers, CancellationToken ct)
    {
        var analyzers = new List<IAnalyzer> { new MethodAnalyzer(), new TypeAnalyzer() };
        var emitter = new ObsidianEmitter(fanInThreshold, complexityThreshold);

        PipelineResult result = null!;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var progress = CreateProgress(ctx);

                // Rebuild enrichers with live progress
                var (liveEnrichers, liveLlm) = BuildEnrichersWithProgress(enrichers, progress);

                var pipeline = new Pipeline.Pipeline(analyzers, liveEnrichers, emitter);
                result = await pipeline.RunAsync(context, outputDir, progress, ct);

                // Copy enrichment metrics from the live enricher
                if (liveLlm is not null)
                {
                    result.EntitiesEnriched = liveLlm.EntitiesEnriched;
                    result.EntitiesCached = liveLlm.EntitiesCached;
                    result.EntitiesFailed = liveLlm.EntitiesFailed;
                    result.InputTokensUsed = liveLlm.InputTokensUsed;
                    result.OutputTokensUsed = liveLlm.OutputTokensUsed;
                }

                // Mark all tasks complete (progress tasks handled by CreateProgress)
            });

        return result;
    }

    /// <summary>
    /// Runs an async operation within a Spectre.Console progress context.
    /// Used for incremental pipeline operations that manage their own progress reporting.
    /// </summary>
    private static async Task<PipelineResult> RunWithProgress(
        Func<ProgressContext, Task<PipelineResult>> operation)
    {
        PipelineResult result = null!;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                result = await operation(ctx);
            });

        return result;
    }

    /// <summary>
    /// Creates a pipeline progress reporter from a Spectre.Console progress context.
    /// </summary>
    private static IProgress<PipelineProgress> CreateProgress(ProgressContext ctx)
    {
        var analysisTask = ctx.AddTask("Analyzing...", maxValue: 1);
        var enrichmentTask = ctx.AddTask("Enriching...", maxValue: 1);
        enrichmentTask.IsIndeterminate = true;
        var emissionTask = ctx.AddTask("Emitting...", maxValue: 1);
        emissionTask.IsIndeterminate = true;

        return new Progress<PipelineProgress>(p =>
        {
            switch (p.Stage)
            {
                case PipelineStage.Analyzing:
                    analysisTask.Description = p.Description;
                    if (p.Total > 0)
                    {
                        analysisTask.MaxValue = p.Total;
                        analysisTask.Value = p.Current;
                    }
                    break;

                case PipelineStage.Enriching:
                    enrichmentTask.IsIndeterminate = false;
                    enrichmentTask.Description = p.Description;
                    if (p.Total > 0)
                    {
                        enrichmentTask.MaxValue = p.Total;
                        enrichmentTask.Value = p.Current;
                    }
                    break;

                case PipelineStage.Emitting:
                    emissionTask.IsIndeterminate = false;
                    emissionTask.Description = p.Description;
                    if (p.Total > 0)
                    {
                        emissionTask.MaxValue = p.Total;
                        emissionTask.Value = p.Current;
                    }
                    break;
            }
        });
    }

    #region Path Resolution

    /// <summary>
    /// Resolves the input path to an absolute .sln file path.
    /// Accepts .sln files directly or directories containing exactly one .sln file.
    /// </summary>
    private static string ResolveSolutionPath(string input)
    {
        // Direct .sln file
        if (File.Exists(input) && input.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(input);

        // Directory: find .sln files inside
        if (Directory.Exists(input))
        {
            var slnFiles = Directory.GetFiles(input, "*.sln");
            return slnFiles.Length switch
            {
                1 => Path.GetFullPath(slnFiles[0]),
                0 => throw new InvalidOperationException(
                    $"No .sln file found in '{Path.GetFullPath(input)}'."),
                _ => throw new InvalidOperationException(
                    $"Multiple .sln files found in '{Path.GetFullPath(input)}': " +
                    $"{string.Join(", ", slnFiles.Select(Path.GetFileName))}. " +
                    "Specify one directly.")
            };
        }

        // Input does not exist -- generate suggestions
        throw new FileNotFoundException(BuildSuggestionMessage(input));
    }

    /// <summary>
    /// Resolves the output directory path. Defaults to vault/ adjacent to the .sln file.
    /// </summary>
    private static string ResolveOutputPath(string? output, string solutionPath)
    {
        if (output is not null)
            return Path.GetFullPath(output);

        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        return Path.Combine(solutionDir, "vault");
    }

    /// <summary>
    /// Validates that the output directory is writable by creating a test file.
    /// Fails fast before any analysis begins.
    /// </summary>
    private static void ValidateOutputPath(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var testFile = Path.Combine(outputDir, ".code2obsidian-write-test");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                $"Output directory is not writable: '{outputDir}'");
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Cannot write to output directory '{outputDir}': {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a "Did you mean?" suggestion message for a non-existent input path.
    /// Lists up to 3 .sln files found in the parent directory.
    /// </summary>
    private static string BuildSuggestionMessage(string input)
    {
        var fullPath = Path.GetFullPath(input);
        var parentDir = Path.GetDirectoryName(fullPath);

        if (parentDir is not null && Directory.Exists(parentDir))
        {
            var candidates = Directory.GetFiles(parentDir, "*.sln")
                .Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            if (candidates.Count > 0)
            {
                return $"File not found: '{input}'. Did you mean?\n" +
                       string.Join("\n", candidates.Select(c => $"  {c}"));
            }
        }

        return $"File not found: '{input}'. No .sln files found in '{parentDir ?? "."}'.";
    }

    #endregion

    #region Summary Rendering

    /// <summary>
    /// Renders the end-of-run summary table using Spectre.Console.
    /// </summary>
    private static void RenderSummary(PipelineResult result)
    {
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Stage")
            .AddColumn("Duration")
            .AddColumn("Items");

        var analysisItems = result.WasIncremental
            ? $"{result.FilesAnalyzed} files ({result.FilesSkipped} skipped)"
            : $"{result.ProjectsAnalyzed} projects, {result.FilesAnalyzed} files";

        table.AddRow(
            "Analysis",
            result.AnalysisDuration.ToString(@"mm\:ss\.ff"),
            analysisItems);

        // Enrichment row: show metrics if enrichment ran
        string enrichmentItems;
        if (result.EntitiesEnriched > 0 || result.EntitiesCached > 0)
        {
            enrichmentItems = $"{result.EntitiesEnriched} enriched, {result.EntitiesCached} cached, {result.EntitiesFailed} failed";
        }
        else
        {
            enrichmentItems = result.EnrichersRun == 0 ? "No enrichers configured" : $"{result.EnrichersRun} enrichers";
        }

        table.AddRow(
            "Enrichment",
            result.EnrichmentDuration.ToString(@"mm\:ss\.ff"),
            enrichmentItems);

        // Token usage row (only if tokens were used)
        if (result.InputTokensUsed > 0 || result.OutputTokensUsed > 0)
        {
            var totalTokens = result.InputTokensUsed + result.OutputTokensUsed;
            table.AddRow(
                "  Tokens",
                "",
                $"{totalTokens:N0} ({result.InputTokensUsed:N0} in, {result.OutputTokensUsed:N0} out)");
        }

        var emissionItems = $"{result.NotesGenerated} notes";
        if (result.WasIncremental && result.NotesDeleted > 0)
            emissionItems += $" ({result.NotesDeleted} stale deleted)";

        table.AddRow(
            "Emission",
            result.EmissionDuration.ToString(@"mm\:ss\.ff"),
            emissionItems);

        table.AddRow(
            "[bold]Total[/]",
            $"[bold]{result.TotalDuration.ToString(@"mm\:ss\.ff")}[/]",
            $"[bold]{result.NotesGenerated} notes generated[/]");

        AnsiConsole.Write(table);
    }

    #endregion
}
