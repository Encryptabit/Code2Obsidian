using System.Text.Json;
using Code2Obsidian.Enrichment.Config;

namespace Code2Obsidian.Tests.Enrichment.Config;

public sealed class ClaudeCodeProcessPoolTests
{
    [Fact]
    public async Task StartAsync_rejects_invalid_pool_size()
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ClaudeCodeProcessPool.StartAsync(0, serena: null, CancellationToken.None));

        Assert.Contains(">= 1", ex.Message);
    }

    [Fact]
    public async Task StartAsync_creates_bounded_lanes_without_mcp_artifacts_when_serena_is_disabled()
    {
        await using var pool = await ClaudeCodeProcessPool.StartAsync(3, serena: null, CancellationToken.None);

        Assert.Equal(3, pool.LaneCount);
        Assert.Equal([1, 2, 3], pool.Lanes.Select(lane => lane.LaneNumber).ToArray());
        Assert.All(pool.Lanes, lane => Assert.Null(lane.McpConfigPath));
    }

    [Fact]
    public async Task StartAsync_creates_temp_mcp_configs_and_removes_them_on_dispose()
    {
        var serenaCommandPath = CreateTempCommandFile();
        string[] configPaths;

        try
        {
            var pool = await ClaudeCodeProcessPool.StartAsync(
                2,
                new SerenaMcpConfig(
                    Enabled: true,
                    Command: serenaCommandPath,
                    Context: "claude-code",
                    Env: new Dictionary<string, string>
                    {
                        ["SERENA_LOG"] = "debug"
                    }),
                CancellationToken.None);

            configPaths = pool.Lanes
                .Select(lane => lane.McpConfigPath)
                .OfType<string>()
                .ToArray();

            Assert.Equal(2, configPaths.Length);
            Assert.All(configPaths, path => Assert.True(File.Exists(path), $"Expected MCP config at {path}"));

            var firstConfigJson = await File.ReadAllTextAsync(configPaths[0]);
            using var document = JsonDocument.Parse(firstConfigJson);
            var serena = document.RootElement.GetProperty("mcpServers").GetProperty("serena");
            Assert.Equal(serenaCommandPath, serena.GetProperty("command").GetString());
            Assert.Equal("debug", serena.GetProperty("env").GetProperty("SERENA_LOG").GetString());

            await pool.DisposeAsync();

            Assert.All(configPaths, path => Assert.False(File.Exists(path), $"Expected disposed MCP config to be deleted: {path}"));
        }
        finally
        {
            File.Delete(serenaCommandPath);
        }
    }

    [Fact]
    public async Task StartAsync_surfaces_host_serena_validation_failures()
    {
        var missingCommandPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-serena-{Guid.NewGuid():N}",
            "serena");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClaudeCodeProcessPool.StartAsync(
                1,
                new SerenaMcpConfig(
                    Enabled: true,
                    Command: missingCommandPath,
                    Args: ["start-mcp-server"]),
                CancellationToken.None));

        Assert.Contains("Claude process pool", ex.Message);
        Assert.Contains("Serena MCP command", ex.Message);
        Assert.Contains("Code2Obsidian process", ex.Message);
        Assert.Contains("serena.command", ex.Message);
    }

    private static string CreateTempCommandFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serena-command-{Guid.NewGuid():N}");
        File.WriteAllText(path, "echo serena");
        return path;
    }
}
