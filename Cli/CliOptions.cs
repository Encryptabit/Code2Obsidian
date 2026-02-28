namespace Code2Obsidian.Cli;

/// <summary>
/// Strongly-typed CLI options record, created AFTER input resolution.
/// SolutionPath is the resolved absolute path to the .sln file.
/// OutputDirectory is the resolved absolute path to the output vault folder.
/// FanInThreshold is the fan-in count at which methods get danger-tagged (default 10).
/// ComplexityThreshold is the cyclomatic complexity at which methods get danger-tagged (default 15).
/// Incremental enables incremental mode (default): only regenerate notes for changed files.
/// FullRebuild forces state wipe and full analysis even when state exists.
/// DryRun shows what would be regenerated without writing files.
/// Enrich enables LLM-powered plain-English summaries for methods and classes.
/// Suggestions enables LLM-powered optimization/refactor suggestions.
/// LlmProvider overrides the LLM provider from the config file (anthropic, openai, ollama, codex).
/// LlmModel overrides the LLM model name from the config file.
/// LlmApiKey overrides the LLM API key from the config file (or $ENV_VAR reference).
/// LlmEndpoint overrides the LLM endpoint URL from the config file.
/// CodexWslDistro selects the WSL distro to launch Codex app-server instances in (Windows only).
/// PoolSize spawns that many local Codex app-server instances (codex provider only).
/// TraceCodexWs enables Codex websocket frame tracing to stderr.
/// </summary>
public sealed record CliOptions(
    string SolutionPath,
    string OutputDirectory,
    int FanInThreshold = 10,
    int ComplexityThreshold = 15,
    bool Incremental = true,
    bool FullRebuild = false,
    bool DryRun = false,
    bool Enrich = false,
    bool Suggestions = false,
    string? LlmProvider = null,
    string? LlmModel = null,
    string? LlmApiKey = null,
    string? LlmEndpoint = null,
    string? CodexWslDistro = null,
    int? PoolSize = null,
    bool TraceCodexWs = false);
