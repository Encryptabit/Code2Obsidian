using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Analysis;

/// <summary>
/// Immutable result of the analysis pipeline stage.
/// Contains all discovered methods and the call graph with string-based IDs.
/// </summary>
public sealed class AnalysisResult
{
    public IReadOnlyDictionary<MethodId, MethodInfo> Methods { get; }
    public CallGraph CallGraph { get; }
    public int ProjectCount { get; }
    public int FileCount { get; }

    public AnalysisResult(
        IReadOnlyDictionary<MethodId, MethodInfo> methods,
        CallGraph callGraph,
        int projectCount,
        int fileCount)
    {
        Methods = methods;
        CallGraph = callGraph;
        ProjectCount = projectCount;
        FileCount = fileCount;
    }
}
