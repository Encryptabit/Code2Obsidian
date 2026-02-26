using System.Diagnostics;
using System.Security.Cryptography;
using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Analyzers;
using Code2Obsidian.Analysis.Models;
using Code2Obsidian.Emission;
using Code2Obsidian.Enrichment;
using Code2Obsidian.Incremental;
using Code2Obsidian.Loading;

namespace Code2Obsidian.Pipeline;

/// <summary>
/// Encapsulates ALL incremental orchestration logic in the Pipeline layer (INFR-06).
/// Handles two-pass analysis, change detection, ripple computation, selective emission,
/// stale note cleanup, and state persistence. Program.cs only routes CLI flags here.
/// </summary>
public sealed class IncrementalPipeline
{
    private readonly AnalysisContext _context;
    private IProgress<PipelineProgress>? _progress;
    private readonly string _outputDirectory;
    private readonly string _stateDbPath;
    private readonly int _fanInThreshold;
    private readonly int _complexityThreshold;

    public IncrementalPipeline(
        AnalysisContext context,
        IProgress<PipelineProgress>? progress,
        string outputDirectory,
        string stateDbPath,
        int fanInThreshold = 10,
        int complexityThreshold = 15)
    {
        _context = context;
        _progress = progress;
        _outputDirectory = outputDirectory;
        _stateDbPath = stateDbPath;
        _fanInThreshold = fanInThreshold;
        _complexityThreshold = complexityThreshold;
    }

    /// <summary>
    /// Allows updating the progress reporter after construction (used when
    /// the Spectre.Console ProgressContext is created after the pipeline instance).
    /// </summary>
    internal void SetProgress(IProgress<PipelineProgress>? progress)
    {
        _progress = progress;
    }

    /// <summary>
    /// Cases B and D: Full analysis with state save.
    /// Used for first incremental run (no prior state) and --full-rebuild.
    /// Runs full analysis via Pipeline, then saves state for future incremental runs.
    /// </summary>
    public async Task<PipelineResult> RunFullWithStateSaveAsync(CancellationToken ct)
    {
        // Run full pipeline
        var analyzers = new List<IAnalyzer> { new MethodAnalyzer(), new TypeAnalyzer() };
        var enrichers = new List<IEnricher>();
        var emitter = new ObsidianEmitter(_fanInThreshold, _complexityThreshold);
        var pipeline = new Pipeline(analyzers, enrichers, emitter);

        var result = await pipeline.RunAsync(_context, _outputDirectory, _progress, ct);

        // Save state after successful emission
        if (result.AnalysisResult is not null && result.EmitResult is not null)
        {
            var state = new IncrementalState(_stateDbPath);
            SaveState(state, result.AnalysisResult, result.EmitResult, affectedFiles: null, priorState: null);
        }

        return result;
    }

    /// <summary>
    /// Case C: Full incremental flow -- two-pass analysis, ripple, merge, selective emission,
    /// stale cleanup, state save. Implemented in Task 3.
    /// </summary>
    public async Task<PipelineResult> RunIncrementalAsync(IncrementalState state, CancellationToken ct)
    {
        // Placeholder -- implemented in Task 3
        throw new NotImplementedException("RunIncrementalAsync will be implemented in Task 3.");
    }

    /// <summary>
    /// Case E: Dry-run mode -- detect changes and report what would be regenerated
    /// without writing any files or updating state. Implemented in Task 3.
    /// </summary>
    public async Task<PipelineResult> RunDryRunAsync(IncrementalState state, CancellationToken ct)
    {
        // Placeholder -- implemented in Task 3
        throw new NotImplementedException("RunDryRunAsync will be implemented in Task 3.");
    }

    /// <summary>
    /// Adds .code2obsidian-state.db* to the vault's .gitignore file.
    /// </summary>
    public static void EnsureGitignore(string outputDirectory)
    {
        var gitignorePath = Path.Combine(outputDirectory, ".gitignore");
        const string pattern = ".code2obsidian-state.db*";

        if (File.Exists(gitignorePath))
        {
            var content = File.ReadAllText(gitignorePath);
            if (content.Contains(pattern, StringComparison.Ordinal))
                return; // Already present
            File.AppendAllText(gitignorePath, $"\n{pattern}\n");
        }
        else
        {
            File.WriteAllText(gitignorePath, $"{pattern}\n");
        }
    }

