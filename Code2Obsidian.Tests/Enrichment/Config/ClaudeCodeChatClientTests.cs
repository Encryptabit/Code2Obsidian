using Code2Obsidian.Enrichment.Config;
using Code2Obsidian.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace Code2Obsidian.Tests.Enrichment.Config;

public sealed class ClaudeCodeChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_returns_assistant_text_and_usage_from_claude_json()
    {
        const string systemPrompt = "You are a precise XML formatter.";
        const string userPrompt = "Summarize MethodA using XML tags.";
        const string assistantXml = "<summary>MethodA does work.</summary><purpose>It enriches code.</purpose>";

        var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
            ExitCode: 0,
            StandardOutput: FakeClaudeJson.Success(assistantXml, inputTokens: 17, outputTokens: 9),
            StandardError: string.Empty));
        var client = new ClaudeCodeChatClient(
            model: "claude-sonnet-4-5",
            workingDirectory: "/repo/solution",
            commandRunner: commandRunner,
            timeout: TimeSpan.FromSeconds(15));

        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, userPrompt)
            ]);

        Assert.Equal(assistantXml, response.Text);
        Assert.Equal(17, response.Usage?.InputTokenCount);
        Assert.Equal(9, response.Usage?.OutputTokenCount);

        var request = Assert.Single(commandRunner.Requests);
        Assert.Equal("claude", request.FileName);
        Assert.Equal("/repo/solution", request.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
        Assert.Equal(
            [
                "-p",
                userPrompt,
                "--output-format",
                "json",
                "--model",
                "claude-sonnet-4-5",
                "--append-system-prompt",
                systemPrompt
            ],
            request.Arguments);
    }

    [Fact]
    public async Task GetResponseAsync_appends_mcp_config_flags_when_configured()
    {
        const string userPrompt = "Summarize MethodA using XML tags.";
        const string mcpConfigPath = "/tmp/claude-serena-mcp.json";

        var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
            ExitCode: 0,
            StandardOutput: FakeClaudeJson.Success("<summary>ok</summary>"),
            StandardError: string.Empty));
        var client = new ClaudeCodeChatClient(
            model: "claude-sonnet-4-5",
            commandRunner: commandRunner,
            mcpConfigPath: mcpConfigPath);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, userPrompt)]);

        var request = Assert.Single(commandRunner.Requests);
        Assert.Equal(
            [
                "-p",
                userPrompt,
                "--output-format",
                "json",
                "--model",
                "claude-sonnet-4-5",
                "--mcp-config",
                mcpConfigPath,
                "--strict-mcp-config"
            ],
            request.Arguments);
    }

    [Fact]
    public void Constructor_throws_when_mcp_config_path_is_blank()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ClaudeCodeChatClient(
            model: "claude-sonnet-4-5",
            mcpConfigPath: "   "));

        Assert.Contains("MCP config path", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_throws_timeout_exception_with_provider_context()
    {
        var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
            ExitCode: -1,
            StandardOutput: string.Empty,
            StandardError: "waiting for approval",
            TimedOut: true));
        var client = new ClaudeCodeChatClient(
            model: "claude-sonnet-4-5",
            commandRunner: commandRunner,
            timeout: TimeSpan.FromSeconds(12));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("Claude Code CLI timed out", ex.Message);
        Assert.Contains("12 seconds", ex.Message);
        Assert.Contains("waiting for approval", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_throws_actionable_error_when_claude_executable_is_missing()
    {
        var commandRunner = FakeCommandRunner.FromException(new FileNotFoundException("claude not found"));
        var client = new ClaudeCodeChatClient(model: "claude-sonnet-4-5", commandRunner: commandRunner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("'claude'", ex.Message);
        Assert.Contains("not found on PATH", ex.Message);
        Assert.Contains("claude-code", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResponseAsync_throws_when_json_is_malformed()
    {
        var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
            ExitCode: 0,
            StandardOutput: "not-json",
            StandardError: "parse failed"));
        var client = new ClaudeCodeChatClient(model: "claude-sonnet-4-5", commandRunner: commandRunner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("malformed JSON", ex.Message);
        Assert.Contains("parse failed", ex.Message);
        Assert.Contains("not-json", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_throws_when_result_field_is_missing()
    {
        var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
            ExitCode: 0,
            StandardOutput: FakeClaudeJson.MissingResult(),
            StandardError: string.Empty));
        var client = new ClaudeCodeChatClient(model: "claude-sonnet-4-5", commandRunner: commandRunner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("result", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResponseAsync_throws_login_remediation_when_claude_session_is_logged_out()
    {
        var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
            ExitCode: 4,
            StandardOutput: string.Empty,
            StandardError: "Error: not logged in. Run `claude auth login` to continue."));
        var client = new ClaudeCodeChatClient(model: "claude-sonnet-4-5", commandRunner: commandRunner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("authentication failed", ex.Message);
        Assert.Contains("exit code 4", ex.Message);
        Assert.Contains("claude auth login", ex.Message);
        Assert.Contains("claude auth status", ex.Message);
        Assert.DoesNotContain("claude setup-token", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_throws_auth_status_remediation_when_claude_session_is_expired()
    {
        var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
            ExitCode: 7,
            StandardOutput: string.Empty,
            StandardError: "Session expired. Run `claude auth status` for more information."));
        var client = new ClaudeCodeChatClient(model: "claude-sonnet-4-5", commandRunner: commandRunner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("authentication failed", ex.Message);
        Assert.Contains("session appears invalid or expired", ex.Message);
        Assert.Contains("claude auth status", ex.Message);
        Assert.DoesNotContain("claude setup-token", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_throws_setup_token_remediation_and_redacts_secret_values()
    {
        const string processSecret = "live-secret-123";
        const string stderrSecret = "top-secret-456";
        var originalValue = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", processSecret);

        try
        {
            var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
                ExitCode: 9,
                StandardOutput: string.Empty,
                StandardError: $"Invalid API key for Claude auth. Run `claude setup-token`; ANTHROPIC_API_KEY={stderrSecret}"));
            var client = new ClaudeCodeChatClient(model: "claude-sonnet-4-5", commandRunner: commandRunner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")]));

            Assert.Contains("authentication failed", ex.Message);
            Assert.Contains("claude setup-token", ex.Message);
            Assert.DoesNotContain("claude auth login", ex.Message);
            Assert.DoesNotContain(processSecret, ex.Message);
            Assert.DoesNotContain(stderrSecret, ex.Message);
            Assert.Contains("<redacted>", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalValue);
        }
    }

    [Fact]
    public async Task GetResponseAsync_keeps_non_auth_failures_on_generic_non_zero_exit_path()
    {
        const string processSecret = "live-secret-123";
        const string stderrSecret = "top-secret-456";
        var originalValue = Environment.GetEnvironmentVariable("CODE2OBSIDIAN_TEST_SECRET");
        Environment.SetEnvironmentVariable("CODE2OBSIDIAN_TEST_SECRET", processSecret);

        try
        {
            var commandRunner = FakeCommandRunner.FromResult(new CommandRunnerResult(
                ExitCode: 9,
                StandardOutput: string.Empty,
                StandardError: $"worker crashed while reading pipe {processSecret}; CUSTOM_SECRET={stderrSecret}"));
            var client = new ClaudeCodeChatClient(model: "claude-sonnet-4-5", commandRunner: commandRunner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")]));

            Assert.Contains("exited with code 9", ex.Message);
            Assert.DoesNotContain("authentication failed", ex.Message);
            Assert.DoesNotContain("claude auth login", ex.Message);
            Assert.DoesNotContain("claude auth status", ex.Message);
            Assert.DoesNotContain("claude setup-token", ex.Message);
            Assert.DoesNotContain(processSecret, ex.Message);
            Assert.DoesNotContain(stderrSecret, ex.Message);
            Assert.Contains("<redacted>", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE2OBSIDIAN_TEST_SECRET", originalValue);
        }
    }
}
