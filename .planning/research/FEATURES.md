# Feature Landscape

**Domain:** C# codebase analysis and Obsidian knowledge graph generation for developer onboarding
**Researched:** 2026-02-25

## Table Stakes

Features users expect from a tool that generates a navigable knowledge graph from C# codebases. Missing any of these means the vault fails at its core purpose: letting a new developer understand unfamiliar code without reading every file.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| **Class-level notes** | Methods without class context are orphans. Every competitor (DocFX, NDepend, GitNexus, CodeGraph) produces class-level documentation. Without it, users must mentally reconstruct what class a method belongs to. | Medium | Roslyn `INamedTypeSymbol` gives class name, base type, interfaces, members. Emit one note per class with member index, inheritance, and links to method notes. |
| **Inheritance and interface tracking** | In enterprise C# (especially IRepository patterns, NHibernate), understanding what implements what is the single most common question during onboarding. Abstract layers hide real behavior. | Medium | Roslyn `BaseType`, `AllInterfaces`, and `FindImplementationForInterfaceMember` are built-in. Emit wikilinks from interface note to all implementors and vice versa. |
| **Rich type signatures** | Current output shows `ToDisplayString()` which is bare. Users need parameter types, return types, access modifiers, and attributes to understand a method without opening the source. | Low | Already have `IMethodSymbol`; extract `ReturnType`, `Parameters`, `DeclaredAccessibility`, `GetAttributes()`. Straightforward property access. |
| **Namespace/project organization tags** | With thousands of notes in a flat vault, tags are the ONLY way to scope navigation. Without namespace and project tags, the vault is an unsearchable wall. | Low | Already emitting YAML frontmatter with tags. Add `namespace: X`, `project: Y` to frontmatter. Tag-folder plugin does the rest. |
| **Property and field extraction** | Classes are not just methods. Properties and fields define state, and enterprise codebases (NHibernate entities, DTOs) are often property-heavy with minimal methods. Skipping them means missing half the picture. | Low | Roslyn `IPropertySymbol` and `IFieldSymbol` from `GetMembers()`. Render in class note. |
| **Constructor analysis** | Currently skipped by `IsUserMethod` filter (constructors are `MethodKind.Constructor`). Constructor injection is the primary DI pattern in .NET. Missing constructors means missing all dependency wiring. | Low | Remove constructor exclusion from filter, or add specific constructor handling. Render injected services as links. |
| **Bi-directional wikilinks with disambiguation** | Current wikilinks use bare method names (`[[MethodName]]`), which collide when multiple classes have same-named methods (e.g., `Save`, `Get`, `Handle`). Broken links destroy graph navigability. | Medium | Use `ClassName.MethodName` as note filename and wikilink target. Requires updating all link generation. |
| **Source file path context** | Per-method mode includes path, but per-file mode does not show the full path prominently. Users need to know WHERE in the repo a class lives. | Low | Already captured in `MethodInfo.FilePath`. Emit relative path in frontmatter. |

## Differentiators

