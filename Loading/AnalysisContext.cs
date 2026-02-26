using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Code2Obsidian.Loading;

/// <summary>
/// Immutable container holding the loaded solution and metadata needed for analysis.
/// Owns the MSBuildWorkspace lifetime — dispose after pipeline execution completes.
/// Replaces the loose HashSet&lt;IAssemblySymbol&gt; pattern from the original Program.cs
/// with assembly name strings for more robust cross-compilation comparison.
/// </summary>
public sealed class AnalysisContext : IDisposable
{
    private readonly MSBuildWorkspace _workspace;

    /// <summary>
    /// The loaded Roslyn Solution.
    /// </summary>
    public Solution Solution { get; }

    /// <summary>
    /// Set of assembly names belonging to projects in this solution.
    /// Used by analyzers to determine if a symbol is "user code" vs external dependency.
    /// </summary>
    public IReadOnlySet<string> ProjectAssemblyNames { get; }

    public AnalysisContext(MSBuildWorkspace workspace, Solution solution, IReadOnlySet<string> projectAssemblyNames)
    {
        _workspace = workspace;
        Solution = solution;
        ProjectAssemblyNames = projectAssemblyNames;
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
