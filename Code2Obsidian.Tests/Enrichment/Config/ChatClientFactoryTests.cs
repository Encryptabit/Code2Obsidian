using System.Reflection;
using Code2Obsidian.Enrichment.Config;
using Microsoft.Extensions.AI;

namespace Code2Obsidian.Tests.Enrichment.Config;

public sealed class ChatClientFactoryTests
{
    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public void CreateFromConfig_requires_api_key_for_sdk_backed_providers(string provider)
    {
        var config = new LlmConfig(provider, "test-model", ApiKey: "$MISSING_PROVIDER_KEY");

        var ex = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.CreateFromConfig(config));

        Assert.Contains("MISSING_PROVIDER_KEY", ex.Message);
    }

    [Fact]
    public void CreateFromConfig_ollama_skips_api_key_resolution()
    {
        var config = new LlmConfig(
            Provider: "ollama",
            Model: "llama3.1",
            ApiKey: "$MISSING_PROVIDER_KEY",
            Endpoint: "http://localhost:11434");

        var client = ChatClientFactory.CreateFromConfig(config);

        Assert.Equal("OllamaApiClient", client.GetType().Name);
    }

    [Fact]
    public void CreateFromConfig_codex_skips_api_key_resolution()
    {
        var config = new LlmConfig(
            Provider: "codex",
            Model: "gpt-5-codex",
            ApiKey: "$MISSING_PROVIDER_KEY",
            Endpoint: "ws://localhost:8080");

        var client = ChatClientFactory.CreateFromConfig(config);

        Assert.IsType<CodexChatClient>(client);
    }

    [Fact]
    public void CreateFromConfig_codex_uses_round_robin_when_multiple_endpoints_are_configured()
    {
        var config = new LlmConfig(
            Provider: "codex",
            Model: "gpt-5-codex",
            Endpoints: ["ws://localhost:8080", "ws://localhost:8081"]);

        var client = ChatClientFactory.CreateFromConfig(config);

        Assert.IsType<RoundRobinChatClient>(client);
    }

    [Fact]
    public void CreateFromConfig_claude_code_returns_dedicated_claude_code_chat_client()
    {
        var config = new LlmConfig(
            Provider: "claude-code",
            Model: "claude-sonnet-4-5",
            ApiKey: "$MISSING_PROVIDER_KEY");

        var client = ChatClientFactory.CreateFromConfig(config, solutionDirectory: "/repo/solution");

        Assert.IsType<ClaudeCodeChatClient>(client);
    }

    [Fact]
    public async Task CreateFromConfig_claude_code_uses_round_robin_when_managed_pool_has_multiple_lanes()
    {
        var config = new LlmConfig(
            Provider: "claude-code",
            Model: "claude-sonnet-4-5");
        await using var pool = await ClaudeCodeProcessPool.StartAsync(2, serena: null, CancellationToken.None);

        var client = ChatClientFactory.CreateFromConfig(
            config,
            solutionDirectory: "/repo/solution",
            claudeProcessPool: pool);

        var roundRobin = Assert.IsType<RoundRobinChatClient>(client);
        var laneClients = GetRoundRobinClients(roundRobin);

        Assert.Equal(2, laneClients.Length);
        Assert.All(laneClients, laneClient => Assert.IsType<ClaudeCodeChatClient>(laneClient));
        Assert.All(laneClients, laneClient => Assert.Null(GetClaudeMcpConfigPath((ClaudeCodeChatClient)laneClient)));
    }

    [Fact]
    public async Task CreateFromConfig_claude_code_single_managed_lane_carries_mcp_config_path()
    {
        var config = new LlmConfig(
            Provider: "claude-code",
            Model: "claude-sonnet-4-5");
        var serenaCommandPath = CreateTempCommandFile();

        try
        {
            await using var pool = await ClaudeCodeProcessPool.StartAsync(
                1,
                new SerenaMcpConfig(Enabled: true, Command: serenaCommandPath, Args: ["start-mcp-server"]),
                CancellationToken.None);

            var client = ChatClientFactory.CreateFromConfig(
                config,
                solutionDirectory: "/repo/solution",
                claudeProcessPool: pool);

            var claudeClient = Assert.IsType<ClaudeCodeChatClient>(client);
            Assert.Equal(pool.Lanes[0].McpConfigPath, GetClaudeMcpConfigPath(claudeClient));
        }
        finally
        {
            File.Delete(serenaCommandPath);
        }
    }

    [Fact]
    public void CreateFromConfig_unknown_provider_without_endpoint_lists_known_providers()
    {
        var config = new LlmConfig(
            Provider: "custom-provider",
            Model: "custom-model");

        var ex = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.CreateFromConfig(config));

        Assert.Contains("Known providers", ex.Message);
        Assert.Contains("claude-code", ex.Message);
    }

    private static IChatClient[] GetRoundRobinClients(RoundRobinChatClient client)
    {
        var field = typeof(RoundRobinChatClient).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<IChatClient[]>(field!.GetValue(client));
    }

    private static string? GetClaudeMcpConfigPath(ClaudeCodeChatClient client)
    {
        var field = typeof(ClaudeCodeChatClient).GetField("_mcpConfigPath", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (string?)field!.GetValue(client);
    }

    private static string CreateTempCommandFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serena-command-{Guid.NewGuid():N}");
        File.WriteAllText(path, "echo serena");
        return path;
    }
}
