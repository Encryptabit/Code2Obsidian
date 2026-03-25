using Code2Obsidian.Enrichment.Config;

namespace Code2Obsidian.Tests.Enrichment.Config;

public sealed class LlmProviderCatalogTests
{
    public static TheoryData<string, bool, bool, bool, bool, bool, bool, string, string?, string?> ProviderMatrix => new()
    {
        { "anthropic", true, false, false, false, false, false, "claude-haiku-4-5", "$ANTHROPIC_API_KEY", null },
        { "claude-code", false, false, true, true, false, true, "claude-sonnet-4-5", null, null },
        { "openai", true, false, false, false, false, false, "gpt-4o-mini", "$OPENAI_API_KEY", null },
        { "ollama", false, true, false, false, false, false, "llama3.1", null, "http://localhost:11434" },
        { "codex", false, true, false, true, true, true, "gpt-5-codex", null, "ws://localhost:8080" }
    };

    [Theory]
    [MemberData(nameof(ProviderMatrix))]
    public void Get_returns_expected_capabilities(
        string providerId,
        bool requiresApiKey,
        bool usesEndpoint,
        bool usesLocalCliAuthSession,
        bool supportsManagedPooling,
        bool supportsEndpointPooling,
        bool supportsSerena,
        string defaultModel,
        string? defaultApiKeyEnvVar,
        string? defaultEndpoint)
    {
        var provider = LlmProviderCatalog.Get(providerId);

        Assert.Equal(providerId, provider.Id);
        Assert.Equal(requiresApiKey, provider.RequiresApiKey);
        Assert.Equal(usesEndpoint, provider.UsesEndpoint);
        Assert.Equal(usesLocalCliAuthSession, provider.UsesLocalCliAuthSession);
        Assert.Equal(supportsManagedPooling, provider.SupportsManagedPooling);
        Assert.Equal(supportsEndpointPooling, provider.SupportsEndpointPooling);
        Assert.Equal(supportsSerena, provider.SupportsSerena);
        Assert.Equal(defaultModel, provider.DefaultModel);
        Assert.Equal(defaultApiKeyEnvVar, provider.DefaultApiKeyEnvVar);
        Assert.Equal(defaultEndpoint, provider.DefaultEndpoint);
    }

    [Fact]
    public void ClaudeCode_advertises_managed_pooling_without_endpoint_pooling()
    {
        var provider = LlmProviderCatalog.Get("claude-code");

        Assert.False(provider.UsesEndpoint);
        Assert.True(provider.UsesLocalCliAuthSession);
        Assert.True(provider.SupportsManagedPooling);
        Assert.False(provider.SupportsEndpointPooling);
        Assert.True(provider.SupportsSerena);
    }

    [Fact]
    public void ClaudeCode_declares_conflicting_api_auth_env_vars()
    {
        var provider = LlmProviderCatalog.Get("claude-code");

        Assert.Equal(["ANTHROPIC_API_KEY", "CLAUDE_API_KEY"], provider.ConflictingApiAuthEnvVars);
    }

    [Fact]
    public void Get_throws_for_unknown_provider()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LlmProviderCatalog.Get("mystery-provider"));

        Assert.Contains("Known providers", ex.Message);
        Assert.Contains("claude-code", ex.Message);
    }
}
