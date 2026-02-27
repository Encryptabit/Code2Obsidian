using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Incremental;

/// <summary>
/// Combines freshly analyzed data (for changed + ripple files) with stored data
/// (for unchanged files) into a complete AnalysisResult that the emitter can use
/// as if it were a full analysis. The emitter only writes notes for dirty files,
/// but needs the full result for collision detection (wikilinks).
/// </summary>
public sealed class AnalysisResultMerger
{
    /// <summary>
    /// Merges fresh analysis results with stored state for unchanged files.
    /// </summary>
    /// <param name="freshResult">Analysis result from reanalyzed files.</param>
    /// <param name="state">Stored incremental state from previous run.</param>
    /// <param name="reanalyzedFiles">Files that were reanalyzed (changed + ripple).</param>
    /// <returns>Complete AnalysisResult usable by the emitter.</returns>
    public static AnalysisResult Merge(
        AnalysisResult freshResult,
        IncrementalState state,
        IReadOnlySet<string> reanalyzedFiles)
    {
        var methods = MergeMethods(freshResult, state, reanalyzedFiles);
        var types = MergeTypes(freshResult, state, reanalyzedFiles);
        var callGraph = MergeCallGraph(freshResult, state, reanalyzedFiles);
        var implementors = MergeImplementors(freshResult);

        // ProjectCount and FileCount come from the pipeline (full solution), not the merger.
        // Use freshResult values as they reflect the actual solution structure.
        return new AnalysisResult(
            methods,
            callGraph,
            types,
            implementors,
            freshResult.ProjectCount,
            freshResult.FileCount);
    }

    /// <summary>
    /// Merges methods: fresh methods + stored method_index entries for unchanged files.
    /// Stored method_index is lightweight (method_id, containing_type, file_path) but
    /// sufficient for collision detection in the emitter.
    /// </summary>
    private static IReadOnlyDictionary<MethodId, MethodInfo> MergeMethods(
        AnalysisResult freshResult,
        IncrementalState state,
        IReadOnlySet<string> reanalyzedFiles)
    {
        var merged = new Dictionary<MethodId, MethodInfo>();

        // Start with all fresh methods
        foreach (var (methodId, methodInfo) in freshResult.Methods)
            merged[methodId] = methodInfo;

        // Add stored methods for unchanged files (lightweight stubs for collision detection)
        var storedMethodIndex = state.GetMethodIndex();
        foreach (var (methodIdStr, (containingType, filePath)) in storedMethodIndex)
        {
            if (reanalyzedFiles.Contains(filePath))
                continue; // Fresh data takes precedence

            var methodId = new MethodId(methodIdStr);
            if (merged.ContainsKey(methodId))
                continue; // Already have fresh data

            // Extract method name from the method ID string
            // Format: "Namespace.ClassName.MethodName(params)"
            var methodName = ExtractMethodName(methodIdStr);
            var typeId = new TypeId(containingType);

            // Create lightweight stub -- only ContainingTypeName and FilePath matter
            // for collision detection in the emitter
            var stub = new MethodInfo(
                Id: methodId,
                Name: methodName,
                ContainingTypeName: containingType,
                ContainingTypeId: typeId,
                FilePath: filePath,
                DisplaySignature: methodIdStr, // best available from stored data
                DocComment: null,
                Namespace: ExtractNamespace(containingType),
                ProjectName: "",
                AccessModifier: "public",
                CyclomaticComplexity: 0);

            merged[methodId] = stub;
        }

        return merged;
    }

