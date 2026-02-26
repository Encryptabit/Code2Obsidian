namespace Code2Obsidian.Analysis.Models;

/// <summary>
/// Directed call graph using string-based MethodId edges.
/// Maintains both forward (CallsOut) and reverse (CalledBy) edge maps.
/// </summary>
public sealed class CallGraph
{
    private static readonly IReadOnlySet<MethodId> EmptySet = new EmptyMethodIdSet();

    /// <summary>
    /// Lightweight empty set implementation to avoid allocating HashSet on .NET 8.
    /// </summary>
    private sealed class EmptyMethodIdSet : IReadOnlySet<MethodId>
    {
        public int Count => 0;
        public bool Contains(MethodId item) => false;
        public bool IsProperSubsetOf(IEnumerable<MethodId> other) => other.Any();
        public bool IsProperSupersetOf(IEnumerable<MethodId> other) => false;
        public bool IsSubsetOf(IEnumerable<MethodId> other) => true;
        public bool IsSupersetOf(IEnumerable<MethodId> other) => !other.Any();
        public bool Overlaps(IEnumerable<MethodId> other) => false;
        public bool SetEquals(IEnumerable<MethodId> other) => !other.Any();
        public IEnumerator<MethodId> GetEnumerator() => Enumerable.Empty<MethodId>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private readonly Dictionary<MethodId, HashSet<MethodId>> _callsOut = new();
    private readonly Dictionary<MethodId, HashSet<MethodId>> _calledBy = new();

    /// <summary>
    /// Forward edges: caller -> set of callees.
    /// </summary>
    public IReadOnlyDictionary<MethodId, HashSet<MethodId>> CallsOut => _callsOut;

    /// <summary>
    /// Reverse edges: callee -> set of callers. Computed automatically from AddEdge.
    /// </summary>
    public IReadOnlyDictionary<MethodId, HashSet<MethodId>> CalledBy => _calledBy;

    /// <summary>
    /// Adds a directed edge from caller to callee, maintaining both forward and reverse maps.
    /// </summary>
    public void AddEdge(MethodId caller, MethodId callee)
    {
        if (!_callsOut.TryGetValue(caller, out var callees))
        {
            callees = new HashSet<MethodId>();
            _callsOut[caller] = callees;
        }
        callees.Add(callee);

        if (!_calledBy.TryGetValue(callee, out var callers))
        {
            callers = new HashSet<MethodId>();
            _calledBy[callee] = callers;
        }
        callers.Add(caller);
    }

    /// <summary>
    /// Returns the set of methods called by the given method, or empty set if none.
    /// </summary>
    public IReadOnlySet<MethodId> GetCallees(MethodId method)
    {
        return _callsOut.TryGetValue(method, out var callees)
            ? callees
            : EmptySet;
    }

    /// <summary>
    /// Returns the set of methods that call the given method, or empty set if none.
    /// </summary>
    public IReadOnlySet<MethodId> GetCallers(MethodId method)
    {
        return _calledBy.TryGetValue(method, out var callers)
            ? callers
            : EmptySet;
    }
}
