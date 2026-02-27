using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// IChatClient implementation that speaks the Codex JSON-RPC-over-WebSocket protocol.
/// Maintains one websocket session but starts a fresh Codex thread per turn to keep
/// context bounded, and auto-declines approval requests (we only want text responses).
/// </summary>
public sealed class CodexChatClient : IChatClient, IDisposable
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private ClientWebSocket? _ws;
    private int _nextId = 1;
    private bool _disposed;
    private bool _initialized;

    public CodexChatClient(Uri endpoint, string model)
    {
        _endpoint = endpoint;
        _model = model;
    }

    public ChatClientMetadata Metadata => new("codex", _endpoint, _model);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _turnLock.WaitAsync(cancellationToken);
        try
        {
            Exception? lastFailure = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    return await GetResponseInternalAsync(chatMessages, cancellationToken);
                }
                catch (TimeoutException ex) when (attempt < 2)
                {
                    lastFailure = ex;
                    ResetConnection();
                }
                catch (WebSocketException ex) when (attempt < 2)
                {
                    lastFailure = ex;
                    ResetConnection();
                }
                catch (InvalidOperationException ex) when (attempt < 2)
                {
                    lastFailure = ex;
                    ResetConnection();
                }
            }

            throw lastFailure ?? new InvalidOperationException("Codex turn failed after retry.");
        }
        finally
        {
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TurnTimeout);
        var turnToken = timeoutCts.Token;

        try
        {
            await EnsureConnectedAsync(turnToken);

            // Build input as array of content objects per Codex protocol
            var inputArray = BuildInputArray(chatMessages);
            var threadId = await StartThreadAsync(turnToken);

            // Send turn/start (thread is fresh for this request)
            var turnId = NextId();
            await SendAsync(new JsonObject
            {
                ["id"] = turnId,
                ["method"] = "turn/start",
                ["params"] = new JsonObject
                {
                    ["threadId"] = threadId,
                    ["input"] = inputArray
                }
            }, turnToken);

            // Collect response
            var textBuilder = new StringBuilder();
            long? inputTokens = null;
            long? outputTokens = null;
            var turnCompleted = false;

            while (!turnCompleted)
            {
                var msg = await ReceiveAsync(turnToken);
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
                    case "item/fileChange/requestApproval":
                        // Auto-decline - we only want text
                        var approvalId = msg["id"];
                        if (approvalId is not null)
                        {
                            await SendAsync(new JsonObject
                            {
                                ["id"] = approvalId.DeepClone(),
                                ["result"] = new JsonObject
                                {
                                    ["approved"] = false,
                                    ["reason"] = "Code2Obsidian enrichment - text only"
                                }
                            }, turnToken);
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
            throw new TimeoutException($"Codex turn timed out after {TurnTimeout.TotalMinutes:0} minutes.");
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
        await SendAsync(new JsonObject
        {
            ["id"] = threadStartId,
            ["method"] = "thread/start",
            ["params"] = new JsonObject
            {
                ["model"] = _model,
                ["approvalPolicy"] = "never"
            }
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

        ms.Position = 0;
        return JsonSerializer.Deserialize<JsonObject>(ms);
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
}
