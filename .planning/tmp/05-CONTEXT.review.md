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

### Token estimation & controls
- No cost estimation — token estimation is the useful metric (provider-agnostic)
- Display: live progress counter during enrichment showing tokens used vs estimated
- No token limit flag — user controls scope via file selection or `--incremental`
- No confirmation prompt — passing `--enrich` is the confirmation, start immediately

### Caching & incremental behavior
- Cache storage: extend existing `.code2obsidian.db` SQLite with a summaries table — one store for everything
- Cache invalidation: any structural change triggers re-enrichment (signature, body, dependencies, or callers change — not just body hash)
- Incremental interaction: `--incremental --enrich` only sends changed/new methods to LLM — cached summaries reused for unchanged code
- Force re-enrichment: `--enrich-all` flag bypasses cache and re-enriches every method/class (useful after model upgrade)

### Claude's Discretion
- Summary length calibration per method complexity
- Exact prompt design for LLM calls
- MEAI adapter registration pattern
- Token estimation algorithm
- Interactive setup flow UX details
- Error handling for LLM API failures (retries, timeouts, partial results)

</decisions>

<specifics>
## Specific Ideas

- Token counter is more useful than cost counter since cost-per-token varies by provider
- Interactive first-run setup when `--enrich` is passed without config — save config so subsequent runs just work
- `--enrich-all` as the escape hatch for full re-enrichment (model upgrade scenario)

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 05-llm-enrichment*
*Context gathered: 2026-02-26*
