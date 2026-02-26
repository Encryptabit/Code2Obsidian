# Technology Stack

**Project:** Code2Obsidian
**Researched:** 2026-02-25
**Overall Confidence:** HIGH

## Recommended Stack

### Runtime

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| .NET 8.0 (LTS) | 8.0.x | Target framework | Stay on .NET 8 for now. Both .NET 8 (LTS) and .NET 9 (STS) reach end-of-support in November 2026 simultaneously. .NET 10 (LTS, released Nov 2025) is the next long-term option. However, upgrading to .NET 10 gains nothing for this tool and risks MSBuild/Roslyn compatibility issues with the target codebases being analyzed. The tool must load solutions built on .NET 6/7/8 -- matching the target runtime reduces friction. Plan a .NET 10 upgrade as a separate future task when enterprise codebases migrate. | HIGH |

### Core Analysis Engine (Roslyn)

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Microsoft.CodeAnalysis.CSharp.Workspaces | 4.14.0 | C# syntax tree and semantic analysis | Already in use. Version 5.0.0 is available (published Nov 2025, ships with .NET 10 tooling) and targets .NET 8+, so it could be upgraded. However, 4.14.0 is stable and proven with the existing codebase. **Upgrade to 5.0.0 only if a specific new Roslyn API is needed** -- it introduces dependency on System.Collections.Immutable >= 9.0.0 and System.Composition >= 9.0.0 which may conflict with older MSBuild toolchains. | HIGH |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 4.14.0 | Solution/project loading via MSBuild | Already in use. Same version-pinning rationale as above. The MSBuild workspace package in 5.0.0 depends on Microsoft.Build.Framework >= 17.11.31, which should be fine but gains nothing for our use case. | HIGH |
| Microsoft.Build.Locator | 1.11.2 | Locate and register MSBuild assemblies | **Upgrade from 1.9.1 to 1.11.2.** This is a low-risk upgrade. Version 1.11.2 (Nov 2025) improves .NET SDK discovery and handles more installation scenarios, which directly addresses the fragile MSBuild registration fallback chain identified in the concerns audit. | HIGH |

### LLM Integration

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Microsoft.Extensions.AI | 10.3.0 | Provider-agnostic LLM abstraction layer (IChatClient interface, middleware pipeline) | This is the official Microsoft abstraction for LLM integration in .NET. It provides `IChatClient` -- a single interface that all three required providers (Anthropic, OpenAI, Ollama) implement. This means the enrichment engine codes against `IChatClient` once, and provider selection is purely a configuration concern. Includes built-in middleware for telemetry, caching, and rate limiting via familiar DI patterns. This replaces the need for Semantic Kernel, which is heavier and designed for agent orchestration -- overkill for simple prompt-and-response enrichment. | HIGH |
| Microsoft.Extensions.AI.OpenAI | 10.3.0 | OpenAI provider for IChatClient | Official Microsoft package. Wraps the OpenAI .NET SDK (2.8.0) and exposes it as IChatClient via `.AsIChatClient()`. Also works with OpenAI-compatible endpoints (Azure OpenAI, local proxies). | HIGH |
| Anthropic | 12.4.0 | Anthropic/Claude provider for IChatClient | Official Anthropic C# SDK (taken over from community package at v10+). Implements IChatClient from Microsoft.Extensions.AI.Abstractions >= 10.2.0 via `.AsIChatClient()`. Currently in beta but actively maintained (last updated Feb 2026). The official SDK is the right choice over the community Anthropic.SDK package. | MEDIUM |
| OllamaSharp | 5.4.16 | Ollama (local model) provider for IChatClient | Recommended replacement for the deprecated Microsoft.Extensions.AI.Ollama package. Implements both IChatClient and IEmbeddingGenerator from Microsoft.Extensions.AI. Powers Microsoft Semantic Kernel and .NET Aspire Ollama integrations. Battle-tested. | HIGH |

