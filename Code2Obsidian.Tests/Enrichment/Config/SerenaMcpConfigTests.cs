using System.Text.Json;
using Code2Obsidian.Enrichment.Config;

namespace Code2Obsidian.Tests.Enrichment.Config;

public sealed class SerenaMcpConfigTests
{
    [Fact]
    public void BuildCodexConfigOverrides_preserves_codex_toml_override_shape()
    {
        var config = new SerenaMcpConfig(
            Enabled: true,
            Command: "/opt/serena/bin/serena",
            Args: ["start-mcp-server", "--context", "codex"],
            Env: new Dictionary<string, string>
            {
                ["SERENA_LOG"] = "warn"
            },
            StartupTimeoutSec: 15,
            ToolTimeoutSec: 45);

        var overrides = SerenaMcpSettings.BuildCodexConfigOverrides(config);

        Assert.Contains("mcp_servers.serena.command=\"/opt/serena/bin/serena\"", overrides);
        Assert.Contains("mcp_servers.serena.args=[\"start-mcp-server\", \"--context\", \"codex\"]", overrides);
        Assert.Contains("mcp_servers.serena.startup_timeout_sec=15", overrides);
        Assert.Contains("mcp_servers.serena.tool_timeout_sec=45", overrides);
        Assert.Contains("mcp_servers.serena.env.SERENA_LOG=\"warn\"", overrides);
        Assert.DoesNotContain(overrides, entry => entry.Contains("mcpServers", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildClaudeMcpConfigJson_serializes_claude_native_mcp_servers_shape()
    {
        var commandPath = CreateTempCommandFile();

        try
        {
            var config = new SerenaMcpConfig(
                Enabled: true,
                Command: commandPath,
                Context: "claude-code",
                Env: new Dictionary<string, string>
                {
                    ["SERENA_LOG"] = "warn"
                });

            var json = SerenaMcpSettings.BuildClaudeMcpConfigJson(config);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("mcpServers", out var mcpServers));
            Assert.False(root.TryGetProperty("mcp_servers", out _));
            Assert.True(mcpServers.TryGetProperty("serena", out var serena));
            Assert.Equal(commandPath, serena.GetProperty("command").GetString());

            var args = serena.GetProperty("args")
                .EnumerateArray()
                .Select(element => element.GetString())
                .ToArray();
            Assert.Contains("start-mcp-server", args);
            Assert.Contains("--context", args);
            Assert.Contains("claude-code", args);
            Assert.Contains("--project-from-cwd", args);

            var env = serena.GetProperty("env");
            Assert.Equal("warn", env.GetProperty("SERENA_LOG").GetString());
        }
        finally
        {
            File.Delete(commandPath);
        }
    }

    [Fact]
    public void BuildClaudeMcpConfigJson_uses_configured_path_environment_for_host_validation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"serena-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var commandPath = Path.Combine(tempDir, "serena");
        File.WriteAllText(commandPath, "echo serena");

        try
        {
            var config = new SerenaMcpConfig(
                Enabled: true,
                Command: "serena",
                Args: ["start-mcp-server"],
                Env: new Dictionary<string, string>
                {
                    ["PATH"] = tempDir,
                    ["SERENA_LOG"] = "debug"
                });

            var json = SerenaMcpSettings.BuildClaudeMcpConfigJson(config);

            using var document = JsonDocument.Parse(json);
            var serena = document.RootElement.GetProperty("mcpServers").GetProperty("serena");
            Assert.Equal("serena", serena.GetProperty("command").GetString());
            Assert.Equal(tempDir, serena.GetProperty("env").GetProperty("PATH").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildClaudeMcpConfigJson_throws_when_command_is_not_host_accessible()
    {
        var missingCommand = Path.Combine(Path.GetTempPath(), $"missing-serena-{Guid.NewGuid():N}", "serena");
        var config = new SerenaMcpConfig(
            Enabled: true,
            Command: missingCommand,
            Args: ["start-mcp-server"]);

        var ex = Assert.Throws<InvalidOperationException>(() => SerenaMcpSettings.BuildClaudeMcpConfigJson(config));

        Assert.Contains("local host", ex.Message);
        Assert.Contains("host PATH", ex.Message);
        Assert.Contains("serena.command", ex.Message);
    }

    private static string CreateTempCommandFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serena-command-{Guid.NewGuid():N}");
        File.WriteAllText(path, "echo serena");
        return path;
    }
}
