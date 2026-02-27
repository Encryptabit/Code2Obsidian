# Phase 4: Incremental Mode - Research

**Researched:** 2026-02-26
**Domain:** Git-based change detection, SQLite state storage, selective Roslyn analysis, incremental pipeline orchestration
**Confidence:** HIGH

## Summary

Phase 4 adds an `--incremental` flag that compares the current git state against a stored snapshot, identifies changed source files, computes a one-hop ripple of affected neighbors via the stored call graph, and regenerates only those notes. The core challenge is layering change detection ON TOP of the existing pipeline without restructuring it -- the current `IAnalyzer -> IEnricher -> IEmitter` flow must still work, just with a filtered file set and smarter emission.

A critical architectural insight from this research: Roslyn's MSBuildWorkspace must still load the entire solution and produce compilations for all projects to get valid semantic models (type resolution, cross-project references). The incremental optimization is NOT at the Roslyn compilation level -- it is at the **iteration level**: we still call `project.GetCompilationAsync()`, but we only visit documents that are in the changed/affected set. This means the time savings come from skipping syntax tree walking, method extraction, and call graph construction for unchanged files -- not from skipping Roslyn project loading.

The state storage decision (SQLite) is well-matched: we need relational queries (find callers of a method, find files referencing a type) that would be awkward with flat files. LibGit2Sharp provides in-process git operations without requiring a git CLI installation. Microsoft.Data.Sqlite is the standard .NET SQLite provider.

**Primary recommendation:** Build an `IChangeDetector` abstraction with git-primary/hash-fallback implementations, an `IncrementalState` class wrapping SQLite, and modify the Pipeline to accept a file filter. Keep Roslyn loading full-solution; optimize only the per-document iteration.

<user_constraints>

## User Constraints (from CONTEXT.md)

### Locked Decisions
- Git diff as primary change detection; content hashing as fallback for non-git repos or dirty working trees
- One-hop ripple: changed files + their direct callers/callees are regenerated
- Two-pass approach: re-analyze changed files first to get current call graph, then use NEW graph to find affected neighbors
- Structural changes (renames, namespace moves, base class changes) trigger wider regeneration -- all files referencing the changed type
- State stored as a hidden SQLite database in the output vault root (e.g., `.code2obsidian-state.db`)
- Contains: last commit hash, per-file content hashes, call graph relationships, type reference index
- Missing or corrupted state triggers silent full rebuild -- no errors, no prompts, just works
- State file should be gitignored by default (machine-local, automatically rebuilt)
- `--incremental` flag triggers incremental mode (INFR-03)
- Without `--incremental`, full analysis is always performed (default behavior unchanged)
- When `--incremental` is used but no state exists, performs full analysis and saves state for next run (INFR-05)
- `--full-rebuild` flag forces state wipe + full analysis even when state exists
- `--dry-run` flag shows what would be regenerated without actually doing it (discretionary scope)
- Progress display shows skipped work: "Analyzing 12/200 files (188 unchanged)"
- Stale notes (deleted classes/methods) are automatically deleted from the vault
- Deletion reporting only in verbose mode (-v); silent by default
- Git renames tracked and mapped to vault note renames (preserves Obsidian backlinks)

### Claude's Discretion
- Index/overview note regeneration strategy (always vs. only when affected)
- SQLite schema design and migration strategy
- Exact content hash algorithm choice
- How to detect structural vs. method-level changes in the diff

### Deferred Ideas (OUT OF SCOPE)
None -- discussion stayed within phase scope

</user_constraints>

<phase_requirements>

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| INFR-03 | Incremental mode (`--incremental`) only regenerates notes for files changed since last run | Git change detection + one-hop ripple algorithm + SQLite state store + filtered pipeline iteration |
| INFR-04 | Change detection uses git diff against stored commit hash | LibGit2Sharp `repo.Diff.Compare<TreeChanges>(oldTree, newTree)` with stored HEAD commit SHA |
| INFR-05 | First run without prior state performs full analysis gracefully | SQLite `PRAGMA user_version` check; missing/corrupt DB triggers full analysis + state save |