### CLI Framework

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| System.CommandLine | 2.0.3 | CLI argument parsing, help generation, tab completion | Replace the hand-rolled queue-based argument parser. System.CommandLine 2.0.3 is the current stable release (targeting stable 2.0.0 around .NET 10 timeframe). It provides: typed option binding, auto-generated help text, shell completions, middleware pipeline for cross-cutting concerns, and NativeAOT support. The existing parser identified in the concerns audit is fragile and will become unmaintainable as we add `--enrich`, `--incremental`, `--config`, `--provider`, and other flags. 32% smaller than previous betas with 40% faster parsing. | HIGH |

### Git Integration

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| LibGit2Sharp | 0.31.0 | Git diff detection for incremental mode | Provides native git operations without shelling out to `git` CLI. Supports diffing commits/trees, reading file status, and accessing repository history. Targets .NET 8.0 and .NET Framework 4.7.2. The alternative (shelling out to `git diff --name-only`) is simpler but fragile -- it requires git on PATH, parsing stdout, and handling encoding. LibGit2Sharp gives typed APIs: `repo.Diff.Compare<TreeChanges>(oldTree, newTree)` returns structured change lists. The tradeoff is a native binary dependency (libgit2), but this is well-handled via the LibGit2Sharp.NativeBinaries transitive package. | HIGH |

### Configuration

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| System.Text.Json | (built-in) | Read/write `.code2obsidian.json` config file | Already part of .NET 8 runtime -- zero additional dependencies. Use `JsonSerializer.Deserialize<T>()` for strongly-typed config binding. For a CLI tool that reads a single config file, the full Microsoft.Extensions.Configuration stack is unnecessary overhead. A simple JSON config file in the project root (or user home directory) with provider settings, API keys, and enrichment preferences is sufficient. | HIGH |

### Logging / Progress Reporting

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Spectre.Console | 0.54.0 | Rich console output: progress bars, status spinners, styled text | The tool currently uses raw `Console.WriteLine`. For large solutions (100+ files), users need progress feedback during analysis and especially during LLM enrichment (which is slow). Spectre.Console provides: `AnsiConsole.Progress()` for multi-step progress, `AnsiConsole.Status()` for spinners during API calls, styled error/warning output, and table rendering for summaries. Much lighter than Serilog for a CLI tool that writes to stdout only. | HIGH |

### Markdown / YAML Generation

| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| StringBuilder (built-in) | N/A | Markdown template rendering | Continue using `StringBuilder` for markdown generation. The current approach works and is zero-dependency. Markdown is simple string concatenation -- a templating engine (Scriban, Razor) adds complexity without proportional value for structured frontmatter + sections. | HIGH |
| (No YAML library) | N/A | YAML frontmatter in markdown | Continue hand-writing YAML frontmatter with string concatenation. The frontmatter is trivially simple (`tags`, `aliases`, `type` fields). YamlDotNet (16.3.0) exists but is overkill for emitting 3-5 flat key-value pairs. Only add YamlDotNet if frontmatter grows to include nested structures or user-defined fields. | HIGH |

## Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| LLM Abstraction | Microsoft.Extensions.AI (MEAI) | Semantic Kernel 1.72.0 | Semantic Kernel is an agent orchestration framework -- overkill for prompt-and-response enrichment. MEAI provides the same IChatClient interface (Semantic Kernel's primitives were migrated to MEAI) without the agent, planner, and kernel abstractions. Using SK would pull in 15+ transitive dependencies for features we will never use. |
| LLM Abstraction | Microsoft.Extensions.AI (MEAI) | Direct HTTP calls per provider | Maintaining 3 separate HTTP client implementations (Anthropic, OpenAI, Ollama) with different auth, streaming, and error handling patterns is exactly the problem MEAI solves. The abstraction cost is minimal and the maintainability gain is significant. |
| Anthropic SDK | Anthropic 12.4.0 (official) | Anthropic.SDK 5.10.0 (community) | The official SDK now implements IChatClient natively. The community package also supports IChatClient but will likely lose relevance as the official SDK matures. Go with the official one for long-term support. |
| CLI Parsing | System.CommandLine 2.0.3 | Spectre.Console.Cli 0.53.1 | System.CommandLine is the Microsoft-blessed approach, heading toward .NET BCL inclusion. Spectre.Console.Cli is being split into its own repo for a 1.0 release -- version churn risk. System.CommandLine also has NativeAOT support and shell completion built in. |
| CLI Parsing | System.CommandLine 2.0.3 | CommandLineParser 2.9.1 | CommandLineParser is mature but has no active development. System.CommandLine is the modern replacement with better async support and middleware patterns. |
| CLI Parsing | System.CommandLine 2.0.3 | Hand-rolled (current) | The current queue-based parser is fragile, has no help generation, and will become unmaintainable as the CLI surface grows. Not viable for 10+ flags. |
| Git Integration | LibGit2Sharp 0.31.0 | Shell out to `git` CLI | Requires git on PATH, stdout parsing, encoding handling, and process management. Works for simple cases but breaks in CI/CD environments, Docker containers without git, and when dealing with non-UTF8 paths. LibGit2Sharp gives typed APIs and works anywhere .NET runs. |
| Git Integration | LibGit2Sharp 0.31.0 | File timestamp comparison | Timestamps are unreliable (git checkout resets them, CI/CD builds have uniform timestamps). Git diff is the only reliable change detection for code repositories. |
| Logging | Spectre.Console 0.54.0 | Serilog 4.3.1 | Serilog is designed for structured logging to sinks (files, seq, elasticsearch). This tool writes to stdout for human consumption. Spectre.Console provides rich terminal UI (progress, tables, colors) which is what a CLI tool needs. |
| Logging | Spectre.Console 0.54.0 | Raw Console.WriteLine (current) | No progress feedback, no color, no structured output. Unacceptable UX when processing 100+ files with LLM enrichment that takes minutes. |
| Code Metrics | Custom Roslyn walker | ArchiMetrics.Analysis 2.0.1 | ArchiMetrics provides cyclomatic complexity, afferent/efferent coupling, and other metrics via Roslyn. However, it was last updated in 2017 and targets old Roslyn versions (incompatible with 4.14.0). Build our own `CyclomaticComplexityWalker` using `CSharpSyntaxWalker` -- it is ~50 lines of code to count branch points (`if`, `while`, `for`, `case`, `&&`, `||`, `??`, `catch`). Fan-in/fan-out we already compute via the call graph. |
| Target Framework | .NET 8.0 | .NET 10.0 | .NET 10 is LTS and current, but upgrading provides no features this tool needs. The analyzed codebases are on .NET 6/7/8. The Roslyn 4.14.0 packages target .NET Standard 2.0, so they work on both. Upgrade to .NET 10 later as a dedicated task. |
| Config | System.Text.Json | Microsoft.Extensions.Configuration | Full configuration pipeline (JSON provider, env vars, command-line override, options pattern, DI) is designed for ASP.NET Core applications. For a CLI tool reading one JSON file, `JsonSerializer.Deserialize<Config>(File.ReadAllText(path))` is simpler, faster, and zero-dependency. |
| YAML | String concatenation | YamlDotNet 16.3.0 | The YAML frontmatter is 3-5 flat fields. A YAML serialization library adds a dependency for `tags:\n  - method\n`. Not worth it unless frontmatter becomes user-configurable with nested structures. |

## Package Reference Summary

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Roslyn / MSBuild (existing, upgrade Build.Locator) -->
    <PackageReference Include="Microsoft.Build.Locator" Version="1.11.2" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.14.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="4.14.0" />

    <!-- CLI Framework -->
    <PackageReference Include="System.CommandLine" Version="2.0.3" />

    <!-- LLM Integration (provider-agnostic) -->
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.3.0" />

    <!-- LLM Providers (one or more based on user config) -->
    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.3.0" />
    <PackageReference Include="Anthropic" Version="12.4.0" />
    <PackageReference Include="OllamaSharp" Version="5.4.16" />

    <!-- Git Integration -->
    <PackageReference Include="LibGit2Sharp" Version="0.31.0" />

    <!-- Console UX -->
    <PackageReference Include="Spectre.Console" Version="0.54.0" />
  </ItemGroup>
