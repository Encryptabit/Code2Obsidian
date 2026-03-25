namespace ClaudeCodeFixtureSolution;

public sealed class Greeter
{
    public string SayHello(string name)
    {
        var displayName = NormalizeName(name);
        return $"Hello, {displayName}!";
    }

    public string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "friend";

        return name.Trim();
    }
}