</phase_requirements>

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.Data.Sqlite | 8.0.11 | SQLite state database (ADO.NET) | Microsoft's official lightweight SQLite provider for .NET; no ORM overhead |
| LibGit2Sharp | 0.31.0 | Git diff and commit history access | In-process libgit2 bindings; no git CLI dependency; supports .NET 8 |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Security.Cryptography (built-in) | n/a | SHA256 content hashing | Fallback change detection when git is unavailable |
| System.CommandLine | 2.0.3 | CLI flags `--incremental`, `--full-rebuild`, `--dry-run` | Already in project; extend existing RootCommand |
| Spectre.Console | 0.54.0 | Progress display with skip counts | Already in project; extend progress reporting |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Microsoft.Data.Sqlite | Dapper + SQLite | Dapper adds ORM convenience but another dependency; raw ADO.NET is sufficient for ~6 tables |
| Microsoft.Data.Sqlite | JSON flat file | Flat file cannot do relational queries like "find all callers of method X"; SQLite is correct |
| LibGit2Sharp | Process.Start("git") | CLI approach is simpler but requires git installed; LibGit2Sharp works on any machine with the NuGet package |
| SHA256 | xxHash/MD5 | SHA256 is built-in, fast enough for file-level hashing, and collision-safe. xxHash is faster but requires a NuGet package; MD5 is not collision-safe |

**Installation:**
```bash
dotnet add package Microsoft.Data.Sqlite --version 8.0.11
dotnet add package LibGit2Sharp --version 0.31.0
```

**Note on Microsoft.Data.Sqlite version:** Use 8.0.x (the .NET 8 family) rather than 10.0.x to match the project's .NET 8 target framework. Version 10.0.x targets .NET 10 primarily. The latest 8.0.x patch should be used for bug fixes.

## Architecture Patterns

### Recommended Project Structure
```
Code2Obsidian/
+-- Analysis/             # existing
+-- Cli/                  # existing
+-- Emission/             # existing
+-- Enrichment/           # existing
+-- Loading/              # existing
+-- Pipeline/             # existing
+-- Incremental/          # NEW
|   +-- IChangeDetector.cs         # abstraction for git/hash detection
|   +-- GitChangeDetector.cs       # LibGit2Sharp implementation
|   +-- HashChangeDetector.cs      # SHA256 fallback implementation
|   +-- IncrementalState.cs        # SQLite state read/write
|   +-- StateSchema.cs             # schema creation + migration
|   +-- ChangeSet.cs               # domain model: what changed
|   +-- RippleCalculator.cs        # one-hop neighbor computation
|   +-- StaleNoteDetector.cs       # finds notes to delete
+-- Program.cs            # extended with new CLI flags
```

### Pattern 1: Two-Pass Incremental Analysis
**What:** First pass analyzes only changed files to get their NEW call graph. Second pass uses the new graph to compute the one-hop ripple set (direct callers + callees of changed methods). Both passes iterate only filtered documents within a full Roslyn compilation.
**When to use:** Every `--incremental` run with existing state.
**Why two passes:** The call graph from the PREVIOUS run is stale -- if a method was renamed or its calls changed, the old graph would ripple to the wrong neighbors. The new graph accurately reflects current code.

```
Pass 1: For each changed file:
  - Walk syntax tree, extract methods, build call edges
  - Compare extracted TypeIds/MethodIds against stored state
  - If structural change detected -> mark as "wide ripple"

Pass 2: Using NEW call graph from pass 1 + STORED graph for unchanged files:
  - For each method in changed files:
    - Add callers from stored graph (unchanged code calling into changed code)
    - Add callees from new graph (changed code calling into unchanged code)
  - Collect all affected files from the union
  - Re-analyze those neighbor files too
```

