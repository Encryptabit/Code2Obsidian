using Microsoft.Extensions.AI;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Distributes requests across multiple IChatClient instances using round-robin selection.
/// Used to fan out Codex turns across multiple app-server endpoints.
/// </summary>
public sealed class RoundRobinChatClient : IChatClient, IDisposable, IAsyncDisposable
{
    private readonly IChatClient[] _clients;
    private int _nextIndex = -1;
    private bool _disposed;

    public RoundRobinChatClient(IEnumerable<IChatClient> clients)
    {
        _clients = clients.Where(c => c is not null).ToArray();
        if (_clients.Length == 0)
            throw new ArgumentException("At least one chat client is required.", nameof(clients));
    }

    public ChatClientMetadata Metadata => new("round-robin", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return NextClient().GetResponseAsync(chatMessages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return NextClient().GetStreamingResponseAsync(chatMessages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(RoundRobinChatClient))
            return this;

        foreach (var client in _clients)
        {
            var service = client.GetService(serviceType, serviceKey);
            if (service is not null)
                return service;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in _clients)
        {
            if (client is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in _clients)
        {
            if (client is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (client is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private IChatClient NextClient()
    {
        var index = Interlocked.Increment(ref _nextIndex);
        return _clients[index % _clients.Length];
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RoundRobinChatClient));
    }
}
