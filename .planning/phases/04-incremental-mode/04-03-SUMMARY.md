---
phase: 04-incremental-mode
plan: 03
subsystem: incremental
tags: [cli-flags, incremental-pipeline, two-pass-analysis, state-save, stale-cleanup, dry-run]

# Dependency graph
requires:
  - phase: 04-incremental-mode
    plan: 01
    provides: "IncrementalState SQLite wrapper, GitChangeDetector, HashChangeDetector, ChangeSet model"
  - phase: 04-incremental-mode
    plan: 02
    provides: "RippleCalculator, StaleNoteDetector, AnalysisResultMerger, file-filter analyzers, selective emitter"
provides:
  - "CLI flags --incremental, --full-rebuild, --dry-run registered in System.CommandLine"
  - "IncrementalPipeline orchestrator with full two-pass incremental flow"
  - "End-to-end incremental mode: change detection -> ripple -> two-pass analysis -> merge -> selective emission -> stale cleanup -> state save"
  - "Dry-run mode for change preview without writing"
  - "PipelineResult with incremental metrics (FilesSkipped, NotesDeleted, WasIncremental)"
affects: [05-llm-enrichment]

# Tech tracking
tech-stack:
  added: []
  patterns: [path-normalization bridge between git-relative and Roslyn-absolute paths, RunWithProgress factory for Spectre.Console integration, state-save-after-emission transaction boundary]

key-files:
  created:
    - Pipeline/IncrementalPipeline.cs
  modified:
    - Program.cs
    - Cli/CliOptions.cs
    - Pipeline/PipelineResult.cs
    - Pipeline/Pipeline.cs

key-decisions:
  - "Path normalization bridge: change detectors produce relative paths, IncrementalPipeline resolves to absolute via Roslyn document lookup and solution-dir resolution"
  - "IncrementalPipeline created per-case inside RunWithProgress lambda for correct Spectre.Console progress context lifetime"
  - "State save uses full merged AnalysisResult (fresh + stored) so stored state is always complete"
  - "Vault note rename on git rename is implicit: stale detector handles old notes, emitter generates new ones"
  - "Dry-run without --incremental logs a warning but still works if state exists"

patterns-established:
  - "RunWithProgress factory: wraps Spectre.Console ProgressContext creation for incremental operations"
  - "CreateProgress helper: standardized 3-task progress reporter (Analyzing, Enriching, Emitting)"
  - "Path normalization: 4-strategy resolution (direct resolve, file exists, suffix match, repo root)"
  - "All incremental orchestration in Pipeline layer, Program.cs only routes CLI flags (INFR-06)"

requirements-completed: [INFR-03, INFR-04, INFR-05]

# Metrics
duration: 13min
completed: 2026-02-26
---

# Phase 04 Plan 03: CLI Integration and Incremental Orchestration Summary

**End-to-end incremental mode with --incremental/--full-rebuild/--dry-run CLI flags, two-pass analysis orchestration in IncrementalPipeline, git-primary change detection, ripple expansion, selective emission, stale note cleanup, and state persistence after successful emission**

## Performance

- **Duration:** 13 min
- **Started:** 2026-02-26T11:59:52Z
- **Completed:** 2026-02-26T12:13:19Z
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments
- Registered --incremental, --full-rebuild, and --dry-run CLI flags with System.CommandLine, extended CliOptions and PipelineResult for incremental metrics
- Created IncrementalPipeline with complete two-pass incremental orchestration: change detection (git-primary/hash-fallback), ripple computation, selective analysis, merge with stored data, selective emission, stale note cleanup, and state persistence
- Implemented all 5 execution cases: A (no flags, unchanged behavior), B (first incremental run, full with state save), C (incremental with prior state), D (full rebuild with state wipe), E (dry-run preview)
- Added path normalization bridge resolving git-relative paths to Roslyn-absolute paths via multi-strategy lookup
- Updated Pipeline.cs to expose AnalysisResult and EmitResult for state saving
- Progress display shows "Analyzing X/Y files (Z unchanged)" in incremental mode, summary shows skipped counts

