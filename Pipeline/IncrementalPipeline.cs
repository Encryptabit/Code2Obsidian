using System.Diagnostics;
using System.Security.Cryptography;
using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Analyzers;
using Code2Obsidian.Analysis.Models;
using Code2Obsidian.Emission;
using Code2Obsidian.Enrichment;
using Code2Obsidian.Incremental;
using Code2Obsidian.Loading;
using LibGit2Sharp;
using Microsoft.CodeAnalysis;

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
    private readonly IReadOnlyList<IEnricher> _enrichers;
    private readonly string[]? _excludePatterns;

    public IncrementalPipeline(
        AnalysisContext context,
        IProgress<PipelineProgress>? progress,
        string outputDirectory,
        string stateDbPath,
        int fanInThreshold = 10,
        int complexityThreshold = 15,
        IReadOnlyList<IEnricher>? enrichers = null,
        string[]? excludePatterns = null)
    {
        _context = context;
        _progress = progress;
        _outputDirectory = outputDirectory;
        _stateDbPath = stateDbPath;
        _fanInThreshold = fanInThreshold;
        _complexityThreshold = complexityThreshold;
        _enrichers = enrichers ?? new List<IEnricher>();
        _excludePatterns = excludePatterns;
    }

    /// <summary>
    /// Cases B and D: Full analysis with state save.
    /// Used for first incremental run (no prior state) and --full-rebuild.
    /// Runs full analysis via Pipeline, then saves state for future incremental runs.
    /// </summary>
    public async Task<PipelineResult> RunFullWithStateSaveAsync(CancellationToken ct)
    {
        // Run full pipeline
        var analyzers = new List<IAnalyzer> { new MethodAnalyzer(null, _excludePatterns), new TypeAnalyzer(null, _excludePatterns) };
        var emitter = new ObsidianEmitter(_fanInThreshold, _complexityThreshold);
        var pipeline = new Pipeline(analyzers, _enrichers, emitter);

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
    /// Case C: Full incremental flow.
    /// Two-pass analysis, ripple computation, merge with stored data, selective emission,
    /// stale note cleanup, and state save after successful emission.
    /// </summary>
    public async Task<PipelineResult> RunIncrementalAsync(IncrementalState state, CancellationToken ct)
    {
        var result = new PipelineResult();
        var sw = Stopwatch.StartNew();

        // Count total .cs documents for progress denominator
        int totalDocuments = CountCSharpDocuments();

        // Step 1: Change detection -- git-primary with hash fallback
        _progress?.Report(new PipelineProgress(
            PipelineStage.Analyzing,
            "Detecting changes...",
            0,
            totalDocuments));

        var solutionDir = Path.GetDirectoryName(_context.Solution.FilePath ?? _outputDirectory)!;
        var changeSet = DetectChanges(solutionDir, state);

        if (changeSet is null || changeSet.IsFullRebuild)
        {
            // Change detection failed or signaled full rebuild -- fall through to full
            _progress?.Report(new PipelineProgress(
                PipelineStage.Analyzing,
                "Change detection requires full rebuild...",
                0,
                totalDocuments));
            return await RunFullWithStateSaveAsync(ct);
        }

        // Normalize change set paths to absolute paths matching Roslyn document.FilePath
        var absoluteChangedFiles = NormalizeToAbsolutePaths(changeSet.ChangedFilePaths, solutionDir);

        // If no source changes are detected, fast exit unless enrichment is enabled.
        if (absoluteChangedFiles.Count == 0 && changeSet.DeletedFilePaths.Count == 0)
        {
            if (_enrichers.Count > 0)
            {
                _progress?.Report(new PipelineProgress(
                    PipelineStage.Analyzing,
                    "No source changes detected. Running enrichment pass for uncached entities...",
                    0,
                    totalDocuments));

                var fullResult = await RunFullWithStateSaveAsync(ct);
                fullResult.WasIncremental = true;
                fullResult.Warnings.Add(
                    "No source changes detected; ran enrichment pass to retry uncached entities.");
                return fullResult;
            }

            result.AnalysisDuration = sw.Elapsed;
            result.FilesAnalyzed = 0;
            result.FilesSkipped = totalDocuments;
            result.NotesGenerated = 0;
            result.NotesDeleted = 0;
            result.WasIncremental = true;
            result.Warnings.Add("No changes detected since last run.");
            return result;
        }

        // Handle git renames: rename vault notes to preserve Obsidian backlinks
        foreach (var (oldRelPath, newRelPath) in changeSet.RenamedPaths)
        {
            var oldAbsPath = ResolveAbsolutePath(oldRelPath, solutionDir);
            var newAbsPath = ResolveAbsolutePath(newRelPath, solutionDir);
            RenameVaultNotes(oldAbsPath, newAbsPath, solutionDir);
        }

        int analyzeCount = absoluteChangedFiles.Count;

        _progress?.Report(new PipelineProgress(
            PipelineStage.Analyzing,
            $"Analyzing {analyzeCount}/{totalDocuments} files ({totalDocuments - analyzeCount} unchanged)",
            0,
            analyzeCount));

        // Step 2: Pass 1 -- Analyze changed files only
        var pass1Analyzers = new List<IAnalyzer>
        {
            new MethodAnalyzer(absoluteChangedFiles, _excludePatterns),
            new TypeAnalyzer(absoluteChangedFiles, _excludePatterns)
        };
        var pass1Builder = new AnalysisResultBuilder();
        foreach (var analyzer in pass1Analyzers)
        {
            await analyzer.AnalyzeAsync(_context, pass1Builder, _progress, ct);
        }
        var pass1Result = pass1Builder.Build();

        // Step 3: Ripple computation
        _progress?.Report(new PipelineProgress(
            PipelineStage.Analyzing,
            "Computing ripple effect...",
            analyzeCount / 2,
            analyzeCount));

        var affectedFiles = RippleCalculator.ComputeAffectedFiles(absoluteChangedFiles, pass1Result, state);

        // Add files for deleted types (deletion ripple via structural detection is handled inside ComputeAffectedFiles)
        var absoluteDeletedFiles = NormalizeToAbsolutePaths(changeSet.DeletedFilePaths, solutionDir);
        foreach (var deleted in absoluteDeletedFiles)
            affectedFiles.Add(deleted);

        // Step 4: Pass 2 -- if ripple expanded the file set, reanalyze with expanded filter
        AnalysisResult freshAnalysis;
        if (affectedFiles.Count > absoluteChangedFiles.Count)
        {
            var expandedFilter = new HashSet<string>(affectedFiles, StringComparer.OrdinalIgnoreCase);

            _progress?.Report(new PipelineProgress(
                PipelineStage.Analyzing,
                $"Ripple expanded to {expandedFilter.Count} files, reanalyzing...",
                analyzeCount,
                expandedFilter.Count));

            var pass2Analyzers = new List<IAnalyzer>
            {
                new MethodAnalyzer(expandedFilter, _excludePatterns),
                new TypeAnalyzer(expandedFilter, _excludePatterns)
            };
            var pass2Builder = new AnalysisResultBuilder();
            foreach (var analyzer in pass2Analyzers)
            {
                await analyzer.AnalyzeAsync(_context, pass2Builder, _progress, ct);
            }
            freshAnalysis = pass2Builder.Build();
            analyzeCount = expandedFilter.Count;
        }
        else
        {
            freshAnalysis = pass1Result;
        }

        result.AnalysisDuration = sw.Elapsed;
        result.FilesAnalyzed = analyzeCount;
        result.FilesSkipped = totalDocuments - analyzeCount;
        result.ProjectsAnalyzed = freshAnalysis.ProjectCount;

        // Explicitly complete the analysis stage before switching to enrichment.
        _progress?.Report(new PipelineProgress(
            PipelineStage.Analyzing,
            "Analysis complete",
            totalDocuments,
            totalDocuments));

        sw.Restart();

        // Step 5: Merge fresh analysis with stored data for complete result
        _progress?.Report(new PipelineProgress(
            PipelineStage.Enriching,
            "Merging with stored analysis data...",
            0,
            1));

        var reanalyzedFileSet = new HashSet<string>(affectedFiles, StringComparer.OrdinalIgnoreCase);
        var llmDirtyFileSet = new HashSet<string>(absoluteChangedFiles, StringComparer.OrdinalIgnoreCase);
        var mergedAnalysis = AnalysisResultMerger.Merge(freshAnalysis, state, reanalyzedFileSet);

        // Enrichment pass - only enrich entities in dirty files (incremental mode)
        var enrichedResult = new EnrichedResult(mergedAnalysis);
        for (int i = 0; i < _enrichers.Count; i++)
        {
            var enricher = _enrichers[i];
            // In incremental mode, reconstruct LlmEnricher with dirty file filter
            // so only git-tracked changed/new entities are sent to the LLM.
            // Ripple-expanded files are still reanalyzed/emitted for correctness,
            // but they don't trigger fresh enrichment calls by default.
            if (enricher is LlmEnricher llm)
            {
                enricher = new LlmEnricher(
                    llm.Client,
                    llm.Cache,
                    llm.Config,
                    _progress,
                    llm.ConfirmEnrichment,
                    dirtyFiles: llmDirtyFileSet,
                    analysisRoot: llm.AnalysisRoot,
                    includeSummary: llm.IncludeSummary,
                    includeSuggestions: llm.IncludeSuggestions);
            }
            _progress?.Report(new PipelineProgress(
                PipelineStage.Enriching,
                $"Running {enricher.Name}...",
                i,
                _enrichers.Count));
            await enricher.EnrichAsync(mergedAnalysis, enrichedResult, ct);
            result.EnrichersRun++;
        }

        _progress?.Report(new PipelineProgress(
            PipelineStage.Enriching,
            "Merge complete",
            1,
            1));

        result.EnrichmentDuration = sw.Elapsed;
        sw.Restart();

        // Step 6: Selective emission -- only write notes for dirty files
        _progress?.Report(new PipelineProgress(
            PipelineStage.Emitting,
            $"Emitting notes for {analyzeCount} affected files...",
            0,
            analyzeCount));

        var emitter = new ObsidianEmitter(_fanInThreshold, _complexityThreshold, dirtyFiles: reanalyzedFileSet);
        var emitResult = await emitter.EmitAsync(enrichedResult, _outputDirectory, ct);

        result.NotesGenerated = emitResult.NotesWritten;
        result.Warnings.AddRange(emitResult.Warnings);

        // Step 7: Stale note cleanup
        var storedNotes = state.GetEmittedNotes();
        var currentEntityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (methodId, _) in mergedAnalysis.Methods)
            currentEntityIds.Add(methodId.Value);
        foreach (var (typeId, _) in mergedAnalysis.Types)
            currentEntityIds.Add(typeId.Value);

        var staleNotes = StaleNoteDetector.FindStaleNotes(storedNotes, currentEntityIds, reanalyzedFileSet);
        int notesDeleted = 0;
        foreach (var stalePath in staleNotes)
        {
            try
            {
                if (File.Exists(stalePath))
                {
                    File.Delete(stalePath);
                    notesDeleted++;
                }
            }
            catch (IOException)
            {
                result.Warnings.Add($"Failed to delete stale note: {stalePath}");
            }
        }
        result.NotesDeleted = notesDeleted;

        _progress?.Report(new PipelineProgress(
            PipelineStage.Emitting,
            $"Emission complete ({emitResult.NotesWritten} written, {notesDeleted} stale deleted)",
            emitResult.NotesWritten,
            emitResult.NotesWritten));

        result.EmissionDuration = sw.Elapsed;

        // Step 8: Save state AFTER successful emission (transaction boundary)
        SaveState(state, mergedAnalysis, emitResult, reanalyzedFileSet, state);

        result.WasIncremental = true;
        result.AnalysisResult = mergedAnalysis;
        result.EmitResult = emitResult;

        return result;
    }

    /// <summary>
    /// Case E: Dry-run mode -- detect changes and report what would change
    /// without writing any files or updating state.
    /// </summary>
    public async Task<PipelineResult> RunDryRunAsync(IncrementalState state, CancellationToken ct)
    {
        var result = new PipelineResult();
        var sw = Stopwatch.StartNew();

        int totalDocuments = CountCSharpDocuments();

        // Step 1: Change detection
        _progress?.Report(new PipelineProgress(
            PipelineStage.Analyzing,
            "Detecting changes (dry run)...",
            0,
            totalDocuments));

        var solutionDir = Path.GetDirectoryName(_context.Solution.FilePath ?? _outputDirectory)!;
        var changeSet = DetectChanges(solutionDir, state);

        if (changeSet is null || changeSet.IsFullRebuild)
        {
            result.AnalysisDuration = sw.Elapsed;
            result.WasIncremental = true;
            result.Warnings.Add("Dry run: change detection requires full rebuild. Run with --incremental (no --dry-run) to rebuild.");
            return result;
        }

        var absoluteChangedFiles = NormalizeToAbsolutePaths(changeSet.ChangedFilePaths, solutionDir);

        if (absoluteChangedFiles.Count == 0 && changeSet.DeletedFilePaths.Count == 0)
        {
            result.AnalysisDuration = sw.Elapsed;
            result.FilesAnalyzed = 0;
            result.FilesSkipped = totalDocuments;
            result.WasIncremental = true;
            result.Warnings.Add("Dry run: no changes detected since last run.");
            return result;
        }

        // Step 2: Analyze changed files for ripple computation
        var pass1Analyzers = new List<IAnalyzer>
        {
            new MethodAnalyzer(absoluteChangedFiles, _excludePatterns),
            new TypeAnalyzer(absoluteChangedFiles, _excludePatterns)
        };
        var pass1Builder = new AnalysisResultBuilder();
        foreach (var analyzer in pass1Analyzers)
        {
            await analyzer.AnalyzeAsync(_context, pass1Builder, _progress, ct);
        }
        var pass1Result = pass1Builder.Build();

        // Step 3: Ripple computation
        var affectedFiles = RippleCalculator.ComputeAffectedFiles(absoluteChangedFiles, pass1Result, state);
        var absoluteDeletedFiles = NormalizeToAbsolutePaths(changeSet.DeletedFilePaths, solutionDir);
        foreach (var deleted in absoluteDeletedFiles)
            affectedFiles.Add(deleted);

        // Step 4: Stale note detection
        var storedNotes = state.GetEmittedNotes();
        var reanalyzedFileSet = new HashSet<string>(affectedFiles, StringComparer.OrdinalIgnoreCase);

        // For dry run, we need current entity IDs from a quick pass
        var currentEntityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (methodId, _) in pass1Result.Methods)
            currentEntityIds.Add(methodId.Value);
        foreach (var (typeId, _) in pass1Result.Types)
            currentEntityIds.Add(typeId.Value);

        var staleNotes = StaleNoteDetector.FindStaleNotes(storedNotes, currentEntityIds, reanalyzedFileSet);

        result.AnalysisDuration = sw.Elapsed;
        result.FilesAnalyzed = absoluteChangedFiles.Count;
        result.FilesSkipped = totalDocuments - absoluteChangedFiles.Count;
        result.NotesGenerated = 0; // Dry run: nothing written
        result.NotesDeleted = staleNotes.Count; // Would be deleted
        result.WasIncremental = true;

        // Report summary
        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"Dry run: {absoluteChangedFiles.Count} changed files detected");
        summary.AppendLine($"  Ripple expands to {affectedFiles.Count} affected files");
        summary.AppendLine($"  {staleNotes.Count} stale notes would be deleted");
        summary.AppendLine($"  {totalDocuments - affectedFiles.Count} files would be skipped");
        result.Warnings.Add(summary.ToString());

        _progress?.Report(new PipelineProgress(
            PipelineStage.Emitting,
            $"Dry run complete: {affectedFiles.Count} files would be regenerated",
            1,
            1));

        return result;
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

    // -----------------------------------------------------------------------
    //  Change detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects changes using git-primary with hash-fallback strategy.
    /// </summary>
    private ChangeSet? DetectChanges(string solutionDir, IncrementalState state)
    {
        // Try git first
        try
        {
            var gitDetector = new GitChangeDetector();
            var changeSet = gitDetector.DetectChanges(solutionDir, state);
            if (changeSet is not null)
                return changeSet;
        }
        catch
        {
            // Git detection failed -- fall through to hash
        }

        // Fallback to hash-based detection
        var hashDetector = new HashChangeDetector();
        return hashDetector.DetectChanges(solutionDir, state);
    }

    // -----------------------------------------------------------------------
    //  Path normalization
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts relative file paths (from change detectors) to absolute paths
    /// matching Roslyn document.FilePath format.
    /// </summary>
    private HashSet<string> NormalizeToAbsolutePaths(IReadOnlySet<string> relativePaths, string solutionDir)
    {
        // First, build a lookup of all Roslyn document file paths for efficient matching
        var roslynPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in _context.Solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not null)
                {
                    // Normalize to forward-slash for comparison
                    var normalized = document.FilePath.Replace('\\', '/');
                    roslynPaths[normalized] = document.FilePath;

                    // Also index by file name for fallback matching
                    var fileName = Path.GetFileName(document.FilePath);
                    // Don't overwrite if multiple files have same name
                    roslynPaths.TryAdd(fileName, document.FilePath);
                }
            }
        }

        var absolutePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in relativePaths)
        {
            // Strategy 1: Direct resolve against solution directory
            var candidate = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
            if (roslynPaths.TryGetValue(candidate.Replace('\\', '/'), out var roslynPath))
            {
                absolutePaths.Add(roslynPath);
                continue;
            }

            // Strategy 2: Try the candidate path directly (if it exists)
            if (File.Exists(candidate))
            {
                absolutePaths.Add(candidate);
                continue;
            }

            // Strategy 3: Search Roslyn paths for suffix match
            var normalizedRelative = relativePath.Replace('\\', '/');
            var match = roslynPaths.Keys
                .FirstOrDefault(k => k.EndsWith("/" + normalizedRelative, StringComparison.OrdinalIgnoreCase)
                    || k.Equals(normalizedRelative, StringComparison.OrdinalIgnoreCase));
            if (match is not null && roslynPaths.TryGetValue(match, out var matched))
            {
                absolutePaths.Add(matched);
                continue;
            }

            // Strategy 4: Try resolving against repo root (may differ from solution dir)
            try
            {
                var repoRoot = Repository.Discover(solutionDir);
                if (repoRoot is not null)
                {
                    // Repository.Discover returns path to .git directory
                    var rootDir = Path.GetDirectoryName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (rootDir is not null)
                    {
                        var repoCandidate = Path.GetFullPath(Path.Combine(rootDir, relativePath));
                        if (File.Exists(repoCandidate))
                        {
                            absolutePaths.Add(repoCandidate);
                            continue;
                        }
                    }
                }
            }
            catch
            {
                // Not a git repo or error
            }

            // If all strategies fail, use the candidate as-is (best effort)
            absolutePaths.Add(candidate);
        }

        return absolutePaths;
    }

    /// <summary>
    /// Resolves a single relative path to absolute using the solution directory.
    /// </summary>
    private static string ResolveAbsolutePath(string relativePath, string solutionDir)
    {
        return Path.GetFullPath(Path.Combine(solutionDir, relativePath));
    }

    // -----------------------------------------------------------------------
    //  Vault operations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Renames vault notes when source files are renamed, preserving Obsidian backlinks.
    /// </summary>
    private void RenameVaultNotes(string oldFilePath, string newFilePath, string solutionDir)
    {
        // Update all file path references in the state DB so that:
        // 1. Stale note detection can correctly associate notes with the new source file
        // 2. Ripple calculation uses the new file path for callers/callees/type references
        // The renamed file will be reanalyzed anyway (it's in ChangedFilePaths), so fresh
        // data replaces these entries at SaveState. But correct paths are needed during
        // the pipeline run for stale detection and ripple computation.
        // solutionDir is passed so file_hashes (stored as relative) can also be updated.
        var state = new IncrementalState(_stateDbPath);
        if (!state.TryLoad(out _))
            return;

        state.UpdateFilePathReferences(oldFilePath, newFilePath, solutionDir);
    }

    // -----------------------------------------------------------------------
    //  Helper: count documents
    // -----------------------------------------------------------------------

    /// <summary>
    /// Counts total C# documents across all projects in the solution.
    /// </summary>
    private int CountCSharpDocuments()
    {
        int count = 0;
        foreach (var project in _context.Solution.Projects)
        {
            if (project.Language == LanguageNames.CSharp)
            {
                count += project.Documents.Count();
            }
        }
        return count;
    }

    // -----------------------------------------------------------------------
    //  State persistence
    // -----------------------------------------------------------------------

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
        string? solutionDir = null;
        try
        {
            solutionDir = Path.GetDirectoryName(
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
        var fileHashes = BuildFileHashes(analysis, affectedFiles, priorState, solutionDir);

        // 2. Call edges from analysis call graph
        var callEdges = BuildCallEdges(analysis);

        // 3. Type references (preserve prior state for unchanged files)
        var typeReferences = BuildTypeReferences(analysis, affectedFiles, priorState);

        // 4. Type files: which files define each type
        var typeFiles = BuildTypeFiles(analysis);

        // 5. Emitted notes from emission result
        var emittedNotes = BuildEmittedNotes(emitResult, priorState, affectedFiles);

        // 6. Type metadata for structural change detection (preserve prior state for unchanged files)
        var typeMetadata = BuildTypeMetadata(analysis, affectedFiles, priorState);

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
        IncrementalState? priorState,
        string? solutionDir)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // If we have prior state and a limited affected set, start with stored hashes
        if (priorState is not null && affectedFiles is not null)
        {
            foreach (var (path, hash) in priorState.GetFileHashes())
            {
                // Stored hashes may be absolute (legacy) or relative.
                // Convert to absolute for affectedFiles check.
                var absolutePath = solutionDir is not null && !Path.IsPathRooted(path)
                    ? Path.GetFullPath(Path.Combine(solutionDir, path))
                    : path;

                if (!affectedFiles.Contains(absolutePath))
                    hashes[NormalizeToRelative(path, solutionDir)] = hash;
            }
        }

        // Collect all file paths from the analysis result
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in analysis.Methods.Values)
            filePaths.Add(method.FilePath);
        foreach (var type in analysis.Types.Values)
            filePaths.Add(type.FilePath);

        // Compute hashes for analyzed files, storing as relative paths
        foreach (var filePath in filePaths)
        {
            if (affectedFiles is not null && !affectedFiles.Contains(filePath))
                continue;

            try
            {
                if (File.Exists(filePath))
                {
                    var key = NormalizeToRelative(filePath, solutionDir);
                    hashes[key] = HashChangeDetector.ComputeFileHash(filePath);
                }
            }
            catch
            {
                // Skip files that can't be hashed
            }
        }

        return hashes;
    }

    /// <summary>
    /// Normalizes a file path to a forward-slash relative path for consistent hash key storage.
    /// If the path is already relative, just normalizes slashes.
    /// </summary>
    private static string NormalizeToRelative(string path, string? solutionDir)
    {
        if (solutionDir is not null && Path.IsPathRooted(path))
            return Path.GetRelativePath(solutionDir, path).Replace('\\', '/');

        return path.Replace('\\', '/');
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

    private static List<(string TypeId, string FilePath)> BuildTypeReferences(
        AnalysisResult analysis,
        IReadOnlySet<string>? affectedFiles,
        IncrementalState? priorState)
    {
        var refs = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();

        // If incremental, carry forward prior type_references for unchanged files
        if (affectedFiles is not null && priorState is not null)
        {
            foreach (var (typeId, filePath) in priorState.GetTypeReferences())
            {
                if (!affectedFiles.Contains(filePath))
                {
                    var key = (typeId, filePath);
                    if (seen.Add(key))
                        refs.Add(key);
                }
            }
        }

        // Build fresh refs only from types/methods in affected files (or all if full rebuild)
        foreach (var method in analysis.Methods.Values)
        {
            if (affectedFiles is not null && !affectedFiles.Contains(method.FilePath))
                continue;

            var key = (method.ContainingTypeId.Value, method.FilePath);
            if (seen.Add(key))
                refs.Add(key);
        }

        foreach (var type in analysis.Types.Values)
        {
            if (affectedFiles is not null && !affectedFiles.Contains(type.FilePath))
                continue;

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
        AnalysisResult analysis,
        IReadOnlySet<string>? affectedFiles,
        IncrementalState? priorState)
    {
        var metadata = new List<(string, string?, string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // If incremental, carry forward prior type_metadata for types in unchanged files
        if (affectedFiles is not null && priorState is not null)
        {
            var priorTypeIndex = priorState.GetTypeIndex();
            var priorMetadata = priorState.GetTypeMetadata();

            foreach (var (typeIdStr, (_, _, filePath, _)) in priorTypeIndex)
            {
                if (!affectedFiles.Contains(filePath) && priorMetadata.TryGetValue(typeIdStr, out var meta))
                {
                    metadata.Add((typeIdStr, meta.BaseClass, meta.Interfaces, meta.Namespace));
                    seen.Add(typeIdStr);
                }
            }
        }

        // Build fresh metadata for types in affected files (or all if full rebuild)
        foreach (var type in analysis.Types.Values)
        {
            if (affectedFiles is not null && !affectedFiles.Contains(type.FilePath))
                continue;

            if (!seen.Add(type.Id.Value))
                continue;

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
