# Phase 3: Output Quality & Metrics - Context

**Gathered:** 2026-02-25
**Status:** Ready for planning

<domain>
## Phase Boundary

The generated vault uses collision-free wikilinks, rich YAML frontmatter with computed metrics, pattern-based tags, and danger annotations that flag risky methods. This phase does NOT add new analysis capabilities — it enriches existing analysis output with queryable metadata, consistent linking, and risk visibility.

</domain>

<decisions>
## Implementation Decisions

### Claude's Discretion

User deferred all implementation decisions to Claude — the output is best evaluated in practice and adjusted iteratively. The following defaults will guide planning:

**Frontmatter schema:**
- Fields: `namespace`, `project`, `source_file`, `access_modifier`, `complexity` (cyclomatic), `fan_in`, `fan_out`, `pattern` (detected architectural pattern), `tags` (list)
- Naming convention: snake_case for all frontmatter keys (Dataview-friendly)
- Method notes and class notes share a common base schema; class notes add `member_count`, `dependency_count`
- Metrics that can't be computed (e.g., fan-in for a method with no callers) use `0` rather than omitting the field, so Dataview queries don't need null checks

**Danger thresholds:**
- Single-tier threshold with configurable values (not tiered warn/danger/critical)
- Default fan-in threshold: 10 (methods called by 10+ other methods get `danger/high-fan-in` tag)
- Default cyclomatic complexity threshold: 15 (methods above get `danger/high-complexity` tag)
- Thresholds configurable via CLI flags (`--fan-in-threshold`, `--complexity-threshold`)
- Danger tags appear in both frontmatter `tags` list and as inline callouts in the note body

**Pattern detection:**
- Patterns detected: `repository`, `controller`, `service`, `middleware`, `factory`, `handler`
- Detection by class name suffix matching (e.g., `*Repository`, `*Controller`) — simple and predictable
- Classes matching a pattern get a `pattern/<name>` tag in frontmatter
- No pattern = no tag (don't force a classification)

**Wikilink format:**
- Method notes: `[[ClassName.MethodName]]` — disambiguated by class prefix
- Class notes: `[[ClassName]]` — no prefix needed at class level
- Namespace collision (two classes with same name in different namespaces): `[[Namespace.ClassName]]` fallback
- External types (outside analyzed solution): plain text, not wikilinks — no broken links in the vault

</decisions>

<specifics>
## Specific Ideas

No specific requirements — open to standard approaches. User will iterate after seeing output.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope.

</deferred>

---

*Phase: 03-output-quality-metrics*
*Context gathered: 2026-02-25*
