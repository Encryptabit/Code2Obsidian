---
phase: 01-cli-foundation-pipeline-architecture
plan: 01
subsystem: infra
tags: [roslyn, pipeline, domain-models, csharp, analysis, emission]

# Dependency graph
requires: []
provides:
  - IAnalyzer, IEnricher, IEmitter pipeline stage interfaces
  - Domain models (MethodId, TypeId, MethodInfo, CallGraph) with string-based IDs
  - AnalysisResult and AnalysisResultBuilder for analysis output
  - SolutionLoader with MSBuild registration
  - MethodAnalyzer porting Roslyn extraction logic from Program.cs
  - ObsidianEmitter porting markdown generation from Program.cs
  - System.CommandLine 2.0.3 and Spectre.Console 0.54.0 NuGet packages
affects: [01-02, phase-02, phase-03]

# Tech tracking
tech-stack:
  added: [System.CommandLine 2.0.3, Spectre.Console 0.54.0]
  patterns: [pipeline-stages, domain-model-boundary, builder-pattern, readonly-record-struct]

key-files:
  created:
    - Analysis/IAnalyzer.cs
    - Analysis/AnalysisResult.cs
    - Analysis/AnalysisResultBuilder.cs
    - Analysis/Models/MethodId.cs
    - Analysis/Models/TypeId.cs
    - Analysis/Models/MethodInfo.cs
    - Analysis/Models/CallGraph.cs
    - Analysis/Analyzers/MethodAnalyzer.cs
    - Analysis/Analyzers/AnalysisHelpers.cs
    - Loading/SolutionLoader.cs
    - Loading/AnalysisContext.cs
    - Enrichment/IEnricher.cs
    - Enrichment/EnrichedResult.cs
    - Emission/IEmitter.cs
    - Emission/EmitResult.cs
    - Emission/ObsidianEmitter.cs
  modified:
    - Code2Obsidian.csproj

key-decisions:
  - "Merged CallGraphAnalyzer into MethodAnalyzer to avoid double iteration over syntax trees"
  - "Used readonly record struct for MethodId and TypeId (value semantics, zero-allocation equality)"
  - "Assembly name strings instead of IAssemblySymbol references for user-method filtering (more robust across compilations)"
  - "Extracted AnalysisHelpers as shared utility for IsUserMethod, GetMethodDocstring, NormalizeSpaces"
  - "Per-method emission mode as default for Phase 1 (--per-file/--per-method distinction removed per research)"

patterns-established:
  - "Pipeline boundary: Roslyn types confined to Analysis/ folder; Emission/ uses only domain models"
  - "MethodId.FromSymbol() as the single Roslyn-to-domain conversion point"
  - "Builder pattern for AnalysisResult accumulation during multi-analyzer passes"
  - "Sealed classes/records for immutable pipeline stage outputs"

requirements-completed: [INFR-06]

# Metrics
duration: 5min
completed: 2026-02-26
---

# Phase 1 Plan 01: Pipeline Architecture Extraction Summary

**Pipeline stage interfaces (IAnalyzer/IEnricher/IEmitter), domain models with string-based IDs, and ported analysis+emission logic from monolithic Program.cs**

## Performance

- **Duration:** 5 min
- **Started:** 2026-02-26T04:18:57Z
- **Completed:** 2026-02-26T04:24:01Z
- **Tasks:** 2
- **Files modified:** 17

## Accomplishments
- Extracted pipeline architecture with IAnalyzer, IEnricher (stub), and IEmitter interfaces
- Created domain models (MethodId, TypeId, MethodInfo, CallGraph) that enforce Roslyn-to-domain boundary using string-based IDs
- Ported method extraction, call graph construction, XML doc parsing, and markdown emission from 493-line Program.cs into dedicated Analysis/ and Emission/ folders
- Added System.CommandLine 2.0.3 and Spectre.Console 0.54.0 packages for Plan 02

## Task Commits

Each task was committed atomically:

