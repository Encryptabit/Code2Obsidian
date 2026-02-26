# Phase 2: Class & Type Analysis - Context

**Gathered:** 2026-02-25
**Status:** Ready for planning

<domain>
## Phase Boundary

Every class and interface in the analyzed solution gets its own Obsidian note with structural context — inheritance, interfaces, members, DI dependencies, and rich method signatures. Per-method notes continue to exist; class notes act as hub pages linking to them.

**New notes created in Phase 2** (class notes, interface notes) use correct `[[ClassName.MethodName]]` and `[[ClassName]]` wikilink format from the start. Existing method note link rewrites (fixing "Calls →" / "Called-by ←" to use `[[ClassName.MethodName]]` format) are deferred to Phase 3 (OUTP-01: collision-free wikilinks).

Phase 2 introduces minimal YAML frontmatter for type-relationship fields only (base class, interfaces, namespace). Phase 3 owns the full frontmatter expansion: metrics, pattern tags, Dataview-compatible fields, and danger annotations. Phase 5 handles LLM enrichment (the "What it does" / "Improvements" TODO sections).

</domain>

<decisions>
## Implementation Decisions

### Note architecture
- Class notes are NEW hub pages — they link to per-method notes, not replace them
- Both class notes and method notes exist in the vault simultaneously
- Class notes create hub nodes in Graph View: class → methods, class → base class, class → interfaces
- Interface notes include a "Known Implementors" section with wikilinks to all implementing classes

### Wikilink format (Phase 2 scope)
- New class/interface notes use correct `[[ContainingType.MethodName]]` format for member links (matches method note file names)
- New class/interface notes use `[[Namespace.ClassName]]` format for type relationship links (base class, interfaces) — fully qualified to prevent collisions when duplicate simple names exist across namespaces
- Note file names: class/interface notes use `Sanitize(FullName).md`, method notes use `Sanitize(ContainingType.MethodName).md`
- Existing method note link rewrites (Calls/Called-by sections) are Phase 3 scope (OUTP-01)

### Method signatures (STRC-08)
- Per-method notes contain complete signatures: access modifier, return type, method name, and all parameter names with types (satisfies success criterion #5)
- The class note member index is a compact navigation listing (wikilinks to method notes) — Claude has discretion over its density (e.g., return type only vs full params)
- The completeness guarantee lives in the method notes; the class note index is a quick-scan aid

### Type relationships
- Inheritance and interface information goes in YAML frontmatter (sidebar-style metadata)
- Phase 2 frontmatter is minimal: base_class, interfaces, namespace, source_file fields only
- Phase 3 expands frontmatter with metrics, tags, and Dataview-compatible fields
- Base class and implemented interfaces stored as fully qualified type names that match note file names

### Member index
- Members listed in declaration order (same order as source file)
- No access modifier prefixes in the compact index — keep entries clean for scanning
- Full signatures with access modifiers appear in the individual method notes (STRC-08)

### Interface notes
- Include a "Known Implementors" section in the note body with wikilinks
- This is body content, not just frontmatter — visible and clickable for navigation

### Claude's Discretion
- DI dependency presentation: whether constructor-injected types get a dedicated section or just appear in the constructor entry
- Member index density: formatting choice only (wikilink + return type vs wikilink + full params) — signature completeness is guaranteed in method notes
- Exact YAML frontmatter field names and structure
- How to handle generic type parameters in wikilinks

</decisions>

<specifics>
## Specific Ideas

- User's existing vault uses Graph View heavily — class hub nodes should create meaningful graph structure (class → methods, class → base, class → interfaces, class → DI deps)
- Current method note wikilinks are broken (bare method names don't match file names) — known issue, deferred to Phase 3 OUTP-01
- "What it does" and "Improvements" TODO sections are Phase 5 (LLM enrichment) — don't touch those placeholders
- User ran per-method mode on a real codebase (~500+ methods) — the vault is already large, so class notes should be navigation aids, not duplicating content

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 02-class-type-analysis*
*Context gathered: 2026-02-25*
