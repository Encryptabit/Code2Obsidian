using System.Text;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Manages a bounded set of Claude Code lanes plus any temporary MCP config artifacts
/// needed to run Serena through Claude's native <c>--mcp-config</c> path.
/// </summary>
public sealed class ClaudeCodeProcessPool : IAsyncDisposable, IDisposable
{
    private readonly string? _artifactDirectory;
    private bool _disposed;

    private ClaudeCodeProcessPool(
        IReadOnlyList<ClaudeCodeProcessLane> lanes,
        SerenaMcpConfig? serena,
        string? bootstrapMessage,
        string? artifactDirectory)
    {
        Lanes = lanes;
        Serena = serena;
        BootstrapMessage = bootstrapMessage;
        _artifactDirectory = artifactDirectory;
    }

    public IReadOnlyList<ClaudeCodeProcessLane> Lanes { get; }

    public int LaneCount => Lanes.Count;

    public SerenaMcpConfig? Serena { get; }

    public string? BootstrapMessage { get; }

    public static async Task<ClaudeCodeProcessPool> StartAsync(
        int laneCount,
        SerenaMcpConfig? serena,
        CancellationToken cancellationToken)
    {
        if (laneCount < 1)
            throw new ArgumentOutOfRangeException(nameof(laneCount), "Pool size must be >= 1.");

        var normalizedSerena = SerenaMcpSettings.Normalize(serena);
        string? bootstrapMessage = null;
        string? artifactDirectory = null;

        try
        {
            if (normalizedSerena?.Enabled == true)
            {
                var ensured = await SerenaMcpSettings.EnsureCommandAvailableAsync(
                    normalizedSerena,
                    wslDistro: null,
                    cancellationToken);
                normalizedSerena = ensured.Config;
                bootstrapMessage = ensured.InstalledMessage;
                artifactDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "Code2Obsidian",
                    "claude-pool",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(artifactDirectory);
            }

            var lanes = new List<ClaudeCodeProcessLane>(laneCount);
            for (var index = 0; index < laneCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? mcpConfigPath = null;
                if (normalizedSerena?.Enabled == true)
                {
                    mcpConfigPath = Path.Combine(artifactDirectory!, $"lane-{index + 1:D2}-mcp.json");
                    var mcpConfigJson = SerenaMcpSettings.BuildClaudeMcpConfigJson(normalizedSerena);
                    await File.WriteAllTextAsync(
                        mcpConfigPath,
                        mcpConfigJson,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        cancellationToken);
                }

                lanes.Add(new ClaudeCodeProcessLane(index + 1, mcpConfigPath));
            }

            return new ClaudeCodeProcessPool(lanes, normalizedSerena, bootstrapMessage, artifactDirectory);
        }
        catch (InvalidOperationException ex) when (normalizedSerena?.Enabled == true)
        {
            CleanupArtifacts(artifactDirectory);
            throw new InvalidOperationException(
                $"Claude process pool could not prepare Serena MCP config: {ex.Message}",
                ex);
        }
        catch
        {
            CleanupArtifacts(artifactDirectory);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CleanupArtifacts(_artifactDirectory);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void CleanupArtifacts(string? artifactDirectory)
    {
        if (string.IsNullOrWhiteSpace(artifactDirectory) || !Directory.Exists(artifactDirectory))
            return;

        try
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp Claude MCP config artifacts.
        }
    }
}

public sealed record ClaudeCodeProcessLane(int LaneNumber, string? McpConfigPath);