</Project>
```

## Installation

```bash
# From the project directory
dotnet add package Microsoft.Build.Locator --version 1.11.2
dotnet add package System.CommandLine --version 2.0.3
dotnet add package Microsoft.Extensions.AI --version 10.3.0
dotnet add package Microsoft.Extensions.AI.OpenAI --version 10.3.0
dotnet add package Anthropic --version 12.4.0
dotnet add package OllamaSharp --version 5.4.16
dotnet add package LibGit2Sharp --version 0.31.0
dotnet add package Spectre.Console --version 0.54.0
```

## Architecture Implications

### LLM Provider Selection Pattern

The MEAI stack enables a clean provider factory pattern:

```csharp
// Config determines which IChatClient implementation is created
IChatClient CreateClient(LlmConfig config) => config.Provider switch
{
    "anthropic" => new AnthropicClient(config.ApiKey).AsIChatClient(config.Model),
    "openai"    => new OpenAI.Chat.ChatClient(config.Model, config.ApiKey).AsIChatClient(),
    "ollama"    => new OllamaApiClient(new Uri(config.Endpoint), config.Model),
    _ => throw new InvalidOperationException($"Unknown provider: {config.Provider}")
};

// All enrichment code uses IChatClient -- provider-agnostic
async Task<string> EnrichMethod(IChatClient client, string sourceCode, string context)
{
    var response = await client.GetResponseAsync($"""
        Summarize what this C# method does in plain English.
        Include: purpose, side effects, error handling approach.

        ```csharp
        {sourceCode}
        ```

        Context: {context}
        """);
    return response.Text;
}
```

### Configuration File Shape

```json
{
  "llm": {
    "provider": "anthropic",
    "model": "claude-sonnet-4-20250514",
    "apiKey": "${ANTHROPIC_API_KEY}",
    "endpoint": null,
    "maxTokens": 1024,
    "temperature": 0.3
  },
  "output": {
    "mode": "per-file",
    "directory": "./_obsidian",
    "includeSourceSnippets": true,
    "maxSnippetLines": 30
  },
  "analysis": {
    "skipPatterns": ["*.Designer.cs", "*.g.cs", "*/Migrations/*"],
    "dangerThresholds": {
      "highFanIn": 10,
      "cyclomaticComplexity": 15,
      "parameterCount": 6
    }
  }
}
```

### Cyclomatic Complexity Implementation

Build a custom `CSharpSyntaxWalker` rather than depending on ArchiMetrics:

```csharp
// ~50 lines, zero dependencies beyond Roslyn (already present)
internal sealed class CyclomaticComplexityWalker : CSharpSyntaxWalker
{
    public int Complexity { get; private set; } = 1; // Every method starts at 1

    public override void VisitIfStatement(IfStatementSyntax node) { Complexity++; base.VisitIfStatement(node); }
    public override void VisitWhileStatement(WhileStatementSyntax node) { Complexity++; base.VisitWhileStatement(node); }
    public override void VisitForStatement(ForStatementSyntax node) { Complexity++; base.VisitForStatement(node); }
    public override void VisitForEachStatement(ForEachStatementSyntax node) { Complexity++; base.VisitForEachStatement(node); }
    public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node) { Complexity++; base.VisitCaseSwitchLabel(node); }
    public override void VisitCatchClause(CatchClauseSyntax node) { Complexity++; base.VisitCatchClause(node); }
    public override void VisitConditionalExpression(ConditionalExpressionSyntax node) { Complexity++; base.VisitConditionalExpression(node); }
    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression)
            || node.IsKind(SyntaxKind.CoalesceExpression))
            Complexity++;
        base.VisitBinaryExpression(node);
    }
}
```

## Dependency Graph

```
Code2Obsidian
  +-- Microsoft.CodeAnalysis.CSharp.Workspaces 4.14.0
  |     +-- Microsoft.CodeAnalysis.CSharp
  |     +-- Microsoft.CodeAnalysis.Workspaces.Common
  +-- Microsoft.CodeAnalysis.Workspaces.MSBuild 4.14.0
  +-- Microsoft.Build.Locator 1.11.2
  +-- System.CommandLine 2.0.3
  +-- Microsoft.Extensions.AI 10.3.0
  |     +-- Microsoft.Extensions.AI.Abstractions 10.3.0
  +-- Microsoft.Extensions.AI.OpenAI 10.3.0
  |     +-- OpenAI 2.8.0
  +-- Anthropic 12.4.0
  |     +-- Microsoft.Extensions.AI.Abstractions >= 10.2.0
  +-- OllamaSharp 5.4.16
  +-- LibGit2Sharp 0.31.0
  |     +-- LibGit2Sharp.NativeBinaries (transitive, platform-specific)
  +-- Spectre.Console 0.54.0
