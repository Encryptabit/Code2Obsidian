using System.Text;
using System.Text.RegularExpressions;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Keeps the latest log line per Codex endpoint for live UI rendering.
/// </summary>
public static class CodexLogBoard
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly TimeSpan NotificationThrottle = TimeSpan.FromMilliseconds(200);
    private const int MaxStoredMessageChars = 280;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTimeOffset Timestamp, string Message)> LatestByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> EndpointOrder = new();
    private static event Action? Updated;
    private static DateTimeOffset _lastNotificationUtc = DateTimeOffset.MinValue;
    private static bool _notificationScheduled;
    private static Timer? _notificationTimer;

    public static void Reset()
    {
        lock (Gate)
        {
            LatestByEndpoint.Clear();
            EndpointOrder.Clear();
            _lastNotificationUtc = DateTimeOffset.MinValue;
            _notificationScheduled = false;
            _notificationTimer?.Dispose();
            _notificationTimer = null;
        }

        NotifyUpdated();
    }

    public static void ConfigureInstances(IEnumerable<string> endpoints)
    {
        var normalized = endpoints
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(NormalizeEndpoint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (Gate)
        {
            LatestByEndpoint.Clear();
            EndpointOrder.Clear();
            foreach (var endpoint in normalized)
            {
                EndpointOrder.Add(endpoint);
                LatestByEndpoint[endpoint] = (DateTimeOffset.MinValue, "(idle)");
            }
        }

        NotifyUpdated();
    }

    public static void Report(string endpoint, string message)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(message))
            return;

        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        var normalizedMessage = NormalizeMessage(message);

        lock (Gate)
        {
            if (!LatestByEndpoint.ContainsKey(normalizedEndpoint))
                EndpointOrder.Add(normalizedEndpoint);
            LatestByEndpoint[normalizedEndpoint] = (DateTimeOffset.Now, normalizedMessage);
        }

        NotifyUpdated();
    }

    public static IReadOnlyList<(string Endpoint, DateTimeOffset Timestamp, string Message)> Snapshot()
    {
        lock (Gate)
        {
            var result = new List<(string Endpoint, DateTimeOffset Timestamp, string Message)>(EndpointOrder.Count);
            foreach (var endpoint in EndpointOrder)
            {
                var entry = LatestByEndpoint.TryGetValue(endpoint, out var value)
                    ? value
                    : (Timestamp: DateTimeOffset.MinValue, Message: "(idle)");
                result.Add((endpoint, entry.Timestamp, entry.Message));
            }
            return result;
        }
    }

    public static IDisposable Subscribe(Action onUpdated)
    {
        lock (Gate)
        {
            Updated += onUpdated;
        }

        return new Subscription(onUpdated);
    }

    private static void Unsubscribe(Action onUpdated)
    {
        lock (Gate)
        {
            Updated -= onUpdated;
        }
    }

    private static void NotifyUpdated()
    {
        Action? handlers = null;
        TimeSpan scheduleAfter = TimeSpan.Zero;
        var shouldSchedule = false;

        lock (Gate)
        {
            if (Updated is null)
                return;

            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _lastNotificationUtc;

            if (elapsed >= NotificationThrottle)
            {
                _lastNotificationUtc = now;
                _notificationScheduled = false;
                _notificationTimer?.Dispose();
                _notificationTimer = null;
                handlers = Updated;
            }
            else if (!_notificationScheduled)
            {
                _notificationScheduled = true;
                shouldSchedule = true;
                scheduleAfter = NotificationThrottle - elapsed;
            }
        }

        handlers?.Invoke();

        if (shouldSchedule)
            ScheduleDeferredNotification(scheduleAfter);
    }

    private static void ScheduleDeferredNotification(TimeSpan delay)
    {
        lock (Gate)
        {
            _notificationTimer?.Dispose();
            _notificationTimer = new Timer(
                _ => FlushDeferredNotification(),
                null,
                delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private static void FlushDeferredNotification()
    {
        Action? handlers;
        lock (Gate)
        {
            _notificationScheduled = false;
            _lastNotificationUtc = DateTimeOffset.UtcNow;
            handlers = Updated;
        }

        handlers?.Invoke();
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        return endpoint.Trim().TrimEnd('/');
    }

    private static string NormalizeMessage(string message)
    {
        var withoutAnsi = AnsiEscapeRegex.Replace(message, string.Empty);
        var sb = new StringBuilder(withoutAnsi.Length);
        var previousWhitespace = false;

        foreach (var ch in withoutAnsi)
        {
            if (char.IsControl(ch) && !char.IsWhiteSpace(ch))
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (previousWhitespace)
                    continue;

                sb.Append(' ');
                previousWhitespace = true;
                continue;
            }

            sb.Append(ch);
            previousWhitespace = false;
        }

        var normalized = sb.ToString().Trim();
        if (normalized.Length == 0)
            return "(empty)";

        if (normalized.Length <= MaxStoredMessageChars)
            return normalized;

        return normalized[..MaxStoredMessageChars] + "...";
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _callback;
        private int _disposed;

        public Subscription(Action callback)
        {
            _callback = callback;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Unsubscribe(_callback);
        }
    }
}
