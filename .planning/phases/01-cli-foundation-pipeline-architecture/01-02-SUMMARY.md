---
phase: 01-cli-foundation-pipeline-architecture
plan: 02
subsystem: infra
tags: [system-commandline, spectre-console, cli, pipeline, progress-bars, csharp]

# Dependency graph
requires:
  - phase: 01-01
    provides: IAnalyzer/IEnricher/IEmitter interfaces, domain models, MethodAnalyzer, ObsidianEmitter, SolutionLoader, System.CommandLine and Spectre.Console packages
provides:
  - Pipeline orchestrator with stage timing and IProgress<PipelineProgress> abstraction
  - PipelineResult with exit code logic (0/1/2) and per-stage metrics
  - CliOptions strongly-typed record for resolved CLI arguments
  - System.CommandLine RootCommand with auto-generated help/version
  - Spectre.Console progress bars wrapping pipeline execution
  - End-of-run summary table with per-stage timing and note counts
  - Input path resolution (directory auto-detect, Did-you-mean suggestions)
  - Output path validation (writable check before analysis)
affects: [phase-02, phase-03, phase-04, phase-05]

# Tech tracking
tech-stack:
  added: []
  patterns: [pipeline-orchestrator, progress-abstraction, thin-cli-shell, path-resolution, exit-code-convention]

key-files:
  created:
    - Cli/CliOptions.cs
    - Pipeline/Pipeline.cs
    - Pipeline/PipelineResult.cs
    - Pipeline/PipelineProgress.cs
  modified:
    - Program.cs

key-decisions:
  - "PipelineProgress record with IProgress<T> abstraction decouples Pipeline from Spectre.Console for testability"
  - "System.CommandLine SetAction with Func<ParseResult, CancellationToken, Task<int>> for async exit code return"
  - "Pipeline.cs has no Spectre.Console dependency -- progress reporting is through IProgress<PipelineProgress>"
  - "PipelineStage enum and PipelineProgress record defined in separate file for clean separation"

patterns-established:
  - "Thin CLI shell: Program.cs contains only arg parsing, progress wrapping, and pipeline composition -- no analysis logic"
  - "Progress abstraction: Pipeline reports through IProgress<PipelineProgress>; UI layer adapts to Spectre.Console ProgressTask"
  - "Exit code convention: 0 = clean success, 1 = success with warnings, 2 = fatal error"
  - "Path resolution: .sln files directly, directory auto-detect, Did-you-mean suggestions for bad paths"
  - "Output validation: Directory.CreateDirectory + write test file before any analysis begins"

requirements-completed: [INFR-01, INFR-02]

# Metrics
duration: 6min
completed: 2026-02-26
---

# Phase 1 Plan 02: CLI Shell and Pipeline Wiring Summary

**System.CommandLine CLI with auto-generated help, Spectre.Console progress bars per pipeline stage, Pipeline orchestrator with IProgress abstraction, and end-of-run summary table**

## Performance

- **Duration:** 6 min
- **Started:** 2026-02-26T04:27:02Z
- **Completed:** 2026-02-26T04:33:11Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Created Pipeline orchestrator that wires analysis/enrichment/emission stages with per-stage timing and IProgress<PipelineProgress> abstraction
- Completely rewrote Program.cs (493 lines -> 317 lines) as a thin CLI shell using System.CommandLine with auto-generated help, --version, and usage errors
- Integrated Spectre.Console progress bars that update during analysis/enrichment/emission stages
- Implemented end-of-run summary table showing per-stage timing, project/file counts, and notes generated
- Added input path resolution with .sln auto-detection, directory support, and Did-you-mean suggestions
- Implemented granular exit codes (0/1/2) and comprehensive error handling

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Pipeline orchestrator, PipelineResult, and CliOptions** - `6392f8e` (feat)
2. **Task 2: Rewrite Program.cs with System.CommandLine, Spectre.Console progress, and error UX** - `501d587` (feat)

## Files Created/Modified
- `Cli/CliOptions.cs` - Strongly-typed CLI options record (SolutionPath, OutputDirectory)
- `Pipeline/Pipeline.cs` - Pipeline orchestrator with stage timing and progress reporting
- `Pipeline/PipelineResult.cs` - Per-stage timing, counts, warnings, and exit code logic
- `Pipeline/PipelineProgress.cs` - PipelineStage enum and PipelineProgress record for UI-agnostic progress reporting
- `Program.cs` - Complete rewrite as thin System.CommandLine shell with Spectre.Console progress and summary table

## Decisions Made

1. **SetAction with Task<int> return type** - System.CommandLine 2.0.3 provides `SetAction(Func<ParseResult, CancellationToken, Task<int>>)` which directly returns the exit code from the async handler. This is cleaner than setting ExitCode on the ParseResult action.

2. **PipelineProgress in separate file** - The PipelineStage enum and PipelineProgress record are in their own file (PipelineProgress.cs) rather than inline in Pipeline.cs, keeping each file focused and making the progress types easy to reference from both Pipeline and Program.cs.

3. **Markup.Escape for user-provided strings** - All user-provided strings (error messages, file paths) passed to AnsiConsole.MarkupLine are wrapped with Markup.Escape() to prevent Spectre.Console markup injection from paths containing brackets.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed System.CommandLine 2.0.3 SetAction API**
- **Found during:** Task 2 (Program.cs rewrite)
- **Issue:** The plan's research showed `SetAction` with a `(ParseResult, CancellationToken)` handler that sets `parseResult.Action!.ExitCode`. In System.CommandLine 2.0.3, `CommandLineAction` does not have an `ExitCode` property. The correct overload is `SetAction(Func<ParseResult, CancellationToken, Task<int>>)` which returns the exit code directly.
- **Fix:** Changed SetAction lambda to return `Task<int>` instead of setting ExitCode property.
- **Files modified:** Program.cs
- **Verification:** `dotnet build` succeeds with zero errors
- **Committed in:** 501d587 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Trivial API fix for System.CommandLine 2.0.3. No scope creep.

## Issues Encountered
None beyond the System.CommandLine API mismatch noted above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Phase 1 is fully complete: INFR-01 (System.CommandLine CLI), INFR-02 (Spectre.Console progress), INFR-06 (pipeline architecture) all delivered
- The tool accepts .sln files and directories, produces markdown output, shows progress bars, and displays a summary table
- Pipeline architecture is extensible: new analyzers can be added to the list in Program.cs, enrichers are wired but empty for Phase 1
- Phase 2 can add new analyzers (type/namespace analysis) by implementing IAnalyzer and adding to the composition list

## Self-Check: PASSED

- All 5 files verified present on disk (Cli/CliOptions.cs, Pipeline/Pipeline.cs, Pipeline/PipelineResult.cs, Pipeline/PipelineProgress.cs, Program.cs)
- Commit 6392f8e verified in git log
- Commit 501d587 verified in git log
- `dotnet build` passes with 0 errors, 0 warnings
- `dotnet run -- --help` displays auto-generated help with input argument and --output option
- `dotnet run -- --version` displays version info
- Program.cs has no `using Microsoft.CodeAnalysis` imports

---
*Phase: 01-cli-foundation-pipeline-architecture*
*Completed: 2026-02-26*