    /// <summary>
    /// Merges types: fresh types + stored type_index entries for unchanged files.
    /// Stored type_index has (type_id, name, full_name, file_path, kind) which is
    /// enough for collision detection.
    /// </summary>
    private static IReadOnlyDictionary<TypeId, TypeInfo> MergeTypes(
        AnalysisResult freshResult,
        IncrementalState state,
        IReadOnlySet<string> reanalyzedFiles)
    {
        var merged = new Dictionary<TypeId, TypeInfo>();

        // Start with all fresh types
        foreach (var (typeId, typeInfo) in freshResult.Types)
            merged[typeId] = typeInfo;

        // Add stored types for unchanged files (lightweight stubs for collision detection)
        var storedTypeIndex = state.GetTypeIndex();
        foreach (var (typeIdStr, (name, fullName, filePath, kind)) in storedTypeIndex)
        {
            if (reanalyzedFiles.Contains(filePath))
                continue; // Fresh data takes precedence

            var typeId = new TypeId(typeIdStr);
            if (merged.ContainsKey(typeId))
                continue; // Already have fresh data

            var typeKind = kind switch
            {
                "Interface" => TypeKindInfo.Interface,
                "Record" => TypeKindInfo.Record,
                "Struct" => TypeKindInfo.Struct,
                _ => TypeKindInfo.Class
            };

            // Create lightweight stub -- Name, FullName, and FilePath matter for collision detection
            var stub = new TypeInfo(
                Id: typeId,
                Name: name,
                FullName: fullName,
                Namespace: ExtractNamespace(fullName),
                Kind: typeKind,
                FilePath: filePath,
                BaseClassFullName: null,
                BaseClassName: null,
                InterfaceFullNames: [],
                InterfaceNames: [],
                Properties: [],
                Fields: [],
                Constructors: [],
                MethodIds: [],
                DocComment: null,
                ProjectName: "",
                AccessModifier: "public");

            merged[typeId] = stub;
        }

        return merged;
    }

    /// <summary>
    /// Merges call graphs: fresh edges + stored edges where BOTH caller and callee
    /// files are NOT in the reanalyzed set. Edges involving reanalyzed files come
    /// from fresh data only.
    /// </summary>
    private static CallGraph MergeCallGraph(
        AnalysisResult freshResult,
        IncrementalState state,
        IReadOnlySet<string> reanalyzedFiles)
    {
        var merged = new CallGraph();

        // Add all fresh call edges
        foreach (var (caller, callees) in freshResult.CallGraph.CallsOut)
        {
            foreach (var callee in callees)
                merged.AddEdge(caller, callee);
        }

        // Add stored edges where BOTH files are NOT reanalyzed
        var storedEdges = state.GetCallEdges();
        foreach (var (callerId, calleeId, callerFile, calleeFile) in storedEdges)
        {
            if (reanalyzedFiles.Contains(callerFile) || reanalyzedFiles.Contains(calleeFile))
                continue; // These edges come from fresh data

            merged.AddEdge(new MethodId(callerId), new MethodId(calleeId));
        }

        return merged;
    }

    /// <summary>
    /// Passes through fresh implementors. Implementor data from unchanged files cannot
    /// be reconstructed from type_references alone (type_references tracks file references,
    /// not interface implementations). The emitter only uses implementors for interface
    /// "Known Implementors" sections, and unchanged interface notes remain valid.
    /// </summary>
    private static IReadOnlyDictionary<TypeId, IReadOnlyList<TypeId>> MergeImplementors(
        AnalysisResult freshResult)
    {
        return freshResult.Implementors;
    }

    /// <summary>
    /// Extracts the method name from a fully qualified method ID string.
    /// Format: "Namespace.ClassName.MethodName(params)" -> "MethodName"
    /// </summary>
    private static string ExtractMethodName(string methodIdValue)
    {
        var parenIndex = methodIdValue.IndexOf('(');
        if (parenIndex < 0) parenIndex = methodIdValue.Length;

        var dotIndex = methodIdValue.LastIndexOf('.', parenIndex - 1);
        if (dotIndex < 0) return methodIdValue;

        return methodIdValue.Substring(dotIndex + 1, parenIndex - dotIndex - 1);
    }

    /// <summary>
    /// Extracts the namespace from a fully qualified type name.
    /// Format: "Namespace.SubNamespace.ClassName" -> "Namespace.SubNamespace"
    /// </summary>
    private static string ExtractNamespace(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot < 0 ? "" : fullTypeName.Substring(0, lastDot);
    }
}