### Pattern 2: SQLite State with PRAGMA user_version Migration
**What:** Use `PRAGMA user_version` to track schema version. On open, check version and run migration SQL if needed. Version 0 = no schema (fresh DB). Version 1 = initial schema.
**When to use:** Every state database open.
**Example:**
```csharp
// Source: SQLite official docs + Microsoft.Data.Sqlite docs
using var connection = new SqliteConnection($"Data Source={dbPath}");
connection.Open();

// Enable WAL mode for performance
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "PRAGMA journal_mode = 'wal'";
    cmd.ExecuteNonQuery();
}

// Check schema version
int version;
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "PRAGMA user_version";
    version = Convert.ToInt32(cmd.ExecuteScalar());
}

if (version < 1)
{
    // Run initial schema creation
    MigrateToV1(connection);
}
```

### Pattern 3: File Filter Injection into Pipeline
**What:** The Pipeline receives an optional `IReadOnlySet<string>` of file paths to analyze. Analyzers check each document against this set and skip non-matching files. When null (full mode), all files are analyzed.
**When to use:** Incremental runs.
**Why this approach:** Minimal disruption to existing Pipeline/IAnalyzer interfaces. The AnalysisContext still loads the full solution (required for Roslyn semantic models). Only the per-document iteration is filtered.

```csharp
// In MethodAnalyzer.AnalyzeAsync:
foreach (var document in project.Documents)
{
    if (fileFilter is not null && !fileFilter.Contains(document.FilePath!))
    {
        skippedCount++;
        continue; // skip unchanged files
    }
    // ... existing analysis logic
}
```

### Pattern 4: Structural Change Detection via Roslyn Comparison
**What:** After re-analyzing a changed file, compare extracted TypeInfo/MethodInfo against stored state to detect structural changes (type renames, base class changes, interface list changes, namespace moves).
**When to use:** During pass 1, for each changed file.
**Detection heuristic:**

```
For each TypeId in the changed file:
  1. Look up stored TypeInfo for that TypeId
  2. If TypeId not found in state -> NEW type (wide ripple: all files in same namespace)
  3. If stored TypeId exists but:
     a. BaseClassFullName changed -> structural (ripple all files referencing this type)
     b. InterfaceFullNames changed -> structural (ripple all files referencing this type)
     c. Namespace changed -> structural (ripple all files importing old namespace)
     d. Name changed -> this is caught by TypeId mismatch (TypeId includes namespace.name)
  4. If only MethodIds changed -> method-level (normal one-hop ripple)

For disappearing TypeIds (in state but not in re-analyzed file):
  - Type was deleted or renamed -> wide ripple on all files that referenced it
```

### Anti-Patterns to Avoid
- **Skipping Roslyn compilation for unchanged projects:** Semantic models for changed files may need type info from unchanged projects. The full solution MUST be loaded. Optimize iteration, not loading.
- **Storing Roslyn ISymbol references in state:** Symbols are compilation-scoped and cannot be serialized. Store string-based MethodId/TypeId values only.
- **Re-analyzing the entire solution on any structural change:** Only files referencing the changed type need re-analysis, not all files. Use the type reference index.
- **Trusting the old call graph for ripple computation:** Always use the NEW call graph from re-analyzed files. The old graph may reference deleted/renamed methods.
- **Writing state before emission completes:** If emission fails partway, the state would claim files are up-to-date when their notes are stale. Write state AFTER successful emission.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Git diff between commits | Process.Start("git diff") wrapper | LibGit2Sharp `repo.Diff.Compare<TreeChanges>()` | In-process, no git dependency, typed API, rename detection built-in |
| SQLite database access | Raw P/Invoke to sqlite3.dll | Microsoft.Data.Sqlite | Standard ADO.NET provider, maintained by Microsoft, handles native binary bundling |
| Content hashing | Custom hash function | `SHA256.HashData(stream)` | Built into .NET 8, one-liner static API, cryptographic strength unnecessary but free |
| Git rename detection | Custom heuristic comparing file contents | LibGit2Sharp SimilarityOptions.Renames | Libgit2 implements git's rename detection algorithm with configurable threshold |
| Schema migration | Custom migration framework | `PRAGMA user_version` + sequential migration methods | SQLite's built-in mechanism; simple versioned methods (MigrateToV1, MigrateToV2) are sufficient for ~3 schema versions |

