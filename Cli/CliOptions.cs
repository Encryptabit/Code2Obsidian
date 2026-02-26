namespace Code2Obsidian.Cli;

/// <summary>
/// Strongly-typed CLI options record, created AFTER input resolution.
/// SolutionPath is the resolved absolute path to the .sln file.
/// OutputDirectory is the resolved absolute path to the output vault folder.
/// </summary>
public sealed record CliOptions(string SolutionPath, string OutputDirectory);
