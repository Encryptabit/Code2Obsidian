# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-25)

**Core value:** A developer can point this tool at any C# solution and get an Obsidian vault that lets them understand what the code does, how it connects, and where the danger zones are -- without reading every file.
**Current focus:** Phase 05.1 - Structured XML Output & Progress Fix

## Current Position

Phase: 5.1 (Structured XML Output & Progress Fix)
Plan: 2 of 2 in current phase -- COMPLETE
Status: Phase 05.1 COMPLETE
Last activity: 2026-02-26 -- Completed 05.1-01 (Structured XML Output) [executed after 05.1-02]

Progress: [############] 100%

## Performance Metrics

**Velocity:**
- Total plans completed: 14
- Average duration: 7.9 min
- Total execution time: 1.95 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01 | 2 | 11 min | 5.5 min |
| 02 | 2 | 8 min | 4 min |
| 03 | 2 | 8 min | 4 min |
| 04 | 3 | 39 min | 13 min |
| 05 | 3 | 33 min | 11 min |
| 05.1 | 2 | 22 min | 11 min |

**Recent Trend:**
- Last 5 plans: 05-02 (9 min), 05-03 (16 min), 05.1-01 (~11 min), 05.1-02 (11 min)
- Trend: Consistent ~11 min for moderate scope plans

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- [Context]: Phase 1 success criterion #4 (backward compatibility) dropped — output format unconstrained during refactor
- [Roadmap]: 5 phases derived from 25 requirements; STRC/OUTP split across Phase 2 (types) and Phase 3 (metrics/output)
- [Roadmap]: Incremental mode (Phase 4) ordered before LLM enrichment (Phase 5) to prevent wasted API calls on unchanged files
- [Research]: MEAI (Microsoft.Extensions.AI) chosen for provider-agnostic LLM integration; Anthropic SDK beta risk contained to adapter layer
- [01-01]: Merged CallGraphAnalyzer into MethodAnalyzer to avoid double iteration over syntax trees
- [01-01]: Assembly name strings instead of IAssemblySymbol references for user-method filtering
- [01-01]: Per-method emission mode as default for Phase 1
- [01-02]: Pipeline reports progress through IProgress<PipelineProgress> abstraction (no Spectre.Console dependency in Pipeline)
- [01-02]: System.CommandLine SetAction with Func<ParseResult, CancellationToken, Task<int>> for async exit code return
- [01-02]: Exit code convention: 0 = clean success, 1 = success with warnings, 2 = fatal error
- [02-01]: Using alias resolves TypeInfo name collision between domain model and Microsoft.CodeAnalysis.TypeInfo
- [02-01]: AllInterfaces for implementor reverse index, Interfaces for display (STRC-04 transitive closure)
- [02-01]: RichSignatureFormat shared between TypeAnalyzer (constructors) and MethodAnalyzer (methods)
- [02-02]: Class notes are hub pages coexisting with method notes, not replacing them
- [02-02]: Wikilink resolution uses knownTypes lookup -- user types get wikilinks, external types get plain text
- [02-02]: DI dependencies deduped by TypeNoteFullName across ALL constructors
- [02-02]: Interface notes use separate "Known Implementors" body section
- [03-01]: Expression-bodied methods handled via MethodDeclarationSyntax cast for ExpressionBody access
- [03-01]: ShouldDescend pruning excludes lambdas, local functions, anonymous methods from complexity count
- [03-01]: Metadata propagation: project.Name flows through analyzer to domain model
- [03-02]: Collision detection scans both Types and Methods for containing type name disambiguation
- [03-02]: Pattern detection uses case-insensitive suffix matching (6 patterns: repository, controller, service, middleware, factory, handler)
- [03-02]: Interface notes hardcode dependency_count: 0 since interfaces have no DI constructors
- [03-02]: Type wikilinks in class body also use collision-aware resolution via ResolveTypeWikilink
- [04-01]: LibGit2Sharp Tree+DiffTargets overload does not accept CompareOptions; working dir renames appear as Delete+Add
- [04-01]: IncrementalState NOT IDisposable -- open-per-operation to avoid Windows file locking
- [04-01]: Full state replacement per run (DELETE all + INSERT) rather than incremental table updates
- [04-02]: RippleCalculator uses static methods consuming IncrementalState read API (no instance state)
- [04-02]: AnalysisResultMerger creates lightweight stubs from stored indexes for collision detection only
- [04-02]: Implementor merging passes through fresh result only -- type_references cannot reconstruct interface implementations
- [04-02]: File filter and dirty-files use optional constructor parameters with null defaults for backward compatibility
- [04-02]: EmitResult extended with EmittedNotes tuple list for state storage tracking
- [04-03]: Path normalization bridge: change detectors produce relative paths, IncrementalPipeline resolves to absolute via Roslyn document lookup
- [04-03]: IncrementalPipeline created per-case inside RunWithProgress lambda for correct Spectre.Console progress context lifetime
- [04-03]: State save uses full merged AnalysisResult so stored state is always a complete snapshot
- [04-03]: All incremental orchestration in Pipeline layer (IncrementalPipeline), Program.cs only routes CLI flags (INFR-06)
- [05-01]: Anthropic SDK 12.8.0 uses ClientOptions { ApiKey } constructor, not string constructor
- [05-01]: Unknown LLM providers fall back to OpenAI-compatible endpoint when config.Endpoint is set
- [05-01]: BodySource added as optional last parameter with null default to avoid breaking merger stub construction
- [05-02]: MEAI UsageDetails.InputTokenCount/OutputTokenCount are long?, cast to int for Interlocked.Add tracking
- [05-02]: OperationCanceledException propagated (not swallowed) to honor CancellationToken semantics
- [05-02]: Methods processed before types in enrichment ordering for deterministic behavior
- [05-03]: LlmEnricher reconstructed per Spectre.Console progress context via BuildEnrichersWithProgress for correct IProgress wiring
- [05-03]: Cost confirmation Func callback injected from Program.cs to keep LlmEnricher free of Spectre.Console dependency
- [05-03]: ## Summary section placed after danger callouts (methods) and after source path (types) for prominent placement
- [05.1-01]: XDocument.Parse wraps raw text in <root> element for safe multi-element XML parsing
- [05.1-01]: Triple fallback: XDocument -> regex with Singleline -> raw text wrapping (never crashes)
- [05.1-01]: WebUtility.HtmlDecode applied to all extracted XML values for entity decoding
- [05.1-01]: Tags stored as comma-separated string in SQLite, split on read
- [05.1-01]: SanitizeTag: lowercase + strip non-alphanumeric except hyphen for YAML safety
- [05.1-01]: Purpose rendered as bold first line in ## Summary, followed by detail paragraph
- [05.1-02]: AnsiConsole.Live() replaces Progress() to eliminate jitter from shared TaskDescriptionColumn resizing
- [05.1-02]: Lock-serialized IProgress callbacks for thread-safe concurrent enrichment updates
- [05.1-02]: RunFullPipeline delegates to RunWithProgress for single Live display implementation

### Roadmap Evolution

- Phase 05.1 inserted after Phase 5: Structured XML Output & Progress Fix (URGENT)

### Pending Todos

None yet.

### Blockers/Concerns

- Anthropic C# SDK 12.8.0 installed and working -- constructor uses ClientOptions, not string (verified)

## Session Continuity

Last session: 2026-02-26
Stopped at: Completed 05.1-01-PLAN.md (all Phase 05.1 plans done)
Resume file: N/A -- all plans executed
