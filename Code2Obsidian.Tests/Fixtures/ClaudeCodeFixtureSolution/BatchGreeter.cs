namespace ClaudeCodeFixtureSolution;

public sealed class BatchGreeter
{
    public string BuildStatusMessage(string? name, bool isOnline)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "friend" : name.Trim();
        return isOnline
            ? $"{displayName} is online and ready to chat."
            : $"{displayName} is offline right now.";
    }

    public IReadOnlyList<string> BuildPersonalizedGreetings(IEnumerable<string?> names)
    {
        return names
            .Select(name => string.IsNullOrWhiteSpace(name) ? "Hello, friend!" : $"Hello, {name.Trim()}!")
            .ToArray();
    }

    public string BuildPartingMessage(string? name)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "friend" : name.Trim();
        return $"See you later, {displayName}.";
    }
}
