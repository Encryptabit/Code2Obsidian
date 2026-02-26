# Code2Obsidian

## What This Is

A .NET CLI tool that analyzes C# solutions using Roslyn and generates an Obsidian-compatible markdown vault with call graphs, method summaries, class documentation, and danger annotations. Designed for onboarding onto large, unfamiliar codebases — turning static analysis into a navigable knowledge graph.

## Core Value

A developer can point this tool at any C# solution and get an Obsidian vault that lets them understand what the code does, how it connects, and where the danger zones are — without reading every file.

## Requirements

### Validated

- ✓ Roslyn-based solution loading and compilation — existing
- ✓ Method extraction with user-code filtering — existing
- ✓ Call graph construction (calls-out and called-by) — existing
- ✓ Per-file markdown emission with YAML frontmatter and tags — existing
- ✓ Per-method markdown emission with source path context — existing
- ✓ Wikilink generation for Obsidian graph view — existing
- ✓ XML doc comment extraction and formatting — existing
- ✓ MSBuild registration with VS and dotnet SDK fallback — existing
- ✓ CLI argument parsing (solution path, mode, output dir) — existing

### Active

- [ ] Class-level notes — class purpose, inheritance chain, implemented interfaces, member overview
- [ ] Danger annotations — flag high fan-in methods, event handlers, hot paths, Akka actor message handlers
- [ ] LLM enrichment (--enrich flag) — send method/class source to configurable LLM API for plain-English summaries
- [ ] Configurable LLM provider — support Anthropic, OpenAI, local/Ollama via config
- [ ] Incremental mode — only regenerate markdown for files changed since last run (git diff based)
- [ ] Rich static metadata — parameter types, return types, cyclomatic complexity, attributes, access modifiers
- [ ] Tag-based organization — emit tags for namespace, project, class type (actor, controller, repository, service, etc.)
- [ ] Pattern detection — identify repository pattern, actor pattern, event handler, API endpoint, middleware
- [ ] Interface/inheritance tracking — wikilinks to base classes, implemented interfaces, derived types
- [ ] Dependency injection awareness — detect constructor-injected services, map service dependencies

### Out of Scope

- Non-C# analysis (Go, React, TypeScript) — focus on C#/.NET where Roslyn gives deep analysis; other stacks need different tooling
- Real-time/watch mode — regeneration is triggered manually, not on file save
- Obsidian plugin — this is a standalone CLI, not an Obsidian extension
- Cross-solution analysis — each solution analyzed independently; service-to-service mapping is out of scope
- Code modification/refactoring — this tool reads and reports, never writes back to source

## Context

The primary use case is onboarding onto large enterprise C# codebases, specifically:

- **Core API**: Main internal .NET API with NHibernate ORM, abstract IRepository layer, MSSQL databases. Foundation of all products. Being migrated from NHibernate to Dapper. Understanding the data access layer and call paths is critical before refactoring.
- **CentralStation**: Akka.NET actor model, event-driven via Kafka and RabbitMQ. Arrival prediction engine. Methods can be triggered by events firing 100x/second — touching the wrong method without understanding its call frequency can take down the server. Heavy caching requirements. Extremely difficult to debug.

Both codebases are large, poorly documented, and have patterns that are "hard to follow." The tool needs to surface understanding that's currently locked in tribal knowledge.

The Obsidian vault should be flat (for graph view compatibility) with tags for organization. The tag-folder plugin handles the hierarchy.

## Constraints

- **Tech stack**: .NET 8, Roslyn/Microsoft.CodeAnalysis — must remain a single self-contained CLI tool
- **Output format**: Obsidian-compatible markdown with YAML frontmatter, wikilinks, and tags
- **Vault structure**: Flat file layout (graph view requirement), organized via tags
- **LLM integration**: Opt-in only (--enrich flag), must work without any LLM configured
- **Incremental**: Git diff based change detection, must handle repos with no prior run gracefully
- **Performance**: Must handle large solutions (hundreds of files) without excessive memory or time

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Flat vault structure with tags | Obsidian graph view works best with flat layout; tag-folder plugin handles hierarchy | — Pending |
| LLM enrichment as opt-in flag | Keeps default runs fast and free; avoids surprise API costs on large codebases | — Pending |
| Git diff for incremental detection | Precise change tracking, natural for developer workflow, avoids stale timestamp issues | — Pending |
| Configurable LLM provider | User needs flexibility across Anthropic, OpenAI, and local models; no vendor lock-in | — Pending |
| Single CLI tool architecture | Simplicity over plugin system; one binary that does the job | — Pending |

---
*Last updated: 2026-02-25 after initialization*
