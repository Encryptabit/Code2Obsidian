# Requirements: Code2Obsidian

**Defined:** 2026-02-25
**Core Value:** A developer can point this tool at any C# solution and get an Obsidian vault that lets them understand what the code does, how it connects, and where the danger zones are — without reading every file.

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Structural Analysis

- [ ] **STRC-01**: Tool generates one note per class with purpose summary, member index, and source path
- [ ] **STRC-02**: Class notes show inheritance chain (base classes) as wikilinks
- [ ] **STRC-03**: Class notes show implemented interfaces as wikilinks
- [ ] **STRC-04**: Interface notes link to all known implementors
- [ ] **STRC-05**: Class notes extract and list properties and fields with types
- [ ] **STRC-06**: Constructor parameters are extracted and linked as DI dependencies
- [ ] **STRC-07**: Classes are tagged with detected patterns (repository, controller, service, middleware)
- [ ] **STRC-08**: Rich method signatures include return type, parameters with types, and access modifiers

### Output Quality

- [ ] **OUTP-01**: Wikilinks use `[[ClassName.MethodName]]` format to avoid name collisions
- [ ] **OUTP-02**: Notes include namespace and project tags in YAML frontmatter
- [ ] **OUTP-03**: YAML frontmatter includes Dataview-compatible fields (complexity, fan_in, fan_out, pattern, access)
- [ ] **OUTP-04**: Methods with fan-in above configurable threshold are tagged `danger/high-fan-in`
- [ ] **OUTP-05**: Cyclomatic complexity is computed and included in frontmatter; high-complexity methods tagged
- [ ] **OUTP-06**: Source file relative path included in all note frontmatter

### LLM Enrichment

- [ ] **LLM-01**: `--enrich` flag triggers LLM-generated structured summaries (purpose, summary, tags) for methods and classes
- [ ] **LLM-02**: LLM provider is configurable via JSON config file (Anthropic, OpenAI, Ollama)
- [ ] **LLM-03**: LLM summaries are cached by source content hash to avoid redundant API calls
- [ ] **LLM-04**: Tool works fully without any LLM configured (summaries are additive, not required)
- [ ] **LLM-05**: Cost estimation displayed before LLM enrichment begins on large codebases
- [x] **LLM-06**: LLM responses parsed as structured XML (summary/purpose/tags) with robust fallback for malformed output
- [x] **LLM-07**: LLM-generated tags sanitized to valid YAML tokens before frontmatter insertion

### Infrastructure

- [ ] **INFR-01**: CLI uses System.CommandLine for argument parsing with auto-generated help
- [x] **INFR-02**: Jitter-free progress display during analysis and enrichment of large solutions (Spectre.Console Live)
- [ ] **INFR-03**: Incremental mode (`--incremental`) only regenerates notes for files changed since last run
- [ ] **INFR-04**: Change detection uses git diff against stored commit hash
- [ ] **INFR-05**: First run without prior state performs full analysis gracefully
- [ ] **INFR-06**: Monolithic Program.cs refactored into pipeline architecture (IAnalyzer → IEnricher → IEmitter)

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Domain-Specific Analysis

- **DOMN-01**: Akka.NET actor message handler detection (Receive<T> patterns, message flow mapping)
- **DOMN-02**: NHibernate/Dapper data access layer tagging (migration frontier identification)
- **DOMN-03**: Event handler subscription tracking (button.Click += OnClick patterns)

### Extended Features

- **EXTD-01**: Cross-solution analysis (shared interface mapping across repos)
- **EXTD-02**: Git history integration (change frequency hotspots, authorship mapping)
- **EXTD-03**: Cognitive complexity metric (SonarSource alternative to cyclomatic)

## Out of Scope

| Feature | Reason |
|---------|--------|
| Non-C# language support | Roslyn is C#/VB only; other stacks need different tooling |
| Real-time / watch mode | Adds enormous complexity; manual trigger is sufficient |
| Obsidian plugin | CLI generates standard markdown; Obsidian renders natively |
| Code modification / refactoring | Read-only analysis is a trust boundary |
| HTML/PDF documentation output | DocFX and Doxygen already do this; Obsidian markdown is the differentiator |
| Assembly-level dependency graphs | NDepend does this definitively; Code2Obsidian focuses on source-level semantics |
| Interactive graph visualization | Obsidian graph view IS the visualization layer |
| Framework-specific detection | Tool must behave consistently across all C# projects — no special-casing for Akka, NHibernate, etc. |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| STRC-01 | Phase 2 | Pending |
| STRC-02 | Phase 2 | Pending |
| STRC-03 | Phase 2 | Pending |
| STRC-04 | Phase 2 | Pending |
| STRC-05 | Phase 2 | Pending |
| STRC-06 | Phase 2 | Pending |
| STRC-07 | Phase 3 | Pending |
| STRC-08 | Phase 2 | Pending |
| OUTP-01 | Phase 3 | Pending |
| OUTP-02 | Phase 3 | Pending |
| OUTP-03 | Phase 3 | Pending |
| OUTP-04 | Phase 3 | Pending |
| OUTP-05 | Phase 3 | Pending |
| OUTP-06 | Phase 3 | Pending |
| LLM-01 | Phase 5 | Pending |
| LLM-02 | Phase 5 | Pending |
| LLM-03 | Phase 5 | Pending |
| LLM-04 | Phase 5 | Pending |
| LLM-05 | Phase 5 | Pending |
| LLM-06 | Phase 5 | Complete |
| LLM-07 | Phase 5 | Complete |
| INFR-01 | Phase 1 | Pending |
| INFR-02 | Phase 1 | Complete |
| INFR-03 | Phase 4 | Pending |
| INFR-04 | Phase 4 | Pending |
| INFR-05 | Phase 4 | Pending |
| INFR-06 | Phase 1 | Pending |

**Coverage:**
- v1 requirements: 27 total
- Mapped to phases: 27
- Unmapped: 0

---
*Requirements defined: 2026-02-25*
*Last updated: 2026-02-25 after roadmap creation*
