---
phase: 04-incremental-mode
plan: 01
subsystem: incremental
tags: [sqlite, libgit2sharp, change-detection, sha256, state-persistence]

# Dependency graph
requires:
  - phase: 03-output-quality
    provides: "Existing pipeline architecture, domain models (MethodInfo, TypeInfo, TypeId, MethodId)"
provides:
  - "IChangeDetector abstraction with git-primary/hash-fallback implementations"
  - "ChangeSet domain model with rename tracking and convenience properties"
  - "IncrementalState SQLite wrapper with 9-table schema and full CRUD"
  - "StateSchema with PRAGMA user_version migration and WAL mode"
affects: [04-02-PLAN, 04-03-PLAN]

# Tech tracking
tech-stack:
  added: [Microsoft.Data.Sqlite 8.0.11, LibGit2Sharp 0.31.0]
  patterns: [PRAGMA user_version migration, WAL mode, open-per-operation SQLite connections, parameterized inserts, full state replacement per run]

key-files:
  created:
    - Incremental/ChangeSet.cs
    - Incremental/IChangeDetector.cs
    - Incremental/GitChangeDetector.cs
    - Incremental/HashChangeDetector.cs
    - Incremental/StateSchema.cs
    - Incremental/IncrementalState.cs
  modified:
    - Code2Obsidian.csproj

key-decisions:
  - "LibGit2Sharp Tree+DiffTargets overload does not accept CompareOptions; working directory renames appear as Delete+Add pairs"
  - "IncrementalState is NOT IDisposable -- connections opened and closed per operation to avoid Windows file locking"
  - "Full state replacement per run (DELETE all + INSERT) rather than incremental table updates for simplicity"
  - "Insert helpers extracted to private static methods for readability in SaveState"
  - "Connection string built from const prefix + DbPath rather than string interpolation in OpenConnection"

patterns-established:
  - "PRAGMA user_version migration: check version, run MigrateToVN sequentially"
  - "Open-per-operation SQLite: no held connections during pipeline execution"
  - "Graceful corruption: catch SqliteException in all reads, return empty defaults"
  - "IChangeDetector returns null on detection failure (caller falls back to HashChangeDetector)"

requirements-completed: [INFR-04, INFR-05]

# Metrics
duration: 17min
completed: 2026-02-26
---

# Phase 04 Plan 01: Incremental Infrastructure Summary

**SQLite 9-table state storage with PRAGMA migration, LibGit2Sharp git change detector with rename tracking, SHA256 hash fallback detector, and ChangeSet domain model**

## Performance

- **Duration:** 17 min
- **Started:** 2026-02-26T11:27:25Z
- **Completed:** 2026-02-26T11:44:02Z
- **Tasks:** 2
- **Files modified:** 7

## Accomplishments
- Installed Microsoft.Data.Sqlite 8.0.11 and LibGit2Sharp 0.31.0 NuGet packages matching .NET 8 target
- Created ChangeSet domain model with FileChangeKind enum, FileChange record, and convenience properties (ChangedFilePaths, DeletedFilePaths, RenamedPaths)
- Implemented GitChangeDetector using LibGit2Sharp with committed tree diff + working directory diff, rename detection via SimilarityOptions.Renames, and .cs file filtering
- Implemented HashChangeDetector using SHA256.HashData for non-git repos with Added/Modified/Deleted detection
- Created 9-table SQLite schema (6 base + 3 metadata) with WAL mode, 10 indexes, and PRAGMA user_version migration
- Built IncrementalState with full read/write/delete API covering all 9 tables, graceful corruption handling, and parameterized SQL

## Task Commits

Each task was committed atomically:

1. **Task 1: NuGet packages and ChangeSet domain model + change detectors** - `7faf2cc` (feat)
2. **Task 2: SQLite state storage with complete 9-table schema** - `46b086f` (feat)

## Files Created/Modified
- `Code2Obsidian.csproj` - Added Microsoft.Data.Sqlite 8.0.11 and LibGit2Sharp 0.31.0 package references
- `Incremental/ChangeSet.cs` - FileChangeKind enum, FileChange record, ChangeSet record with convenience properties
- `Incremental/IChangeDetector.cs` - Change detection abstraction returning ChangeSet or null
- `Incremental/GitChangeDetector.cs` - LibGit2Sharp implementation with committed + working tree comparison
- `Incremental/HashChangeDetector.cs` - SHA256 fallback for non-git repos
- `Incremental/StateSchema.cs` - 9-table schema creation with indexes and PRAGMA user_version
- `Incremental/IncrementalState.cs` - SQLite state read/write/delete for all 9 tables

## Decisions Made
- LibGit2Sharp `Diff.Compare<TreeChanges>(Tree, DiffTargets)` overload does not accept CompareOptions -- working directory rename detection is not available through this API; renames in working directory appear as Delete + Add pairs (committed changes DO get rename detection)
- IncrementalState uses open-per-operation pattern (not IDisposable) per research guidance on SQLite Windows file locking
- SaveState performs full state replacement (DELETE all + INSERT) rather than incremental updates -- simpler and correct since pipeline produces complete state each run
- Private static InsertX helper methods extracted from SaveState for readability

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed LibGit2Sharp API mismatch for DiffTargets overload**
- **Found during:** Task 1 (GitChangeDetector compilation)
- **Issue:** Plan specified `repo.Diff.Compare<TreeChanges>(Tree, DiffTargets, CompareOptions)` but this overload does not exist in LibGit2Sharp 0.31.0. The Tree+DiffTargets signature only accepts `(Tree, DiffTargets)`.
- **Fix:** Removed CompareOptions parameter from working directory diff call. Rename detection still works for committed changes (tree-to-tree overload).
- **Files modified:** Incremental/GitChangeDetector.cs
- **Verification:** `dotnet build` succeeds with 0 errors
- **Committed in:** 7faf2cc (Task 1 commit)

**2. [Rule 3 - Blocking] Removed stale file from incorrect Node.js path**
- **Found during:** Task 2 (build verification)
- **Issue:** Earlier Node.js script created `ProjectsCode2ObsidianIncrementalIncrementalState.cs` at project root due to Windows path.join behavior. Roslyn picked it up as a source file with syntax errors.
- **Fix:** Deleted the stale file.
- **Files modified:** Deleted `ProjectsCode2ObsidianIncrementalIncrementalState.cs`
- **Verification:** `dotnet build` succeeds after removal
- **Committed in:** Not committed (file was never staged)

---

**Total deviations:** 2 auto-fixed (1 bug, 1 blocking)
**Impact on plan:** Both auto-fixes were necessary for compilation. No scope creep. Working directory rename detection limitation is documented and acceptable.

## Issues Encountered
- Bash heredoc quoting on Windows Git Bash conflicted with C# string interpolation and raw string literals in large files -- resolved by writing content to .txt temp file via Write tool then copying via bash cp command

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All 6 Incremental/ files compile and are ready for consumption by plan 04-02 (RippleCalculator, AnalysisResultMerger, selective emission)
- IncrementalState API is complete -- downstream plans consume it without modification
- Schema includes all 9 tables upfront so no migration changes needed in 04-02 or 04-03

---
*Phase: 04-incremental-mode*
*Plan: 01*
*Completed: 2026-02-26*

## Self-Check: PASSED

All 7 files verified present. Both task commits (7faf2cc, 46b086f) verified in git log.
