namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Central registry for first-class LLM providers and the capability flags that
/// drive setup prompts and chat-client factory behavior.
/// </summary>
public sealed record LlmProviderCapabilities(
    string Id,
    string DefaultModel,
    string? DefaultApiKeyEnvVar,
    string? DefaultEndpoint,
    bool RequiresApiKey,
    bool UsesEndpoint,
    bool UsesLocalCliAuthSession,
    bool SupportsManagedPooling,
    bool SupportsEndpointPooling,
    bool SupportsSerena,
    string[] ConflictingApiAuthEnvVars);

public static class LlmProviderCatalog
{
    private static readonly LlmProviderCapabilities[] Providers =
    [
        new(
            Id: "anthropic",
            DefaultModel: "claude-haiku-4-5",
            DefaultApiKeyEnvVar: "$ANTHROPIC_API_KEY",
            DefaultEndpoint: null,
            RequiresApiKey: true,
            UsesEndpoint: false,
            UsesLocalCliAuthSession: false,
            SupportsManagedPooling: false,
            SupportsEndpointPooling: false,
            SupportsSerena: false,
            ConflictingApiAuthEnvVars: []),
        new(
            Id: "claude-code",
            DefaultModel: "claude-sonnet-4-5",
            DefaultApiKeyEnvVar: null,
            DefaultEndpoint: null,
            RequiresApiKey: false,
            UsesEndpoint: false,
            UsesLocalCliAuthSession: true,
            SupportsManagedPooling: true,
            SupportsEndpointPooling: false,
            SupportsSerena: true,
            ConflictingApiAuthEnvVars: ["ANTHROPIC_API_KEY", "CLAUDE_API_KEY"]),
        new(
            Id: "openai",
            DefaultModel: "gpt-4o-mini",
            DefaultApiKeyEnvVar: "$OPENAI_API_KEY",
            DefaultEndpoint: null,
            RequiresApiKey: true,
            UsesEndpoint: false,
            UsesLocalCliAuthSession: false,
            SupportsManagedPooling: false,
            SupportsEndpointPooling: false,
            SupportsSerena: false,
            ConflictingApiAuthEnvVars: []),
        new(
            Id: "ollama",
            DefaultModel: "llama3.1",
            DefaultApiKeyEnvVar: null,
            DefaultEndpoint: "http://localhost:11434",
            RequiresApiKey: false,
            UsesEndpoint: true,
            UsesLocalCliAuthSession: false,
            SupportsManagedPooling: false,
            SupportsEndpointPooling: false,
            SupportsSerena: false,
            ConflictingApiAuthEnvVars: []),
        new(
            Id: "codex",
            DefaultModel: "gpt-5-codex",
            DefaultApiKeyEnvVar: null,
            DefaultEndpoint: "ws://localhost:8080",
            RequiresApiKey: false,
            UsesEndpoint: true,
            UsesLocalCliAuthSession: false,
            SupportsManagedPooling: true,
            SupportsEndpointPooling: true,
            SupportsSerena: true,
            ConflictingApiAuthEnvVars: [])
    ];

    private static readonly IReadOnlyDictionary<string, LlmProviderCapabilities> ProvidersById =
        Providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<LlmProviderCapabilities> All => Providers;

    public static IReadOnlyList<string> KnownProviderIds { get; } =
        Providers.Select(provider => provider.Id).ToArray();

    public static bool TryGet(string provider, out LlmProviderCapabilities? capabilities)
    {
        return ProvidersById.TryGetValue(provider, out capabilities);
    }

    public static LlmProviderCapabilities Get(string provider)
    {
        if (TryGet(provider, out var capabilities) && capabilities is not null)
            return capabilities;

        throw new InvalidOperationException(
            $"Unknown LLM provider '{provider}'. Known providers: {FormatKnownProviders()}.");
    }

    public static string FormatKnownProviders() => string.Join(", ", KnownProviderIds);
}
