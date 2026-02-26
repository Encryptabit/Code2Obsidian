# Phase 4: Incremental Mode - Context

**Gathered:** 2026-02-26
**Status:** Ready for planning
**Review:** Approved (836293f6)

## Phase Boundary

Git-based change detection with selective regeneration for large codebases. When a developer re-runs the tool with `--incremental` after code changes, only affected files are reanalyzed and their notes regenerated. Without the flag (or on first run), full analysis is performed.

## Implementation Decisions

### Change Detection Scope
- Git diff as primary change detection; content hashing as fallback for non-git repos or dirty working trees
- One-hop ripple: changed files + their direct callers/callees are regenerated
- Two-pass approach: re-analyze changed files first to get current call graph, then use NEW graph to find affected neighbors
- Structural changes (renames, namespace moves, base class changes) trigger wider regeneration — all files referencing the changed type

### State Storage
- State stored as a hidden SQLite database in the output vault root (e.g., `.code2obsidian-state.db`)
- Contains: last commit hash, per-file content hashes, call graph relationships, type reference index
- Missing or corrupted state triggers silent full rebuild — no errors, no prompts, just works
- State file should be gitignored by default (machine-local, automatically rebuilt)

### CLI Behavior & Flags
- `--incremental` flag triggers incremental mode (INFR-03): only regenerates notes for files changed since last run
- Without `--incremental`, full analysis is always performed (default behavior unchanged from prior phases)
- When `--incremental` is used but no state exists, performs full analysis and saves state for next run (INFR-05)
- `--full-rebuild` flag forces state wipe + full analysis even when state exists (explicit rebuild trigger)
- `--dry-run` flag shows what would be regenerated without actually doing it (discretionary scope)
- Progress display shows skipped work: "Analyzing 12/200 files (188 unchanged)"

### Output Handling
- Stale notes (deleted classes/methods) are automatically deleted from the vault
- Deletion reporting only in verbose mode (-v); silent by default
- Git renames tracked and mapped to vault note renames (preserves Obsidian backlinks)

### Claude's Discretion
- Index/overview note regeneration strategy (always vs. only when affected)
- SQLite schema design and migration strategy
- Exact content hash algorithm choice
- How to detect structural vs. method-level changes in the diff

## Specific Ideas

- The two target codebases (Core API, CentralStation) are large (hundreds of files) — incremental mode is critical for daily use
- Progress display should make the speed benefit visible ("188 unchanged") to justify the feature
- Silent full rebuild on missing state means the tool "just works" even if someone clones a fresh vault

## Deferred Ideas

None — discussion stayed within phase scope

---

*Phase: 04-incremental-mode*
*Context gathered: 2026-02-26*
