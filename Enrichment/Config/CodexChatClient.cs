using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// IChatClient implementation that speaks the Codex JSON-RPC-over-WebSocket protocol.
/// Starts a fresh Codex thread per turn, and can optionally recycle the websocket
/// session between turns when connection churn is acceptable.
/// File change approvals are always denied, and shell command approvals can be
/// disabled to enforce Serena-first enrichment.
/// </summary>
public sealed class CodexChatClient : IChatClient, IDisposable
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);
    private static readonly int TraceMaxChars = ReadPositiveIntEnv("CODEX_TRACE_WS_MAX_CHARS", 220);
    private static readonly bool TraceShowAll =
        string.Equals(Environment.GetEnvironmentVariable("CODEX_TRACE_WS_SHOW_ALL"), "1", StringComparison.Ordinal);
    private static readonly HashSet<string> TraceImportantMethods = new(StringComparer.Ordinal)
    {
        "initialize",
        "initialized",
        "thread/start",
        "turn/start",
        "turn/started",
        "turn/completed",
        "turn/failed",
        "item/agentMessage/delta",
        "thread/tokenUsage/updated",
        "item/commandExecution/requestApproval",
        "item/fileChange/requestApproval",
        "codex/event/agent_reasoning",
        "codex/event/agent_message",
        "codex/event/task_started",
        "codex/event/task_complete",
        "codex/event/task_error",
        "codex/event/exec_command_begin",
        "codex/event/exec_command_end",
        "codex/event/exec_output"
    };
    private static readonly HashSet<string> TraceImportantItemTypes = new(StringComparer.Ordinal)
    {
        "reasoning",
        "agentMessage",
        "commandExecution"
    };

    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _reasoningEffort;
    private readonly string? _serviceTier;
    private readonly string? _cwd;
    private readonly bool _traceWs;
    private readonly bool _allowCommandExecution;
    private readonly bool _resetConnectionAfterTurn;
    private readonly Func<Uri, string, CancellationToken, Task>? _onEndpointRecycleRequested;
    private readonly int _restartEndpointAfterTurns;
    private readonly Action<Uri, string>? _onEndpointUnavailable;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private ClientWebSocket? _ws;
    private int _nextId = 1;
    private int _completedTurns;
    private bool _disposed;
    private bool _initialized;

    public CodexChatClient(
        Uri endpoint,
        string model,
        bool traceWs = false,
        Action<Uri, string>? onEndpointUnavailable = null,
        string? reasoningEffort = null,
        string? serviceTier = null,
        string? cwd = null,
        bool allowCommandExecution = true,
        bool resetConnectionAfterTurn = false,
        Func<Uri, string, CancellationToken, Task>? onEndpointRecycleRequested = null,
        int restartEndpointAfterTurns = 0)
    {
        _endpoint = endpoint;
        _model = model;
        _reasoningEffort = reasoningEffort;
        _serviceTier = serviceTier;
        _cwd = cwd;
        _traceWs = traceWs;
        _allowCommandExecution = allowCommandExecution;
        _resetConnectionAfterTurn = resetConnectionAfterTurn;
        _onEndpointRecycleRequested = onEndpointRecycleRequested;
        _restartEndpointAfterTurns = Math.Max(0, restartEndpointAfterTurns);
        _onEndpointUnavailable = onEndpointUnavailable;
    }

    public ChatClientMetadata Metadata => new("codex", _endpoint, _model);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _turnLock.WaitAsync(cancellationToken);
        var turnSucceeded = false;
        try
        {
            var response = await GetResponseInternalAsync(chatMessages, cancellationToken);
            turnSucceeded = true;
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportEndpointUnavailable(ex);
            throw;
        }
        finally
        {
            var endpointRecycled = false;
            if (turnSucceeded && !_disposed)
                endpointRecycled = await MaybeRecycleEndpointAfterTurnAsync(cancellationToken);

            if (_resetConnectionAfterTurn && !_disposed && !endpointRecycled)
                ResetConnection();
            _turnLock.Release();
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming not supported by CodexChatClient. Use GetResponseAsync.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(CodexChatClient))
            return this;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetConnection();
        _turnLock.Dispose();
    }

    // ---- Private protocol helpers ----

    private async Task<ChatResponse> GetResponseInternalAsync(
        IEnumerable<ChatMessage> chatMessages,
        CancellationToken cancellationToken)
    {
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(TurnTimeout);
        var startupToken = startupCts.Token;

        try
        {
            await EnsureConnectedAsync(startupToken);

            // Build input as array of content objects per Codex protocol
            var inputArray = BuildInputArray(chatMessages);
            var threadId = await StartThreadAsync(startupToken);

            // Send turn/start (thread is fresh for this request)
            var turnId = NextId();
            var turnParams = new JsonObject
            {
                ["threadId"] = threadId,
                ["input"] = inputArray
            };
            if (!string.IsNullOrWhiteSpace(_reasoningEffort))
                turnParams["effort"] = _reasoningEffort;
            await SendAsync(new JsonObject
            {
                ["id"] = turnId,
                ["method"] = "turn/start",
                ["params"] = turnParams
            }, startupToken);

            // Collect response
            var textBuilder = new StringBuilder();
            long? inputTokens = null;
            long? outputTokens = null;
            var turnCompleted = false;

            while (!turnCompleted)
            {
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveCts.CancelAfter(TurnTimeout);
                var msg = await ReceiveAsync(receiveCts.Token);
                if (msg is null)
                    throw new InvalidOperationException("WebSocket closed before turn/completed.");

                // Handle JSON-RPC error responses (e.g. invalid model/thread)
                if (msg["id"] is JsonNode responseId &&
                    responseId.GetValue<int>() == turnId &&
                    msg["error"] is JsonNode errorNode)
                {
                    var code = errorNode["code"]?.GetValue<int>() ?? -1;
                    var message = errorNode["message"]?.GetValue<string>() ?? "Unknown error";
                    throw new InvalidOperationException(
                        $"Codex turn/start failed (code {code}): {message}");
                }

                var method = msg["method"]?.GetValue<string>();

                switch (method)
                {
                    case "item/agentMessage/delta":
                        var delta = msg["params"]?["delta"]?.GetValue<string>();
                        if (delta is not null)
                            textBuilder.Append(delta);
                        break;

                    case "thread/tokenUsage/updated":
                        var usage = msg["params"];
                        inputTokens = usage?["inputTokens"]?.GetValue<long>();
                        outputTokens = usage?["outputTokens"]?.GetValue<long>();
                        break;

                    case "item/commandExecution/requestApproval":
                        var commandApprovalId = msg["id"];
                        if (commandApprovalId is not null)
                        {
                            using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            replyCts.CancelAfter(TurnTimeout);
                            await SendAsync(new JsonObject
                            {
                                ["id"] = commandApprovalId.DeepClone(),
                                ["result"] = new JsonObject
                                {
                                    ["approved"] = _allowCommandExecution,
                                    ["reason"] = _allowCommandExecution
                                        ? "Code2Obsidian enrichment allows read-only code lookup"
                                        : "Code2Obsidian Serena enrichment forbids shell command lookup; use Serena tools instead"
                                }
                            }, replyCts.Token);
                        }
                        break;

                    case "item/fileChange/requestApproval":
                        var fileChangeApprovalId = msg["id"];
                        if (fileChangeApprovalId is not null)
                        {
                            using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            replyCts.CancelAfter(TurnTimeout);
                            await SendAsync(new JsonObject
                            {
                                ["id"] = fileChangeApprovalId.DeepClone(),
                                ["result"] = new JsonObject
                                {
                                    ["approved"] = false,
                                    ["reason"] = "Code2Obsidian enrichment is analysis-only"
                                }
                            }, replyCts.Token);
                        }
                        break;

                    case "turn/completed":
                        turnCompleted = true;
                        break;

                    default:
                        // Ignore unknown notifications (item/agentMessage/start, etc.)
                        break;
                }
            }

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, textBuilder.ToString()));

            if (inputTokens.HasValue || outputTokens.HasValue)
            {
                response.Usage = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens
                };
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Codex endpoint produced no activity for {TurnTimeout.TotalMinutes:0} minutes.");
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ws is { State: WebSocketState.Open } && _initialized)
            return;

        // Close any previous failed connection before reconnect
        ResetConnection();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(_endpoint, ct);

        try
        {
            // 1. Send initialize with clientInfo object
            var initId = NextId();
            await SendAsync(new JsonObject
            {
                ["id"] = initId,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "code2obsidian",
                        ["title"] = "Code2Obsidian",
                        ["version"] = "1.0"
                    }
                }
            }, ct);

            await WaitForResponseAsync(initId, ct);

            // 2. Send initialized notification (no id)
            await SendAsync(new JsonObject
            {
                ["method"] = "initialized"
            }, ct);
            _initialized = true;
        }
        catch
        {
            ResetConnection();
            throw;
        }
    }

    private async Task<string> StartThreadAsync(CancellationToken ct)
    {
        var threadStartId = NextId();
        var threadParams = new JsonObject
        {
            ["model"] = _model,
            ["approvalPolicy"] = "never"
        };
        if (!string.IsNullOrWhiteSpace(_serviceTier))
            threadParams["serviceTier"] = _serviceTier;
        if (!string.IsNullOrWhiteSpace(_cwd))
            threadParams["cwd"] = _cwd;
        await SendAsync(new JsonObject
        {
            ["id"] = threadStartId,
            ["method"] = "thread/start",
            ["params"] = threadParams
        }, ct);

        var threadResponse = await WaitForResponseAsync(threadStartId, ct);
        return threadResponse?["result"]?["thread"]?["id"]?.GetValue<string>()
               ?? throw new InvalidOperationException(
                   "Codex did not return a thread id in result.thread.id.");
    }

    private async Task<JsonObject?> WaitForResponseAsync(int expectedId, CancellationToken ct)
    {
        while (true)
        {
            var msg = await ReceiveAsync(ct);
            if (msg is null)
                throw new InvalidOperationException("WebSocket closed while waiting for response.");

            // Match by id
            if (msg["id"] is JsonNode idNode && idNode.GetValue<int>() == expectedId)
            {
                // Surface JSON-RPC error payloads
                if (msg["error"] is JsonNode errorNode)
                {
                    var code = errorNode["code"]?.GetValue<int>() ?? -1;
                    var message = errorNode["message"]?.GetValue<string>() ?? "Unknown error";
                    throw new InvalidOperationException(
                        $"Codex RPC error (code {code}): {message}");
                }
                return msg;
            }

            // Discard notifications during handshake
        }
    }

    private void ResetConnection()
    {
        try
        {
            _ws?.Abort();
        }
        catch
        {
            // best-effort shutdown
        }

        _ws?.Dispose();
        _ws = null;
        _initialized = false;
    }

    private int NextId() => Interlocked.Increment(ref _nextId);

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        if (_traceWs)
            TraceFrame("C->S", bytes.Length, message, json);
    }

    private async Task<JsonObject?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new ArraySegment<byte>(new byte[8192]);
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws!.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer.Array!, buffer.Offset, result.Count);
        }
        while (!result.EndOfMessage);

        var sizeBytes = (int)ms.Length;
        var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, sizeBytes);
        var payload = JsonSerializer.Deserialize<JsonObject>(json);
        if (_traceWs)
            TraceFrame("S->C", sizeBytes, payload, json);
        return payload;
    }

    private static JsonArray BuildInputArray(IEnumerable<ChatMessage> messages)
    {
        // Codex turn/start expects input as an array of content objects:
        // [{ "type": "text", "text": "..." }]
        // Flatten system + user messages into a single text block
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role == ChatRole.System ? "System"
                     : msg.Role == ChatRole.User ? "User"
                     : "Assistant";
            sb.AppendLine($"[{role}]");
            sb.AppendLine(msg.Text);
            sb.AppendLine();
        }

        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = sb.ToString().TrimEnd()
            }
        };
    }

    private void TraceFrame(string direction, int sizeBytes, JsonObject? payload, string raw)
    {
        if (!ShouldTraceFrame(payload, raw))
            return;

        var summary = SummarizeFrame(payload, raw);
        CodexLogBoard.Report(_endpoint.ToString(), $"{direction} {sizeBytes}b {TruncateForTrace(summary)}");
    }

    private static bool ShouldTraceFrame(JsonObject? payload, string raw)
    {
        if (TraceShowAll)
            return true;

        if (payload is null)
            return raw.Contains("error", StringComparison.OrdinalIgnoreCase);

        if (payload["error"] is not null)
            return true;

        var method = payload["method"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(method) && TraceImportantMethods.Contains(method))
            return true;

        if (method is "item/started" or "item/completed")
        {
            var itemType = payload["params"]?["item"]?["type"]?.GetValue<string>();
            return itemType is not null && TraceImportantItemTypes.Contains(itemType);
        }

        return false;
    }

    private static string SummarizeFrame(JsonObject? payload, string raw)
    {
        if (payload is null)
            return raw;

        var method = payload["method"]?.GetValue<string>() ?? "";
        var parameters = payload["params"];

        if (method == "item/agentMessage/delta")
        {
            var delta = parameters?["delta"]?.GetValue<string>() ?? "";
            return $"{method}: {delta}";
        }

        if (method == "codex/event/agent_reasoning")
        {
            var text = parameters?["msg"]?["text"]?.GetValue<string>() ?? "";
            return $"{method}: {text}";
        }

        if (method == "codex/event/agent_message")
        {
            var phase = parameters?["msg"]?["phase"]?.GetValue<string>() ?? "";
            var message = parameters?["msg"]?["message"]?.GetValue<string>() ?? "";
            return $"{method}[{phase}]: {message}";
        }

        if (method == "codex/event/task_started")
        {
            var turnId = parameters?["msg"]?["turn_id"]?.GetValue<string>() ?? "";
            return $"{method}: turn_id={turnId}";
        }

        if (method == "codex/event/task_complete")
        {
            var turnId = parameters?["msg"]?["turn_id"]?.GetValue<string>() ?? "";
            return $"{method}: turn_id={turnId}";
        }

        if (method == "codex/event/task_error")
        {
            var msg = parameters?["msg"]?.ToJsonString() ?? "";
            return $"{method}: {msg}";
        }

        if (method == "thread/tokenUsage/updated")
        {
            var inputTokens = parameters?["inputTokens"]?.GetValue<long?>() ?? 0;
            var outputTokens = parameters?["outputTokens"]?.GetValue<long?>() ?? 0;
            return $"{method}: inputTokens={inputTokens}, outputTokens={outputTokens}";
        }

        if (method is "item/started" or "item/completed")
        {
            var item = parameters?["item"];
            var itemType = item?["type"]?.GetValue<string>() ?? "?";
            if (itemType == "reasoning")
            {
                var summary = item?["summary"]?.ToJsonString() ?? "[]";
                return $"{method}[reasoning]: {summary}";
            }

            if (itemType == "agentMessage")
            {
                var phase = item?["phase"]?.GetValue<string>() ?? "";
                var text = item?["text"]?.GetValue<string>() ?? "";
                return $"{method}[agentMessage:{phase}]: {text}";
            }

            if (itemType == "commandExecution")
            {
                var status = item?["status"]?.GetValue<string>() ?? "";
                var command = item?["command"]?.GetValue<string>() ?? "";
                return $"{method}[commandExecution:{status}]: {command}";
            }

            return $"{method}[{itemType}]";
        }

        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
        {
            var id = payload["id"]?.ToJsonString() ?? "null";
            return $"{method}: id={id}";
        }

        if (!string.IsNullOrWhiteSpace(method))
            return $"{method}: {raw}";

        if (payload["id"] is not null && payload["error"] is not null)
            return $"rpc/error id={payload["id"]}: {payload["error"]}";

        if (payload["id"] is not null && payload["result"] is not null)
            return $"rpc/result id={payload["id"]}: {payload["result"]}";

        return raw;
    }

    private static string TruncateForTrace(string text)
    {
        var oneLine = text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        if (oneLine.Length <= TraceMaxChars)
            return oneLine;
        return oneLine[..TraceMaxChars] + "...(truncated)";
    }

    private static int ReadPositiveIntEnv(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private async Task<bool> MaybeRecycleEndpointAfterTurnAsync(CancellationToken cancellationToken)
    {
        if (_restartEndpointAfterTurns <= 0 || _onEndpointRecycleRequested is null)
            return false;

        var completedTurns = Interlocked.Increment(ref _completedTurns);
        if (completedTurns % _restartEndpointAfterTurns != 0)
            return false;

        ResetConnection();
        CodexLogBoard.Report(
            _endpoint.ToString(),
            $"recycling endpoint after {completedTurns} successful turn(s)");

        try
        {
            using var recycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recycleCts.CancelAfter(TimeSpan.FromSeconds(30));
            await _onEndpointRecycleRequested(
                _endpoint,
                $"successful turn budget reached ({_restartEndpointAfterTurns})",
                recycleCts.Token);
            CodexLogBoard.Report(_endpoint.ToString(), "endpoint recycle complete");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CodexLogBoard.Report(_endpoint.ToString(), "endpoint recycle timed out");
        }
        catch (Exception ex)
        {
            CodexLogBoard.Report(_endpoint.ToString(), $"endpoint recycle failed: {ex.Message}");
        }

        return true;
    }

    private void ReportEndpointUnavailable(Exception ex)
    {
        var reason = ClassifyEndpointFailure(ex);
        if (reason is null)
            return;

        var endpointText = _endpoint.ToString();
        try
        {
            CodexLogBoard.Report(endpointText, $"endpoint unavailable ({reason}): {ex.Message}");
            _onEndpointUnavailable?.Invoke(_endpoint, $"{reason}: {ex.Message}");
        }
        catch
        {
            // best-effort reporting
        }
    }

    private static string? ClassifyEndpointFailure(Exception ex)
    {
        if (ex is TimeoutException)
            return "unresponsive";

        if (ex is WebSocketException)
            return "offline";

        if (ex is InvalidOperationException invalid &&
            invalid.Message.Contains("WebSocket closed", StringComparison.OrdinalIgnoreCase))
        {
            return "offline";
        }

        return null;
    }
}