    /// <summary>
    /// Extracts state data from analysis and emission results and saves to SQLite.
    /// When affectedFiles is null, treats all files as affected (full analysis).
    /// When priorState is non-null, merges fresh data for affected files with stored data
    /// for unchanged files.
    /// </summary>
    internal void SaveState(
        IncrementalState state,
        AnalysisResult analysis,
        EmitResult emitResult,
        IReadOnlySet<string>? affectedFiles,
        IncrementalState? priorState)
    {
        // Determine current commit SHA from git (best effort)
        string? commitSha = null;
        try
        {
            var solutionDir = Path.GetDirectoryName(
                _context.Solution.FilePath ?? _outputDirectory);
            if (solutionDir is not null)
            {
                var gitDetector = new GitChangeDetector();
                var dummyChangeSet = gitDetector.DetectChanges(solutionDir, null);
                commitSha = dummyChangeSet?.CommitSha;
            }
        }
        catch
        {
            // Non-git repo or error -- commitSha stays null
        }

        // 1. File hashes: compute SHA256 for affected files, merge with stored for unchanged
        var fileHashes = BuildFileHashes(analysis, affectedFiles, priorState);

        // 2. Call edges from analysis call graph
        var callEdges = BuildCallEdges(analysis);

        // 3. Type references: for each type, record which files reference it
        // (We extract this from the analysis data by looking at method/type file paths)
        var typeReferences = BuildTypeReferences(analysis);

        // 4. Type files: which files define each type
        var typeFiles = BuildTypeFiles(analysis);

        // 5. Emitted notes from emission result
        var emittedNotes = BuildEmittedNotes(emitResult, priorState, affectedFiles);

        // 6. Type metadata for structural change detection
        var typeMetadata = BuildTypeMetadata(analysis);

        // 7. Method index for collision detection
        var methodIndex = BuildMethodIndex(analysis);

        // 8. Type index for collision detection
        var typeIndex = BuildTypeIndex(analysis);

        state.SaveState(
            commitSha,
            fileHashes,
            callEdges,
            typeReferences,
            typeFiles,
            emittedNotes,
            typeMetadata,
            methodIndex,
            typeIndex);
    }

    private Dictionary<string, string> BuildFileHashes(
        AnalysisResult analysis,
        IReadOnlySet<string>? affectedFiles,
        IncrementalState? priorState)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // If we have prior state and a limited affected set, start with stored hashes
        if (priorState is not null && affectedFiles is not null)
        {
            foreach (var (path, hash) in priorState.GetFileHashes())
            {
                if (!affectedFiles.Contains(path))
                    hashes[path] = hash;
            }
        }

