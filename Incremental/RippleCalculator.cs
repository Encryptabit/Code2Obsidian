using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Models;

namespace Code2Obsidian.Incremental;

/// <summary>
/// Computes the full set of affected files from changed files using one-hop
/// callers/callees from stored and fresh call graphs, plus structural change detection.
/// Used by the incremental pipeline to determine which files need reanalysis beyond
/// the directly changed set.
/// </summary>
public sealed class RippleCalculator
{
    /// <summary>
    /// Computes the complete set of affected files by combining:
    /// 1. The originally changed files
    /// 2. One-hop callers (unchanged code calling into changed code)
    /// 3. One-hop callees (changed code calling into unchanged code)
    /// 4. Structural ripple (type deletions, base class changes, interface changes, namespace moves)
    /// </summary>
    public static HashSet<string> ComputeAffectedFiles(
        IReadOnlySet<string> changedFiles,
        AnalysisResult freshAnalysis,
        IncrementalState state)
    {
        var affected = new HashSet<string>(changedFiles, StringComparer.OrdinalIgnoreCase);

        // One-hop callers: unchanged code calling into changed code.
        // For each method in freshAnalysis whose file is in changedFiles,
        // look up stored callers and add their files.
        foreach (var (methodId, method) in freshAnalysis.Methods)
        {
            if (!changedFiles.Contains(method.FilePath))
                continue;

            // Callers of this changed method (from stored state)
            var callers = state.GetCallers(methodId.Value);
            foreach (var callerId in callers)
            {
                var callerFile = state.GetFileForMethod(callerId);
                if (callerFile is not null)
                    affected.Add(callerFile);
            }
        }

        // One-hop callees: changed code calling into unchanged code.
        // For each method in freshAnalysis whose file is in changedFiles,
        // iterate its fresh call graph callees and add their files.
        foreach (var (methodId, method) in freshAnalysis.Methods)
        {
            if (!changedFiles.Contains(method.FilePath))
                continue;

            var callees = freshAnalysis.CallGraph.GetCallees(methodId);
            foreach (var calleeId in callees)
            {
                // Try fresh analysis first (callee might be in a changed file too)
                if (freshAnalysis.Methods.TryGetValue(calleeId, out var calleeMethod))
                {
                    affected.Add(calleeMethod.FilePath);
                }
                else
                {
                    // Callee is in an unchanged file -- look up in stored state
                    var calleeFile = state.GetFileForMethod(calleeId.Value);
                    if (calleeFile is not null)
                        affected.Add(calleeFile);
                }
            }
        }

        // Structural ripple: type deletions, base class changes, interface changes, namespace moves
        var structuralFiles = DetectStructuralRipple(freshAnalysis, changedFiles, state);
        foreach (var file in structuralFiles)
            affected.Add(file);

        return affected;
    }

    /// <summary>
    /// Detects structural changes (deleted types, base class changes, interface changes,
    /// namespace moves) and returns the set of files that reference affected types.
    /// </summary>
    public static HashSet<string> DetectStructuralRipple(
        AnalysisResult freshAnalysis,
        IReadOnlySet<string> changedFiles,
        IncrementalState state)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var storedTypeReferences = state.GetTypeReferences();
        var storedTypeFiles = state.GetTypeFiles();
        var storedTypeMetadata = state.GetTypeMetadata();

        // Build lookup: typeId -> list of files that reference it
        var typeRefLookup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (typeId, filePath) in storedTypeReferences)
        {
            if (!typeRefLookup.TryGetValue(typeId, out var files))
            {
                files = new List<string>();
                typeRefLookup[typeId] = files;
            }
            files.Add(filePath);
        }

        // Build lookup: typeId -> set of files where the type is defined
        var typeFileLookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (typeId, filePath) in storedTypeFiles)
        {
            if (!typeFileLookup.TryGetValue(typeId, out var files))
            {
                files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                typeFileLookup[typeId] = files;
            }
            files.Add(filePath);
        }

        // Build set of fresh type IDs from changed files for quick lookup
        var freshTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (typeId, typeInfo) in freshAnalysis.Types)
        {
            if (changedFiles.Contains(typeInfo.FilePath))
                freshTypeIds.Add(typeId.Value);
        }

        // Check each fresh type from changed files for structural changes
        foreach (var (typeId, typeInfo) in freshAnalysis.Types)
        {
            if (!changedFiles.Contains(typeInfo.FilePath))
                continue;

            // If type is NOT in stored type_files, it is new -- no wide ripple needed
            if (!typeFileLookup.ContainsKey(typeId.Value))
                continue;

            // Compare stored vs fresh metadata
            if (!storedTypeMetadata.TryGetValue(typeId.Value, out var storedMeta))
                continue; // No stored metadata -- skip comparison

            var freshBaseClass = typeInfo.BaseClassFullName;
            var freshInterfaces = string.Join(",",
                typeInfo.InterfaceFullNames.OrderBy(i => i, StringComparer.Ordinal));
            var freshNamespace = typeInfo.Namespace;

            var storedInterfaces = storedMeta.Interfaces;
            var storedNamespace = storedMeta.Namespace;

            bool structuralChange =
                !string.Equals(freshBaseClass, storedMeta.BaseClass, StringComparison.Ordinal) ||
                !string.Equals(freshInterfaces, storedInterfaces, StringComparison.Ordinal) ||
                !string.Equals(freshNamespace, storedNamespace, StringComparison.Ordinal);

            if (structuralChange && typeRefLookup.TryGetValue(typeId.Value, out var referencingFiles))
            {
                foreach (var file in referencingFiles)
                    affected.Add(file);
            }
        }

        // Check for deleted types: type IDs that were in stored type_files for changed files
        // but are NOT present in freshAnalysis.Types
        foreach (var (typeId, typeFiles) in typeFileLookup)
        {
            // Check if any of this type's files are in the changed set
            bool typeInChangedFile = false;
            foreach (var file in typeFiles)
            {
                if (changedFiles.Contains(file))
                {
                    typeInChangedFile = true;
                    break;
                }
            }

            if (!typeInChangedFile)
                continue;

            // If the type is not in fresh analysis (it was deleted), trigger wide ripple
            if (!freshTypeIds.Contains(typeId) && typeRefLookup.TryGetValue(typeId, out var referencingFiles))
            {
                foreach (var file in referencingFiles)
                    affected.Add(file);
            }
        }

        return affected;
    }
}
