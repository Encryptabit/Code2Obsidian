# Roadmap: Code2Obsidian

## Overview

Code2Obsidian is a brownfield .NET CLI with working Roslyn analysis and basic markdown emission. The roadmap decomposes the monolithic codebase into a pipeline architecture, layers in rich class-level and type analysis, refines output quality with metrics and pattern detection, adds git-based incremental mode for large codebases, and caps with opt-in LLM enrichment. Each phase delivers a verifiable capability that builds on the previous one.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [ ] **Phase 1: CLI Foundation & Pipeline Architecture** - Refactor monolith into pipeline, add proper CLI parsing and progress UX
- [ ] **Phase 2: Class & Type Analysis** - Generate class-level notes with inheritance, interfaces, DI, and rich method signatures
- [ ] **Phase 3: Output Quality & Metrics** - Pattern detection, danger annotations, cyclomatic complexity, Dataview-compatible frontmatter
- [ ] **Phase 4: Incremental Mode** - Git-based change detection with selective regeneration for large codebases
- [ ] **Phase 5: LLM Enrichment** - Provider-agnostic LLM summaries with caching and cost estimation

## Phase Details

### Phase 1: CLI Foundation & Pipeline Architecture
**Goal**: The tool has a clean CLI interface, shows progress during analysis, and is internally decomposed into testable pipeline stages
**Depends on**: Nothing (first phase)
**Requirements**: INFR-01, INFR-02, INFR-06
**Success Criteria** (what must be TRUE):
  1. Running `Code2Obsidian --help` displays auto-generated help with all flags and arguments
  2. Running analysis on a multi-project solution shows a progress bar that updates as projects and files are processed
  3. The codebase is organized as IAnalyzer, IEnricher, IEmitter pipeline stages with no analysis logic in Program.cs
  4. Existing functionality (method-level markdown, call graphs, wikilinks) continues to work identically after refactor
**Plans**: TBD

### Phase 2: Class & Type Analysis
**Goal**: Every class and interface in the analyzed solution gets its own Obsidian note with structural context -- inheritance, interfaces, members, DI dependencies, and rich method signatures
**Depends on**: Phase 1
**Requirements**: STRC-01, STRC-02, STRC-03, STRC-04, STRC-05, STRC-06, STRC-08
**Success Criteria** (what must be TRUE):
  1. Every class in the analyzed solution has a dedicated note containing a purpose summary, member index, and source file path
  2. Class notes show base classes and implemented interfaces as clickable wikilinks that navigate to the corresponding type note
  3. Interface notes include a "Known Implementors" section with wikilinks to all classes that implement that interface
  4. Constructor parameters are listed with their types and linked as dependency injection dependencies
  5. Method signatures in all notes include return type, parameter names with types, and access modifiers
**Plans**: TBD

### Phase 3: Output Quality & Metrics
**Goal**: The generated vault uses collision-free wikilinks, rich YAML frontmatter with computed metrics, pattern-based tags, and danger annotations that flag risky methods
**Depends on**: Phase 2
**Requirements**: STRC-07, OUTP-01, OUTP-02, OUTP-03, OUTP-04, OUTP-05, OUTP-06
**Success Criteria** (what must be TRUE):
  1. Wikilinks use `[[ClassName.MethodName]]` format and clicking them in Obsidian navigates to the correct note without ambiguity
  2. Opening any note in Obsidian shows YAML frontmatter containing namespace tag, project tag, and source file path
  3. Dataview queries against the vault can filter by complexity, fan_in, fan_out, pattern, and access modifier fields
  4. Methods with fan-in above a configurable threshold appear with a `danger/high-fan-in` tag; high-complexity methods are similarly tagged
  5. Classes are tagged with detected architectural patterns (repository, controller, service, middleware) visible in Obsidian tag navigation
**Plans**: TBD

### Phase 4: Incremental Mode
**Goal**: Developers working on large codebases can re-run the tool after code changes and only changed files are reanalyzed, cutting regeneration time dramatically
**Depends on**: Phase 3
**Requirements**: INFR-03, INFR-04, INFR-05
**Success Criteria** (what must be TRUE):
  1. Running with `--incremental` after modifying one file in a 200-file solution regenerates only the notes affected by that change
  2. The tool stores and compares git commit hashes to determine which files changed since the last run
  3. First run on a repo with no prior state performs full analysis without errors or special setup
**Plans**: TBD

### Phase 5: LLM Enrichment
**Goal**: Developers can opt into LLM-generated plain-English summaries that explain what methods and classes do, with provider flexibility and cost controls
**Depends on**: Phase 4
**Requirements**: LLM-01, LLM-02, LLM-03, LLM-04, LLM-05
**Success Criteria** (what must be TRUE):
  1. Running with `--enrich` produces notes that include plain-English summaries explaining what each method and class does
  2. The LLM provider is switchable via a JSON config file between Anthropic, OpenAI, and Ollama without code changes
  3. Running `--enrich` twice on the same unchanged code does not make redundant API calls (summaries are cached by content hash)
  4. Running without `--enrich` or without any LLM configured produces a complete vault with all structural analysis, just without LLM summaries
  5. Before enrichment begins on a large codebase, the tool displays an estimated API cost and prompts for confirmation
**Plans**: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 1 -> 2 -> 3 -> 4 -> 5

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. CLI Foundation & Pipeline Architecture | 0/? | Not started | - |
| 2. Class & Type Analysis | 0/? | Not started | - |
| 3. Output Quality & Metrics | 0/? | Not started | - |
| 4. Incremental Mode | 0/? | Not started | - |
| 5. LLM Enrichment | 0/? | Not started | - |