```

## Version Pinning Strategy

- **Roslyn packages**: Pin all three to the same version (4.14.0). Mismatched Roslyn versions cause assembly binding failures at runtime. Only upgrade as a coordinated set.
- **MEAI packages**: Pin Microsoft.Extensions.AI and Microsoft.Extensions.AI.OpenAI to the same version (10.3.0). The Anthropic and OllamaSharp packages declare their own compatible range for Microsoft.Extensions.AI.Abstractions.
- **LibGit2Sharp**: Pin to 0.31.0. This package bundles a native binary (libgit2) -- version changes can introduce platform-specific issues.
- **Everything else**: Use latest stable. These packages have stable APIs and low upgrade risk.

## Risk Assessment

| Package | Risk | Mitigation |
|---------|------|------------|
| Anthropic 12.4.0 | MEDIUM -- official SDK is in beta, API may have breaking changes between versions | Pin version, wrap in adapter. The IChatClient interface is stable (owned by Microsoft) even if the Anthropic SDK churns. |
| System.CommandLine 2.0.3 | LOW -- was in long beta but now at stable 2.0.3, heading toward BCL inclusion | API is stable. Migration from hand-rolled parser is one-time cost. |
| LibGit2Sharp 0.31.0 | LOW -- native dependency adds platform-specific concerns | Well-tested on Windows/Linux/macOS. The NativeBinaries package handles platform detection. |
| Microsoft.Extensions.AI 10.3.0 | LOW -- backed by Microsoft, the IChatClient interface is designed to be stable | This is the foundational AI abstraction for .NET going forward. Safe bet. |
| Spectre.Console 0.54.0 | LOW -- mature library, widely used, active development | Version 0.x but has been stable for years. The CLI package is being split out; we use only the core rendering package. |

## Sources

- [NuGet: Microsoft.CodeAnalysis.CSharp.Workspaces 5.0.0](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Workspaces/) - Framework targets, dependency verification
- [NuGet: Microsoft.CodeAnalysis 5.0.0](https://www.nuget.org/packages/Microsoft.CodeAnalysis/) - Latest Roslyn version
- [NuGet: Microsoft.Build.Locator 1.11.2](https://www.nuget.org/packages/Microsoft.Build.Locator/) - Latest stable version
- [Microsoft.Extensions.AI Libraries - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) - Official MEAI documentation, IChatClient interface
- [NuGet: Microsoft.Extensions.AI 10.3.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/) - Latest version, Feb 2026
- [NuGet: Microsoft.Extensions.AI.OpenAI 10.3.0](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI) - OpenAI provider
- [NuGet: Anthropic 12.4.0](https://www.nuget.org/packages/Anthropic/) - Official Anthropic C# SDK
- [Anthropic C# SDK - GitHub](https://github.com/anthropics/anthropic-sdk-csharp) - IChatClient support documentation
- [NuGet: OllamaSharp 5.4.16](https://www.nuget.org/packages/OllamaSharp) - Recommended Ollama provider
- [OllamaSharp - GitHub](https://github.com/awaescher/OllamaSharp) - IChatClient implementation details
- [NuGet: System.CommandLine 2.0.3](https://www.nuget.org/packages/System.CommandLine) - CLI parsing library
- [NuGet: LibGit2Sharp 0.31.0](https://www.nuget.org/packages/LibGit2Sharp) - Git integration
- [NuGet: Spectre.Console 0.54.0](https://www.nuget.org/packages/spectre.console) - Console UI library
- [.NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) - .NET 8/9/10 lifecycle
- [Generative AI with LLMs in C# - .NET Blog](https://devblogs.microsoft.com/dotnet/generative-ai-with-large-language-models-in-dotnet-and-csharp/) - MEAI as recommended starting point
- [NuGet: Microsoft.Extensions.AI.Ollama (deprecated)](https://www.nuget.org/packages/Microsoft.Extensions.AI.Ollama) - Deprecation notice, migrate to OllamaSharp

---

*Stack research: 2026-02-25*
