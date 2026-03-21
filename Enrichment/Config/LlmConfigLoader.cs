using System.Text.Json;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Loads and saves LLM configuration from/to a JSON file.
/// Supports environment variable expansion for API keys using $VAR_NAME syntax.
/// </summary>
public static class LlmConfigLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Attempts to load LLM config from the specified path.
    /// Returns null if the file does not exist.
    /// Expands API key values starting with '$' by looking up the environment variable.
    /// </summary>
    public static LlmConfig? TryLoad(string configPath)
    {
        if (!File.Exists(configPath))
            return null;

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<LlmConfig>(json, SerializerOptions);

        if (config is null)
            return null;

        // Expand environment variable reference in ApiKey
        if (config.ApiKey is { } apiKey && apiKey.StartsWith('$'))
        {
            var envVarName = apiKey.Substring(1);
            var envValue = Environment.GetEnvironmentVariable(envVarName);

            if (!string.IsNullOrEmpty(envValue))
            {
                config = config with { ApiKey = envValue };
            }
            // If env var is null/empty, leave the $VAR string as-is.
            // ChatClientFactory will fail with a clear message.
        }

        var normalizedEndpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? null
            : config.Endpoint.Trim();

        string[]? normalizedEndpoints = null;
        if (config.Endpoints is { Length: > 0 })
        {
            normalizedEndpoints = config.Endpoints
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (normalizedEndpoints.Length == 0)
                normalizedEndpoints = null;
        }

        string[]? normalizedExclude = null;
        if (config.Exclude is { Length: > 0 })
        {
            normalizedExclude = config.Exclude
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (normalizedExclude.Length == 0)
                normalizedExclude = null;
        }

        config = config with
        {
            Endpoint = normalizedEndpoint,
            Endpoints = normalizedEndpoints,
            Exclude = normalizedExclude,
            Serena = SerenaMcpSettings.Normalize(config.Serena)
        };

        return config;
    }

    /// <summary>
    /// Serializes and writes the config to disk with indented JSON formatting.
    /// </summary>
    public static void Save(string configPath, LlmConfig config)
    {
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(configPath, json);
    }
}
