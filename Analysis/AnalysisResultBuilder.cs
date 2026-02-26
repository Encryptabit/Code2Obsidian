using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Analysis;

/// <summary>
/// Mutable builder that accumulates methods and call edges during analysis.
/// Thread-unsafe -- intended for single-threaded sequential analyzer execution.
/// </summary>
public sealed class AnalysisResultBuilder
{
    private readonly Dictionary<MethodId, MethodInfo> _methods = new();
    private readonly CallGraph _callGraph = new();
    private int _projectCount;
    private int _fileCount;

    /// <summary>
    /// Adds a discovered method to the result. Duplicate MethodIds are silently ignored.
    /// </summary>
    public void AddMethod(MethodInfo method)
    {
        _methods.TryAdd(method.Id, method);
    }

    /// <summary>
    /// Adds a directed call edge from caller to callee.
    /// </summary>
    public void AddCallEdge(MethodId caller, MethodId callee)
    {
        _callGraph.AddEdge(caller, callee);
    }

    /// <summary>
    /// Increments the count of analyzed projects.
    /// </summary>
    public void IncrementProjectCount()
    {
        _projectCount++;
    }

    /// <summary>
    /// Increments the count of analyzed source files.
    /// </summary>
    public void IncrementFileCount()
    {
        _fileCount++;
    }

    /// <summary>
    /// Builds the immutable AnalysisResult from accumulated data.
    /// </summary>
    public AnalysisResult Build()
    {
        return new AnalysisResult(
            _methods,
            _callGraph,
            _projectCount,
            _fileCount);
    }
}