**Key insight:** The incremental system has many moving parts (change detection, state storage, ripple computation, filtered analysis, stale cleanup), but each individual piece maps to a well-understood solution. The complexity is in the ORCHESTRATION, not the individual components.

## Common Pitfalls

### Pitfall 1: Roslyn Compilation Must Load Full Solution
**What goes wrong:** Attempting to skip project loading for unchanged projects, then getting null semantic models or missing type resolution when analyzing changed files.
**Why it happens:** Roslyn semantic models resolve types across project boundaries. A changed file in Project A may reference types from unchanged Project B. Without Project B's compilation, the semantic model cannot resolve those references.
**How to avoid:** Always load the full solution via MSBuildWorkspace. Filter at the document iteration level, not the project compilation level. The time cost of loading is fixed; the savings come from skipping per-document analysis.
**Warning signs:** NullReferenceException in GetDeclaredSymbol, type names resolving to ErrorType, missing cross-project call graph edges.

### Pitfall 2: Stale Call Graph Edges After Method Rename
**What goes wrong:** Using the stored call graph to compute ripple for a renamed method. The old graph has edges to `OldClass.OldMethod`, which no longer exists. The ripple misses the actual callers of `NewClass.NewMethod`.
**Why it happens:** The two-pass approach is designed to prevent this, but implementation might accidentally use stored edges for changed files.
**How to avoid:** Pass 1 produces a FRESH call graph for changed files. The ripple computation uses: (a) fresh graph for changed-file methods, (b) stored graph for unchanged-file methods calling INTO changed files (these edges are on the caller side, which is unchanged).
**Warning signs:** After renaming a method, its callers' notes still show the old name; callers' notes are not regenerated.

### Pitfall 3: Content Hash vs. Git Hash Mismatch
**What goes wrong:** Git reports a file as unchanged (same commit), but the file content has been modified in the working tree (uncommitted changes). Or vice versa: git shows a diff but the content hash is the same (whitespace-only change).
**Why it happens:** Git diff compares committed state; content hash compares actual file bytes. They can disagree.
**How to avoid:** Use git diff as PRIMARY detection (committed + working tree changes). For the hash fallback (non-git repos), compare stored content hash against current file hash. When using git, compare against `DiffTargets.Index | DiffTargets.WorkingDirectory` to catch both staged and unstaged changes.
**Warning signs:** Incremental runs miss uncommitted changes; or regenerate notes for whitespace-only changes.

### Pitfall 4: SQLite Connection Lifetime and File Locking
**What goes wrong:** Keeping a SQLite connection open during the entire pipeline run causes file lock issues on Windows. Or opening/closing connections frequently causes performance problems.
**Why it happens:** SQLite on Windows uses file-level locking. Long-running operations with an open connection can block other processes.
**How to avoid:** Open connection at start of incremental state read, close after reading. Open again at end for state write. Use WAL mode (`PRAGMA journal_mode = 'wal'`) for better concurrent read performance. Keep transactions short.
**Warning signs:** "database is locked" errors; .db-wal and .db-shm files not being cleaned up.

### Pitfall 5: State Written Before Emission Completes
**What goes wrong:** State records "file X analyzed at commit Y" but emission fails partway through. Next incremental run skips file X because state says it's current, but the note was never written.
**Why it happens:** Writing state eagerly after analysis but before emission.
**How to avoid:** Write state ONLY after the full pipeline (analysis + enrichment + emission) succeeds. If any stage fails, do not update state. This ensures the next run will retry failed files.
**Warning signs:** Missing notes in vault that should exist; `--full-rebuild` produces more notes than incremental.

