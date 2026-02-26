using System.CommandLine;
using System.CommandLine.Parsing;
using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Analyzers;
using Code2Obsidian.Emission;
using Code2Obsidian.Enrichment;
using Code2Obsidian.Loading;
using Code2Obsidian.Pipeline;
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

        var rootCommand = new RootCommand("Analyze a C# solution and generate an Obsidian vault")
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

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    private static async Task<int> RunPipelineAsync(
        string input, string? output, CancellationToken ct)
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

            // Compose pipeline (no DI container in Phase 1)
            var analyzers = new List<IAnalyzer> { new MethodAnalyzer(), new TypeAnalyzer() };
            var enrichers = new List<IEnricher>();
            var emitter = new ObsidianEmitter();
            var pipeline = new Pipeline.Pipeline(analyzers, enrichers, emitter);

            // Run pipeline with Spectre.Console progress
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
                    var analysisTask = ctx.AddTask("Analyzing...", maxValue: 1);
                    var enrichmentTask = ctx.AddTask("Enriching...", maxValue: 1);
                    enrichmentTask.IsIndeterminate = true;
                    var emissionTask = ctx.AddTask("Emitting...", maxValue: 1);
                    emissionTask.IsIndeterminate = true;

                    var progress = new Progress<PipelineProgress>(p =>
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

                    result = await pipeline.RunAsync(context, outputDir, progress, ct);

                    // Mark all tasks complete
                    analysisTask.Value = analysisTask.MaxValue;
                    enrichmentTask.Value = enrichmentTask.MaxValue;
                    emissionTask.Value = emissionTask.MaxValue;
                });

            // Add loader diagnostics as warnings BEFORE rendering
            if (loader.Diagnostics.Count > 0)
            {
                result!.Warnings.AddRange(loader.Diagnostics);
            }

            // Display end-of-run summary (after progress context closes)
            RenderSummary(result!);

            // Display warnings after summary
            if (result!.Warnings.Count > 0)
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

        table.AddRow(
            "Analysis",
            result.AnalysisDuration.ToString(@"mm\:ss\.ff"),
            $"{result.ProjectsAnalyzed} projects, {result.FilesAnalyzed} files");

        table.AddRow(
            "Enrichment",
            result.EnrichmentDuration.ToString(@"mm\:ss\.ff"),
            result.EnrichersRun == 0 ? "skipped" : $"{result.EnrichersRun} enrichers");

        table.AddRow(
            "Emission",
            result.EmissionDuration.ToString(@"mm\:ss\.ff"),
            $"{result.NotesGenerated} notes");

        table.AddRow(
            "[bold]Total[/]",
            $"[bold]{result.TotalDuration.ToString(@"mm\:ss\.ff")}[/]",
            $"[bold]{result.NotesGenerated} notes generated[/]");

        AnsiConsole.Write(table);
    }

    #endregion
}