Features that set Code2Obsidian apart from DocFX, Doxygen, NDepend, and the emerging code-graph tools (GitNexus, FalkorDB, CodeGraph). These turn Code2Obsidian from "another doc generator" into "the onboarding tool for dangerous enterprise codebases."

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| **Danger annotations (fan-in / hot-path / frequency markers)** | The killer feature for the CentralStation use case. No existing tool marks "this method fires 100x/sec" or "this has 47 callers, touch it and everything breaks." CodeScene does behavioral hotspot analysis from git history, but not from static call graph fan-in. This is the difference between "documentation" and "survival guide." | Medium | Calculate fan-in from existing `callsIn` dictionary. Threshold-based tagging: `danger/high-fan-in`, `danger/hot-path`. Emit warning callouts in markdown. Threshold should be configurable. |
| **Pattern detection (repository, actor, controller, middleware, event handler)** | Enterprise codebases follow patterns that are invisible in flat method lists. Tagging a class as `pattern/repository` or `pattern/actor` gives the new developer instant architectural context. No general-purpose doc tool does this for .NET-specific patterns. | Medium | Heuristic-based: class inherits `ReceiveActor`/`UntypedActor` = actor pattern. Implements `IRepository<T>` = repository. Has `[ApiController]` attribute = controller. Class name conventions as fallback. |
| **LLM-generated plain-English summaries** | Method signatures and call graphs show structure but not intent. LLM summaries answer "what does this actually DO?" GitNexus offers LLM wiki generation but requires their infrastructure. Code2Obsidian doing it offline with configurable provider (Anthropic, OpenAI, Ollama) is unique. | High | Requires: config file for API keys/endpoints, rate limiting, cost estimation, prompt engineering per entity type, caching to avoid re-summarizing unchanged code. Must work WITHOUT LLM (summaries are additive, not required). |
| **Dependency injection mapping** | In constructor-injection-heavy codebases, the DI graph IS the architecture. Showing "UserService depends on IRepository, ILogger, ICache" as a navigable graph reveals the actual system topology. NDepend shows dependencies at assembly level; this shows it at service level. | Medium | Parse constructor parameters, resolve interface types, emit as wikilinks. If `IServiceCollection` registration is detectable, map interface to implementation. |
| **Incremental mode (git-diff based)** | Large enterprise solutions (500+ files) take minutes to analyze. Re-analyzing unchanged files is wasted time. No competing tool in the Obsidian doc-gen space offers incremental updates. | High | Git diff to detect changed files, hash-based cache of previous analysis, selective re-emission. Must handle: first run (no cache), deleted files (remove stale notes), renamed files, transitive invalidation (if A calls B and B changed, A's "calls" section is still valid but B's content changed). |
| **Akka.NET actor message handler detection** | Extremely niche but critical for the CentralStation use case. Detecting `Receive<T>()` registrations and mapping message types to handler methods gives the actor system's actual message flow. No tool does this. | Medium | Pattern-match on `Receive<T>()`, `ReceiveAsync<T>()`, and `Command<T>()` in actor constructors/`PreStart`. Extract `T` as the message type. Link message type notes to handler method notes. |
| **NHibernate/Dapper data access layer mapping** | For the Core API migration use case: identifying which repositories use NHibernate vs. Dapper, mapping entity classes to their persistence layer, and highlighting the migration frontier. | Medium | Detect NHibernate references (`ISession`, `ICriteria`, HQL strings) vs. Dapper references (`SqlMapper`, `Query<T>`, `Execute`). Tag classes accordingly: `data-access/nhibernate`, `data-access/dapper`, `data-access/mixed`. |
| **Cyclomatic complexity scoring** | Surfaces "this method is a 47-branch nightmare" without requiring the user to read it. Helps prioritize what to study during onboarding. NDepend does this but costs money and outputs to its own UI, not Obsidian. | Medium | Count branching nodes in syntax tree (`if`, `switch`, `case`, `while`, `for`, `foreach`, `&&`, `||`, `??`, `?.`, `catch`). Emit as frontmatter property and as a tag when above threshold (e.g., `complexity/high`). |
| **Obsidian Dataview-compatible frontmatter** | With rich frontmatter (complexity, fan-in, pattern, namespace, project), users can build Dataview queries like "show all high-complexity methods in the Payments namespace." This turns the vault from static docs into a queryable database. | Low | Structure frontmatter fields consistently: `complexity`, `fan_in`, `fan_out`, `pattern`, `namespace`, `project`, `access`. Dataview reads YAML natively. |

## Anti-Features

Features to explicitly NOT build. These are tempting but outside scope, technically risky, or actively harmful to the tool's purpose.

