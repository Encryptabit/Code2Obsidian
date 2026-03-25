using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// IChatClient implementation that executes one Claude Code CLI print-mode turn.
/// This transport is intentionally isolated from the Codex websocket path.
/// </summary>
public sealed class ClaudeCodeChatClient : IChatClient, IDisposable
{
    private const int DiagnosticMaxChars = 280;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    private static readonly Regex EnvAssignmentSecretPattern = new(
        @"(?<name>\b[A-Z0-9_]*(?:TOKEN|KEY|SECRET|PASSWORD|AUTH)[A-Z0-9_]*\b)\s*=\s*(?<value>[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JsonSecretPattern = new(
        "\"(?<name>[A-Za-z0-9_]*(?:token|key|secret|password|auth)[A-Za-z0-9_]*)\"\\s*:\\s*\"(?<value>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] LoginRequiredIndicators =
    [
        "claude auth login",
        "not logged in",
        "login required",
        "log in",
        "no active session",
        "authenticate with claude"
    ];
    private static readonly string[] SessionStatusIndicators =
    [
        "claude auth status",
        "session expired",
        "session invalid",
        "invalid session",
        "expired session",
        "session has expired"
    ];
    private static readonly string[] TokenSetupIndicators =
    [
        "claude setup-token",
        "setup-token",
        "api key",
        "api-key",
        "anthropic_api_key",
        "claude_api_key",
        "access token",
        "bearer token",
        "token auth",
        "token-based auth"
    ];

    private readonly string _model;
    private readonly string _workingDirectory;
    private readonly ICommandRunner _commandRunner;
    private readonly TimeSpan _timeout;
    private readonly string? _mcpConfigPath;

    public ClaudeCodeChatClient(
        string model,
        string? workingDirectory = null,
        ICommandRunner? commandRunner = null,
        TimeSpan? timeout = null,
        string? mcpConfigPath = null)
    {
        _model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("Claude Code model is required.", nameof(model))
            : model;
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workingDirectory);
        _commandRunner = commandRunner ?? ProcessCommandRunner.Instance;
        _timeout = timeout is { } value && value > TimeSpan.Zero
            ? value
            : DefaultTimeout;
        _mcpConfigPath = string.IsNullOrWhiteSpace(mcpConfigPath)
            ? null
            : mcpConfigPath.Trim();

        if (mcpConfigPath is not null && _mcpConfigPath is null)
        {
            throw new ArgumentException(
                "Claude MCP config path cannot be blank when Serena is enabled.",
                nameof(mcpConfigPath));
        }
    }

