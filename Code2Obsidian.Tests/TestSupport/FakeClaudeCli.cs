using System.Globalization;
using System.Text;

namespace Code2Obsidian.Tests.TestSupport;

public sealed class FakeClaudeCli : IAsyncDisposable
{
    private const string InvocationStartMarker = "-- invocation-start --";
    private const string InvocationEndMarker = "-- invocation-end --";

    private FakeClaudeCli(string rootDirectory, string binDirectory, string transcriptPath)
    {
        RootDirectory = rootDirectory;
        BinDirectory = binDirectory;
        TranscriptPath = transcriptPath;
    }

    public string RootDirectory { get; }

    public string BinDirectory { get; }

    public string TranscriptPath { get; }

    public static async Task<FakeClaudeCli> CreateSuccessAsync(
        string assistantXml,
        string? stderr = null,
        int exitCode = 0,
        long inputTokens = 17,
        long outputTokens = 9,
        int invocationDelayMs = 0)
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "Code2Obsidian.Tests",
            $"fake-claude-{Guid.NewGuid():N}");
        var binDirectory = Path.Combine(rootDirectory, "bin");
        var transcriptPath = Path.Combine(rootDirectory, "claude-transcript.log");
        Directory.CreateDirectory(binDirectory);

        var stdoutPayload = FakeClaudeJson.Success(assistantXml, inputTokens, outputTokens);
        var fakeCli = new FakeClaudeCli(rootDirectory, binDirectory, transcriptPath);

        await fakeCli.WriteLauncherAsync(stdoutPayload, stderr, exitCode, invocationDelayMs);
        return fakeCli;
    }

    public string PrependToPath(string? existingPath)
    {
        return string.IsNullOrWhiteSpace(existingPath)
            ? BinDirectory
            : $"{BinDirectory}{Path.PathSeparator}{existingPath}";
    }

    public string ReadTranscript()
    {
        return File.Exists(TranscriptPath)
            ? File.ReadAllText(TranscriptPath)
            : string.Empty;
    }

    public IReadOnlyList<FakeClaudeInvocation> ReadInvocations()
    {
        if (!File.Exists(TranscriptPath))
            return [];

        var invocations = new List<FakeClaudeInvocation>();
        InvocationBuilder? current = null;

        foreach (var rawLine in File.ReadLines(TranscriptPath))
        {
            var line = rawLine.TrimEnd();
            if (line == InvocationStartMarker)
            {
                current = new InvocationBuilder();
                continue;
            }

            if (line == InvocationEndMarker)
            {
                if (current is not null)
                    invocations.Add(current.Build());

                current = null;
                continue;
            }

            if (current is null)
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..];
            current.Apply(key, value);
        }

        return invocations;
    }

    public int ReadPeakConcurrency() => ReadInvocations().DefaultIfEmpty().Max(invocation => invocation?.ActiveAtStart ?? 0);

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp test assets.
        }

        return ValueTask.CompletedTask;
    }

    private async Task WriteLauncherAsync(string stdoutPayload, string? stderr, int exitCode, int invocationDelayMs)
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(BinDirectory, "claude.cmd");
            await File.WriteAllTextAsync(scriptPath, BuildWindowsLauncher(stdoutPayload, stderr, exitCode, invocationDelayMs), utf8NoBom);
            return;
        }

        var launcherPath = Path.Combine(BinDirectory, "claude");
        await File.WriteAllTextAsync(launcherPath, BuildPosixLauncher(stdoutPayload, stderr, exitCode, invocationDelayMs), utf8NoBom);
        File.SetUnixFileMode(
            launcherPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private string BuildPosixLauncher(string stdoutPayload, string? stderr, int exitCode, int invocationDelayMs)
    {
        var escapedTranscriptPath = EscapePosixSingleQuotedLiteral(TranscriptPath);
        var escapedStdoutPayload = EscapePosixSingleQuotedLiteral(stdoutPayload);
        var escapedLockDirectory = EscapePosixSingleQuotedLiteral(Path.Combine(RootDirectory, "transcript-lock"));
        var escapedActiveCountPath = EscapePosixSingleQuotedLiteral(Path.Combine(RootDirectory, "active-count"));
        var stderrBlock = string.IsNullOrEmpty(stderr)
            ? string.Empty
            : $"printf '%s\\n' '{EscapePosixSingleQuotedLiteral(stderr)}' >&2\n";
        var delaySeconds = (invocationDelayMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
        var delayBlock = invocationDelayMs > 0
            ? $"sleep {delaySeconds}\n"
            : string.Empty;

        return $"#!/usr/bin/env bash\nset -euo pipefail\ntranscript_path='{escapedTranscriptPath}'\nlock_dir='{escapedLockDirectory}'\nactive_count_path='{escapedActiveCountPath}'\nmcp_prev=''\nacquire_lock() {{\n  while ! mkdir \"$lock_dir\" 2>/dev/null; do\n    sleep 0.01\n  done\n}}\nrelease_lock() {{\n  rmdir \"$lock_dir\" 2>/dev/null || true\n}}\nappend_line() {{\n  printf '%s\\n' \"$1\" >> \"$transcript_path\"\n}}\nactive_at_start='1'\noverlap_observed='false'\nacquire_lock\nactive_count='0'\nif [ -f \"$active_count_path\" ]; then\n  active_count=\"$(cat \"$active_count_path\")\"\nfi\nactive_count=$((active_count + 1))\nprintf '%s' \"$active_count\" > \"$active_count_path\"\nactive_at_start=\"$active_count\"\nif [ \"$active_at_start\" -gt 1 ]; then\n  overlap_observed='true'\nfi\nappend_line '{InvocationStartMarker}'\nappend_line \"cwd=$PWD\"\nappend_line \"pid=$$\"\nappend_line \"active-at-start=$active_at_start\"\nappend_line \"overlap-observed=$overlap_observed\"\nfor arg in \"$@\"; do\n  append_line \"arg=$arg\"\n  if [ \"$mcp_prev\" = '1' ]; then\n    append_line \"mcp-config-path=$arg\"\n    if [ -f \"$arg\" ]; then\n      append_line \"mcp-config-json=$(tr '\\n' ' ' < \"$arg\")\"\n    fi\n    mcp_prev=''\n  elif [ \"$arg\" = '--mcp-config' ]; then\n    mcp_prev='1'\n  fi\ndone\nrelease_lock\n{delayBlock}{stderrBlock}printf '%s' '{escapedStdoutPayload}'\nacquire_lock\nactive_count='0'\nif [ -f \"$active_count_path\" ]; then\n  active_count=\"$(cat \"$active_count_path\")\"\nfi\nif [ \"$active_count\" -gt 0 ]; then\n  active_count=$((active_count - 1))\nfi\nprintf '%s' \"$active_count\" > \"$active_count_path\"\nappend_line \"active-at-end=$active_count\"\nappend_line '{InvocationEndMarker}'\nrelease_lock\nexit {exitCode}\n";
    }

    private string BuildWindowsLauncher(string stdoutPayload, string? stderr, int exitCode, int invocationDelayMs)
    {
        var escapedTranscriptPath = EscapeWindowsBatchLiteral(TranscriptPath);
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("setlocal EnableDelayedExpansion");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo {InvocationStartMarker}");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo cwd=%CD%");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo pid=%RANDOM%");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo active-at-start=1");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo overlap-observed=false");
        builder.AppendLine("set \"MCP_PREV=\"");
        builder.AppendLine(":record_args");
        builder.AppendLine("if \"%~1\"==\"\" goto after_args");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo arg=%~1");
        builder.AppendLine("if defined MCP_PREV (");
        builder.AppendLine($"  >> \"{escapedTranscriptPath}\" echo mcp-config-path=%~1");
        builder.AppendLine("  set \"MCP_PREV=\"");
        builder.AppendLine(") else if /I \"%~1\"==\"--mcp-config\" (");
        builder.AppendLine("  set \"MCP_PREV=1\"");
        builder.AppendLine(")");
        builder.AppendLine("shift");
        builder.AppendLine("goto record_args");
        builder.AppendLine(":after_args");
        if (invocationDelayMs > 0)
            builder.AppendLine($"powershell -NoProfile -Command \"Start-Sleep -Milliseconds {invocationDelayMs}\" >nul 2>&1");
        if (!string.IsNullOrEmpty(stderr))
            builder.AppendLine($">&2 echo {EscapeWindowsBatchLiteral(stderr)}");
        builder.AppendLine($"<nul set /p = {EscapeWindowsBatchLiteral(stdoutPayload)}");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo active-at-end=0");
        builder.AppendLine($">> \"{escapedTranscriptPath}\" echo {InvocationEndMarker}");
        builder.AppendLine($"exit /b {exitCode}");
        return builder.ToString();
    }

    private static string EscapePosixSingleQuotedLiteral(string value)
    {
        return value.Replace("'", "'\"'\"'");
    }

    private static string EscapeWindowsBatchLiteral(string value)
    {
        return value
            .Replace("^", "^^")
            .Replace("&", "^&")
            .Replace("|", "^|")
            .Replace("<", "^<")
            .Replace(">", "^>")
            .Replace("%", "%%");
    }

    public sealed record FakeClaudeInvocation(
        string Cwd,
        IReadOnlyList<string> Arguments,
        string? McpConfigPath,
        string? McpConfigJson,
        int ActiveAtStart,
        int ActiveAtEnd,
        bool OverlapObserved,
        int? ProcessId);

    private sealed class InvocationBuilder
    {
        private readonly List<string> _arguments = [];

        public string Cwd { get; private set; } = string.Empty;

        public string? McpConfigPath { get; private set; }

        public string? McpConfigJson { get; private set; }

        public int ActiveAtStart { get; private set; }

        public int ActiveAtEnd { get; private set; }

        public bool OverlapObserved { get; private set; }

        public int? ProcessId { get; private set; }

        public void Apply(string key, string value)
        {
            switch (key)
            {
                case "cwd":
                    Cwd = value;
                    break;
                case "arg":
                    _arguments.Add(value);
                    break;
                case "mcp-config-path":
                    McpConfigPath = value;
                    break;
                case "mcp-config-json":
                    McpConfigJson = value;
                    break;
                case "active-at-start":
                    if (int.TryParse(value, out var activeAtStart))
                        ActiveAtStart = activeAtStart;
                    break;
                case "active-at-end":
                    if (int.TryParse(value, out var activeAtEnd))
                        ActiveAtEnd = activeAtEnd;
                    break;
                case "overlap-observed":
                    if (bool.TryParse(value, out var overlapObserved))
                        OverlapObserved = overlapObserved;
                    break;
                case "pid":
                    if (int.TryParse(value, out var processId))
                        ProcessId = processId;
                    break;
            }
        }

        public FakeClaudeInvocation Build()
        {
            return new FakeClaudeInvocation(
                Cwd,
                _arguments,
                McpConfigPath,
                McpConfigJson,
                ActiveAtStart,
                ActiveAtEnd,
                OverlapObserved,
                ProcessId);
        }
    }
}