| Anti-Feature | Why Avoid | What to Do Instead |
|--------------|-----------|-------------------|
| **Real-time / watch mode** | Adds enormous complexity (file watchers, debouncing, partial re-analysis, Roslyn incremental compilation). The use case is periodic regeneration during onboarding, not continuous sync. Developer experience for a watch mode is hard to get right and easy to get wrong. | Support incremental mode (git-diff) so re-runs are fast. User triggers manually. |
| **Obsidian plugin** | Building an Obsidian plugin means maintaining TypeScript code, dealing with Obsidian API changes, electron packaging. The CLI is the right abstraction: generate files, let Obsidian render them. Plugin maintenance cost is disproportionate to value. | CLI generates standard markdown. Obsidian's native features (graph view, tags, search, Dataview) handle the rest. |
| **Cross-solution / service-to-service mapping** | Each solution analyzed independently is the right boundary. Cross-solution analysis requires shared type resolution, network protocol understanding (gRPC, REST contracts), and configuration parsing. This is a different product. | Tag notes with `project: X` so users can filter. If two solutions share interfaces via NuGet, those interfaces appear in both vaults. |
| **Code modification / refactoring suggestions** | Read-only analysis is a trust boundary. If the tool writes back to source, users must audit its changes. This converts a low-risk documentation tool into a high-risk refactoring tool. | Emit "Improvements" sections as markdown suggestions (already exists as TODO placeholders). LLM enrichment can populate these as text, never as code changes. |
| **Non-C# language support** | Roslyn is C#/VB only. Adding TypeScript/Go/Python means integrating Tree-sitter or language-specific parsers, each with different AST shapes. Multi-language support fragments the quality of analysis. GitNexus already does multi-language via Tree-sitter. | Stay focused on C#/.NET where Roslyn gives the deepest semantic analysis. Other languages have their own tools. |
| **HTML/PDF documentation output** | DocFX, Doxygen, and VSdocman already generate HTML/PDF docs well. Obsidian markdown is the differentiator. Adding output formats dilutes the product and creates maintenance burden. | Obsidian markdown only. Users who want HTML can use Obsidian Publish or pandoc on the vault. |
| **Assembly-level / NuGet dependency graphs** | NDepend already does this definitively. Competing at assembly dependency visualization is a losing game. The value of Code2Obsidian is source-level, semantic understanding. | Show which project a class belongs to. Show constructor-injected dependencies. Leave assembly-level graphs to NDepend. |
| **Interactive graph visualization** | Building custom graph rendering (D3.js, Cytoscape) is an enormous effort. Obsidian's graph view IS the visualization layer. Building a competing one adds no value. | Emit well-structured wikilinks. Obsidian graph view, local graph, and Dataview handle visualization natively. |
| **Full-text search indexing** | Obsidian already has full-text search. Building a separate search index is redundant. | Ensure filenames and frontmatter are descriptive so Obsidian search works well. |

## Feature Dependencies

```
Constructor Analysis ──> DI Mapping (DI mapping requires parsing constructors)
Class-Level Notes ──> Pattern Detection (patterns are assigned to classes)
Class-Level Notes ──> Inheritance Tracking (inheritance is a class-level concept)
Class-Level Notes ──> Property/Field Extraction (properties belong to classes)
Rich Type Signatures ──> Dataview Frontmatter (signatures feed frontmatter fields)
Call Graph (existing) ──> Danger Annotations (fan-in computed from call graph)
Call Graph (existing) ──> Cyclomatic Complexity (different metric, same rendering)
Namespace Tags ──> Pattern Detection (patterns need namespace context for heuristics)
Bi-directional Links ──> All Other Features (must fix disambiguation before adding more notes)
LLM Summaries ──> Incremental Mode (LLM calls are expensive; cache is essential)
Git-Diff Incremental ──> LLM Summaries (practically, you want incremental before LLM to avoid re-summarizing)
```

Critical path: **Bi-directional link disambiguation** must come first. Every subsequent feature adds more notes to the vault, and name collisions will compound. Fix the linking model before scaling the note count.

## MVP Recommendation

Prioritize (in order):

1. **Bi-directional link disambiguation** -- Fix `[[MethodName]]` to `[[ClassName.MethodName]]` before anything else. Foundation for all other features. Without this, adding more notes makes the vault WORSE.

2. **Class-level notes with inheritance tracking** -- The single biggest gap in current output. A vault of methods without classes is a dictionary without chapters. Includes base class links, interface links, member index.

3. **Constructor analysis and DI mapping** -- Unblocks understanding of how services wire together. In enterprise .NET, the DI graph is the architecture diagram.

