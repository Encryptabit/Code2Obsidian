# Domain Pitfalls

**Domain:** C# code analysis (Roslyn) with LLM enrichment and Obsidian markdown generation
**Researched:** 2026-02-25
**Overall Confidence:** HIGH (Roslyn pitfalls verified against GitHub issues and official docs; LLM pitfalls verified against provider documentation)

---

## Critical Pitfalls

Mistakes that cause rewrites, data loss, or project-blocking failures.

---

### Pitfall 1: Silent Compilation Failures from Missing NuGet Restore

**What goes wrong:** `MSBuildWorkspace.OpenSolutionAsync()` succeeds without error, but `GetCompilationAsync()` returns compilations with zero or broken references. Every `GetSymbolInfo()` call returns null. The tool appears to work but produces empty or garbage output -- no call graph, no inheritance data, no method signatures beyond syntax-level text.

**Why it happens:** MSBuildWorkspace performs a design-time build to discover source files and references. If NuGet packages are not restored before opening the solution, the design-time build silently resolves zero package references. MSBuildWorkspace does NOT throw -- it reports failures only through the `WorkspaceFailed` event and the `Diagnostics` property, both of which most developers never check.

**Consequences:**
- `model.GetDeclaredSymbol(node)` returns null for most/all declarations
- `model.GetSymbolInfo(invocation).Symbol` returns null for all invocations
- Call graph is empty; fan-in/fan-out analysis produces zero edges
- Inheritance tracking returns no base types or implementations
- Tool appears successful (exit code 0) but output is useless

**Warning signs:**
- Methods dictionary has far fewer entries than expected
- CallsOut / CallsIn dictionaries are empty
- `compilation.GetDiagnostics()` contains CS0246 ("type or namespace not found") for common types like `Task`, `ILogger`, `DbContext`
- `workspace.Diagnostics` contains messages about failed reference resolution

**Prevention:**
1. Run `dotnet restore` on the target solution before opening with MSBuildWorkspace
2. Subscribe to `workspace.WorkspaceFailed` and log ALL diagnostics before proceeding
3. After loading, validate compilation health: check `compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error)` -- if error count is high relative to solution size, abort with a clear message
4. Add a `--verbose` flag that dumps `workspace.Diagnostics` for troubleshooting

**Phase:** Core infrastructure (Phase 1). This must be the first validation gate before any analysis runs. The existing code at line 59-61 has no diagnostic checking.