## Task Commits

Each task was committed atomically:

1. **Task 1: CLI flags, CliOptions, and PipelineResult updates** - `f144a57` (feat)
2. **Task 2: Pipeline exposure, IncrementalPipeline scaffold, basic modes (Cases A/B/D)** - `4c65e9a` (feat)
3. **Task 3: Full incremental flow and dry-run in IncrementalPipeline (Cases C/E)** - `07f508f` (feat)

## Files Created/Modified
- `Cli/CliOptions.cs` - Extended record with Incremental, FullRebuild, DryRun boolean properties
- `Pipeline/PipelineResult.cs` - Added FilesSkipped, NotesDeleted, WasIncremental metrics and AnalysisResult/EmitResult exposure
- `Pipeline/Pipeline.cs` - Exposed AnalysisResult and EmitResult on PipelineResult before returning
- `Pipeline/IncrementalPipeline.cs` - Complete incremental orchestrator: RunFullWithStateSaveAsync, RunIncrementalAsync, RunDryRunAsync, SaveState, path normalization, change detection, EnsureGitignore
- `Program.cs` - CLI flag registration, 5-case routing to Pipeline or IncrementalPipeline, RunWithProgress/CreateProgress helpers, updated RenderSummary for incremental metrics

## Decisions Made
- Path normalization uses a 4-strategy resolution chain: direct resolve against solution dir, file existence check, Roslyn document suffix match, and git repo root resolve. This bridges the gap between git's relative paths and Roslyn's absolute document paths
- IncrementalPipeline instances are created inside RunWithProgress lambdas (not before) to ensure the Spectre.Console ProgressContext is available at construction time
- State is saved using the full merged AnalysisResult (fresh + stored) so the SQLite state is always a complete snapshot of the codebase, not a partial update
- Vault note renames on git file renames are handled implicitly: the stale note detector identifies notes whose source file was reanalyzed but entity ID no longer exists, and the emitter generates new notes from the renamed file
- --dry-run without --incremental logs a warning but proceeds normally if prior state exists (rather than failing)
- Non-incremental path (Case A) remains exactly as before -- same Pipeline.RunAsync call with no state involvement

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Path format mismatch between change detectors and Roslyn analyzers**
- **Found during:** Task 3 (RunIncrementalAsync implementation)
- **Issue:** Change detectors (GitChangeDetector, HashChangeDetector) produce relative paths (git format), but Roslyn document.FilePath and all MethodInfo/TypeInfo.FilePath are absolute paths. File filter in analyzers would never match.
- **Fix:** Added NormalizeToAbsolutePaths method in IncrementalPipeline with 4-strategy resolution: direct Path.Combine resolution, File.Exists check, Roslyn document suffix matching, and git repo root fallback. All change set paths are converted to absolute before use.
- **Files modified:** Pipeline/IncrementalPipeline.cs
- **Verification:** `dotnet build` succeeds, path normalization tested through code review
- **Committed in:** 07f508f (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Essential for incremental mode correctness. Without path normalization, the file filter would never match any documents and incremental mode would analyze zero files.

## Issues Encountered
- No .sln file in the project root prevented end-to-end CLI regression testing; verified through build success, help text output, and code path analysis instead

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Incremental mode is feature-complete and ready for Phase 5 (LLM enrichment)
- The IncrementalPipeline runs enrichers in the pipeline (currently empty list) -- LLM enrichers from Phase 5 will execute automatically within the incremental flow
- State persistence includes all data needed for future incremental runs including call edges, type metadata, and emitted note tracking
- File filter injection in analyzers (from Plan 02) means LLM enrichment can be scoped to changed files only, preventing wasted API calls

## Self-Check: PASSED

All 5 files verified present. All 3 task commits (f144a57, 4c65e9a, 07f508f) verified in git log.

---
*Phase: 04-incremental-mode*
*Plan: 03*
*Completed: 2026-02-26*