    public ChatClientMetadata Metadata => new("claude-code", null, _model);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(chatMessages, ChatRole.System, includeRoleHeaders: false);
        var userPrompt = BuildPrompt(chatMessages, ChatRole.System, includeRoleHeaders: true, includeMatchingRole: false);
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            throw new InvalidOperationException(
                "Claude Code CLI requires at least one non-system chat message to execute a turn.");
        }

        var request = new CommandRunnerRequest(
            FileName: "claude",
            Arguments: BuildArguments(userPrompt, prompt),
            WorkingDirectory: _workingDirectory,
            Timeout: _timeout);

        CommandRunnerResult result;
        try
        {
            result = await _commandRunner.RunAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            throw CreateMissingExecutableException(ex);
        }
        catch (Win32Exception ex) when (IsMissingExecutable(ex))
        {
            throw CreateMissingExecutableException(ex);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Claude Code CLI failed to start: {SanitizeDiagnosticText(ex.Message)}",
                ex);
        }

        if (result.TimedOut)
        {
            var stderrSnippet = BuildDiagnosticSuffix(result.StandardError, result.StandardOutput);
            throw new TimeoutException(
                $"Claude Code CLI timed out after {_timeout.TotalSeconds:0} seconds while waiting for a response.{stderrSnippet}");
        }

        if (result.ExitCode != 0)
        {
            throw CreateNonZeroExitException(result);
        }

        return ParseJsonResponse(result.StandardOutput, result.StandardError);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming not supported by ClaudeCodeChatClient. Use GetResponseAsync.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ClaudeCodeChatClient))
            return this;

        return null;
    }

    public void Dispose()
    {
        // No persistent resources to dispose. Each CLI invocation is per-turn.
    }

    private ChatResponse ParseJsonResponse(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                $"Claude Code CLI returned no JSON output.{BuildDiagnosticSuffix(stderr, null)}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            var outputSnippet = BuildOutputSnippet(stdout);
            var diagnosticSuffix = BuildDiagnosticSuffix(stderr, null);
            throw new InvalidOperationException(
                $"Claude Code CLI returned malformed JSON output.{diagnosticSuffix} Output snippet: {outputSnippet}",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryReadString(root, "result", out var assistantText) || string.IsNullOrWhiteSpace(assistantText))
            {
                throw new InvalidOperationException(
                    "Claude Code CLI JSON output did not contain a non-empty 'result' field.");
            }

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, assistantText));
            var usage = TryReadUsage(root);
            if (usage is not null)
                response.Usage = usage;

            return response;
        }
    }

    private IReadOnlyList<string> BuildArguments(string userPrompt, string? systemPrompt)
    {
        var arguments = new List<string>
        {
            "-p",
            userPrompt,
            "--output-format",
            "json",
            "--model",
            _model
        };

        if (!string.IsNullOrWhiteSpace(_mcpConfigPath))
        {
            arguments.Add("--mcp-config");
            arguments.Add(_mcpConfigPath);
            arguments.Add("--strict-mcp-config");
        }

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(systemPrompt);
        }

        return arguments;
    }

    private static string BuildPrompt(
        IEnumerable<ChatMessage> chatMessages,
        ChatRole filteredRole,
        bool includeRoleHeaders,
        bool includeMatchingRole = true)
    {
        var relevantMessages = chatMessages
            .Where(message => includeMatchingRole
                ? message.Role == filteredRole
                : message.Role != filteredRole)
            .ToArray();

        if (relevantMessages.Length == 0)
            return string.Empty;

        if (relevantMessages.Length == 1 && relevantMessages[0].Role == ChatRole.User)
            return relevantMessages[0].Text ?? string.Empty;

        if (!includeRoleHeaders && relevantMessages.All(message => message.Role == filteredRole))
        {
            return string.Join(
                Environment.NewLine + Environment.NewLine,
                relevantMessages.Select(message => message.Text));
        }

        var builder = new StringBuilder();
        foreach (var message in relevantMessages)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append('[')
                .Append(FormatRole(message.Role))
                .AppendLine("]")
                .AppendLine(message.Text ?? string.Empty);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatRole(ChatRole role)
    {
        if (role == ChatRole.System)
            return "System";
        if (role == ChatRole.Assistant)
            return "Assistant";
        if (role == ChatRole.User)
            return "User";

        return string.IsNullOrWhiteSpace(role.Value) ? "Unknown" : role.Value;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString();
        return true;
    }

    private static UsageDetails? TryReadUsage(JsonElement root)
    {
        if (!TryGetProperty(root, "usage", out var usageElement))
        {
            if (!TryGetProperty(root, "metadata", out var metadataElement) ||
                !TryGetProperty(metadataElement, "usage", out usageElement))
            {
                return null;
            }
        }

        var inputTokens = TryReadInt64(usageElement, "input_tokens")
            ?? TryReadInt64(usageElement, "inputTokens");
        var outputTokens = TryReadInt64(usageElement, "output_tokens")
            ?? TryReadInt64(usageElement, "outputTokens");

        if (!inputTokens.HasValue && !outputTokens.HasValue)
            return null;

        return new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens
        };
    }

    private static long? TryReadInt64(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) ||
                    property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static InvalidOperationException CreateMissingExecutableException(Exception innerException)
    {
        return new InvalidOperationException(
            "Claude Code CLI executable 'claude' was not found on PATH. Install Claude Code and ensure the 'claude' command is available before using provider 'claude-code'.",
            innerException);
    }

    private static InvalidOperationException CreateNonZeroExitException(CommandRunnerResult result)
    {
        var authFailureMessage = BuildAuthFailureMessage(
            result.ExitCode,
            result.StandardError,
            result.StandardOutput);
        if (authFailureMessage is not null)
            return new InvalidOperationException(authFailureMessage);

        var details = BuildDiagnosticSuffix(result.StandardError, result.StandardOutput);
        return new InvalidOperationException(
            $"Claude Code CLI exited with code {result.ExitCode}.{details}");
    }

    private static string? BuildAuthFailureMessage(int exitCode, string? stderr, string? stdout)
    {
        var failureKind = ClassifyAuthFailure(stderr, stdout);
        if (!failureKind.HasValue)
            return null;

        var diagnosticSuffix = BuildDiagnosticSuffix(stderr, stdout);
        return failureKind.Value switch
        {
            ClaudeAuthFailureKind.LoginRequired =>
                $"Claude Code CLI authentication failed (exit code {exitCode}). No local Claude login session is available. Run `claude auth login`, then `claude auth status` to confirm the session.{diagnosticSuffix}",
            ClaudeAuthFailureKind.SessionStatusRequired =>
                $"Claude Code CLI authentication failed (exit code {exitCode}). The local Claude auth session appears invalid or expired. Run `claude auth status` to inspect the session, then `claude auth login` if it needs to be refreshed.{diagnosticSuffix}",
            ClaudeAuthFailureKind.TokenSetupRequired =>
                $"Claude Code CLI authentication failed (exit code {exitCode}). Claude reported token-based auth is missing or invalid. Run `claude setup-token` to configure token auth for this machine, then rerun the command.{diagnosticSuffix}",
            _ => null
        };
    }

    private static ClaudeAuthFailureKind? ClassifyAuthFailure(string? stderr, string? stdout)
    {
        var diagnosticText = SanitizeDiagnosticText($"{stderr}\n{stdout}");
        if (string.IsNullOrWhiteSpace(diagnosticText))
            return null;

        if (ContainsAnyIndicator(diagnosticText, TokenSetupIndicators))
            return ClaudeAuthFailureKind.TokenSetupRequired;

        if (ContainsAnyIndicator(diagnosticText, LoginRequiredIndicators))
            return ClaudeAuthFailureKind.LoginRequired;

        if (ContainsAnyIndicator(diagnosticText, SessionStatusIndicators))
            return ClaudeAuthFailureKind.SessionStatusRequired;

        return null;
    }

    private static bool ContainsAnyIndicator(string diagnosticText, IReadOnlyList<string> indicators)
    {
        foreach (var indicator in indicators)
        {
            if (diagnosticText.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private enum ClaudeAuthFailureKind
    {
        LoginRequired,
        SessionStatusRequired,
        TokenSetupRequired
    }

    private static bool IsMissingExecutable(Win32Exception ex)
    {
        return ex.NativeErrorCode == 2 ||
               ex.NativeErrorCode == 3 ||
               ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDiagnosticSuffix(string? stderr, string? stdout)
    {
        var stderrSnippet = SanitizeAndTrim(stderr);
        if (!string.IsNullOrWhiteSpace(stderrSnippet))
            return $" stderr: {stderrSnippet}";

        var stdoutSnippet = SanitizeAndTrim(stdout);
        return string.IsNullOrWhiteSpace(stdoutSnippet) ? string.Empty : $" stdout: {stdoutSnippet}";
    }

    private static string BuildOutputSnippet(string? output)
    {
        var snippet = SanitizeAndTrim(output);
        return string.IsNullOrWhiteSpace(snippet) ? "<empty>" : snippet;
    }

    private static string SanitizeAndTrim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sanitized = SanitizeDiagnosticText(text)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (sanitized.Length <= DiagnosticMaxChars)
            return sanitized;

        return sanitized[..DiagnosticMaxChars] + "...(truncated)";
    }

    private static string SanitizeDiagnosticText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sanitized = text;

        foreach (var environmentVariable in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            if (!LooksSensitive(environmentVariable))
                continue;

            var value = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(value))
                sanitized = sanitized.Replace(value, "<redacted>", StringComparison.Ordinal);
        }

        sanitized = EnvAssignmentSecretPattern.Replace(
            sanitized,
            match => $"{match.Groups["name"].Value}=<redacted>");
        sanitized = JsonSecretPattern.Replace(
            sanitized,
            match => $"\"{match.Groups["name"].Value}\":\"<redacted>\"");

        return sanitized;
    }

    private static bool LooksSensitive(string variableName)
    {
        return variableName.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
               variableName.Contains("KEY", StringComparison.OrdinalIgnoreCase) ||
               variableName.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
               variableName.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
               variableName.Contains("AUTH", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class ProcessCommandRunner : ICommandRunner
    {
        internal static ProcessCommandRunner Instance { get; } = new();

        public async Task<CommandRunnerResult> RunAsync(
            CommandRunnerRequest request,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = BuildStartInfo(request)
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitForExitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(request.Timeout, cancellationToken);

            try
            {
                var completedTask = await Task.WhenAny(waitForExitTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    TryKill(process);
                    await process.WaitForExitAsync(CancellationToken.None);
                    return new CommandRunnerResult(
                        ExitCode: process.HasExited ? process.ExitCode : -1,
                        StandardOutput: await stdoutTask,
                        StandardError: await stderrTask,
                        TimedOut: true);
                }

                await waitForExitTask;
                return new CommandRunnerResult(
                    ExitCode: process.ExitCode,
                    StandardOutput: await stdoutTask,
                    StandardError: await stderrTask);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }
        }

        private static ProcessStartInfo BuildStartInfo(CommandRunnerRequest request)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);

            return startInfo;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}

public sealed record CommandRunnerRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout);

public sealed record CommandRunnerResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false);

public interface ICommandRunner
{
    Task<CommandRunnerResult> RunAsync(CommandRunnerRequest request, CancellationToken cancellationToken);
}