1. **Task 1: Create domain models, pipeline interfaces, and loading infrastructure** - `5ea84a3` (feat)
2. **Task 2: Port analysis and emission logic into pipeline implementations** - `9f4badd` (feat)

## Files Created/Modified
- `Code2Obsidian.csproj` - Added System.CommandLine and Spectre.Console package references
- `Analysis/Models/MethodId.cs` - Readonly record struct wrapping stable method ID string with Roslyn factory method
- `Analysis/Models/TypeId.cs` - Readonly record struct wrapping stable type ID string with Roslyn factory method
- `Analysis/Models/MethodInfo.cs` - Pure domain method model (no Roslyn references)
- `Analysis/Models/CallGraph.cs` - Bidirectional call graph with string-based edges
- `Analysis/AnalysisResult.cs` - Immutable analysis output container
- `Analysis/AnalysisResultBuilder.cs` - Mutable builder for accumulating analysis findings
- `Analysis/IAnalyzer.cs` - Analysis pipeline stage interface
- `Analysis/Analyzers/MethodAnalyzer.cs` - Method extraction + call graph in single pass (merged from two planned analyzers)
- `Analysis/Analyzers/AnalysisHelpers.cs` - Shared IsUserMethod, GetMethodDocstring, NormalizeSpaces utilities
- `Loading/SolutionLoader.cs` - MSBuild registration + solution loading (ported from Program.cs)
- `Loading/AnalysisContext.cs` - Loaded solution + project assembly names container
- `Enrichment/IEnricher.cs` - Enrichment pipeline stage interface (stub for Phase 1)
- `Enrichment/EnrichedResult.cs` - Passthrough wrapper for analysis result
- `Emission/IEmitter.cs` - Emission pipeline stage interface
- `Emission/EmitResult.cs` - Emission output record (notes written + warnings)
- `Emission/ObsidianEmitter.cs` - Per-method markdown generation using domain models

## Decisions Made

1. **Merged CallGraphAnalyzer into MethodAnalyzer** - Both analyzers iterate the same syntax trees. Merging avoids a redundant second pass over all documents. CallGraph.AddEdge() automatically maintains reverse edges, so no separate reverse-edge step is needed. This was explicitly offered as the preferred approach in the plan.

2. **Created AnalysisHelpers as shared utility** - Rather than duplicating IsUserMethod and doc comment extraction, extracted them as static utility methods in a shared class. This keeps MethodAnalyzer focused on iteration and conversion.

3. **Used assembly name strings instead of IAssemblySymbol** - The original Program.cs compared IAssemblySymbol references for user-method filtering. The new code uses `ContainingAssembly.Name` string comparison via the AnalysisContext.ProjectAssemblyNames set, which is more robust across separate compilation instances.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed .NET 8 incompatibility with HashSet.AsReadOnly()**
- **Found during:** Task 1 (CallGraph.cs)
- **Issue:** `HashSet<T>.AsReadOnly()` is a .NET 9+ API. Project targets .NET 8.
- **Fix:** Replaced with a lightweight `EmptyMethodIdSet` class implementing `IReadOnlySet<MethodId>` for the empty set constant.
- **Files modified:** Analysis/Models/CallGraph.cs
- **Verification:** `dotnet build` succeeds with zero errors
- **Committed in:** 5ea84a3 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Trivial fix for .NET 8 compatibility. No scope creep.

## Issues Encountered
None beyond the .NET 8 API compatibility issue noted above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All pipeline infrastructure is in place for Plan 02 to rewrite Program.cs as thin CLI shell
- System.CommandLine and Spectre.Console packages are installed and ready
- Old Program.cs still compiles and works independently alongside the new pipeline code
- Plan 02 will wire the pipeline stages together and replace the monolithic Main method

## Self-Check: PASSED

- All 17 files verified present on disk
- Commit 5ea84a3 verified in git log
- Commit 9f4badd verified in git log
- `dotnet build` passes with 0 errors, 0 warnings

---
*Phase: 01-cli-foundation-pipeline-architecture*
*Completed: 2026-02-26*
