---
phase: 05-llm-enrichment
plan: 02
subsystem: enrichment
tags: [llm, ichatclient, meai, prompts, cost-estimation, semaphore, cache, interlocked]

# Dependency graph
requires:
  - phase: 05-llm-enrichment
    plan: 01
    provides: "IChatClient factory, SummaryCache, ContentHasher, LlmConfig, EnrichedResult dictionaries"
  - phase: 01-pipeline
    provides: "IEnricher interface, PipelineProgress, AnalysisResult, MethodInfo, TypeInfo, CallGraph"
provides:
  - "LlmEnricher: IEnricher implementation with cache-first lookup, SemaphoreSlim concurrency, per-entity error resilience"
  - "PromptBuilder: system and user prompt construction for methods and types, optimized for experienced developers"
  - "CostEstimator: chars/4 token estimation, batch cost calculation, confirmation threshold at 50 entities"
  - "Token usage tracking via Interlocked (InputTokensUsed, OutputTokensUsed) for live progress reporting"
affects: [05-03-PLAN]

# Tech tracking
tech-stack:
  added: []
  patterns: [cache-first enrichment, SemaphoreSlim-bounded parallel LLM calls, per-entity error isolation, Interlocked token accumulation]

key-files:
  created:
    - Enrichment/LlmEnricher.cs
    - Enrichment/Prompts/PromptBuilder.cs
    - Enrichment/CostEstimator.cs
  modified: []

key-decisions:
  - "UsageDetails.InputTokenCount/OutputTokenCount are long? in MEAI 10.3.0, cast to int for Interlocked.Add tracking"
  - "OperationCanceledException propagated (not swallowed) to honor CancellationToken semantics"
  - "Methods processed before types in enrichment ordering for consistent deterministic behavior"

patterns-established:
  - "Cache-first enrichment: TryGet before every LLM call, Put after success, zero API calls on second run"
  - "Per-entity error isolation: try/catch per entity, log warning, increment EntitiesFailed, continue"
  - "Prompt structure: structured key-value format (Signature, Class, Complexity, Calls, Called by) for LLM context"

requirements-completed: [LLM-01, LLM-03, LLM-05]

# Metrics
duration: 9min
completed: 2026-02-26
---

# Phase 5 Plan 2: Core Enrichment Engine Summary

**LlmEnricher with cache-first IChatClient integration, structured method/type prompts for experienced developers, and chars/4 cost estimation with confirmation threshold**

## Performance

- **Duration:** 9 min
- **Started:** 2026-02-26T19:06:24Z
- **Completed:** 2026-02-26T19:15:37Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- Built PromptBuilder with structured method prompts (signature, doc comment, callees, callers, complexity) and type prompts (kind, inheritance, interfaces, DI dependencies, member counts)
- Implemented CostEstimator with chars/4 token heuristic, batch cost calculation from per-token pricing, and confirmation threshold at 50 uncached entities
- Created LlmEnricher implementing IEnricher with cache-first SummaryCache lookup, SemaphoreSlim-bounded parallel LLM calls, per-entity error resilience, and Interlocked token tracking

## Task Commits

Each task was committed atomically:

1. **Task 1: PromptBuilder and CostEstimator** - `cb98041` (feat)
2. **Task 2: LlmEnricher with cache-first, concurrency, and error resilience** - `7c44318` (feat)

## Files Created/Modified
- `Enrichment/Prompts/PromptBuilder.cs` - Static class with SystemPrompt, BuildMethodPrompt, BuildTypePrompt, EstimatePromptTokens
- `Enrichment/CostEstimator.cs` - Static class with EstimateTokens (chars/4), EstimateCost (batch), ShouldConfirm (threshold 50)
- `Enrichment/LlmEnricher.cs` - Sealed class implementing IEnricher with IChatClient, SummaryCache, SemaphoreSlim, Interlocked tracking

## Decisions Made
- MEAI 10.3.0 UsageDetails.InputTokenCount/OutputTokenCount are `long?` not `int?` -- cast to int for Interlocked.Add (discovered during build verification)
- OperationCanceledException is explicitly rethrown (not swallowed by the per-entity catch) to honor CancellationToken contract
- Methods enriched before types for consistent ordering; both share the same SemaphoreSlim instance

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] MEAI token count type mismatch (long? vs int)**
- **Found during:** Task 2 (LlmEnricher build verification)
- **Issue:** Plan specified `response.Usage?.InputTokenCount is int inputTokens` but MEAI 10.3.0 UsageDetails uses `long?` not `int?`
- **Fix:** Changed pattern match to `is long inputTokens` with `(int)inputTokens` cast for Interlocked.Add
- **Files modified:** Enrichment/LlmEnricher.cs (lines 154, 156, 210, 212)
- **Verification:** `dotnet build` succeeds with zero errors
- **Committed in:** 7c44318 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Minor type mismatch in MEAI API surface. No scope creep.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- LlmEnricher is ready to be instantiated with an IChatClient and plugged into the pipeline
- CLI integration (Plan 03) can wire up config loading, LlmEnricher construction, and --enrich flag
- All 3 enrichment files compile cleanly with the existing pipeline architecture

## Self-Check: PASSED

All 3 created files verified present. Both task commits (cb98041, 7c44318) verified in git log. Build succeeds with zero errors and zero warnings.

---
*Phase: 05-llm-enrichment*
*Completed: 2026-02-26*
