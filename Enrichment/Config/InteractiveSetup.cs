using Spectre.Console;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Interactive setup wizard for LLM configuration.
/// Uses Spectre.Console prompts to walk users through provider selection and config creation.
/// Returns null in non-interactive environments (CI, piped input).
/// </summary>
public static class InteractiveSetup
{
    /// <summary>
    /// Runs the interactive setup wizard and saves the config to the specified path.
    /// Returns the created config, or null if the terminal is non-interactive.
    /// </summary>
    public static LlmConfig? RunSetup(string configPath)
    {
        if (Console.IsInputRedirected || !Environment.UserInteractive)
        {
            return null;
        }

        AnsiConsole.MarkupLine("[bold blue]LLM Enrichment Setup[/]");
        AnsiConsole.MarkupLine("No LLM configuration found. Let's set one up.");
        AnsiConsole.MarkupLine($"[grey]This wizard writes {Markup.Escape(configPath)} (usually code2obsidian.llm.json next to your .sln).[/]\n");

        // Provider selection
        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select an LLM [green]provider[/]:")
                .AddChoices(LlmProviderCatalog.KnownProviderIds));
        var providerInfo = LlmProviderCatalog.Get(provider);

        if (providerInfo.Id.Equals("claude-code", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(LlmProviderGuidance.GetAuthSummary(providerInfo))}[/]");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(LlmProviderGuidance.GetEndpointUsageSummary(providerInfo))}[/]");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(LlmProviderGuidance.GetUnsupportedBackendSummary(providerInfo))}[/]");
            AnsiConsole.MarkupLine("[grey]Run `claude auth login` before your first enriched run, then verify with `claude auth status`.[/]");
            AnsiConsole.MarkupLine("[grey]Managed Claude lanes can be added with --pool-size, and Serena uses Claude's native MCP config.[/]");
            if (providerInfo.ConflictingApiAuthEnvVars.Length > 0)
            {
                var authEnvVars = string.Join(", ", providerInfo.ConflictingApiAuthEnvVars.Select(envVar => $"${envVar}"));
                AnsiConsole.MarkupLine($"[grey]Detected possible API-auth override env vars: {Markup.Escape(authEnvVars)}.[/]");
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(LlmProviderGuidance.GetAuthPrecedenceRemediation(providerInfo))}[/]");
            }
        }

        // Model name
        var model = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter [green]model name[/]:")
                .DefaultValue(providerInfo.DefaultModel)
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(model))
            model = providerInfo.DefaultModel;

        // Provider-specific prompts
        string? apiKey = null;
        string? endpoint = null;
        string[]? endpoints = null;
        SerenaMcpConfig? serena = null;

        if (providerInfo.RequiresApiKey)
        {
            var defaultEnvVar = providerInfo.DefaultApiKeyEnvVar ?? "";
            var keyInput = AnsiConsole.Prompt(
                new TextPrompt<string>($"Enter [green]API key[/] or env var name (e.g. {Markup.Escape(defaultEnvVar)}):")
                    .DefaultValue(defaultEnvVar)
                    .AllowEmpty());

            apiKey = string.IsNullOrWhiteSpace(keyInput) ? defaultEnvVar : keyInput;
        }
        else if (providerInfo.UsesEndpoint)
        {
            if (providerInfo.SupportsEndpointPooling)
            {
                var endpointInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("Enter [green]Codex endpoint(s)[/] (comma-separated):")
                        .DefaultValue(providerInfo.DefaultEndpoint ?? "ws://localhost:8080")
                        .AllowEmpty());

                var splitEndpoints = endpointInput
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (splitEndpoints.Length == 0)
                {
                    endpoint = providerInfo.DefaultEndpoint ?? "ws://localhost:8080";
                }
                else
                {
                    endpoint = splitEndpoints[0];
                }

                endpoints = splitEndpoints.Length > 1 ? splitEndpoints : null;
            }
            else
            {
                var endpointLabel = providerInfo.Id.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                    ? "Ollama endpoint"
                    : $"{providerInfo.Id} endpoint";
                endpoint = AnsiConsole.Prompt(
                    new TextPrompt<string>($"Enter [green]{Markup.Escape(endpointLabel)}[/]:")
                        .DefaultValue(providerInfo.DefaultEndpoint ?? string.Empty)
                        .AllowEmpty());

                if (string.IsNullOrWhiteSpace(endpoint))
                    endpoint = providerInfo.DefaultEndpoint;
            }
        }

        if (providerInfo.SupportsSerena)
        {
            var enableSerena = AnsiConsole.Confirm(
                "Enable [green]Serena MCP[/] for symbol lookup? Requires a working `uvx`/Serena setup.",
                defaultValue: false);

            if (enableSerena)
                serena = new SerenaMcpConfig(Enabled: true);
        }

        // Max concurrency
        var maxConcurrency = AnsiConsole.Prompt(
            new TextPrompt<int>("Enter [green]max concurrency[/] (parallel LLM requests):")
                .DefaultValue(3));

        var config = new LlmConfig(
            Provider: provider,
            Model: model,
            ApiKey: apiKey,
            Endpoint: endpoint,
            MaxConcurrency: maxConcurrency,
            Endpoints: endpoints,
            Serena: serena
        );

        LlmConfigLoader.Save(configPath, config);
        AnsiConsole.MarkupLine($"\n[green]Config saved to[/] {Markup.Escape(configPath)}");

        return config;
    }
}
