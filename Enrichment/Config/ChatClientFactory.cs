using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using System.ClientModel;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Creates an IChatClient from LLM configuration.
/// Provider-specific code is isolated here; the enricher only knows IChatClient.
/// Known providers (anthropic, openai, ollama) have convenience factories.
/// Unknown providers fall back to OpenAI-compatible endpoint if configured.
/// </summary>
public static class ChatClientFactory
{
    private static readonly Dictionary<string, Func<LlmConfig, string, IChatClient>> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = CreateAnthropicClient,
        ["openai"] = CreateOpenAIClient,
        ["ollama"] = CreateOllamaClient,
        ["codex"] = CreateCodexClient
    };

    /// <summary>
    /// Creates an IChatClient from the given configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the API key env var was not resolved, or when an unknown provider
    /// has no endpoint configured.
    /// </exception>
    public static IChatClient CreateFromConfig(
        LlmConfig config,
        Action<Uri, string>? codexEndpointUnavailable = null,
        string? solutionDirectory = null)
    {
        var apiKey = ResolveApiKey(config);

        if (config.Provider.Equals("codex", StringComparison.OrdinalIgnoreCase))
            return CreateCodexClient(config, apiKey, codexEndpointUnavailable, solutionDirectory);

        if (KnownProviders.TryGetValue(config.Provider, out var factory))
        {
            return factory(config, apiKey);
        }

        // Unknown provider: try OpenAI-compatible endpoint fallback
        if (!string.IsNullOrEmpty(config.Endpoint))
        {
            return CreateOpenAICompatibleClient(config, apiKey);
        }

        throw new InvalidOperationException(
            $"Unknown LLM provider '{config.Provider}'. " +
            $"Known providers: {string.Join(", ", KnownProviders.Keys)}. " +
            "For other MEAI-compatible providers, set 'endpoint' to an OpenAI-compatible API URL.");
    }

    private static string ResolveApiKey(LlmConfig config)
    {
        if (config.ApiKey is { } key && key.StartsWith('$'))
        {
            throw new InvalidOperationException(
                $"Environment variable '{key.Substring(1)}' is not set. " +
                "Set it before running, or update the API key in your config file.");
        }

        return config.ApiKey ?? "";
    }

    private static IChatClient CreateAnthropicClient(LlmConfig config, string apiKey)
    {
        var client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
        return client.AsIChatClient(config.Model);
    }

    private static IChatClient CreateOpenAIClient(LlmConfig config, string apiKey)
    {
        var client = new OpenAIClient(apiKey);
        return client.GetChatClient(config.Model).AsIChatClient();
    }

    private static IChatClient CreateOllamaClient(LlmConfig config, string _)
    {
        var endpoint = config.Endpoint ?? "http://localhost:11434";
        return new OllamaApiClient(new Uri(endpoint), config.Model);
    }

    private static IChatClient CreateOpenAICompatibleClient(LlmConfig config, string apiKey)
    {
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint!) };
        var client = new OpenAIClient(credential, options);
        return client.GetChatClient(config.Model).AsIChatClient();
    }

    private static IChatClient CreateCodexClient(LlmConfig config, string _) =>
        CreateCodexClient(config, _, null, null);

    private static IChatClient CreateCodexClient(
        LlmConfig config,
        string _,
        Action<Uri, string>? onEndpointUnavailable,
        string? cwd)
    {
        var endpointUris = ResolveCodexEndpoints(config)
            .Select(endpoint => new Uri(endpoint))
            .ToArray();

        if (endpointUris.Length == 1)
        {
            return new CodexChatClient(
                endpointUris[0],
                config.Model,
                traceWs: config.TraceCodexWs,
                onEndpointUnavailable: onEndpointUnavailable,
                reasoningEffort: config.ReasoningEffort,
                serviceTier: config.ServiceTier,
                cwd: cwd);
        }

        var pooledClients = endpointUris
            .Select(uri => (IChatClient)new CodexChatClient(
                uri,
                config.Model,
                traceWs: config.TraceCodexWs,
                onEndpointUnavailable: onEndpointUnavailable,
                reasoningEffort: config.ReasoningEffort,
                serviceTier: config.ServiceTier,
                cwd: cwd))
            .ToArray();
        return new RoundRobinChatClient(pooledClients);
    }

    private static IReadOnlyList<string> ResolveCodexEndpoints(LlmConfig config)
    {
        var endpoints = new List<string>();

        if (config.Endpoints is { Length: > 0 })
        {
            foreach (var endpoint in config.Endpoints)
            {
                var trimmed = endpoint?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    endpoints.Add(trimmed);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Endpoint))
            endpoints.Add(config.Endpoint.Trim());

        if (endpoints.Count == 0)
            endpoints.Add("ws://localhost:8080");

        var deduped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            if (seen.Add(endpoint))
                deduped.Add(endpoint);
        }

        foreach (var endpoint in deduped)
        {
            if (!endpoint.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Codex endpoint must use ws:// or wss:// scheme, got: '{endpoint}'");
            }
        }

        return deduped;
    }
}
