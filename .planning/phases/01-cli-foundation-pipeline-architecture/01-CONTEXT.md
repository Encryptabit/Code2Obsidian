# Phase 1: CLI Foundation & Pipeline Architecture - Context

**Gathered:** 2026-02-25
**Status:** Ready for planning

<domain>
## Phase Boundary

Refactor the existing brownfield .NET CLI into a clean pipeline architecture (IAnalyzer → IEnricher → IEmitter) with proper CLI parsing, progress UX, and informational output. No new analysis capabilities — this phase restructures internals and polishes the CLI experience. Output format may change freely during refactor (no backward compatibility constraint).

</domain>

<decisions>
## Implementation Decisions

### CLI ergonomics (INFR-01)
- System.CommandLine for argument parsing with auto-generated help (per INFR-01)
- Accept both .sln file paths and directory paths as input — auto-detect which was given
- Default output to a `./vault/` folder adjacent to the input path, overridable with `-o`/`--output`
- Default verbosity is informational: progress bars plus key milestones ("Analyzing ProjectX...", "Emitting 42 notes...")
- Silent overwrite of existing output vault on re-run — always overwrite regardless of path, no --force required

### Progress reporting (INFR-02)
- Spectre.Console for progress bars (per INFR-02)
- Phased progress bars: separate bars per pipeline stage (Analyzing... → Enriching... → Emitting...)
- Detailed end-of-run summary: per-project counts, per-stage timing, total notes generated
- Auto-detect non-interactive terminal (piped output) and suppress progress bars automatically

### Error handling UX
- File parse failures: skip the file with a warning, continue analyzing the rest of the solution
- Bad input path: error message with suggestion ("Did you mean path/to/similar.sln?")
- Inline warnings during run AND a collected error summary at the end listing all skipped/failed files with reasons
- Granular exit codes: 0 = clean success, 1 = success with warnings (skipped files), 2 = fatal error
- Validate output path is writable before starting analysis — fail fast on permissions/disk issues

### Claude's Discretion
- Pipeline stage granularity and internal architecture (INFR-06)
- Exact error message wording and suggestion algorithm
- Internal logging strategy
- Progress bar visual styling within Spectre.Console
- End-of-run summary formatting

### Revision History
- Roadmap success criterion #4 (backward compatibility) removed per user decision — output format is unconstrained during refactor
- Silent overwrite behavior confirmed for all output paths (default and custom) — no guardrails needed per user decision

</decisions>

<specifics>
## Specific Ideas

No specific requirements — open to standard approaches

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 01-cli-foundation-pipeline-architecture*
*Context gathered: 2026-02-25*
