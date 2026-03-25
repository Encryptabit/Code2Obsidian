using Code2Obsidian.Enrichment.Config;

namespace Code2Obsidian.Tests.Enrichment.Config;

public sealed class LlmProviderGuidanceTests
{
    [Fact]
    public void ClaudeCode_guidance_describes_cli_auth_and_unsupported_backend()
    {
        var provider = LlmProviderCatalog.Get("claude-code");

        var authSummary = LlmProviderGuidance.GetAuthSummary(provider);
        var endpointSummary = LlmProviderGuidance.GetEndpointUsageSummary(provider);
        var backendSummary = LlmProviderGuidance.GetUnsupportedBackendSummary(provider);

        Assert.Contains("local `claude` CLI auth session", authSummary);
        Assert.Contains("does not use endpoint-style configuration", endpointSummary);
        Assert.Contains("Use the local `claude` CLI auth session instead.", endpointSummary);
        Assert.Contains("claude mcp serve", backendSummary);
        Assert.Contains("not a supported model backend", backendSummary);
    }

    [Fact]
    public void ClaudeCode_endpoint_misuse_names_sources_without_echoing_values()
    {
        var provider = LlmProviderCatalog.Get("claude-code");

        var message = LlmProviderGuidance.GetEndpointMisuseError(
            provider,
            configHasEndpoint: true,
            configHasEndpoints: true,
            cliEndpointOverrideProvided: true);

        Assert.NotNull(message);
        Assert.Contains("'endpoint'", message);
        Assert.Contains("'endpoints'", message);
        Assert.Contains("--llm-endpoint", message);
        Assert.Contains("local `claude` CLI auth session", message);
        Assert.DoesNotContain("http://", message);
        Assert.DoesNotContain("ws://", message);
    }

    [Fact]
    public void Endpoint_misuse_validation_skips_endpoint_backed_unknown_and_non_cli_auth_providers()
    {
        var codex = LlmProviderCatalog.Get("codex");
        var openAi = LlmProviderCatalog.Get("openai");

        Assert.Null(LlmProviderGuidance.GetEndpointMisuseError(codex, true, true, true));
        Assert.Null(LlmProviderGuidance.GetEndpointMisuseError(openAi, true, true, true));
        Assert.Null(LlmProviderGuidance.GetEndpointMisuseError(null, true, true, true));
    }

    [Fact]
    public void ClaudeCode_auth_precedence_warning_lists_sources_only()
    {
        var provider = LlmProviderCatalog.Get("claude-code");

        var warning = LlmProviderGuidance.GetAuthPrecedenceWarning(
            provider,
            ["config/--llm-api-key", "$ANTHROPIC_API_KEY", "$CLAUDE_API_KEY"]);
        var remediation = LlmProviderGuidance.GetAuthPrecedenceRemediation(provider);

        Assert.Contains("Provider 'claude-code' uses the local `claude` CLI auth session.", warning);
        Assert.Contains("config/--llm-api-key", warning);
        Assert.Contains("$ANTHROPIC_API_KEY", warning);
        Assert.Contains("$CLAUDE_API_KEY", warning);
        Assert.Contains("logged-in subscription session", remediation);
    }
}
