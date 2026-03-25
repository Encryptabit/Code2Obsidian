namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Shared provider-facing guidance derived from <see cref="LlmProviderCatalog"/> facts.
/// Keep runtime/setup/help copy anchored here instead of scattering provider-name branches.
/// </summary>
public static class LlmProviderGuidance
{
    public static string GetAuthSummary(LlmProviderCapabilities providerCapabilities)
    {
        ArgumentNullException.ThrowIfNull(providerCapabilities);

        if (providerCapabilities.UsesLocalCliAuthSession)
            return $"Provider '{providerCapabilities.Id}' uses the local `claude` CLI auth session.";

        if (providerCapabilities.RequiresApiKey)
            return $"Provider '{providerCapabilities.Id}' uses API-key authentication.";

        if (providerCapabilities.UsesEndpoint)
            return $"Provider '{providerCapabilities.Id}' uses endpoint-style configuration.";

        return $"Provider '{providerCapabilities.Id}' uses provider-specific local configuration.";
    }

    public static string GetEndpointUsageSummary(LlmProviderCapabilities providerCapabilities)
    {
        ArgumentNullException.ThrowIfNull(providerCapabilities);

        if (providerCapabilities.UsesEndpoint)
        {
            var supportedSources = providerCapabilities.SupportsEndpointPooling
                ? "'endpoint', 'endpoints', or --llm-endpoint"
                : "'endpoint' or --llm-endpoint";
            return $"Provider '{providerCapabilities.Id}' accepts endpoint-style configuration via {supportedSources}.";
        }

        if (providerCapabilities.UsesLocalCliAuthSession)
            return $"Provider '{providerCapabilities.Id}' does not use endpoint-style configuration. Use the local `claude` CLI auth session instead.";

        return $"Provider '{providerCapabilities.Id}' does not use endpoint-style configuration.";
    }

    public static string GetUnsupportedBackendSummary(LlmProviderCapabilities providerCapabilities)
    {
        ArgumentNullException.ThrowIfNull(providerCapabilities);

        return providerCapabilities.UsesLocalCliAuthSession
            ? $"`claude mcp serve` is an MCP tool transport, not a supported model backend for provider '{providerCapabilities.Id}'."
            : $"Provider '{providerCapabilities.Id}' must be configured through its supported auth and transport path.";
    }

    public static string GetAuthPrecedenceWarning(
        LlmProviderCapabilities providerCapabilities,
        IReadOnlyList<string> authSources)
    {
        ArgumentNullException.ThrowIfNull(providerCapabilities);
        ArgumentNullException.ThrowIfNull(authSources);

        if (authSources.Count == 0)
            throw new ArgumentException("At least one auth source is required.", nameof(authSources));

        return $"{GetAuthSummary(providerCapabilities)} Detected API-auth setting(s): {string.Join(", ", authSources)}.";
    }

    public static string GetAuthPrecedenceRemediation(LlmProviderCapabilities providerCapabilities)
    {
        ArgumentNullException.ThrowIfNull(providerCapabilities);

        return providerCapabilities.UsesLocalCliAuthSession
            ? $"{providerCapabilities.Id} may prefer API-key auth over a logged-in subscription session. Remove those API key settings if you intend to use CLI login-based auth."
            : $"Review the configured auth sources for provider '{providerCapabilities.Id}'.";
    }

    public static string[] GetEndpointConfigSources(
        bool configHasEndpoint,
        bool configHasEndpoints,
        bool cliEndpointOverrideProvided)
    {
        var sources = new List<string>(3);

        if (configHasEndpoint)
            sources.Add("'endpoint'");
        if (configHasEndpoints)
            sources.Add("'endpoints'");
        if (cliEndpointOverrideProvided)
            sources.Add("--llm-endpoint");

        return sources.ToArray();
    }

    public static string? GetEndpointMisuseError(
        LlmProviderCapabilities? providerCapabilities,
        bool configHasEndpoint,
        bool configHasEndpoints,
        bool cliEndpointOverrideProvided)
    {
        if (providerCapabilities is null)
            return null;

        var sources = GetEndpointConfigSources(
            configHasEndpoint,
            configHasEndpoints,
            cliEndpointOverrideProvided);

        if (providerCapabilities.UsesEndpoint || !providerCapabilities.UsesLocalCliAuthSession || sources.Length == 0)
            return null;

        var message = $"Provider '{providerCapabilities.Id}' does not use endpoint-style configuration. Remove {FormatList(sources)}.";
        if (providerCapabilities.UsesLocalCliAuthSession)
            message += " Use the local `claude` CLI auth session instead.";

        return message;
    }

    private static string FormatList(IReadOnlyList<string> items)
    {
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            2 => $"{items[0]} or {items[1]}",
            _ => string.Join(", ", items.Take(items.Count - 1)) + $", or {items[^1]}"
        };
    }
}
