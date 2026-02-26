using System.Text.Json.Serialization;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Strongly-typed configuration for LLM provider settings.
/// Loaded from code2obsidian.llm.json, with CLI flag overrides.
/// </summary>
public sealed record LlmConfig(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("apiKey")] string? ApiKey = null,
    [property: JsonPropertyName("endpoint")] string? Endpoint = null,
    [property: JsonPropertyName("maxConcurrency")] int MaxConcurrency = 3,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens = 300,
    [property: JsonPropertyName("costPerInputToken")] decimal CostPerInputToken = 0m,
    [property: JsonPropertyName("costPerOutputToken")] decimal CostPerOutputToken = 0m
);
