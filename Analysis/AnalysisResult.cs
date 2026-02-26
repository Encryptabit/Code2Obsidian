using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Analysis;

/// <summary>
/// Immutable result of the analysis pipeline stage.
/// Contains all discovered methods, types, the call graph, and implementor reverse index.
/// </summary>
public sealed class AnalysisResult
{
    public IReadOnlyDictionary<MethodId, MethodInfo> Methods { get; }
    public CallGraph CallGraph { get; }
    public IReadOnlyDictionary<TypeId, TypeInfo> Types { get; }
    public IReadOnlyDictionary<TypeId, IReadOnlyList<TypeId>> Implementors { get; }
    public int ProjectCount { get; }
    public int FileCount { get; }

    public AnalysisResult(
        IReadOnlyDictionary<MethodId, MethodInfo> methods,
        CallGraph callGraph,
        IReadOnlyDictionary<TypeId, TypeInfo> types,
        IReadOnlyDictionary<TypeId, IReadOnlyList<TypeId>> implementors,
        int projectCount,
        int fileCount)
    {
        Methods = methods;
        CallGraph = callGraph;
        Types = types;
        Implementors = implementors;
        ProjectCount = projectCount;
        FileCount = fileCount;
    }
}