4. **Namespace/project tags and rich frontmatter** -- Makes the vault navigable at scale. Enables Dataview queries. Low complexity, high impact.

5. **Danger annotations (fan-in thresholds)** -- The "survival guide" feature. Leverages existing call graph data. Tags high-fan-in methods so users know what not to touch.

6. **Pattern detection** -- Tags classes as actor/repository/controller/service. Instant architectural context for onboarding developers.

Defer:

- **LLM summaries**: High complexity, requires config system, API integration, rate limiting, and caching. Build the structural foundation first. LLM enrichment is additive -- it makes good notes better but cannot fix bad structure.
- **Incremental mode**: Important for large codebases but not needed until the full analysis pipeline is stable. Optimizing a pipeline that is still changing is premature.
- **Cyclomatic complexity**: Useful but not blocking. Can be added independently at any point.
- **Akka.NET message handler detection**: Very valuable for CentralStation but niche. Add after pattern detection framework exists.
- **NHibernate/Dapper mapping**: Same -- niche but valuable. Add after pattern detection framework.

## Confidence Assessment

| Area | Confidence | Reason |
|------|------------|--------|
| Table stakes features | HIGH | Verified against DocFX, NDepend, GitNexus, CodeGraph feature sets. All produce class-level docs, type hierarchies, and rich signatures. |
| Danger annotations (differentiator) | HIGH | Confirmed no competing tool does static fan-in-based danger marking for Obsidian output. CodeScene does behavioral hotspots from git history, which is complementary but different. |
| Pattern detection feasibility | MEDIUM | Heuristic-based detection is well-understood but accuracy varies. Akka.NET patterns are detectable via Roslyn; repository patterns depend on naming conventions. |
| LLM integration complexity | MEDIUM | Multiple tools (GitNexus, GitHub Copilot) do LLM-assisted documentation, but integrating configurable providers with caching is non-trivial. Ollama local support is straightforward; cloud provider rate limiting needs care. |
| Obsidian performance at scale | MEDIUM | Forum reports confirm graph view struggles above ~10k notes. Local graph view works fine. Flat vault with tags is the recommended approach. May need to test with real vault sizes. |
| Incremental mode complexity | HIGH | Git-diff approach is well-understood, but transitive invalidation (method A calls changed method B) adds complexity. Hash-based caching is the standard pattern. |

## Sources

- [DocFX documentation](https://dotnet.github.io/docfx/) -- .NET API documentation generator features
- [NDepend features](https://www.ndepend.com/features/dependency-graph-matrix-architecture) -- Dependency graph, 82 code metrics, VS 2026 integration
- [GitNexus](https://github.com/abhigyanpatwari/GitNexus) -- Zero-server knowledge graph for code intelligence, 8 language support, Graph RAG
- [CodeGraph by KnackLabs](https://www.knacklabs.ai/solutions/codegraph) -- Codebase knowledge graph with incremental updates, MCP integration
- [FalkorDB Code Graph](https://www.falkordb.com/blog/code-graph-analysis-visualize-source-code/) -- Code graph analysis for Java/Python, natural language querying
- [CodeScene developer onboarding](https://codescene.com/resources/use-cases/developer-onboarding) -- Behavioral hotspot analysis, knowledge maps, offboarding risk
- [Roslyn semantic analysis](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis) -- Symbol APIs, inheritance, interface implementation detection
- [Obsidian graph view](https://help.obsidian.md/plugins/graph) -- Graph view capabilities and performance characteristics
- [Obsidian large vault performance](https://forum.obsidian.md/t/obsidian-graph-view-doesnt-work-for-a-large-vault/106287) -- 130k notes takes 10min to index; graph view freezes at scale
- [Akka.NET design patterns](https://petabridge.com/blog/top-akkadotnet-design-patterns/) -- 30+ cataloged patterns for actor composition
- [Doxygen 1.16.1](https://www.doxygen.nl/) -- Multi-language doc generation with class hierarchy diagrams
- [VSdocman](https://www.helixoft.com/vsdocman/overview.html) -- MSDN-style documentation with VS 2026 support

---

*Feature landscape research: 2026-02-25*
