using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Code2Obsidian.Tests.TestSupport;

namespace Code2Obsidian.Tests.Integration;

public sealed class ClaudeCodeProviderIntegrationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
    private static readonly string FixtureSourceDirectory = Path.Combine(
        RepoRoot,
        "Code2Obsidian.Tests",
        "Fixtures",
        "ClaudeCodeFixtureSolution");
    private static readonly string AppAssemblyPath = Path.Combine(
        RepoRoot,
        "bin",
        "Debug",
        "net10.0",
        "Code2Obsidian.dll");

    [Fact]
    public async Task Help_output_lists_claude_code_and_existing_providers()
    {
        var result = await RunAppAsync(["--help"]);
        var normalizedOutput = NormalizeOutput(result.CombinedOutput);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--llm-provider", normalizedOutput);
        Assert.Contains("anthropic", normalizedOutput);
        Assert.Contains("claude-code", normalizedOutput);
        Assert.Contains("openai", normalizedOutput);
        Assert.Contains("ollama", normalizedOutput);
        Assert.Contains("codex", normalizedOutput);
        Assert.Contains("managed pooling", normalizedOutput);
        Assert.Contains("claude-code local process lanes", normalizedOutput);
        Assert.Contains("use --llm-endpoint only for endpoint-backed/custom providers", normalizedOutput);
        Assert.Contains("claude-code uses local `claude` CLI auth instead", normalizedOutput);
    }

    [Fact]
    public async Task Readme_documents_claude_subscription_setup_and_unsupported_backend_path()
    {
        var readmePath = Path.Combine(RepoRoot, "README.md");
        Assert.True(File.Exists(readmePath), $"Expected README at {readmePath}");

        var readme = await File.ReadAllTextAsync(readmePath);

        Assert.Contains("code2obsidian.llm.json", readme);
        Assert.Contains("claude auth login", readme);
        Assert.Contains("claude auth status", readme);
        Assert.Contains("claude mcp serve", readme);
        Assert.Contains("--llm-provider", readme);
        Assert.Contains("--pool-size", readme);
        Assert.Contains("ANTHROPIC_API_KEY", readme);
        Assert.Contains("CLAUDE_API_KEY", readme);
    }

    [Fact]
    public async Task ClaudeCode_provider_supports_pool_size_and_serena_with_managed_lanes()
    {
        await using var fixture = await TemporaryFixtureSolution.CreateAsync(FixtureSourceDirectory);
        await using var fakeClaude = await FakeClaudeCli.CreateSuccessAsync(
            assistantXml: "<ready>true</ready><reason>ready</reason><summary>Produces pooled Claude output.</summary><purpose>Confirms pooled Claude enrichment and Serena MCP wiring.</purpose><tags>claude,serena,pool</tags>",
            stderr: "fake claude pooled serena ok",
            invocationDelayMs: 350);
        var serenaCommandPath = CreateTempSerenaCommandFile();

        try
        {
            await WriteClaudeSerenaConfigAsync(fixture.RootDirectory, serenaCommandPath);

            var outputDirectory = Path.Combine(fixture.RootDirectory, "vault");
            var result = await RunAppAsync(
                [
                    fixture.SolutionPath,
                    "--output", outputDirectory,
                    "--enrich",
                    "--pool-size", "2",
                    "--no-incremental"
                ],
                new Dictionary<string, string?>
                {
                    ["PATH"] = fakeClaude.PrependToPath(Environment.GetEnvironmentVariable("PATH"))
                },
                fixture.RootDirectory);

            Assert.True(result.ExitCode == 0, result.CombinedOutput);
            Assert.Contains("Started Claude process pool", result.CombinedOutput);
            Assert.Contains("Adjusted maxConcurrency to", result.CombinedOutput);
            Assert.Contains("2 managed lane(s)", result.CombinedOutput);
            Assert.Contains("Serena symbol lookup", result.CombinedOutput);

            var invocations = fakeClaude.ReadInvocations();
            Assert.True(invocations.Count >= 6, fakeClaude.ReadTranscript());
            Assert.True(fakeClaude.ReadPeakConcurrency() >= 2, fakeClaude.ReadTranscript());
            Assert.Contains(invocations, invocation => invocation.OverlapObserved || invocation.ActiveAtStart >= 2);
            Assert.All(invocations, invocation => Assert.Equal(fixture.RootDirectory, invocation.Cwd));

            var laneConfigPaths = invocations
                .Select(invocation => invocation.McpConfigPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(laneConfigPaths.Length >= 2, fakeClaude.ReadTranscript());

            var mcpInvocations = invocations.Where(invocation => !string.IsNullOrWhiteSpace(invocation.McpConfigPath)).ToArray();
            Assert.NotEmpty(mcpInvocations);
            Assert.All(mcpInvocations, invocation =>
            {
                Assert.Contains("--mcp-config", invocation.Arguments);
                Assert.Contains("--strict-mcp-config", invocation.Arguments);
                Assert.NotNull(invocation.McpConfigJson);
                Assert.Contains("\"mcpServers\"", invocation.McpConfigJson!);
                Assert.Contains(serenaCommandPath, invocation.McpConfigJson!);
                Assert.Contains("SERENA_LOG", invocation.McpConfigJson!);
            });

            AssertGeneratedNote(outputDirectory, "Greeter.md");
            AssertGeneratedNote(outputDirectory, "Greeter.SayHello.md");
            AssertGeneratedNote(outputDirectory, "Greeter.NormalizeName.md");
            AssertGeneratedNote(outputDirectory, "BatchGreeter.md");
            AssertGeneratedNote(outputDirectory, "BatchGreeter.BuildStatusMessage.md");
            AssertGeneratedNote(outputDirectory, "BatchGreeter.BuildPersonalizedGreetings.md");
            AssertGeneratedNote(outputDirectory, "BatchGreeter.BuildPartingMessage.md");
        }
        finally
        {
            File.Delete(serenaCommandPath);
        }
    }

    [Fact]
    public async Task ClaudeCode_provider_reports_serena_readiness_failures_with_provider_aware_guidance()
    {
        await using var fixture = await TemporaryFixtureSolution.CreateAsync(FixtureSourceDirectory);
        await using var fakeClaude = await FakeClaudeCli.CreateSuccessAsync(
            assistantXml: "<ready>false</ready><reason>Initial Serena instructions were unavailable for this Claude runtime.</reason>",
            stderr: "fake claude serena not ready");
        var serenaCommandPath = CreateTempSerenaCommandFile();

        try
        {
            await WriteClaudeSerenaConfigAsync(fixture.RootDirectory, serenaCommandPath);

            var result = await RunAppAsync(
                [
                    fixture.SolutionPath,
                    "--enrich",
                    "--pool-size", "2",
                    "--no-incremental"
                ],
                new Dictionary<string, string?>
                {
                    ["PATH"] = fakeClaude.PrependToPath(Environment.GetEnvironmentVariable("PATH"))
                },
                fixture.RootDirectory);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Serena is not ready for headless entity enrichment", result.CombinedOutput);
            Assert.Contains("Initial Serena", result.CombinedOutput);
            Assert.Contains("Claude runtime.", result.CombinedOutput);
            Assert.Contains("provider 'claude-code'", result.CombinedOutput);
            Assert.Contains("managed claude-code lanes", result.CombinedOutput);
            Assert.Contains("claude-code lanes", result.CombinedOutput);
            Assert.DoesNotContain("Codex endpoint", result.CombinedOutput);
            Assert.DoesNotContain("Codex app-server", result.CombinedOutput);

            var invocations = fakeClaude.ReadInvocations();
            Assert.Single(invocations);
            Assert.Contains("--mcp-config", invocations[0].Arguments);
            Assert.Contains("--strict-mcp-config", invocations[0].Arguments);
        }
        finally
        {
            File.Delete(serenaCommandPath);
        }
    }

    [Fact]
    public async Task ClaudeCode_provider_runs_real_entrypoint_and_redacts_cli_auth_conflicts()
    {
        await using var fixture = await TemporaryFixtureSolution.CreateAsync(FixtureSourceDirectory);
        await using var fakeClaude = await FakeClaudeCli.CreateSuccessAsync(
            assistantXml: "<summary>Produces trimmed greeting text.</summary><purpose>Formats a greeting for the supplied name.</purpose><tags>greeting,formatter</tags>",
            stderr: "fake claude ok");

        var outputDirectory = Path.Combine(fixture.RootDirectory, "vault");
        const string anthropicSecret = "anthropic-secret-should-not-print";
        const string claudeSecret = "claude-secret-should-not-print";
        var environmentOverrides = new Dictionary<string, string?>
        {
            ["PATH"] = fakeClaude.PrependToPath(Environment.GetEnvironmentVariable("PATH")),
            ["ANTHROPIC_API_KEY"] = anthropicSecret,
            ["CLAUDE_API_KEY"] = claudeSecret
        };

        var result = await RunAppAsync(
            [
                fixture.SolutionPath,
                "--output", outputDirectory,
                "--enrich",
                "--llm-provider", "claude-code",
                "--llm-model", "claude-sonnet-4-5",
                "--no-incremental"
            ],
            environmentOverrides,
            fixture.RootDirectory);

        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        Assert.Contains("Provider 'claude-code' uses the local `claude` CLI auth session", result.CombinedOutput);
        Assert.Contains("$ANTHROPIC_API_KEY", result.CombinedOutput);
        Assert.Contains("$CLAUDE_API_KEY", result.CombinedOutput);
        Assert.DoesNotContain(anthropicSecret, result.CombinedOutput);
        Assert.DoesNotContain(claudeSecret, result.CombinedOutput);
        Assert.Contains("claude-code/claude-sonnet-4-5", result.CombinedOutput);

        var transcript = fakeClaude.ReadTranscript();
        Assert.Contains($"cwd={fixture.RootDirectory}", transcript);
        Assert.Contains("arg=-p", transcript);
        Assert.Contains("arg=--output-format", transcript);
        Assert.Contains("arg=json", transcript);
        Assert.Contains("arg=--append-system-prompt", transcript);

        var typeNotePath = Path.Combine(outputDirectory, "Greeter.md");
        var methodNotePath = Path.Combine(outputDirectory, "Greeter.SayHello.md");
        Assert.True(File.Exists(typeNotePath), $"Expected type note at {typeNotePath}");
        Assert.True(File.Exists(methodNotePath), $"Expected method note at {methodNotePath}");

        var typeNote = await File.ReadAllTextAsync(typeNotePath);
        var methodNote = await File.ReadAllTextAsync(methodNotePath);
        Assert.Contains("## Summary", typeNote);
        Assert.Contains("Formats a greeting for the supplied name.", typeNote);
        Assert.Contains("Produces trimmed greeting text.", typeNote);
        Assert.Contains("Formats a greeting for the supplied name.", methodNote);
        Assert.Contains("Produces trimmed greeting text.", methodNote);
    }

    [Fact]
    public async Task ClaudeCode_provider_surfaces_login_remediation_from_real_entrypoint()
    {
        await using var fixture = await TemporaryFixtureSolution.CreateAsync(FixtureSourceDirectory);
        await using var fakeClaude = await FakeClaudeCli.CreateSuccessAsync(
            assistantXml: "<summary>Should not succeed.</summary>",
            stderr: "Error: not logged in. Run `claude auth login` to continue.",
            exitCode: 4);

        var result = await RunAppAsync(
            [
                fixture.SolutionPath,
                "--enrich",
                "--llm-provider", "claude-code",
                "--llm-model", "claude-sonnet-4-5",
                "--no-incremental"
            ],
            new Dictionary<string, string?>
            {
                ["PATH"] = fakeClaude.PrependToPath(Environment.GetEnvironmentVariable("PATH"))
            },
            fixture.RootDirectory);

        var normalizedOutput = NormalizeOutput(result.CombinedOutput);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Failed to enrich", normalizedOutput);
        Assert.Contains("Claude Code CLI authentication failed", normalizedOutput);
        Assert.Contains("claude auth login", normalizedOutput);
        Assert.Contains("claude auth status", normalizedOutput);
        Assert.DoesNotContain("claude setup-token", normalizedOutput);

        var invocations = fakeClaude.ReadInvocations();
        Assert.NotEmpty(invocations);
        Assert.Contains("arg=-p", fakeClaude.ReadTranscript());
    }

    [Fact]
    public async Task ClaudeCode_provider_rejects_cli_endpoint_override_before_process_startup()
    {
        await using var fixture = await TemporaryFixtureSolution.CreateAsync(FixtureSourceDirectory);
        await using var fakeClaude = await FakeClaudeCli.CreateSuccessAsync(
            assistantXml: "<summary>Should never be used.</summary>",
            stderr: "fake claude should not run");

        var result = await RunAppAsync(
            [
                fixture.SolutionPath,
                "--enrich",
                "--llm-provider", "claude-code",
                "--llm-model", "claude-sonnet-4-5",
                "--llm-endpoint", "http://127.0.0.1:11434/v1",
                "--no-incremental"
            ],
            new Dictionary<string, string?>
            {
                ["PATH"] = fakeClaude.PrependToPath(Environment.GetEnvironmentVariable("PATH"))
            },
            fixture.RootDirectory);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Provider 'claude-code' does not use endpoint-style configuration", result.CombinedOutput);
        Assert.Contains("--llm-endpoint", result.CombinedOutput);
        Assert.Contains("local `claude` CLI auth session", result.CombinedOutput);
        Assert.Contains("claude mcp serve", result.CombinedOutput);
        Assert.DoesNotContain("LLM enabled", result.CombinedOutput);
        Assert.Empty(fakeClaude.ReadInvocations());
    }

    [Fact]
    public async Task Unknown_provider_custom_endpoint_fallback_is_not_blocked_by_claude_validation()
    {
        await using var fixture = await TemporaryFixtureSolution.CreateAsync(FixtureSourceDirectory);

        var result = await RunAppAsync(
            [
                fixture.SolutionPath,
                "--enrich",
                "--llm-provider", "custom-openai",
                "--llm-model", "custom-model",
                "--llm-endpoint", "http://127.0.0.1:9/v1",
                "--no-incremental"
            ],
            workingDirectory: fixture.RootDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("custom-openai/custom-model", result.CombinedOutput);
        Assert.DoesNotContain("does not use endpoint-style configuration", result.CombinedOutput);
        Assert.DoesNotContain("local `claude` CLI auth session", result.CombinedOutput);
        Assert.DoesNotContain("claude mcp serve", result.CombinedOutput);
        Assert.DoesNotContain("LLM setup error", result.CombinedOutput);
    }

    private static void AssertGeneratedNote(string outputDirectory, string relativePath)
    {
        var notePath = Path.Combine(outputDirectory, relativePath);
        Assert.True(File.Exists(notePath), $"Expected note at {notePath}");
    }

    private static async Task WriteClaudeSerenaConfigAsync(string rootDirectory, string serenaCommandPath)
    {
        var configPath = Path.Combine(rootDirectory, "code2obsidian.llm.json");
        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(
                new
                {
                    provider = "claude-code",
                    model = "claude-sonnet-4-5",
                    serena = new
                    {
                        enabled = true,
                        command = serenaCommandPath,
                        context = "claude-code",
                        env = new Dictionary<string, string>
                        {
                            ["SERENA_LOG"] = "warn"
                        }
                    }
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<CliProcessResult> RunAppAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        string? workingDirectory = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory ?? RepoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.StartInfo.ArgumentList.Add(AppAssemblyPath);
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
            {
                if (value is null)
                    process.StartInfo.Environment.Remove(key);
                else
                    process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string CreateTempSerenaCommandFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serena-command-{Guid.NewGuid():N}");
        File.WriteAllText(path, "echo serena");
        return path;
    }

    private static string NormalizeOutput(string text)
    {
        var withoutAnsi = Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
        return Regex.Replace(withoutAnsi, @"\s+", " ").Trim();
    }

    private sealed record CliProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}\n{StandardError}";
    }

    private sealed class TemporaryFixtureSolution : IAsyncDisposable
    {
        private TemporaryFixtureSolution(string rootDirectory, string solutionPath)
        {
            RootDirectory = rootDirectory;
            SolutionPath = solutionPath;
        }

        public string RootDirectory { get; }

        public string SolutionPath { get; }

        public static Task<TemporaryFixtureSolution> CreateAsync(string sourceDirectory)
        {
            var rootDirectory = Path.Combine(
                Path.GetTempPath(),
                "Code2Obsidian.Tests",
                $"fixture-{Guid.NewGuid():N}");
            CopyDirectory(sourceDirectory, rootDirectory);

            var solutionPath = Directory.GetFiles(rootDirectory, "*.sln", SearchOption.TopDirectoryOnly).Single();
            return Task.FromResult(new TemporaryFixtureSolution(rootDirectory, solutionPath));
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                    Directory.Delete(RootDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp fixture assets.
            }

            return ValueTask.CompletedTask;
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var sourceFilePath in Directory.GetFiles(sourceDirectory))
            {
                var destinationFilePath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFilePath));
                File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
            }

            foreach (var sourceSubdirectory in Directory.GetDirectories(sourceDirectory))
            {
                var destinationSubdirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory));
                CopyDirectory(sourceSubdirectory, destinationSubdirectory);
            }
        }
    }
}