**Confidence:** HIGH -- verified against [MSBuildWorkspace usage guide](https://gist.github.com/DustinCampbell/32cd69d04ea1c08a16ae5c4cd21dd3a3), [dotnet/roslyn#15479](https://github.com/dotnet/roslyn/issues/15479), [dotnet/roslyn#20396](https://github.com/dotnet/roslyn/issues/20396).

---

### Pitfall 2: Cross-Project Symbol Identity Breaks Dictionaries and Call Graphs

**What goes wrong:** Two IMethodSymbol references that represent the same method (e.g., `IRepository<T>.Save()`) are not considered equal by `SymbolEqualityComparer.Default` when obtained from different compilations (different projects in the solution). The call graph builds duplicate entries, fan-in counts are wrong, and wikilinks in Obsidian point to nonexistent notes.

**Why it happens:** Roslyn's `SymbolEqualityComparer.Default` compares symbols by reference identity within a single compilation. When Project A calls a method defined in Project B, the symbol for that method in Project A's semantic model is a *metadata symbol* (from the referenced assembly), while in Project B's semantic model it is a *source symbol*. These are different objects that do not compare as equal. The existing code uses `SymbolEqualityComparer.Default` for its dictionaries (line 65, 114), which will fragment when analyzing multi-project solutions.

**Consequences:**
- Same method appears multiple times in output with different identity
- Fan-in analysis undercounts (callers from other projects don't match the definition)
- Wikilinks like `[[Save]]` might point to `IRepository.Save.md` or `ConcreteRepo.Save.md` inconsistently
- NHibernate abstract repository pattern (IRepository defined in one project, implementations in others) is exactly the scenario that triggers this

**Warning signs:**
- Method count is higher than expected (duplicates across projects)
- Fan-in counts seem low for known high-traffic methods
- Multiple markdown files generated for what should be a single method

**Prevention:**
1. Normalize symbols to a canonical form using `SymbolKey` -- Roslyn's `SymbolKey.Create(symbol)` produces a serializable key that can be resolved back in any compilation within the same solution. Use `SymbolKey` as your dictionary key instead of `IMethodSymbol` directly.
2. Alternatively, build a string-based canonical identifier: `$"{symbol.ContainingType.ToDisplayString()}.{symbol.Name}({string.Join(",", symbol.Parameters.Select(p => p.Type.ToDisplayString()))})"` -- but this loses overload fidelity for complex generics.
3. When building the call graph, always resolve invocation targets back to their *definition* symbol using `SymbolFinder.FindSourceDefinitionAsync(symbol, solution)`.

**Phase:** Core analysis refactor (Phase 1 or 2). This must be addressed before multi-project solutions work correctly. The current code's approach of `OriginalDefinition` (line 102) helps for generics but does NOT solve the cross-compilation identity problem.

**Confidence:** HIGH -- verified against [dotnet/roslyn#62465](https://github.com/dotnet/roslyn/issues/62465) (public API request for cross-compilation comparison), [dotnet/roslyn#58226](https://github.com/dotnet/roslyn/issues/58226) (behavior change), [SymbolEqualityComparer docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.symbolequalitycomparer).

---

### Pitfall 3: LLM Cost Explosion on Large Codebases

**What goes wrong:** Running `--enrich` on a 100+ file enterprise solution sends hundreds of API calls, each containing method source code + context. A single run costs $20-50+ and takes hours. Developers enable it once, get a surprise bill, and never use it again.

**Why it happens:** Naive implementation sends one LLM request per method (or per class). Enterprise solutions have 500-2000 methods. Each request includes the method body, surrounding class context, and potentially call graph information. At 1000-3000 tokens per request (input) and 200-500 tokens per response (output), a 1000-method codebase with Claude Sonnet costs roughly:
- Input: 1000 methods x 2000 tokens avg = 2M input tokens = ~$6 (at $3/MTok)
- Output: 1000 methods x 300 tokens avg = 300K output tokens = ~$4.50 (at $15/MTok)
- Total: ~$10.50 per run, more with retries

With Claude Opus or GPT-4o, costs multiply 5-10x. With no caching, every re-run pays full price.

**Consequences:**
- Unexpected API costs alienate users
- Long runtimes (30+ minutes for serial processing) make the tool impractical
- Rate limiting kicks in (Anthropic: 50 RPM at Tier 1), causing failures mid-run
- Partial completion leaves inconsistent output (some files enriched, others not)

**Warning signs:**
- No `--dry-run` or cost estimation before execution
- No caching of previous LLM responses
- No progress indicator showing how many requests remain
- Serial processing with no concurrency control

**Prevention:**
1. **Mandatory dry-run estimation**: Before any LLM calls, count methods, estimate tokens, calculate cost, and display to user. Require `--confirm` or interactive confirmation for runs over a threshold (e.g., $1).
2. **Content-hash caching**: SHA-256 hash the method body + prompt template. Store LLM responses keyed by hash in a `.code2obsidian/cache/` directory. Skip methods whose source hasn't changed.
3. **Tiered model routing**: Use a cheaper/smaller model (Ollama local, GPT-4o-mini, Haiku) for routine methods, reserve expensive models for complex/flagged methods only.
4. **Batch API usage**: Anthropic and OpenAI offer batch APIs at 50% discount that don't count against real-time rate limits. Default to batch mode for full-solution enrichment.
5. **Budget ceiling**: Accept `--max-cost 5.00` flag that stops enrichment when estimated spend reaches the limit.
6. **Concurrency with rate limiting**: Use a semaphore/token bucket to send N concurrent requests (respecting provider RPM limits) while tracking cumulative cost.

**Phase:** LLM enrichment design (Phase 3 or whenever LLM integration is built). Cost controls MUST be designed before the first LLM call is made, not retrofitted.

**Confidence:** HIGH -- verified against [Anthropic rate limits](https://www.requesty.ai/blog/rate-limits-for-llm-providers-openai-anthropic-and-deepseek), [OpenAI rate limits](https://platform.openai.com/docs/guides/rate-limits), [LLM caching best practices](https://aws.amazon.com/blogs/database/optimize-llm-response-costs-and-latency-with-effective-caching/).

---

### Pitfall 4: Incremental Mode Produces Stale/Inconsistent Output

**What goes wrong:** Git-diff-based incremental processing re-analyzes only changed files, but the call graph, inheritance hierarchy, and fan-in counts depend on the ENTIRE solution. A method's callers might change without the method's own file changing. The incremental output is stale or contradicts full-run output.

**Why it happens:** Code analysis has transitive dependencies. Consider: File A calls method X in File B. Developer modifies File A to remove the call to X. Incremental mode detects File A changed but does NOT re-analyze File B. File B's markdown still shows "Called-by: A.Method()" -- a phantom caller. Worse, fan-in count for X is now wrong, and danger annotations based on fan-in are stale.

**Consequences:**
- Obsidian vault contains contradictory information (method X says it's called by A, but A's note doesn't mention X)
- Fan-in-based danger annotations are wrong (method still flagged as high-risk after callers were removed, or not flagged after callers were added)
- Users learn to distrust incremental output and always re-run full analysis, negating the feature's value
- Inheritance tracking is especially fragile: adding a new interface implementation in file C doesn't trigger re-analysis of the interface definition in file D

**Warning signs:**
- Wikilinks point to methods that no longer exist
- Fan-in counts don't match grep of actual callers
- "Called-by" section lists methods that were renamed or deleted

**Prevention:**
1. **Two-tier incremental strategy**: Use git diff to identify changed files, but always rebuild the FULL solution compilation (it's fast -- the expensive part is LLM calls, not Roslyn). Then re-extract call graph edges for changed files AND their transitive dependents.
2. **Dependency graph for invalidation**: Maintain a `.code2obsidian/deps.json` that maps each output file to its input dependencies (source files, call graph edges). When any dependency changes, invalidate and regenerate the output file.
3. **Separate concerns**: Make Roslyn analysis always full-solution (it takes seconds to minutes, not hours). Make LLM enrichment incremental (it's the expensive part). Only re-enrich methods whose source code hash changed.
4. **Staleness markers**: If incremental mode cannot guarantee consistency for a file, mark it in the YAML frontmatter: `stale: true` with a timestamp, so users know to re-run full analysis.

**Phase:** Incremental processing (Phase 2 or 3). Design the invalidation strategy BEFORE implementing incremental mode. Retrofitting correct invalidation onto a naive file-diff approach is a rewrite.

**Confidence:** HIGH -- this is a well-known problem in incremental build systems (Make, Bazel, MSBuild all solve this differently). The pattern is universal, not Roslyn-specific.

---

### Pitfall 5: MSBuildWorkspace Hangs or Crashes on CI/Build Agents

**What goes wrong:** `MSBuildWorkspace.OpenSolutionAsync()` hangs indefinitely on CI servers (especially Azure DevOps Windows 2022 agents) or crashes with cryptic MEF composition errors. The tool works perfectly on developer machines.

**Why it happens:** MSBuildWorkspace depends on having a compatible MSBuild installation. CI agents may have different .NET SDK versions, Visual Studio versions, or no Visual Studio at all. Starting with Roslyn 4.9.0+, `OpenSolutionAsync` uses out-of-process build hosts that can hang when the build host process exits unexpectedly. The `MSBuildLocator` picks up the wrong SDK or none at all. Developer machines typically have Visual Studio installed (providing a reliable MSBuild), but CI agents may only have the .NET SDK.

**Consequences:**
- Tool hangs forever in CI pipelines, consuming build minutes
- Intermittent failures make the tool seem unreliable
- Different analysis results between local and CI runs
- Blocks adoption for teams that want to automate vault generation

**Warning signs:**
- Tool works locally but fails in Docker or CI
- `EnsureMsbuildRegistered()` silently picks a different SDK version
- No timeout on `OpenSolutionAsync()`

**Prevention:**
1. Add a `CancellationToken` with a configurable timeout (default: 5 minutes) to `OpenSolutionAsync()`
2. Log which MSBuild instance was registered (version, path) for debugging
3. Document required SDK version in tool's README
4. Test the tool in a Docker container (mcr.microsoft.com/dotnet/sdk:8.0) as part of CI
5. Pin Roslyn package versions and test against specific .NET SDK versions
6. Consider adding `--msbuild-path` flag for users to manually specify MSBuild location

**Phase:** Core infrastructure (Phase 1). The existing `EnsureMsbuildRegistered()` method (lines 243-277) has good fallback logic but no timeout or logging of which instance was selected.

**Confidence:** HIGH -- verified against [dotnet/roslyn#75967](https://github.com/dotnet/roslyn/issues/75967) (hangs on Azure CI), [dotnet/roslyn#75292](https://github.com/dotnet/roslyn/issues/75292) (BuildHost process exit), [dotnet/roslyn#14325](https://github.com/dotnet/roslyn/issues/14325) (takes very long time).

---

## Moderate Pitfalls

Issues that cause incorrect output or degraded experience but don't block the project.

---

### Pitfall 6: Akka.NET Actor Message Handlers Are Invisible to Standard Call Graph Analysis

**What goes wrong:** In Akka.NET codebases, message handlers registered via `Receive<T>()`, `ReceiveAsync<T>()`, or pattern-matching in `OnReceive()` are lambda-based or delegate-based. Standard Roslyn invocation analysis (`InvocationExpressionSyntax`) finds the registration call but NOT the logical message flow. The call graph shows "Actor.ctor calls Receive<MyMessage>" but misses that `MyMessage` is sent by `ProducerActor.Handle()` via `actorRef.Tell(new MyMessage())`.

**Why it happens:** Actor-model communication is inherently decoupled. `Tell()` sends a message to an `IActorRef`, which is resolved at runtime, not at compile time. Roslyn's semantic model cannot resolve `IActorRef` to a specific actor type because actor references are created via `Context.ActorOf<T>()` or dependency injection. The message type is the only static link between sender and receiver.

**Consequences:**
- Call graph has no edges between actors (they appear as isolated islands)
- Fan-in analysis misses actor message handlers entirely
- Danger annotations for high-traffic message handlers don't fire
- The most architecturally important communication patterns in the Akka.NET codebase are invisible

**Prevention:**
1. Build a **message-type index**: For each type T used in `Receive<T>()`, find all `Tell(new T())` and `Ask<T>()` call sites. Draw edges from sender to receiver via message type.
2. Use Akka.NET's own [Akka.Analyzer](https://github.com/akkadotnet/akka.net/blob/dev/docs/articles/debugging/akka-analyzers.md/) patterns as reference for what to detect.
3. Generate a separate "Message Flow" section in Obsidian output, distinct from direct call graph.
4. Detect `ReceiveActor` subclasses and `UntypedActor.OnReceive` overrides as entry points for handler discovery.

**Phase:** Pattern detection (Phase 3 or 4). This is a domain-specific enhancement. Standard call graph should work first; actor message flow is layered on top.

**Confidence:** MEDIUM -- Akka.Analyzer source confirms the Receive pattern detection approach. Message-type indexing is a known technique in actor system tooling, but implementing it correctly for all Akka.NET patterns (Receive, ReceiveAny, Become, Stash) requires careful work.

---

### Pitfall 7: Generic Types and Abstract Repository Patterns Produce Duplicate or Missing Symbols

**What goes wrong:** An `IRepository<T>` interface with methods like `Save(T entity)` creates different IMethodSymbol instances for `IRepository<Order>.Save()` and `IRepository<Customer>.Save()`. The tool either generates duplicate markdown files (`IRepository.Save.md` overwritten multiple times) or misses concrete implementations because `OriginalDefinition` normalization strips the type arguments needed to distinguish them.

**Why it happens:** Roslyn represents generic instantiations as distinct symbols. `IRepository<Order>.Save()` and `IRepository<Customer>.Save()` have different `IMethodSymbol` instances. Using `OriginalDefinition` collapses them back to `IRepository<T>.Save()`, which is correct for deduplication but loses the concrete type information. Meanwhile, `FindImplementationsAsync` for `IRepository<T>.Save()` finds all implementations, but matching them to specific generic instantiations requires tracking type argument substitution.

**Consequences:**
- File naming collisions: `IRepository.Save.md` is generated for every generic instantiation, each overwriting the previous
- Lost type-argument context: The generated docs say "Save(T entity)" instead of "Save(Order entity)"
- Inheritance tree is confusing: all `IRepository<T>` implementations appear under one node regardless of T

**Prevention:**
1. Include type arguments in the canonical identifier: `IRepository<Order>.Save` not just `IRepository.Save`
2. Use `symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)` for file naming, with proper sanitization
3. For the Obsidian vault, create a parent note `IRepository<T>` that links to instantiation-specific notes
4. When building inheritance trees, distinguish between the open generic definition and its closed instantiations

**Phase:** Core analysis (Phase 2). The current `Sanitize()` method (line 363) handles invalid filename chars but doesn't address generic type argument collisions. The filename pattern on line 158 (`{method.ContainingType?.Name}.{method.Name}.md`) will collide for all `IRepository<T>` instantiations.

**Confidence:** HIGH -- verified against existing code and [Roslyn generic symbol discussion](https://github.com/dotnet/roslyn/discussions/65410).

---

### Pitfall 8: Obsidian Wikilink Collisions for Common Method Names

**What goes wrong:** Methods like `ToString()`, `Dispose()`, `Handle()`, `Execute()`, `Save()` exist in dozens of classes. The current wikilink format `[[MethodName]]` creates ambiguous links. Obsidian resolves `[[Save]]` to whichever `Save.md` it finds first (nondeterministic), or shows a disambiguation popup that breaks the reading flow.

**Why it happens:** The existing code (lines 205-206, 213-214) generates wikilinks using only the method name: `[[{callee.Name}]]`. For enterprise codebases with hundreds of classes, method name collisions are the norm, not the exception. Every `IDisposable` implementation has `Dispose`, every command handler has `Execute` or `Handle`.

**Consequences:**
- Clicking a wikilink navigates to the wrong method's note
- Obsidian graph view shows false relationships (all `Dispose` methods appear connected)
- Users lose trust in the navigation and stop using wikilinks
- The vault's primary value proposition (interconnected documentation) is undermined

**Prevention:**
1. Use fully qualified wikilinks: `[[ClassName.MethodName]]` matching the file naming convention
2. In per-file mode, use section links: `[[FileName#MethodName]]`
3. Add aliases in YAML frontmatter for short names: `aliases: [Save]` so Obsidian can still resolve informal references
4. Generate an index note per class that lists all its methods with qualified links

**Phase:** Output generation refactor (Phase 1 or 2). This is a breaking change to the current output format but essential for correctness at scale. Do it before users build workflows around the current format.

**Confidence:** HIGH -- verified against [Obsidian internal links documentation](https://deepwiki.com/obsidianmd/obsidian-help/4.2-internal-links-and-graph-view) and observable in the existing code.

---

### Pitfall 9: Cyclomatic Complexity Calculation Disagrees with Other Tools

**What goes wrong:** The cyclomatic complexity values reported by Code2Obsidian don't match Visual Studio's built-in code metrics, SonarQube, or other established tools. Users question the tool's accuracy, especially when danger annotations are based on complexity thresholds.

**Why it happens:** There is no single universally agreed formula for cyclomatic complexity in C#. Different tools count different constructs:
- Some count `&&` and `||` as branches, others don't
- Pattern matching (`switch` expressions, `is` patterns) may or may not add to complexity
- `catch` blocks: some tools count each catch, others don't
- Null-coalescing (`??`) and conditional access (`?.`): inconsistent treatment
- Visual Studio's own metrics tool disagrees with its CLI counterpart (`Metrics.exe`)

**Consequences:**
- Developers argue about whether the complexity numbers are "right"
- Danger annotations fire on methods that other tools consider acceptable (or miss methods they flag)
- Credibility of the entire analysis is undermined by one metric disagreement

**Prevention:**
1. Document exactly which constructs contribute to the complexity count
2. Implement the same formula as Microsoft's `CA1502` rule (the most widely recognized in .NET): 1 + count of (`if`, `while`, `for`, `foreach`, `case`, `default`, `continue`, `goto`, `&&`, `||`, `??`, `catch`, `?:` ternary)
3. Add `complexity_formula: "CA1502"` to YAML frontmatter so users know which standard is used
4. Consider also computing Cognitive Complexity (SonarSource's metric) as an alternative, since it better reflects readability
5. Allow users to set their own thresholds via configuration rather than hardcoding "high complexity = danger"

**Phase:** Metric calculation (Phase 2 or 3). Implement complexity calculation once, correctly, with clear documentation. Don't ship a half-baked version that will need to change later.

**Confidence:** MEDIUM -- verified that [tools disagree on calculation](https://github.com/dotnet/roslyn-analyzers/issues/1840). The specific construct list for CA1502 needs validation against [Microsoft docs](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1502) during implementation.

---

### Pitfall 10: Memory Exhaustion on Large Solutions

**What goes wrong:** Loading an enterprise solution with 50-100+ projects into MSBuildWorkspace consumes 4-8GB of RAM. On CI agents with limited memory or developer machines running other processes, the tool crashes with `OutOfMemoryException` or causes system-wide thrashing.

**Why it happens:** MSBuildWorkspace loads ALL projects and their compilations into memory simultaneously. Each compilation includes syntax trees, semantic models, and metadata references. The current code calls `GetCompilationAsync()` and `GetSemanticModelAsync()` for every document, holding all results in memory via the dictionaries on lines 65-66. For a solution with 100 projects averaging 50 files each, that's 5000 syntax trees + 5000 semantic models + 100 compilations all resident simultaneously.

**Consequences:**
- OOM crash mid-analysis with no partial output saved
- System becomes unresponsive during analysis
- Cannot analyze the largest enterprise solutions (the ones that benefit most from documentation)

**Prevention:**
1. Process projects one at a time: load, analyze, extract data to lightweight DTOs, then allow GC to reclaim the compilation
2. Use `project.GetCompilationAsync()` per-project rather than holding all compilations simultaneously
3. For cross-project analysis (call graph edges), do a two-pass approach: first pass collects lightweight symbol keys, second pass resolves cross-references
4. Add `--max-memory` flag or detect available memory and warn before loading
5. Consider using `LoadMetadataForReferencedProjects` property to use pre-built assemblies for referenced projects instead of loading their source

**Phase:** Core infrastructure (Phase 1-2). The current single-pass approach (lines 68-111) loads everything eagerly. Refactoring to streaming/per-project processing is a significant change but necessary for enterprise-scale targets.

**Confidence:** HIGH -- verified against [dotnet/roslyn#3078](https://github.com/dotnet/roslyn/issues/3078), [dotnet/roslyn#78868](https://github.com/dotnet/roslyn/issues/78868), [dotnet/roslyn#56576](https://github.com/dotnet/roslyn/issues/56576).

---

## Minor Pitfalls

Issues worth knowing about but unlikely to cause major problems.

---

### Pitfall 11: Partial Classes Produce Fragmented Analysis

**What goes wrong:** Partial classes (common in WPF, Blazor, and source-generated code) have their declarations split across multiple files. The tool may analyze each partial declaration separately, missing members defined in other files. `GetDeclaredSymbol` for a partial class declaration returns the full type symbol, but iterating `DescendantNodes()` of one file's syntax tree only finds members declared in that file.

**Prevention:**
1. After collecting all methods per file, group by `ContainingType` to merge partial class members
2. Use `INamedTypeSymbol.GetMembers()` from the semantic model to get ALL members regardless of which file they're declared in
3. In per-file output mode, add cross-references: "See also: [OtherPartialFile.md]"

**Phase:** Core analysis (Phase 2). Low priority unless target codebases use partial classes heavily.

**Confidence:** MEDIUM -- partial class behavior verified from Roslyn API semantics; specific `GetDeclaredSymbol` behavior with partials noted in [dotnet/roslyn#48](https://github.com/dotnet/roslyn/issues/48).

---

### Pitfall 12: Ollama Context Window Silently Truncates Input

**What goes wrong:** Ollama defaults to a 4096-token context window regardless of the model's actual capability. Large method bodies or class-level context sent to a local Ollama model get silently truncated (oldest tokens dropped), producing LLM responses that reference only the tail end of the input.

**Prevention:**
1. Set `num_ctx` explicitly when calling Ollama API (e.g., 8192 or 16384 for code analysis)
2. Pre-tokenize input and check it fits within the configured context window before sending
3. If input exceeds context, chunk it or summarize the class context rather than sending it raw
4. Document minimum model requirements (e.g., "Ollama models need at least 8K context for class-level analysis")

**Phase:** LLM integration (Phase 3). Must be addressed when adding Ollama provider support.

**Confidence:** HIGH -- verified against [Ollama context length docs](https://docs.ollama.com/context-length) and multiple community reports of silent truncation.

---

### Pitfall 13: YAML Frontmatter Special Character Injection

**What goes wrong:** Method names, class names, or file paths containing special YAML characters (colons, quotes, brackets, Unicode) corrupt the YAML frontmatter in generated markdown files. Obsidian fails to parse the frontmatter, losing tags, metadata, and properties.

**Prevention:**
1. Always quote string values in YAML frontmatter: `source_file: "path/to/file.cs"` not `source_file: path/to/file.cs`
2. Escape or sanitize values that might contain `: ` (colon-space), `#`, `[`, `]`, `{`, `}`
3. Use a YAML serialization library rather than string concatenation for frontmatter generation
4. Test with adversarial inputs: method names like `operator==`, generic types like `Dictionary<string, List<int>>`, paths with spaces

**Phase:** Output generation (Phase 1-2). The current code (lines 135-137, 229-233) uses raw `StringBuilder.AppendLine()` for YAML. Low risk currently but will break as more metadata is added to frontmatter.

**Confidence:** HIGH -- observable from current code; YAML spec is well-documented.

---

### Pitfall 14: Event Handler and DI Registration Patterns Missed by Invocation Analysis

**What goes wrong:** Dependency injection registrations (`services.AddScoped<IFoo, Foo>()`) and event handler subscriptions (`button.Click += OnClick`) create runtime connections that don't appear as direct invocations in Roslyn's syntax tree. The call graph misses these critical wiring points.

**Prevention:**
1. Detect common DI registration patterns (`AddScoped`, `AddTransient`, `AddSingleton`, `Bind`, `Register`) and create edges from interface to implementation
2. Detect event handler patterns (`+=` on event members) and create edges from event source to handler
3. Mark these as "indirect" edges in the call graph with a different visual style in Obsidian output
4. For Akka.NET specifically, detect `Props.Create<T>()` and `Context.ActorOf<T>()` as actor instantiation points

**Phase:** Pattern detection (Phase 3-4). Layer on top of working direct call graph.

**Confidence:** MEDIUM -- these patterns are well-known in static analysis tools, but comprehensive detection across all DI frameworks (Microsoft.Extensions.DI, Autofac, Ninject, etc.) is a large surface area. Focus on Microsoft.Extensions.DI first.

---

## Phase-Specific Warnings

| Phase Topic | Likely Pitfall | Mitigation | Severity |
|-------------|---------------|------------|----------|
| Solution loading (Phase 1) | #1 Silent compilation failures, #5 CI hangs | NuGet restore gate, WorkspaceFailed handler, timeouts | CRITICAL |
| Core analysis refactor (Phase 1-2) | #2 Cross-project symbol identity, #7 Generic dedup | SymbolKey or canonical string IDs, generic-aware naming | CRITICAL |
| Output generation (Phase 1-2) | #8 Wikilink collisions, #13 YAML injection | Qualified wikilinks, YAML library | MODERATE |
| Incremental processing (Phase 2-3) | #4 Stale output | Full Roslyn analysis + incremental LLM enrichment | CRITICAL |
| LLM enrichment (Phase 3) | #3 Cost explosion, #12 Ollama truncation | Cost estimation, caching, context window management | CRITICAL |
| Pattern detection (Phase 3-4) | #6 Actor invisibility, #14 DI/event patterns | Message-type indexing, DI pattern detection | MODERATE |
| Metric calculation (Phase 2-3) | #9 Complexity disagreement | Document formula, match CA1502, configurable thresholds | LOW |
| Scale testing (Phase 2) | #10 Memory exhaustion | Per-project processing, streaming architecture | MODERATE |

---

## Sources

### Roslyn / MSBuildWorkspace
- [Using MSBuildWorkspace (Dustin Campbell)](https://gist.github.com/DustinCampbell/32cd69d04ea1c08a16ae5c4cd21dd3a3) - canonical usage guide
- [dotnet/roslyn#15479: MSBuildWorkspace 0 references](https://github.com/dotnet/roslyn/issues/15479)
- [dotnet/roslyn#75967: OpenSolutionAsync hangs on CI](https://github.com/dotnet/roslyn/issues/75967)
- [dotnet/roslyn#75182: Warnings reported as errors](https://github.com/dotnet/roslyn/issues/75182)
- [dotnet/roslyn#62465: Cross-compilation symbol comparison](https://github.com/dotnet/roslyn/issues/62465)
- [dotnet/roslyn#58226: SymbolEqualityComparer behavior change](https://github.com/dotnet/roslyn/issues/58226)
- [dotnet/roslyn#3078: OOM loading large solutions](https://github.com/dotnet/roslyn/issues/3078)
- [dotnet/roslyn#78868: High memory mid-sized solution](https://github.com/dotnet/roslyn/issues/78868)
- [dotnet/roslyn#23913: GetSymbolInfo returns null](https://github.com/dotnet/roslyn/issues/23913)
- [dotnet/roslyn-analyzers#1840: Metrics.exe disagrees with VS](https://github.com/dotnet/roslyn-analyzers/issues/1840)
- [Roslyn performance considerations](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Performance-considerations-for-large-solutions.md)
- [SymbolEqualityComparer docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.symbolequalitycomparer)
- [CA1502: Avoid excessive complexity](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1502)
- [FindDerivedClassesAsync docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.findsymbols.symbolfinder.findderivedclassesasync)

### Akka.NET
- [Akka.Analyzer (Roslyn analyzer for Akka.NET)](https://github.com/akkadotnet/akka.net/blob/dev/docs/articles/debugging/akka-analyzers.md/)
- [ReceiveActor API](https://getakka.net/articles/actors/receive-actor-api.html)

### LLM Cost and Rate Limiting
- [Rate Limits for LLM Providers](https://www.requesty.ai/blog/rate-limits-for-llm-providers-openai-anthropic-and-deepseek)
- [OpenAI Rate Limits](https://platform.openai.com/docs/guides/rate-limits)
- [LLM Response Caching (AWS)](https://aws.amazon.com/blogs/database/optimize-llm-response-costs-and-latency-with-effective-caching/)
- [Cost-Effective LLM Applications](https://www.glukhov.org/post/2025/11/cost-effective-llm-applications/)

### Ollama
- [Ollama Context Length](https://docs.ollama.com/context-length)

### Obsidian
- [Obsidian Internal Links](https://deepwiki.com/obsidianmd/obsidian-help/4.2-internal-links-and-graph-view)
