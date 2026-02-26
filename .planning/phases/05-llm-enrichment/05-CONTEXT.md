# Phase 5: LLM Enrichment - Context

**Gathered:** 2026-02-26
**Status:** Ready for planning

<domain>
## Phase Boundary

Opt-in LLM-generated plain-English summaries for methods and classes, with provider flexibility via MEAI abstraction and content-hash-based caching. The tool produces a complete structural vault without `--enrich`; summaries are additive. Provider setup, prompt engineering, and cache storage are in scope. New structural analysis, new note types, or new CLI commands are not.

</domain>

<decisions>
## Implementation Decisions

### Summary content & tone
- Audience: experienced developers — assume domain knowledge, reference implementation details
- Length: Claude's discretion based on complexity — simple methods get one-liners, complex methods get short paragraphs
- Class summaries: responsibility-focused — describe the class's role and purpose, not its member list
- Placement: dedicated `## Summary` section in each note, alongside existing structural sections

### Provider configuration
- Config approach: JSON config file for defaults (`code2obsidian.llm.json`), CLI flags override — works for both local dev and CI
- Provider abstraction: Microsoft.Extensions.AI (MEAI) — any MEAI-compatible provider works automatically, no hardcoded provider list
- API keys: config file can reference env vars (`"apiKey": "$ANTHROPIC_API_KEY"`) with environment variable fallback
- Missing provider: if `--enrich` is passed with no LLM configured, interactively walk user through provider setup and save config for next time

### Cost estimation & confirmation
- Before enrichment on a large codebase, display estimated API cost and prompt for confirmation (LLM-05)
- Cost estimation should be provider-aware (use model pricing metadata from config or MEAI if available)
- Additionally show live token progress counter during enrichment (tokens used vs estimated) — user preference for token-centric UX
- Confirmation threshold: Claude's discretion on what constitutes "large" — small runs may auto-proceed, large runs prompt

### Caching & incremental behavior
- Cache storage: extend existing `.code2obsidian-state.db` SQLite with a summaries table — one store for everything
- Cache key: content hash covering signature, body, dependencies, and callers — any structural change triggers re-enrichment
- Incremental interaction: `--incremental --enrich` only sends changed/new methods to LLM — cached summaries reused for unchanged code
- Running `--enrich` twice on unchanged code makes zero API calls (LLM-03)

### Claude's Discretion
- Summary length calibration per method complexity
- Exact prompt design for LLM calls
- MEAI adapter registration pattern
- Token estimation algorithm
- Interactive setup flow UX details
- Error handling for LLM API failures (retries, timeouts, partial results)
- Confirmation threshold for cost prompt (what counts as "large")

</decisions>

<specifics>
## Specific Ideas

- Token counter during enrichment is more useful than cost counter for user awareness (cost varies by provider)
- Interactive first-run setup when `--enrich` is passed without config — save config so subsequent runs just work

</specifics>

<deferred>
## Deferred Ideas

- `--enrich-all` flag to bypass cache and force full re-enrichment (useful after model upgrade) — not in current requirements

</deferred>

---

*Phase: 05-llm-enrichment*
*Context gathered: 2026-02-26*
