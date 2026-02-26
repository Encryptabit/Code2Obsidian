namespace Code2Obsidian.Cli;

/// <summary>
/// Strongly-typed CLI options record, created AFTER input resolution.
/// SolutionPath is the resolved absolute path to the .sln file.
/// OutputDirectory is the resolved absolute path to the output vault folder.
/// FanInThreshold is the fan-in count at which methods get danger-tagged (default 10).
/// ComplexityThreshold is the cyclomatic complexity at which methods get danger-tagged (default 15).
/// </summary>
public sealed record CliOptions(
    string SolutionPath,
    string OutputDirectory,
    int FanInThreshold = 10,
    int ComplexityThreshold = 15);
