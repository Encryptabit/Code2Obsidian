using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net.WebSockets;
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
using Spectre.Console.Rendering;

namespace Code2Obsidian;

internal static class Program
{
    private static void ConfigureConsole()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
    }

    private static async Task<int> Main(string[] args)
    {
        ConfigureConsole();

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
            Description = "Enable incremental mode (default): only regenerate notes for files changed since last run"
        };

        var noIncrementalOption = new Option<bool>("--no-incremental")
        {
            Description = "Disable incremental mode and run full analysis without reading/saving incremental state"
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

        var suggestionsOption = new Option<bool>("--suggestions")
        {
            Description = "Generate LLM-powered optimization/refactor suggestions for entities"
        };

        var llmProviderOption = new Option<string?>("--llm-provider")
        {
            Description = $"LLM provider ({LlmProviderCatalog.FormatKnownProviders()}; use --llm-endpoint only for endpoint-backed/custom providers, and use local `claude` CLI auth for claude-code)"
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
            Description = "LLM endpoint URL for endpoint-backed providers (codex/ollama/custom); claude-code uses local `claude` CLI auth instead"
        };

        var codexWslDistroOption = new Option<string?>("--codex-wsl-distro")
        {
            Description = "WSL distro to launch Codex app-server pool in (Windows only)"
        };

        var poolSizeOption = new Option<int?>("--pool-size")
        {
            Description = "Spawn N managed workers for providers that support managed pooling (codex app-server endpoints, claude-code local process lanes)"
        };

        var traceCodexWsOption = new Option<bool>("--trace-codex-ws")
        {
            Description = "Print Codex websocket frame traces (works with pooled endpoints)"
        };

        var excludeOption = new Option<string?>("--exclude")
        {
            Description = "Comma/semicolon-separated glob patterns for file paths to exclude from analysis"
        };

        var rootCommand = new RootCommand("Analyze a C# solution and generate an Obsidian vault")
        {
            inputArgument,
            outputOption,
            fanInThresholdOption,
            complexityThresholdOption,
            incrementalOption,
            noIncrementalOption,
            fullRebuildOption,
            dryRunOption,
            enrichOption,
            suggestionsOption,
            llmProviderOption,
            llmModelOption,
            llmApiKeyOption,
            llmEndpointOption,
            codexWslDistroOption,
            poolSizeOption,
            traceCodexWsOption,
            excludeOption
        };

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var fanInThreshold = parseResult.GetValue(fanInThresholdOption);
            var complexityThreshold = parseResult.GetValue(complexityThresholdOption);
            var incrementalRequested = parseResult.GetValue(incrementalOption);
            var noIncremental = parseResult.GetValue(noIncrementalOption);
            if (incrementalRequested && noIncremental)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Cannot use both --incremental and --no-incremental.");
                return 1;
            }
            var incremental = !noIncremental;
            var fullRebuild = parseResult.GetValue(fullRebuildOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var enrich = parseResult.GetValue(enrichOption);
            var suggestions = parseResult.GetValue(suggestionsOption);
            var llmProvider = parseResult.GetValue(llmProviderOption);
            var llmModel = parseResult.GetValue(llmModelOption);
            var llmApiKey = parseResult.GetValue(llmApiKeyOption);
            var llmEndpoint = parseResult.GetValue(llmEndpointOption);
            var codexWslDistro = parseResult.GetValue(codexWslDistroOption);
            var poolSize = parseResult.GetValue(poolSizeOption);
            var traceCodexWs = parseResult.GetValue(traceCodexWsOption);
            var excludeRaw = parseResult.GetValue(excludeOption);

            return await RunPipelineAsync(input, output, fanInThreshold, complexityThreshold,
                incremental, fullRebuild, dryRun, enrich, suggestions, llmProvider, llmModel, llmApiKey, llmEndpoint,
                codexWslDistro, poolSize, traceCodexWs, excludeRaw,
                cancellationToken);
        });

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    private static async Task<int> RunPipelineAsync(
        string input, string? output, int fanInThreshold, int complexityThreshold,
        bool incremental, bool fullRebuild, bool dryRun,
        bool enrich, bool suggestions, string? llmProvider, string? llmModel, string? llmApiKey, string? llmEndpoint,
        string? codexWslDistro, int? poolSize, bool traceCodexWs, string? excludeRaw,
        CancellationToken ct)
    {
        CodexAppServerPool? codexPool = null;
        ClaudeCodeProcessPool? claudePool = null;
        IChatClient? llmClient = null;
        try
        {
            CodexLogBoard.Reset();

            var llmRequested = enrich || suggestions;
            if (poolSize is not null && !llmRequested)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --pool-size requires --enrich or --suggestions.");
                return 1;
            }

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

            // Parse CLI exclude patterns and merge with config file exclude patterns
            var cliExclude = ParseExcludePatterns(excludeRaw);
            string[]? configExclude = null;
            {
                var configPathForExclude = Path.Combine(Path.GetDirectoryName(solutionPath)!, "code2obsidian.llm.json");
                var excludeConfig = LlmConfigLoader.TryLoad(configPathForExclude);
                configExclude = excludeConfig?.Exclude;
            }
            var excludePatterns = MergeExcludePatterns(configExclude, cliExclude);

            // Set up enrichment if --enrich is passed
            var enrichers = new List<IEnricher>();
            LlmEnricher? llmEnricher = null;

            if (llmRequested)
            {
                var configPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "code2obsidian.llm.json");
                var config = LlmConfigLoader.TryLoad(configPath);
                var configHasEndpoint = !string.IsNullOrWhiteSpace(config?.Endpoint);
                var configHasEndpoints = config?.Endpoints is { Length: > 0 };
                var cliEndpointOverrideProvided = !string.IsNullOrWhiteSpace(llmEndpoint);

                // Expand env vars in CLI API key (same pattern as LlmConfigLoader)
                if (llmApiKey is not null && llmApiKey.StartsWith('$'))
                {
                    var envValue = Environment.GetEnvironmentVariable(llmApiKey.Substring(1));
                    if (!string.IsNullOrEmpty(envValue))
                        llmApiKey = envValue;
                    // If env var not set, leave as-is; ChatClientFactory will report the error
                }

                var endpointOverride = ParseEndpointOverride(llmEndpoint);

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
                            Endpoint = endpointOverride.primaryEndpoint ?? config.Endpoint,
                            Endpoints = llmEndpoint is null
                                ? config.Endpoints
                                : endpointOverride.pooledEndpoints
                        };
                    }
                    else if (llmProvider is not null && llmModel is not null)
                    {
                        // All required fields provided via CLI -- no config file needed
                        config = new LlmConfig(
                            Provider: llmProvider,
                            Model: llmModel,
                            ApiKey: llmApiKey,
                            Endpoint: endpointOverride.primaryEndpoint,
                            Endpoints: endpointOverride.pooledEndpoints,
                            TraceCodexWs: traceCodexWs);
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
                        AnsiConsole.MarkupLine("[yellow]Expected filename:[/] code2obsidian.llm.json");
                        AnsiConsole.MarkupLine("[yellow]For claude-code, authenticate first:[/] `claude auth login` then `claude auth status`.");
                        AnsiConsole.MarkupLine("[yellow]Or provide --llm-provider and --llm-model on the command line.[/]");
                        return 1;
                    }
                }

                if (traceCodexWs)
                {
                    config = config with { TraceCodexWs = true };
                }

                var providerCapabilities = ResolveKnownProviderCapabilities(config.Provider);
                var isCodexProvider = providerCapabilities?.Id.Equals("codex", StringComparison.OrdinalIgnoreCase) == true;
                var isClaudeCodeProvider = providerCapabilities?.Id.Equals("claude-code", StringComparison.OrdinalIgnoreCase) == true;

                EmitProviderWarnings(config, providerCapabilities);

                var endpointMisuseError = LlmProviderGuidance.GetEndpointMisuseError(
                    providerCapabilities,
                    configHasEndpoint,
                    configHasEndpoints,
                    cliEndpointOverrideProvided);
                if (endpointMisuseError is not null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(endpointMisuseError)}");
                    AnsiConsole.MarkupLine(
                        $"[yellow]{Markup.Escape(LlmProviderGuidance.GetUnsupportedBackendSummary(providerCapabilities!))}[/]");
                    return 1;
                }

                if (SerenaMcpSettings.IsEnabled(config) && providerCapabilities?.SupportsSerena != true)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Error:[/] Serena MCP symbol lookup is not supported with provider '{Markup.Escape(config.Provider)}'. Supported providers: {Markup.Escape(FormatProvidersSupporting(capabilities => capabilities.SupportsSerena))}.");
                    return 1;
                }

                if (poolSize is not null && providerCapabilities?.SupportsManagedPooling != true)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Error:[/] --pool-size is not supported with provider '{Markup.Escape(config.Provider)}'. Supported providers: {Markup.Escape(FormatProvidersSupporting(capabilities => capabilities.SupportsManagedPooling))}.");
                    return 1;
                }

                var manageCodexPool = isCodexProvider &&
                    (poolSize is not null || ShouldAutoStartCodexPool(config));
                var manageClaudePool = isClaudeCodeProvider &&
                    (poolSize is not null || SerenaMcpSettings.IsEnabled(config));

                if (isCodexProvider && !manageCodexPool)
                {
                    CodexLogBoard.ConfigureInstances(GetConfiguredEndpoints(config));
                }
                else if (!isCodexProvider)
                {
                    CodexLogBoard.Reset();
                }

                // Launch a managed local Codex app-server pool when explicitly requested,
                // or by default for local/loopback Codex endpoints.
                if (manageCodexPool)
                {
                    var effectiveCodexWslDistro = ResolveCodexPoolWslDistro(codexWslDistro);

                    if (SerenaMcpSettings.IsEnabled(config))
                    {
                        try
                        {
                            var (resolvedSerena, installedMessage) =
                                await SerenaMcpSettings.EnsureCommandAvailableAsync(
                                    config.Serena,
                                    effectiveCodexWslDistro,
                                    ct);
                            config = config with { Serena = resolvedSerena };
                            if (!string.IsNullOrWhiteSpace(installedMessage))
                                AnsiConsole.MarkupLine($"[green]{Markup.Escape(installedMessage)}[/]");
                        }
                        catch (InvalidOperationException ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Serena setup error:[/] {Markup.Escape(ex.Message)}");
                            return 1;
                        }
                    }

                    var effectivePoolSize = poolSize ?? CountConfiguredEndpoints(config);
                    if (effectivePoolSize < 1)
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] Codex pool size must be >= 1.");
                        return 1;
                    }

                    var baseEndpoint = ResolveCodexPoolBaseEndpoint(config);
                    var plannedEndpoints = CodexAppServerPool.PreviewEndpoints(baseEndpoint, effectivePoolSize);
                    CodexLogBoard.ConfigureInstances(plannedEndpoints);
                    try
                    {
                        codexPool = await CodexAppServerPool.StartAsync(
                            effectivePoolSize,
                            baseEndpoint,
                            effectiveCodexWslDistro,
                            ct,
                            workingDirectory: Path.GetDirectoryName(solutionPath),
                            serena: config.Serena);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Codex pool startup failed:[/] {Markup.Escape(ex.Message)}");
                        return 1;
                    }

                    var originalConcurrency = config.MaxConcurrency;
                    var adjustedConcurrency = Math.Max(originalConcurrency, codexPool.Endpoints.Count);
                    config = config with
                    {
                        Endpoint = codexPool.Endpoints[0],
                        Endpoints = codexPool.Endpoints.ToArray(),
                        MaxConcurrency = adjustedConcurrency
                    };

                    AnsiConsole.MarkupLine(
                        $"[green]Started Codex app-server pool:[/] {codexPool.Endpoints.Count} instances");
                    if (adjustedConcurrency != originalConcurrency)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]Adjusted maxConcurrency to[/] {adjustedConcurrency} to match pool size.");
                    }
                }
                else if (manageClaudePool)
                {
                    var effectivePoolSize = poolSize ?? 1;
                    if (effectivePoolSize < 1)
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] Claude pool size must be >= 1.");
                        return 1;
                    }

                    try
                    {
                        claudePool = await ClaudeCodeProcessPool.StartAsync(
                            effectivePoolSize,
                            config.Serena,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Claude pool startup failed:[/] {Markup.Escape(ex.Message)}");
                        return 1;
                    }

                    var originalConcurrency = config.MaxConcurrency;
                    config = config with
                    {
                        Serena = claudePool.Serena,
                        MaxConcurrency = claudePool.LaneCount
                    };

                    if (!string.IsNullOrWhiteSpace(claudePool.BootstrapMessage))
                        AnsiConsole.MarkupLine($"[green]{Markup.Escape(claudePool.BootstrapMessage)}[/]");

                    AnsiConsole.MarkupLine(
                        $"[green]Started Claude process pool:[/] {claudePool.LaneCount} lane(s)");
                    if (config.MaxConcurrency != originalConcurrency)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]Adjusted maxConcurrency to[/] {config.MaxConcurrency} to match Claude lane count.");
                    }
                }

                if (isCodexProvider)
                {
                    var configuredEndpoints = GetConfiguredEndpoints(config);
                    var (reachable, unreachable) = await ProbeCodexEndpointsAsync(configuredEndpoints, ct);

                    if (reachable.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] No reachable Codex websocket endpoints were found.");
                        if (poolSize is null)
                        {
                            AnsiConsole.MarkupLine(
                                "[yellow]Tip:[/] Start Codex app-server instances manually, or pass [bold]--pool-size N[/] to launch a local pool.");
                        }

                        foreach (var failed in unreachable.Take(3))
                        {
                            AnsiConsole.MarkupLine(
                                $"  [grey]{Markup.Escape(failed.endpoint)}[/]: {Markup.Escape(failed.error)}");
                        }
                        if (unreachable.Count > 3)
                        {
                            AnsiConsole.MarkupLine($"  [grey]...and {unreachable.Count - 3} more endpoint probe failures[/]");
                        }
                        return 1;
                    }

                    if (unreachable.Count > 0)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]Warning:[/] {unreachable.Count} Codex endpoint(s) unreachable; using {reachable.Count} reachable endpoint(s).");
                        foreach (var failed in unreachable.Take(3))
                        {
                            AnsiConsole.MarkupLine(
                                $"  [grey]{Markup.Escape(failed.endpoint)}[/]: {Markup.Escape(failed.error)}");
                        }
                        if (unreachable.Count > 3)
                        {
                            AnsiConsole.MarkupLine($"  [grey]...and {unreachable.Count - 3} more endpoint probe failures[/]");
                        }

                        config = config with
                        {
                            Endpoint = reachable[0],
                            Endpoints = reachable.Count > 1 ? reachable.Skip(1).ToArray() : null
                        };

                        if (config.MaxConcurrency > reachable.Count)
                        {
                            config = config with { MaxConcurrency = reachable.Count };
                            AnsiConsole.MarkupLine(
                                $"[yellow]Adjusted maxConcurrency to[/] {reachable.Count} to match reachable endpoints.");
                        }
                    }
                }

                // Create IChatClient
                IChatClient client;
                Action<Uri, string>? codexEndpointUnavailable = null;
                Func<Uri, string, CancellationToken, Task>? codexEndpointRecycle = null;
                if (codexPool is not null && isCodexProvider)
                {
                    codexEndpointUnavailable = (endpoint, reason) =>
                    {
                        var restarted = codexPool.RequestRestart(endpoint.ToString(), reason);
                        if (!restarted)
                        {
                            CodexLogBoard.Report(endpoint.ToString(), $"restart request ignored ({reason})");
                        }
                    };
                    codexEndpointRecycle = async (endpoint, reason, token) =>
                    {
                        var restarted = await codexPool.RequestRestartAndWaitAsync(endpoint.ToString(), reason, token);
                        if (!restarted)
                        {
                            CodexLogBoard.Report(endpoint.ToString(), $"recycle request ignored ({reason})");
                        }
                    };
                }
                try
                {
                    client = ChatClientFactory.CreateFromConfig(
                        config,
                        codexEndpointUnavailable,
                        solutionDirectory: Path.GetDirectoryName(solutionPath),
                        codexEndpointRecycle: codexEndpointRecycle,
                        claudeProcessPool: claudePool);
                    llmClient = client;
                }
                catch (InvalidOperationException ex)
                {
                    AnsiConsole.MarkupLine($"[red]LLM setup error:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                // Create SummaryCache
                var stateDbPath = Path.Combine(outputDir, ".code2obsidian-state.db");
                var cache = new SummaryCache(stateDbPath);

                // Create LlmEnricher (progress wired per execution case)
                // No confirmation prompt: explicit LLM flags are treated as user consent.
                llmEnricher = new LlmEnricher(
                    client,
                    cache,
                    config,
                    progress: null,
                    confirmEnrichment: null,
                    dirtyFiles: null,
                    analysisRoot: Path.GetDirectoryName(solutionPath),
                    includeSummary: enrich,
                    includeSuggestions: suggestions,
                    fanInThreshold: fanInThreshold,
                    fanOutThreshold: fanInThreshold);
                enrichers.Add(llmEnricher);

                var llmModeText = enrich && suggestions
                    ? "summaries + suggestions"
                    : enrich
                        ? "summaries"
                        : "suggestions";
                var endpointCount = CountConfiguredEndpoints(config);
                if (config.Provider.Equals("codex", StringComparison.OrdinalIgnoreCase) && endpointCount > 1)
                {
                    AnsiConsole.MarkupLine($"[green]LLM enabled ({llmModeText}):[/] {config.Provider}/{config.Model} ([green]{endpointCount} endpoints pooled[/])");
                }
                else if (config.Provider.Equals("claude-code", StringComparison.OrdinalIgnoreCase) && claudePool is not null)
                {
                    AnsiConsole.MarkupLine($"[green]LLM enabled ({llmModeText}):[/] {config.Provider}/{config.Model} ([green]{claudePool.LaneCount} managed lane(s)[/])");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]LLM enabled ({llmModeText}):[/] {config.Provider}/{config.Model}");
                }

                if (SerenaMcpSettings.IsEnabled(config))
                {
                    var serenaConfig = SerenaMcpSettings.Normalize(config.Serena)!;
                    var serenaModeText = serenaConfig.SkipOnboarding
                        ? "headless, automatic onboarding disabled"
                        : "headless";
                    AnsiConsole.MarkupLine($"[green]Serena symbol lookup:[/] enabled ({serenaModeText})");
                }
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
                    result = await RunWithProgress(async progress =>
                    {
                        var (incEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
                        liveLlm = llm;
                        var incPipeline = new IncrementalPipeline(
                            context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold,
                            enrichers: incEnrichers, excludePatterns: excludePatterns);
                        return await incPipeline.RunFullWithStateSaveAsync(ct);
                    });
                    CopyEnrichmentMetrics(result, liveLlm);
                    result.WasIncremental = false;
                }
                else if (dryRun)
                {
                    // Case E: --dry-run -> show what would change without writing
                    if (!incremental)
                    {
                        AnsiConsole.MarkupLine("[yellow]--dry-run with --no-incremental: showing full analysis preview.[/]");
                    }

                    if (!state.TryLoad(out _))
                    {
                        AnsiConsole.MarkupLine("[yellow]No prior state found. Dry run requires a previous incremental run.[/]");
                        AnsiConsole.MarkupLine("[yellow]Run with --incremental first to establish state.[/]");
                        return 1;
                    }

                    result = await RunWithProgress(async progress =>
                    {
                        var incPipeline = new IncrementalPipeline(
                            context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold,
                            excludePatterns: excludePatterns);
                        return await incPipeline.RunDryRunAsync(state, ct);
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
                        result = await RunWithProgress(async progress =>
                        {
                            var (incEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
                            liveLlmB = llm;
                            var incPipeline = new IncrementalPipeline(
                                context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold,
                                enrichers: incEnrichers, excludePatterns: excludePatterns);
                            return await incPipeline.RunFullWithStateSaveAsync(ct);
                        });
                        CopyEnrichmentMetrics(result, liveLlmB);
                        result.WasIncremental = true;
                    }
                    else
                    {
                        // Case C: prior state exists -> run incremental two-pass flow (INFR-03)
                        LlmEnricher? liveLlmC = null;
                        result = await RunWithProgress(async progress =>
                        {
                            var (incEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
                            liveLlmC = llm;
                            var incPipeline = new IncrementalPipeline(
                                context, progress, outputDir, stateDbPath, fanInThreshold, complexityThreshold,
                                enrichers: incEnrichers, excludePatterns: excludePatterns);
                            return await incPipeline.RunIncrementalAsync(state, ct);
                        });
                        CopyEnrichmentMetrics(result, liveLlmC);
                        result.WasIncremental = true;
                    }
                }

                IncrementalPipeline.EnsureGitignore(outputDir);
            }
            else
            {
                // Case A: --no-incremental (and no incremental-only flags) -> full analysis via existing Pipeline (no state)
                result = await RunFullPipeline(context, outputDir, fanInThreshold, complexityThreshold, enrichers, excludePatterns, ct);
            }

            // Add loader diagnostics as warnings BEFORE rendering
            if (loader.Diagnostics.Count > 0)
            {
                result.Warnings.AddRange(loader.Diagnostics);
            }
            if (loader.SuppressedPackageDiagnosticCount > 0)
            {
                result.Warnings.Add(
                    $"{loader.SuppressedPackageDiagnosticCount} MSBuild package diagnostics were suppressed (non-blocking compatibility/vulnerability warnings).");
            }

            // Display end-of-run summary (after progress context closes)
            RenderSummary(result);

            // Display warnings after summary
            if (result.Warnings.Count > 0)
            {
                var uniqueWarnings = result.Warnings
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                const int maxWarningsToDisplay = 25;
                var visibleWarnings = uniqueWarnings.Take(maxWarningsToDisplay).ToList();
                var hiddenWarningCount = uniqueWarnings.Count - visibleWarnings.Count;

                AnsiConsole.MarkupLine($"\n[yellow]{uniqueWarnings.Count} warning(s):[/]");
                foreach (var warning in visibleWarnings)
                    AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(warning)}");

                if (hiddenWarningCount > 0)
                {
                    AnsiConsole.MarkupLine(
                        $"  [yellow]![/] {hiddenWarningCount} additional warning(s) omitted.");
                }
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
        finally
        {
            if (llmClient is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (llmClient is IDisposable disposable)
                disposable.Dispose();

            if (claudePool is not null)
                await claudePool.DisposeAsync();

            if (codexPool is not null)
                await codexPool.DisposeAsync();
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
                    llm.Client,
                    llm.Cache,
                    llm.Config,
                    progress,
                    llm.ConfirmEnrichment,
                    llm.DirtyFiles,
                    llm.AnalysisRoot,
                    llm.IncludeSummary,
                    llm.IncludeSuggestions,
                    llm.FanInThreshold,
                    llm.FanOutThreshold);
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
        foreach (var warning in llm.FailureWarnings)
        {
            result.Warnings.Add($"[enrichment] {warning}");
        }
    }

    /// <summary>
    /// Runs the full pipeline (non-incremental Case A) with Live display.
    /// </summary>
    private static async Task<PipelineResult> RunFullPipeline(
        AnalysisContext context, string outputDir, int fanInThreshold, int complexityThreshold,
        List<IEnricher> enrichers, string[]? excludePatterns, CancellationToken ct)
    {
        var analyzers = new List<IAnalyzer> { new MethodAnalyzer(null, excludePatterns), new TypeAnalyzer(null, excludePatterns) };
        var emitter = new ObsidianEmitter(fanInThreshold, complexityThreshold);

        LlmEnricher? liveLlm = null;
        var result = await RunWithProgress(async progress =>
        {
            // Rebuild enrichers with live progress
            var (liveEnrichers, llm) = BuildEnrichersWithProgress(enrichers, progress);
            liveLlm = llm;

            var pipeline = new Pipeline.Pipeline(analyzers, liveEnrichers, emitter);
            return await pipeline.RunAsync(context, outputDir, progress, ct);
        });

        CopyEnrichmentMetrics(result, liveLlm);
        return result;
    }

    /// <summary>
    /// Runs an async operation within a Spectre live display with fixed-width character bars.
    /// Progress updates only mutate state; render refreshes happen on a periodic loop to avoid
    /// cursor race conditions and append/scroll artifacts on some terminals.
    /// </summary>
    private static async Task<PipelineResult> RunWithProgress(
        Func<IProgress<PipelineProgress>, Task<PipelineResult>> operation)
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
        {
            // Non-interactive output (redirect/piped): skip live rendering.
            var noUiProgress = new Progress<PipelineProgress>(_ => { });
            return await operation(noUiProgress);
        }

        PipelineResult? result = null;
        var state = new ProgressState();
        var stateLock = new object();

        var progress = new Progress<PipelineProgress>(p =>
        {
            lock (stateLock)
            {
                state.Update(p);
            }
        });

        await AnsiConsole
            .Live(new Text(""))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Crop)
            .StartAsync(async ctx =>
        {
            var opTask = operation(progress);
            int? lastRenderedFingerprint = null;
            while (!opTask.IsCompleted)
            {
                var codexEntries = CodexLogBoard.Snapshot();
                var shouldRefresh = false;
                lock (stateLock)
                {
                    var fingerprint = ComputeRenderFingerprint(state, codexEntries);
                    if (lastRenderedFingerprint != fingerprint)
                    {
                        lastRenderedFingerprint = fingerprint;
                        ctx.UpdateTarget(RenderProgressDisplay(state, codexEntries));
                        shouldRefresh = true;
                    }
                }
                if (shouldRefresh)
                    ctx.Refresh();
                await Task.Delay(250);
            }

            result = await opTask;
            var finalCodexEntries = CodexLogBoard.Snapshot();
            lock (stateLock)
            {
                ctx.UpdateTarget(RenderProgressDisplay(state, finalCodexEntries));
            }
            ctx.Refresh();
        });

        if (result is null)
        {
            throw new InvalidOperationException("Pipeline operation completed without a result.");
        }
        return result;
    }

    #region Progress Display

    private sealed class StageState
    {
        public string Description { get; set; } = "";
        public int Current { get; set; }
        public int Total { get; set; }
        public Stopwatch Timer { get; } = new();
    }

    private sealed class ProgressState
    {
        // Stages: 0=Analyzing, 1=Enriching, 2=Emitting
        public StageState[] Stages { get; } = { new(), new(), new() };

        public void Update(PipelineProgress p)
        {
            var index = p.Stage switch
            {
                PipelineStage.Analyzing => 0,
                PipelineStage.Enriching => 1,
                PipelineStage.Emitting => 2,
                _ => -1
            };

            if (index < 0) return;

            var stage = Stages[index];
            stage.Description = p.Description;
            stage.Current = p.Current;
            stage.Total = p.Total;

            if (!stage.Timer.IsRunning && p.Total > 0)
                stage.Timer.Start();

            // Stop timer when stage completes
            if (p.Current >= p.Total && p.Total > 0)
                stage.Timer.Stop();
        }
    }

    private static int ComputeRenderFingerprint(
        ProgressState state,
        IReadOnlyList<(string Endpoint, DateTimeOffset Timestamp, string Message)> codexEntries)
    {
        var hash = new HashCode();

        foreach (var stage in state.Stages)
        {
            hash.Add(stage.Description, StringComparer.Ordinal);
            hash.Add(stage.Current);
            hash.Add(stage.Total);
            hash.Add(stage.Timer.IsRunning);
            hash.Add((int)stage.Timer.Elapsed.TotalSeconds);
        }

        hash.Add(codexEntries.Count);
        foreach (var entry in codexEntries)
        {
            hash.Add(entry.Endpoint, StringComparer.Ordinal);
            hash.Add(entry.Message, StringComparer.Ordinal);
            hash.Add(entry.Timestamp.ToUnixTimeSeconds());
        }

        return hash.ToHashCode();
    }

    private static IRenderable RenderProgressDisplay(
        ProgressState state,
        IReadOnlyList<(string Endpoint, DateTimeOffset Timestamp, string Message)>? codexEntries = null)
    {
        const int barWidth = 40;
        const int timestampWidth = 8;
        const int minEndpointWidth = 19;
        const int maxEndpointWidth = 28;
        const int minMessageWidth = 24;
        const int maxMessageWidth = 140;
        var rows = new List<IRenderable>();

        var activeStageIndex = -1;
        for (var i = 0; i < state.Stages.Length; i++)
        {
            var stage = state.Stages[i];
            if (stage.Total > 0 && stage.Current < stage.Total)
            {
                activeStageIndex = i;
                break;
            }
        }

        var allCompletedLastStage = -1;
        for (var i = 0; i < state.Stages.Length; i++)
        {
            if (state.Stages[i].Total > 0)
                allCompletedLastStage = i;
        }

        var startStageIndex = activeStageIndex >= 0
            ? activeStageIndex
            : allCompletedLastStage;

        for (var i = startStageIndex; i >= 0 && i < state.Stages.Length; i++)
        {
            var stage = state.Stages[i];
            if (stage.Total <= 0) continue;

            var current = Math.Clamp(stage.Current, 0, stage.Total);
            var pct = stage.Total > 0 ? (double)current / stage.Total : 0;
            var filled = (int)(pct * barWidth);
            var empty = barWidth - filled;

            var filledBar = new string('━', filled);
            var emptyBar = new string('━', empty);
            var countText = $"{current}/{stage.Total}";
            var pctText = $"{pct * 100,5:F1}%";
            var elapsed = stage.Timer.Elapsed.ToString(@"hh\:mm\:ss");

            // Line 1: Description
            rows.Add(new Markup($"  {Markup.Escape(stage.Description)}"));
            // Line 2: Bar + completed count + percentage + elapsed
            rows.Add(new Markup($"  [green]{filledBar}[/][grey]{emptyBar}[/] {countText}  {pctText}  {elapsed}"));
            // Blank line between stages
            rows.Add(new Text(""));
        }

        codexEntries ??= CodexLogBoard.Snapshot();
        if (codexEntries.Count > 0)
        {
            rows.Add(new Rule());
            var displayWidth = Math.Max(80, AnsiConsole.Profile.Width);
            var endpointWidth = Math.Clamp(
                codexEntries.Max(e => e.Endpoint.Length),
                minEndpointWidth,
                maxEndpointWidth);
            var messageWidth = Math.Clamp(
                displayWidth - endpointWidth - timestampWidth - 9, // left indent + spaces
                minMessageWidth,
                maxMessageWidth);

            foreach (var entry in codexEntries)
            {
                var timestamp = entry.Timestamp == DateTimeOffset.MinValue
                    ? "--:--:--"
                    : entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
                var endpoint = TruncateForDisplay(entry.Endpoint, endpointWidth).PadRight(endpointWidth);
                var message = TruncateForDisplay(entry.Message, messageWidth);
                rows.Add(new Markup(
                    $"  [grey]{Markup.Escape(endpoint)}[/] [blue]{timestamp}[/] {Markup.Escape(message)}"));
            }
        }

        if (rows.Count == 0)
            return new Text("Starting...");

        return new Rows(rows);
    }

    private static string TruncateForDisplay(string value, int maxChars)
    {
        if (maxChars <= 1)
            return "…";

        if (value.Length <= maxChars)
            return value;

        return value[..(maxChars - 1)] + "…";
    }

    private static LlmProviderCapabilities? ResolveKnownProviderCapabilities(string provider)
    {
        return LlmProviderCatalog.TryGet(provider, out var capabilities) ? capabilities : null;
    }

    private static string FormatProvidersSupporting(Func<LlmProviderCapabilities, bool> predicate)
    {
        var supportedProviders = LlmProviderCatalog.All
            .Where(predicate)
            .Select(capabilities => $"'{capabilities.Id}'")
            .ToArray();

        return supportedProviders.Length == 0
            ? "none"
            : string.Join(", ", supportedProviders);
    }

    private static void EmitProviderWarnings(LlmConfig config, LlmProviderCapabilities? providerCapabilities)
    {
        if (providerCapabilities is null || providerCapabilities.ConflictingApiAuthEnvVars.Length == 0)
            return;

        EmitApiAuthPrecedenceWarning(config, providerCapabilities);
    }

    private static void EmitApiAuthPrecedenceWarning(LlmConfig config, LlmProviderCapabilities providerCapabilities)
    {
        var configuredApiKey = !string.IsNullOrWhiteSpace(config.ApiKey);
        var conflictingEnvVars = providerCapabilities.ConflictingApiAuthEnvVars
            .Where(envVar => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
            .Select(envVar => $"${envVar}")
            .ToArray();

        if (!configuredApiKey && conflictingEnvVars.Length == 0)
            return;

        var authSources = new List<string>();
        if (configuredApiKey)
            authSources.Add("config/--llm-api-key");
        authSources.AddRange(conflictingEnvVars);

        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] {Markup.Escape(LlmProviderGuidance.GetAuthPrecedenceWarning(providerCapabilities, authSources))}");
        AnsiConsole.MarkupLine(
            $"[yellow]{Markup.Escape(LlmProviderGuidance.GetAuthPrecedenceRemediation(providerCapabilities))}[/]");
    }

    #endregion

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

    private static (string? primaryEndpoint, string[]? pooledEndpoints) ParseEndpointOverride(string? rawEndpointInput)
    {
        if (string.IsNullOrWhiteSpace(rawEndpointInput))
            return (null, null);

        var split = rawEndpointInput
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (split.Length == 0)
            return (null, null);

        if (split.Length == 1)
            return (split[0], null);

        return (split[0], split);
    }

    private static string[]? ParseExcludePatterns(string? rawExcludeInput)
    {
        if (string.IsNullOrWhiteSpace(rawExcludeInput))
            return null;

        var split = rawExcludeInput
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return split.Length == 0 ? null : split;
    }

    private static string[]? MergeExcludePatterns(string[]? configExclude, string[]? cliExclude)
    {
        if (configExclude is null && cliExclude is null)
            return null;

        var merged = new List<string>();
        if (configExclude is not null) merged.AddRange(configExclude);
        if (cliExclude is not null) merged.AddRange(cliExclude);

        var result = merged
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return result.Length == 0 ? null : result;
    }

    private static string ResolveCodexPoolBaseEndpoint(LlmConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
            return config.Endpoint;

        if (config.Endpoints is { Length: > 0 })
        {
            var first = config.Endpoints.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }

        return "ws://127.0.0.1:8080";
    }

    private static string? ResolveCodexPoolWslDistro(string? codexWslDistro)
    {
        if (!OperatingSystem.IsWindows())
            return codexWslDistro;

        var distro = string.IsNullOrWhiteSpace(codexWslDistro)
            ? Environment.GetEnvironmentVariable("CODEX_POOL_WSL_DISTRO")
            : codexWslDistro;

        return string.IsNullOrWhiteSpace(distro) ? "Ubuntu" : distro.Trim();
    }

    private static bool ShouldAutoStartCodexPool(LlmConfig config)
    {
        if (!config.Provider.Equals("codex", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var endpoint in GetConfiguredEndpoints(config))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                return false;

            if (!uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!uri.IsLoopback)
                return false;
        }

        return true;
    }

    private static IReadOnlyList<string> GetConfiguredEndpoints(LlmConfig config)
    {
        var endpoints = new List<string>();
        if (config.Endpoints is { Length: > 0 })
        {
            endpoints.AddRange(config.Endpoints.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
            endpoints.Add(config.Endpoint.Trim());

        if (endpoints.Count == 0)
            endpoints.Add("ws://127.0.0.1:8080");

        return endpoints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountConfiguredEndpoints(LlmConfig config)
    {
        return GetConfiguredEndpoints(config).Count;
    }

    private static async Task<(List<string> reachable, List<(string endpoint, string error)> unreachable)> ProbeCodexEndpointsAsync(
        IReadOnlyList<string> endpoints,
        CancellationToken ct)
    {
        var reachable = new List<string>();
        var unreachable = new List<(string endpoint, string error)>();

        foreach (var endpoint in endpoints)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                unreachable.Add((endpoint, "Invalid endpoint URI."));
                continue;
            }

            try
            {
                using var ws = new ClientWebSocket();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(1));
                await ws.ConnectAsync(uri, connectCts.Token);
                reachable.Add(endpoint);

                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe", closeCts.Token);
                }
                catch
                {
                    // Best effort close; successful connect already established reachability.
                }
            }
            catch (Exception ex) when (IsCodexProbeException(ex, ct))
            {
                unreachable.Add((endpoint, ex.Message));
            }
        }

        return (reachable, unreachable);
    }

    private static bool IsCodexProbeException(Exception ex, CancellationToken outerToken)
    {
        if (ex is OperationCanceledException && outerToken.IsCancellationRequested)
            return false;

        return ex is OperationCanceledException
            or WebSocketException
            or HttpRequestException
            or InvalidOperationException;
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
            ? $"{result.FilesAnalyzed} files ({result.FilesSkipped} unchanged)"
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