### Pitfall 6: Partial Class Files
**What goes wrong:** A type is defined across multiple partial class files. Only one file changes, but the type's full definition spans both. The unchanged file's contributions are missed.
**Why it happens:** The existing AnalysisResultBuilder uses `TryAdd` for types -- first registration wins. If the changed file is analyzed but the unchanged partial file is skipped, the TypeInfo is incomplete.
**How to avoid:** When a file contains a partial type, include ALL files contributing to that type in the affected set. The stored state should index `TypeId -> [file1, file2, ...]` for multi-file types.
**Warning signs:** Partial class notes missing members, properties, or interfaces that are declared in the unchanged partial file.

## Code Examples

### LibGit2Sharp: Detect Changed Files Between Commits
```csharp
// Source: LibGit2Sharp wiki (github.com/libgit2/libgit2sharp/wiki/git-diff)
using LibGit2Sharp;

public IReadOnlyList<FileChange> GetChangedFiles(string repoPath, string oldCommitSha)
{
    using var repo = new Repository(repoPath);

    var oldCommit = repo.Lookup<Commit>(oldCommitSha);
    var newCommit = repo.Head.Tip;

    var options = new CompareOptions
    {
        Similarity = SimilarityOptions.Renames // enable rename detection
    };

    var changes = repo.Diff.Compare<TreeChanges>(
        oldCommit.Tree, newCommit.Tree, options);

    var result = new List<FileChange>();
    foreach (var change in changes)
    {
        result.Add(new FileChange(
            change.Path,
            change.OldPath, // non-null for renames
            change.Status   // Added, Modified, Deleted, Renamed, etc.
        ));
    }

    // Also check working directory for uncommitted changes
    var workingChanges = repo.Diff.Compare<TreeChanges>(
        newCommit.Tree,
        DiffTargets.Index | DiffTargets.WorkingDirectory);

    foreach (var change in workingChanges)
    {
        if (!result.Any(r => r.Path == change.Path))
        {
            result.Add(new FileChange(change.Path, change.OldPath, change.Status));
        }
    }

    return result;
}
```

### Microsoft.Data.Sqlite: State Schema (V1)
```csharp
// Source: Microsoft.Data.Sqlite docs + PRAGMA user_version pattern
private static void MigrateToV1(SqliteConnection connection)
{
    using var transaction = connection.BeginTransaction();

    var sql = @"
        CREATE TABLE IF NOT EXISTS run_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            commit_sha TEXT NOT NULL,
            run_timestamp TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS file_hashes (
            file_path TEXT PRIMARY KEY,
            content_hash TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS call_edges (
            caller_id TEXT NOT NULL,
            callee_id TEXT NOT NULL,
            caller_file TEXT NOT NULL,
            callee_file TEXT NOT NULL,
            PRIMARY KEY (caller_id, callee_id)
        );

        CREATE TABLE IF NOT EXISTS type_references (
            type_id TEXT NOT NULL,
            file_path TEXT NOT NULL,
            PRIMARY KEY (type_id, file_path)
        );

        CREATE TABLE IF NOT EXISTS type_files (
            type_id TEXT NOT NULL,
            file_path TEXT NOT NULL,
            PRIMARY KEY (type_id, file_path)
        );

        CREATE TABLE IF NOT EXISTS emitted_notes (
            note_path TEXT PRIMARY KEY,
            source_file TEXT NOT NULL,
            entity_id TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_call_edges_callee ON call_edges(callee_id);
        CREATE INDEX IF NOT EXISTS idx_call_edges_caller_file ON call_edges(caller_file);
        CREATE INDEX IF NOT EXISTS idx_call_edges_callee_file ON call_edges(callee_file);
        CREATE INDEX IF NOT EXISTS idx_type_references_file ON type_references(file_path);
        CREATE INDEX IF NOT EXISTS idx_type_files_file ON type_files(file_path);
        CREATE INDEX IF NOT EXISTS idx_emitted_notes_source ON emitted_notes(source_file);
        CREATE INDEX IF NOT EXISTS idx_emitted_notes_entity ON emitted_notes(entity_id);

        PRAGMA user_version = 1;
    ";

    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
    transaction.Commit();
}
```