        // Collect all file paths from the analysis result
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in analysis.Methods.Values)
            filePaths.Add(method.FilePath);
        foreach (var type in analysis.Types.Values)
            filePaths.Add(type.FilePath);

        // Compute hashes for analyzed files
        foreach (var filePath in filePaths)
        {
            if (affectedFiles is not null && !affectedFiles.Contains(filePath))
                continue;

            try
            {
                if (File.Exists(filePath))
                    hashes[filePath] = HashChangeDetector.ComputeFileHash(filePath);
            }
            catch
            {
                // Skip files that can't be hashed
            }
        }

        return hashes;
    }

    private static List<(string CallerId, string CalleeId, string CallerFile, string CalleeFile)> BuildCallEdges(
        AnalysisResult analysis)
    {
        var edges = new List<(string, string, string, string)>();

        foreach (var (caller, callees) in analysis.CallGraph.CallsOut)
        {
            var callerFile = analysis.Methods.TryGetValue(caller, out var callerInfo)
                ? callerInfo.FilePath
                : "";

            foreach (var callee in callees)
            {
                var calleeFile = analysis.Methods.TryGetValue(callee, out var calleeInfo)
                    ? calleeInfo.FilePath
                    : "";

                edges.Add((caller.Value, callee.Value, callerFile, calleeFile));
            }
        }

        return edges;
    }

    private static List<(string TypeId, string FilePath)> BuildTypeReferences(AnalysisResult analysis)
    {
        // Build type references: for each method, record which types it references
        // by mapping from type -> file that references it (the method's file)
        var refs = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();

        // Each method's containing type is referenced by the method's file
        foreach (var method in analysis.Methods.Values)
        {
            var key = (method.ContainingTypeId.Value, method.FilePath);
            if (seen.Add(key))
                refs.Add(key);
        }

        // Types that appear in the same file reference each other implicitly
        // (for structural change ripple)
        foreach (var type in analysis.Types.Values)
        {
            // Base class reference
            if (type.BaseClassFullName is not null)
            {
                var baseType = analysis.Types.Values.FirstOrDefault(t => t.FullName == type.BaseClassFullName);
                if (baseType is not null)
                {
                    var key = (baseType.Id.Value, type.FilePath);
                    if (seen.Add(key))
                        refs.Add(key);
                }
            }

            // Interface references
            foreach (var ifaceName in type.InterfaceFullNames)
            {
                var ifaceType = analysis.Types.Values.FirstOrDefault(t => t.FullName == ifaceName);
                if (ifaceType is not null)
                {
                    var key = (ifaceType.Id.Value, type.FilePath);
                    if (seen.Add(key))
                        refs.Add(key);
                }
            }

            // Constructor parameter type references (DI dependencies)
            foreach (var ctor in type.Constructors)
            {
                foreach (var param in ctor.Parameters)
                {
                    if (param.TypeNoteFullName is not null)
                    {
                        var paramType = analysis.Types.Values.FirstOrDefault(t => t.FullName == param.TypeNoteFullName);
                        if (paramType is not null)
                        {
                            var key = (paramType.Id.Value, type.FilePath);
                            if (seen.Add(key))
                                refs.Add(key);
                        }
                    }
                }
            }
        }

        return refs;
    }

    private static List<(string TypeId, string FilePath)> BuildTypeFiles(AnalysisResult analysis)
    {
        var typeFiles = new List<(string, string)>();
        foreach (var type in analysis.Types.Values)
        {
            typeFiles.Add((type.Id.Value, type.FilePath));
        }
        return typeFiles;
    }

    private static Dictionary<string, (string SourceFile, string EntityId)> BuildEmittedNotes(
        EmitResult emitResult,
        IncrementalState? priorState,
        IReadOnlySet<string>? affectedFiles)
    {
        var notes = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        // If we have prior state and a limited affected set, start with stored notes for unchanged files
        if (priorState is not null && affectedFiles is not null)
        {
            foreach (var (notePath, (sourceFile, entityId)) in priorState.GetEmittedNotes())
            {
                if (!affectedFiles.Contains(sourceFile))
                    notes[notePath] = (sourceFile, entityId);
            }
        }

        // Add fresh emitted notes
        foreach (var (notePath, sourceFile, entityId) in emitResult.EmittedNotes)
        {
            notes[notePath] = (sourceFile, entityId);
        }

        return notes;
    }

    private static List<(string TypeId, string? BaseClass, string Interfaces, string Namespace)> BuildTypeMetadata(
        AnalysisResult analysis)
    {
        var metadata = new List<(string, string?, string, string)>();
        foreach (var type in analysis.Types.Values)
        {
            var interfaces = string.Join(",",
                type.InterfaceFullNames.OrderBy(i => i, StringComparer.Ordinal));
            metadata.Add((type.Id.Value, type.BaseClassFullName, interfaces, type.Namespace));
        }
        return metadata;
    }

    private static List<(string MethodId, string ContainingType, string FilePath)> BuildMethodIndex(
        AnalysisResult analysis)
    {
        var index = new List<(string, string, string)>();
        foreach (var (methodId, method) in analysis.Methods)
        {
            index.Add((methodId.Value, method.ContainingTypeName, method.FilePath));
        }
        return index;
    }

    private static List<(string TypeId, string Name, string FullName, string FilePath, string Kind)> BuildTypeIndex(
        AnalysisResult analysis)
    {
        var index = new List<(string, string, string, string, string)>();
        foreach (var (typeId, type) in analysis.Types)
        {
            var kind = type.Kind switch
            {
                TypeKindInfo.Interface => "Interface",
                TypeKindInfo.Record => "Record",
                TypeKindInfo.Struct => "Struct",
                _ => "Class"
            };
            index.Add((typeId.Value, type.Name, type.FullName, type.FilePath, kind));
        }
        return index;
    }
}
