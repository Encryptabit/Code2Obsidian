# Research Summary: Code2Obsidian

**Domain:** C# code analysis CLI with LLM enrichment and Obsidian documentation generation
**Researched:** 2026-02-25
**Overall confidence:** HIGH

## Executive Summary

Code2Obsidian is a brownfield .NET 8 CLI tool that uses Roslyn for static analysis of C# solutions, generating Obsidian-compatible markdown with call graphs. The planned extensions -- LLM enrichment, incremental processing, richer static analysis, and pattern detection -- are well-served by the current .NET ecosystem. The key architectural decision is the LLM integration strategy: Microsoft.Extensions.AI (MEAI) provides a provider-agnostic `IChatClient` interface that all three required providers (Anthropic, OpenAI, Ollama) now implement natively. This avoids building custom HTTP clients per provider and makes provider selection a configuration concern.

The Roslyn stack (4.14.0) remains the right foundation. Version 5.0.0 exists but offers no compelling features for this use case. The single biggest risk in the stack is the Anthropic official C# SDK (12.4.0), which is in beta -- but the IChatClient interface it implements is stable (owned by Microsoft), so the risk is contained to the adapter layer. All other packages are mature, widely used, and well-maintained.

The tool should remain a single self-contained CLI. The stack additions (System.CommandLine for CLI parsing, LibGit2Sharp for git diff, Spectre.Console for progress UX, MEAI + providers for LLM) add ~8 NuGet packages but each serves a distinct, justified purpose. No framework-level abstractions (DI containers, hosted services, configuration pipelines) are needed -- this is a CLI that runs, does work, and exits.

Cyclomatic complexity computation should be hand-rolled as a ~50-line `CSharpSyntaxWalker` rather than depending on the outdated ArchiMetrics library (last updated 2017). Fan-in and fan-out are already computed via the existing call graph dictionaries.

## Key Findings

**Stack:** .NET 8 + Roslyn 4.14.0 + MEAI 10.3.0 (IChatClient) + System.CommandLine 2.0.3 + LibGit2Sharp 0.31.0 + Spectre.Console 0.54.0
**Architecture:** Provider-agnostic LLM enrichment via IChatClient; custom CSharpSyntaxWalker for metrics; LibGit2Sharp for incremental change detection
**Critical pitfall:** Do not attempt to load Roslyn 5.0.0 against codebases built with older .NET SDKs -- MSBuild version mismatches cause silent analysis failures

## Implications for Roadmap

Based on research, suggested phase structure:

1. **Refactor & CLI Foundation** - Break the monolith, add System.CommandLine, upgrade Microsoft.Build.Locator
   - Addresses: Tech debt (single-file), fragile CLI parsing, MSBuild registration issues
   - Avoids: Doing too much in one phase; establishes testable boundaries before adding features

2. **Rich Static Analysis** - Class-level analysis, cyclomatic complexity, pattern detection, interface/inheritance tracking
   - Addresses: Table stakes features that require only Roslyn (no new dependencies beyond what exists)
   - Avoids: Coupling analysis improvements to LLM integration

3. **Incremental Mode** - LibGit2Sharp integration, change detection, selective regeneration
   - Addresses: Performance requirement for large codebases; must work before LLM enrichment (which is expensive)
   - Avoids: Expensive LLM calls on unchanged files

4. **LLM Enrichment** - MEAI integration, provider configuration, enrichment pipeline
   - Addresses: The headline feature; requires stable analysis and incremental mode to be cost-effective
   - Avoids: Building enrichment before the analysis output is rich enough to provide good LLM context

5. **Danger Annotations & Advanced Features** - High fan-in flags, hot path detection, Akka actor awareness
   - Addresses: Differentiator features specific to the target codebases
   - Avoids: Scope creep in earlier phases

**Phase ordering rationale:**
- Phase 1 before everything: the monolithic Program.cs cannot absorb 8 new dependencies and 5 new feature areas without first being decomposed
- Phase 2 before Phase 4: LLM enrichment quality depends on rich context (class hierarchy, complexity, patterns) -- enrich after the analysis is complete
- Phase 3 before Phase 4: incremental mode prevents re-enriching unchanged files, which saves money and time
- Phase 5 last: danger annotations are domain-specific refinements that build on all previous capabilities

**Research flags for phases:**
- Phase 4: May need deeper research on prompt engineering strategies and token budget management for large methods
- Phase 2: Standard Roslyn patterns, unlikely to need research
- Phase 3: LibGit2Sharp API is well-documented, low research risk

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | All packages verified on NuGet with current versions and publish dates. MEAI is the official Microsoft recommendation. |
| Features | HIGH | Feature set is well-understood from PROJECT.md. All features are achievable with the recommended stack. |
| Architecture | HIGH | Provider-agnostic pattern via IChatClient is well-documented with official examples. Roslyn walker pattern is standard. |
| Pitfalls | MEDIUM | LLM integration pitfalls (rate limiting, token budgets, prompt quality) are experience-based rather than documentation-based. |

## Gaps to Address

- Prompt engineering strategy for method/class enrichment (what context to include, token budget per call)
- Rate limiting approach for LLM API calls during large solution enrichment (MEAI has middleware but specifics need design)
- Anthropic SDK stability -- official SDK is in beta; monitor for breaking changes during development
- .NET 10 migration timeline -- both .NET 8 and 9 EOL in November 2026; plan upgrade path

---

*Research summary: 2026-02-25*