### SHA256 Content Hashing (Fallback)
```csharp
// Source: .NET 8 System.Security.Cryptography docs
using System.Security.Cryptography;

public static string ComputeFileHash(string filePath)
{
    using var stream = File.OpenRead(filePath);
    var hashBytes = SHA256.HashData(stream);
    return Convert.ToHexString(hashBytes);
}
```

### System.CommandLine: Adding Incremental Flags
```csharp
// Source: System.CommandLine 2.0.3 docs
var incrementalOption = new Option<bool>("--incremental")
{
    Description = "Only regenerate notes for files changed since last run"
};

var fullRebuildOption = new Option<bool>("--full-rebuild")
{
    Description = "Force full analysis even when incremental state exists"
};

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Show what would be regenerated without writing files"
};

rootCommand.Add(incrementalOption);
rootCommand.Add(fullRebuildOption);
rootCommand.Add(dryRunOption);
```

### One-Hop Ripple Computation
```csharp
// Pseudocode for the ripple algorithm
public HashSet<string> ComputeAffectedFiles(
    IReadOnlySet<string> changedFiles,
    CallGraph newGraph,        // from re-analyzing changed files
    IncrementalState state)    // stored call edges + type refs
{
    var affected = new HashSet<string>(changedFiles, StringComparer.OrdinalIgnoreCase);

    // Collect all methods in changed files
    var changedMethods = newGraph.CallsOut.Keys
        .Concat(newGraph.CalledBy.Keys)
        .Where(m => changedFiles.Contains(GetFileForMethod(m)))
        .ToHashSet();

    // One-hop: callers of changed methods (from stored graph -- unchanged code)
    foreach (var method in changedMethods)
    {
        foreach (var caller in state.GetCallers(method))
        {
            var callerFile = state.GetFileForMethod(caller);
            if (callerFile is not null)
                affected.Add(callerFile);
        }
    }

    // One-hop: callees of changed methods (from new graph -- changed code calling out)
    foreach (var method in changedMethods)
    {
        foreach (var callee in newGraph.GetCallees(method))
        {
            var calleeFile = state.GetFileForMethod(callee);
            if (calleeFile is not null)
                affected.Add(calleeFile);
        }
    }

    return affected;
}
```

## SQLite Schema Design (Claude's Discretion)

### Recommendation: Six Tables with Indexes

**Rationale:** The schema must support these queries efficiently:
1. "What is the stored content hash for file X?" (file_hashes)
2. "What methods call into method Y?" (call_edges, indexed on callee_id)
3. "What files reference type Z?" (type_references, indexed on type_id)
4. "Which files contribute to partial type W?" (type_files)
5. "What notes were emitted from file X?" (emitted_notes, for stale cleanup)
6. "What was the last analyzed commit?" (run_state)

See the `MigrateToV1` code example above for the complete schema.

**Migration strategy:** Use `PRAGMA user_version` as version counter. Each version increment has a dedicated method (MigrateToV1, MigrateToV2, etc.). The initial version (0) means no schema exists. On open, check version and run all needed migrations sequentially. For Phase 4, only V1 is needed.

## Content Hash Algorithm (Claude's Discretion)

### Recommendation: SHA256

**Rationale:** SHA256 is built into .NET 8 (`System.Security.Cryptography.SHA256.HashData()`). It is fast enough for file-level hashing (hashing a 200-file solution takes milliseconds). It produces a 64-character hex string that fits comfortably in SQLite. No additional NuGet package required. Collision probability is negligible for this use case.

**Alternative considered:** xxHash is 10x faster but requires a NuGet package (System.IO.Hashing). For a 200-file codebase, SHA256 finishes in <50ms, so the speed difference is immaterial.

## Structural vs. Method-Level Change Detection (Claude's Discretion)

### Recommendation: Roslyn-Based Comparison Against Stored TypeInfo

After re-analyzing a changed file (pass 1), compare the newly extracted `TypeInfo` against what is stored in the state database:

| Field Compared | Change Type | Ripple Scope |
|----------------|-------------|--------------|
| TypeId missing from state | New type added | Normal (method-level only) |
| TypeId in state but not in re-analysis | Type deleted | Wide: all files referencing deleted TypeId |
| BaseClassFullName differs | Inheritance change | Wide: all files referencing this TypeId |
| InterfaceFullNames differ | Interface change | Wide: all files referencing this TypeId |
| Namespace differs | Namespace move | Wide: all files referencing this TypeId |
| Name differs (within same TypeId) | Not possible (TypeId includes name) | n/a |
| Only MethodIds differ | Method added/removed/renamed | Normal: one-hop callers/callees |
| Only method bodies changed | Implementation change | Normal: one-hop callees (new calls) |

This approach reuses the existing TypeInfo/MethodInfo domain models for comparison rather than introducing a separate diff mechanism.

## Index/Overview Note Regeneration (Claude's Discretion)

### Recommendation: Always Regenerate Index Notes

If the project produces any index or overview notes (e.g., a vault-level index page), regenerate them on every incremental run. They are cheap to produce (just iterating over existing data) and their correctness depends on the full set of notes. Skipping their regeneration when any note changes risks stale index content.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `SHA256Managed` class | `SHA256.HashData()` static method | .NET 5+ | One-liner API, no `using` block needed |
| System.Data.SQLite (third-party) | Microsoft.Data.Sqlite (official) | .NET Core era | Microsoft-maintained, lighter footprint, bundled native binaries |
| LibGit2Sharp 0.26.x (netstandard2.0) | LibGit2Sharp 0.31.0 (net8.0) | Dec 2024 | Native .NET 8 target, libgit2 v1.8.4, OpenSSH support |
| `SqliteConnection(string)` journal_mode in connection string | `PRAGMA journal_mode = 'wal'` after open | Microsoft.Data.Sqlite behavior | Connection string `journal_mode` keyword not supported; use PRAGMA |

**Deprecated/outdated:**
- `SHA256Managed`: Use `SHA256.HashData()` static API instead
- `System.Data.SQLite` (community package): Use `Microsoft.Data.Sqlite` (official)
- LibGit2Sharp `SimilarityOptions.Default`: Use `SimilarityOptions.Renames` explicitly for rename detection

## Key Architectural Decisions for Planner

### 1. Pipeline Modification Strategy
The existing `Pipeline.RunAsync()` method must be extended, not replaced. Add an optional `IncrementalContext` parameter that contains:
- The set of files to analyze (null = all files = full mode)
- The stored AnalysisResult data for unchanged files (to merge with fresh analysis)
- The state database handle for writing results after emission

### 2. AnalysisResult Merging
After incremental analysis, the result must MERGE freshly analyzed data with stored data:
- Fresh methods/types from re-analyzed files replace stored entries
- Stored methods/types from unanalyzed files are loaded from state
- Call graph is reconstructed: fresh edges for re-analyzed files + stored edges for unchanged files
- The merged AnalysisResult is passed to the emitter as if it were a full analysis

### 3. Selective Emission
The emitter must know which notes to actually write. Options:
- **Approach A:** Pass the emitter a full AnalysisResult but also a "dirty files" set. Emitter only writes notes for entities from dirty files.
- **Approach B:** Only pass the emitter the subset of methods/types that need regeneration.

Recommend Approach A: the emitter needs the full AnalysisResult for collision detection (wikilinks depend on ALL types, not just changed ones), but only writes notes for affected entities.

### 4. Stale Note Cleanup
After incremental emission, compare the set of emitted note paths against the stored `emitted_notes` table. Notes in the table that are NOT in the current emission set AND whose source file was re-analyzed (meaning the entity was deleted or renamed) should be deleted from the vault.

### 5. Transaction Boundary
All state updates (hashes, call edges, type references, emitted notes) must be written in a single SQLite transaction AFTER successful emission. If any pipeline stage fails, the state is not updated, ensuring the next run retries everything.

## Open Questions

