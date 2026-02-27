using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Analysis;

/// <summary>
/// Mutable builder that accumulates methods, types, and call edges during analysis.
/// Thread-unsafe -- intended for single-threaded sequential analyzer execution.
/// </summary>
public sealed class AnalysisResultBuilder
{
    private readonly Dictionary<MethodId, MethodInfo> _methods = new();
    private readonly CallGraph _callGraph = new();
    private readonly Dictionary<TypeId, TypeInfo> _types = new();
    private readonly Dictionary<TypeId, List<TypeId>> _implementors = new();
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
    /// Adds a discovered type to the result. Uses TryAdd semantics for partial class dedup.
    /// </summary>
    public void AddType(TypeInfo type)
    {
        _types.TryAdd(type.Id, type);
    }

    /// <summary>
    /// Registers a concrete type as an implementor of an interface.
    /// Used to build the reverse index for interface "Known Implementors" sections.
    /// Deduplicates entries (partial classes may register the same implementor multiple times).
    /// </summary>
    public void RegisterImplementor(TypeId interfaceId, TypeId classId)
    {
        if (!_implementors.TryGetValue(interfaceId, out var list))
        {
            list = new List<TypeId>();
            _implementors[interfaceId] = list;
        }
        if (!list.Contains(classId))
            list.Add(classId);
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
        var implementors = _implementors.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<TypeId>)kvp.Value.AsReadOnly());

        return new AnalysisResult(
            _methods,
            _callGraph,
            _types,
            implementors,
            _projectCount,
            _fileCount);
    }
}
