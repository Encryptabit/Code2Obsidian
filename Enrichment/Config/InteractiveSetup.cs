using Spectre.Console;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Interactive setup wizard for LLM configuration.
/// Uses Spectre.Console prompts to walk users through provider selection and config creation.
/// Returns null in non-interactive environments (CI, piped input).
/// </summary>
public static class InteractiveSetup
{
    private static readonly Dictionary<string, string> DefaultModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = "claude-haiku-4-5",
        ["openai"] = "gpt-4o-mini",
        ["ollama"] = "llama3.1",
        ["codex"] = "gpt-5-codex"
    };

    private static readonly Dictionary<string, string> DefaultEnvVars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = "$ANTHROPIC_API_KEY",
        ["openai"] = "$OPENAI_API_KEY"
    };

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
        AnsiConsole.MarkupLine("No LLM configuration found. Let's set one up.\n");

        // Provider selection
        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select an LLM [green]provider[/]:")
                .AddChoices("anthropic", "openai", "ollama", "codex"));

        // Model name
        var defaultModel = DefaultModels.GetValueOrDefault(provider, "");
        var model = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter [green]model name[/]:")
                .DefaultValue(defaultModel)
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(model))
            model = defaultModel;

        // Provider-specific prompts
        string? apiKey = null;
        string? endpoint = null;
        string[]? endpoints = null;

        if (provider is "anthropic" or "openai")
        {
            var defaultEnvVar = DefaultEnvVars.GetValueOrDefault(provider, "");
            var keyInput = AnsiConsole.Prompt(
                new TextPrompt<string>($"Enter [green]API key[/] or env var name (e.g. {Markup.Escape(defaultEnvVar)}):")
                    .DefaultValue(defaultEnvVar)
                    .AllowEmpty());

            apiKey = string.IsNullOrWhiteSpace(keyInput) ? defaultEnvVar : keyInput;
        }
        else if (provider == "ollama")
        {
            endpoint = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]Ollama endpoint[/]:")
                    .DefaultValue("http://localhost:11434")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(endpoint))
                endpoint = "http://localhost:11434";
        }
        else if (provider == "codex")
        {
            var endpointInput = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]Codex endpoint(s)[/] (comma-separated):")
                    .DefaultValue("ws://localhost:8080")
                    .AllowEmpty());

            var splitEndpoints = endpointInput
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (splitEndpoints.Length == 0)
            {
                endpoint = "ws://localhost:8080";
            }
            else
            {
                endpoint = splitEndpoints[0];
            }

            endpoints = splitEndpoints.Length > 1 ? splitEndpoints : null;
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
            Endpoints: endpoints
        );

        LlmConfigLoader.Save(configPath, config);
        AnsiConsole.MarkupLine($"\n[green]Config saved to[/] {Markup.Escape(configPath)}");

        return config;
    }
}