1. **Partial class handling across multiple files**
   - What we know: `AnalysisResultBuilder.AddType()` uses `TryAdd` (first wins). TypeId includes the full qualified name, so all partials share the same TypeId.
   - What's unclear: When one partial file changes, does the current TypeAnalyzer produce a complete TypeInfo from just that file? Or does it need ALL partial files?
   - Recommendation: Test this empirically during implementation. If Roslyn's GetDeclaredSymbol on a partial class returns the MERGED symbol (all partials), then analyzing just the changed file gives complete TypeInfo. If not, all contributing files must be in the affected set. The `type_files` table in the schema supports this.

2. **MSBuildWorkspace loading time dominance**
   - What we know: Loading the solution via MSBuildWorkspace is often the slowest part of the pipeline (10-30 seconds for large solutions). Incremental mode cannot skip this.
   - What's unclear: Whether the perceived benefit of incremental mode is large enough when loading time is fixed.
   - Recommendation: Profile on the target codebases. If loading dominates, consider caching the workspace or solution object. This is a Phase 4+ optimization and should not block initial implementation.

3. **Git rename with content modification**
   - What we know: LibGit2Sharp detects renames with SimilarityOptions.Renames. A rename + content change is reported as a single "Renamed" entry with both OldPath and Path.
   - What's unclear: What similarity threshold to use for rename detection.
   - Recommendation: Use LibGit2Sharp's default threshold (50%). This matches git's default behavior and handles most refactoring scenarios correctly.

## Sources

### Primary (HIGH confidence)
- [Microsoft.Data.Sqlite NuGet](https://www.nuget.org/packages/Microsoft.Data.Sqlite/) - version 8.0.x verified for .NET 8
- [Microsoft.Data.Sqlite official docs](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) - connection strings, PRAGMA, ADO.NET API
- [LibGit2Sharp NuGet](https://www.nuget.org/packages/LibGit2Sharp) - version 0.31.0, .NET 8 target
- [LibGit2Sharp git-diff wiki](https://github.com/libgit2/libgit2sharp/wiki/git-diff) - TreeChanges API, CompareOptions, DiffTargets
- [LibGit2Sharp SimilarityOptions source](https://github.com/libgit2/libgit2sharp/blob/master/LibGit2Sharp/SimilarityOptions.cs) - RenameDetectionMode enum
- [SHA256.HashData .NET 8 docs](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.sha256.hashdata) - static one-shot API
- [SQLite PRAGMA user_version](https://levlaz.org/sqlite-db-migrations-with-pragma-user_version/) - schema migration pattern
- Existing codebase analysis (Pipeline.cs, IAnalyzer.cs, AnalysisResult.cs, ObsidianEmitter.cs, etc.)

### Secondary (MEDIUM confidence)
- [Roslyn GetSemanticModel docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.getsemanticmodel) - semantic model per SyntaxTree
- [LibGit2Sharp CompareOptions examples](https://csharp.hotexamples.com/examples/LibGit2Sharp/CompareOptions/-/php-compareoptions-class-examples.html) - community examples verified against wiki
- [Petabridge Incrementalist](https://petabridge.com/blog/introducing-incrementalist/) - precedent for git-based incremental .NET builds
- [Microsoft.Data.Sqlite connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings) - WAL mode via PRAGMA, not connection string

### Tertiary (LOW confidence)
- MSBuildWorkspace partial compilation behavior -- could not find definitive documentation on whether partial class symbols are merged across files. Needs empirical verification.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Microsoft.Data.Sqlite and LibGit2Sharp are well-documented, stable packages with .NET 8 support verified on NuGet
- Architecture: HIGH - Two-pass approach is well-understood; pipeline modification strategy is constrained by Roslyn's compilation model
- Pitfalls: HIGH - Based on direct codebase analysis and known Roslyn/SQLite behaviors
- Structural change detection: MEDIUM - Heuristic is sound but partial class edge case needs empirical validation
- Performance impact of MSBuildWorkspace loading: LOW - Need profiling data from target codebases

**Research date:** 2026-02-26
**Valid until:** 2026-03-26 (stable domain; library versions may receive patches)
