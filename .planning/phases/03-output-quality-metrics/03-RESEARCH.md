# Phase 3: Output Quality & Metrics - Research

**Researched:** 2026-02-25
**Domain:** Obsidian markdown emission, YAML frontmatter, Roslyn cyclomatic complexity, pattern detection, Dataview compatibility
**Confidence:** HIGH

## Summary

Phase 3 enriches the existing vault output with queryable metadata, collision-free wikilinks, danger annotations, and architectural pattern tags. The phase touches three layers: (1) the domain models (`MethodInfo`, `TypeInfo`) need new fields for metrics and metadata, (2) the analyzers (or a new enricher) must compute cyclomatic complexity and fan-in/fan-out counts, and (3) the emitter must produce expanded YAML frontmatter with snake_case keys, danger tags, pattern tags, and inline callout blocks.

The critical technical finding is that cyclomatic complexity can be computed via a lightweight syntax-tree walk during method analysis, counting branching constructs (`if`, `while`, `for`, `foreach`, `case`, `catch`, `&&`, `||`, `?:`, `??`, `?.`). This mirrors the approach used by Microsoft's own roslyn-analyzers `MetricsHelper.cs`. Fan-in and fan-out are already computable from the existing `CallGraph` -- fan-out = `callGraph.GetCallees(methodId).Count` and fan-in = `callGraph.GetCallers(methodId).Count`. Pattern detection is simple string suffix matching on class names, which requires no new Roslyn APIs.

The wikilink format change (`[[ClassName.MethodName]]` for method notes, existing `[[FullName]]` for type notes) primarily affects how the emitter generates cross-references in the Calls/Called-by sections. The current emitter uses `ExtractMethodName()` which strips to just the bare method name -- this must be updated to use the `ContainingTypeName.MethodName` format. For namespace collisions, the vault already uses fully-qualified names for type note files, so no additional disambiguation is needed at the type level.

**Primary recommendation:** Extend `MethodInfo` with `AccessModifier`, `Namespace`, and `ProjectName` fields. Add a `CyclomaticComplexityCalculator` static utility that walks syntax trees. Compute fan-in/fan-out at emit time from the existing `CallGraph`. Implement pattern detection as a simple name-suffix dictionary lookup. Expand the emitter to produce the full frontmatter schema with danger tags and inline callouts. Add CLI options for threshold configuration.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
User deferred all implementation decisions to Claude -- the output is best evaluated in practice and adjusted iteratively. The following defaults will guide planning:

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
- Detection by class name suffix matching (e.g., `*Repository`, `*Controller`) -- simple and predictable
- Classes matching a pattern get a `pattern/<name>` tag in frontmatter
- No pattern = no tag (don't force a classification)

**Wikilink format:**
- Method notes: `[[ClassName.MethodName]]` -- disambiguated by class prefix
- Class notes: `[[ClassName]]` -- no prefix needed at class level
- Namespace collision (two classes with same name in different namespaces): `[[Namespace.ClassName]]` fallback
- External types (outside analyzed solution): plain text, not wikilinks -- no broken links in the vault

### Claude's Discretion
All implementation decisions are Claude's discretion. Key areas to decide:
- How to compute cyclomatic complexity (syntax walk vs IOperation)
- Where metrics computation lives in the pipeline (analyzer vs enricher vs emit-time)
- Exact callout formatting for danger annotations
- How to handle class-level aggregated complexity (sum vs max vs average of method complexities)

### Deferred Ideas (OUT OF SCOPE)
None -- discussion stayed within phase scope.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| STRC-07 | Classes are tagged with detected patterns (repository, controller, service, middleware) | Pattern detection via class name suffix matching; `pattern/<name>` tag in frontmatter; six patterns: repository, controller, service, middleware, factory, handler |
| OUTP-01 | Wikilinks use `[[ClassName.MethodName]]` format to avoid name collisions | Update `ExtractMethodName()` in emitter to produce `ContainingTypeName.MethodName`; update Calls/Called-by sections; type note wikilinks already use `[[Sanitize(FullName)]]` |
| OUTP-02 | Notes include namespace and project tags in YAML frontmatter | Add `namespace` and `project` fields to all note frontmatter; requires adding `Namespace` and `ProjectName` to `MethodInfo` domain model |
| OUTP-03 | YAML frontmatter includes Dataview-compatible fields (complexity, fan_in, fan_out, pattern, access) | Expand frontmatter with snake_case fields; fan-in/fan-out from CallGraph; complexity from syntax walk; access_modifier from Roslyn DeclaredAccessibility |
| OUTP-04 | Methods with fan-in above configurable threshold are tagged `danger/high-fan-in` | Fan-in = `callGraph.GetCallers(methodId).Count`; threshold default 10; CLI flag `--fan-in-threshold`; tag in frontmatter + callout in body |
| OUTP-05 | Cyclomatic complexity is computed and included in frontmatter; high-complexity methods tagged | Syntax-tree walk counting branch nodes; threshold default 15; CLI flag `--complexity-threshold`; tag `danger/high-complexity` |
| OUTP-06 | Source file relative path included in all note frontmatter | Already present in TypeInfo.FilePath and MethodInfo.FilePath; add to method note frontmatter as `source_file` field |
</phase_requirements>

## Standard Stack

### Core (already in project -- no new dependencies)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.CodeAnalysis.CSharp.Workspaces | 4.14.0 | Roslyn syntax tree walking for complexity | Already used; CSharpSyntaxNode types for branch counting |
| System.CommandLine | 2.0.3 | CLI flags for thresholds | Already used; add `--fan-in-threshold` and `--complexity-threshold` options |
| Spectre.Console | 0.54.0 | Progress display | Already used; no changes needed |

### No new dependencies needed
Phase 3 requires zero new NuGet packages. Cyclomatic complexity is computed by walking the existing Roslyn syntax trees already available during analysis. Fan-in/fan-out are computed from the existing `CallGraph`. Pattern detection is string suffix matching. YAML frontmatter remains simple enough for StringBuilder emission. Obsidian callout syntax is plain markdown (`> [!danger]`).

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Hand-rolled syntax walk for complexity | Microsoft.CodeAnalysis.Metrics NuGet | Adds a dependency; the official package is part of roslyn-analyzers and heavy; our needs are simple (just count branches) |
| IOperation-based complexity counting | Syntax node counting | IOperation is more semantically accurate but much more complex to set up; syntax node counting is what most lightweight tools use and is sufficient for our threshold-based tagging |
| YAML serialization library (YamlDotNet) | StringBuilder | We have ~10 known scalar/list fields; a library is overkill and adds dependency weight |

## Architecture Patterns

### Recommended Changes to Existing Structure
```
Analysis/
  Models/
    MethodInfo.cs         # MODIFIED: add AccessModifier, Namespace, ProjectName
    TypeInfo.cs           # MODIFIED: add ProjectName, AccessModifier
    ...                   # unchanged
  Analyzers/
    MethodAnalyzer.cs     # MODIFIED: populate new MethodInfo fields, compute complexity
    TypeAnalyzer.cs       # MODIFIED: populate new TypeInfo fields
    AnalysisHelpers.cs    # MODIFIED: add CyclomaticComplexity() static method
    ...

Cli/
  CliOptions.cs           # MODIFIED: add FanInThreshold, ComplexityThreshold

Emission/
  ObsidianEmitter.cs      # MODIFIED: expanded frontmatter, danger tags, callouts, wikilink fix

Program.cs                # MODIFIED: wire new CLI options, pass thresholds to emitter
```

### Pattern 1: Cyclomatic Complexity via Syntax Walk
**What:** A static method that accepts a `BaseMethodDeclarationSyntax` (or its `Body`/`ExpressionBody`) and counts branching constructs by walking descendant nodes.
**When to use:** During method analysis in `MethodAnalyzer`, where the syntax node is already available.
**Why not IOperation:** The IOperation approach (used by roslyn-analyzers' `MetricsHelper.cs`) operates on the semantic IOperation tree with `OperationKind.CaseClause`, `OperationKind.Conditional`, etc. While more precise, it requires calling `model.GetOperation()` and walking a separate tree. For our use case (threshold-based tagging, not precise CA1502 enforcement), syntax node counting is sufficient and simpler.

**Decision points to count (matching roslyn-analyzers approach):**
| C# Construct | Roslyn Syntax Node Type | Notes |
|--------------|------------------------|-------|
| `if` | `IfStatementSyntax` | Each `if` adds 1 (else-if is a separate `if`) |
| `while` | `WhileStatementSyntax` | Loop = 1 decision point |
| `for` | `ForStatementSyntax` | Loop = 1 decision point |
| `foreach` | `ForEachStatementSyntax` | Loop = 1 decision point |
| `case` (switch) | `CaseSwitchLabelSyntax` | Each case label = 1 |
| `case` (pattern) | `CasePatternSwitchLabelSyntax` | Each pattern case = 1 |
| `catch` | `CatchClauseSyntax` | Exception handler = 1 |
| `&&` | `BinaryExpressionSyntax` (LogicalAnd) | Short-circuit = 1 |
| `\|\|` | `BinaryExpressionSyntax` (LogicalOr) | Short-circuit = 1 |
| `?:` | `ConditionalExpressionSyntax` | Ternary = 1 |
| `??` | `BinaryExpressionSyntax` (Coalesce) | Null coalesce = 1 |
| `?.` | `ConditionalAccessExpressionSyntax` | Null conditional = 1 |
| `do-while` | `DoStatementSyntax` | Loop = 1 decision point |
| `switch expression arm` | `SwitchExpressionArmSyntax` | Each arm (except discard) = 1 |

**Base value:** Start at 1 (every method has at least one execution path).

**Example:**
```csharp
// Source: Based on dotnet/roslyn-analyzers MetricsHelper.cs approach,
// adapted to syntax-tree walking
public static int ComputeCyclomaticComplexity(BaseMethodDeclarationSyntax declaration)
{
    int complexity = 1; // Base path

    foreach (var node in declaration.DescendantNodes())
    {
        switch (node)
        {
            case IfStatementSyntax:
            case WhileStatementSyntax:
            case ForStatementSyntax:
            case ForEachStatementSyntax:
            case DoStatementSyntax:
            case CaseSwitchLabelSyntax:
            case CasePatternSwitchLabelSyntax:
            case CatchClauseSyntax:
            case ConditionalExpressionSyntax:
            case ConditionalAccessExpressionSyntax:
                complexity++;
                break;

            case BinaryExpressionSyntax binary:
                if (binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                    binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                    binary.IsKind(SyntaxKind.CoalesceExpression))
                    complexity++;
                break;

            case SwitchExpressionArmSyntax arm:
                // Count all arms except the discard pattern (_)
                if (arm.Pattern is not DiscardPatternSyntax)
                    complexity++;
                break;
        }
    }

    return complexity;
}
```

### Pattern 2: Fan-In / Fan-Out from Existing CallGraph
**What:** Computed at emit time (or in an enricher) from the already-built CallGraph.
**When to use:** After analysis is complete, when the full CallGraph is available.
**Why at emit time:** Fan-in requires the complete graph (all methods must be analyzed). Computing during MethodAnalyzer would give partial results. The CallGraph is fully populated after all analyzers run.

**Example:**
```csharp
// In ObsidianEmitter or a MetricsEnricher
int fanOut = analysis.CallGraph.GetCallees(methodId).Count;
int fanIn = analysis.CallGraph.GetCallers(methodId).Count;
```

### Pattern 3: Pattern Detection via Name Suffix
**What:** A static dictionary mapping class name suffixes to pattern names. Check if the type name ends with any known suffix.
**When to use:** During type emission or as a post-analysis enrichment step.
**Why simple:** The context decisions specify suffix matching. No need for Roslyn attribute analysis or interface inspection.

**Example:**
```csharp
private static readonly Dictionary<string, string> PatternSuffixes = new(StringComparer.OrdinalIgnoreCase)
{
    ["Repository"] = "repository",
    ["Controller"] = "controller",
    ["Service"] = "service",
    ["Middleware"] = "middleware",
    ["Factory"] = "factory",
    ["Handler"] = "handler"
};

public static string? DetectPattern(string typeName)
{
    foreach (var (suffix, pattern) in PatternSuffixes)
    {
        if (typeName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return pattern;
    }
    return null;
}
```

### Pattern 4: Expanded YAML Frontmatter (Method Note)
**What:** Unified snake_case frontmatter schema for all notes.
**Format:**
```yaml
---
namespace: "MyNamespace.SubNamespace"
project: "MyProject"
source_file: "src/Services/MyService.cs"
access_modifier: "public"
complexity: 12
fan_in: 3
fan_out: 7
tags:
  - method
  - danger/high-complexity
---
```

### Pattern 5: Expanded YAML Frontmatter (Class Note)
**What:** Class notes extend the base schema with class-specific fields.
**Format:**
```yaml
---
namespace: "MyNamespace.SubNamespace"
project: "MyProject"
source_file: "src/Services/MyService.cs"
access_modifier: "public"
base_class: "MyNamespace.BaseService"
interfaces:
  - "MyNamespace.IService"
member_count: 8
dependency_count: 3
pattern: "service"
tags:
  - class
  - pattern/service
---
```

### Pattern 6: Danger Callout in Note Body
**What:** Obsidian callout block in the note body for danger annotations.
**Syntax:** Uses native Obsidian callout syntax (blockquote with `[!danger]` type).
**Example:**
```markdown
> [!danger] High Fan-In
> This method is called by 15 other methods. Changes here have wide impact.

> [!danger] High Complexity
> Cyclomatic complexity: 22. Consider refactoring into smaller methods.
```

### Pattern 7: Wikilink Fix for Calls/Called-by Sections
**What:** Update the `ExtractMethodName()` helper and the Calls/Called-by rendering to use `[[ContainingType.MethodName]]` format instead of bare `[[MethodName]]`.
**Why:** OUTP-01 requires `[[ClassName.MethodName]]` format for collision avoidance. The current `ExtractMethodName()` strips to just the method name, which creates ambiguity when multiple classes have methods with the same name (e.g., `Process`, `Execute`, `Handle`).
**Current behavior:** `[[MethodName]]` -- strips everything before the last dot before the opening paren.
**Target behavior:** `[[Sanitize(ContainingType.MethodName)]]` -- matches the method note file name.

**Implementation:** Instead of extracting just the method name from the MethodId string, look up the MethodInfo from `analysis.Methods` and format as `Sanitize($"{method.ContainingTypeName}.{method.Name}")`. This matches the file naming convention already used in the emitter.

### Anti-Patterns to Avoid
- **Computing fan-in/fan-out during MethodAnalyzer:** The call graph is incomplete during analysis (methods not yet visited have no edges). Compute after all analyzers complete.
- **Using IOperation tree for cyclomatic complexity:** Overkill for threshold-based tagging. Syntax walk is simpler and sufficient.
- **Omitting metrics fields when value is 0:** Use `0` explicitly so Dataview queries work without null checks (`WHERE complexity > 10` fails if some notes lack the field).
- **Using `#` prefix for tags in YAML frontmatter:** Obsidian expects bare strings in the `tags` YAML list (e.g., `- method` not `- "#method"`).
- **Creating a YAML serialization dependency:** With ~10 known fields, StringBuilder is cleaner than adding YamlDotNet.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Cyclomatic complexity | Custom control-flow graph analysis | Syntax node descendant walk counting branches | The branch-counting approach matches what roslyn-analyzers uses (via IOperation, but equivalent via syntax); building a CFG is enormously complex |
| Fan-in / fan-out | Separate graph traversal | `CallGraph.GetCallers().Count` / `CallGraph.GetCallees().Count` | Already built in Phase 1; the CallGraph maintains both forward and reverse edges |
| Pattern detection | AST-based pattern matching or attribute inspection | String suffix lookup on type name | Context decision specifies suffix matching; more complex detection is out of scope |
| YAML frontmatter | YAML library | StringBuilder with known field set | ~10 fields, all scalar or simple list; library adds dependency for no benefit |
| Access modifier string | Parsing from DisplaySignature | `AnalysisHelpers.AccessibilityToString()` during analysis | Already exists; just store the result in MethodInfo |

**Key insight:** Most of Phase 3's work is plumbing -- getting data that already exists (or is trivially computable) into the right place in the output format. The hard part is not the computation; it's updating the domain models and emitter consistently across method notes, class notes, and interface notes.

## Common Pitfalls

### Pitfall 1: Nested Lambda Complexity Inflation
**What goes wrong:** Syntax walk counts branches inside lambdas and local functions within a method, inflating the containing method's complexity score.
**Why it happens:** `DescendantNodes()` traverses into nested lambdas and local functions.
**How to avoid:** Exclude `SimpleLambdaExpressionSyntax`, `ParenthesizedLambdaExpressionSyntax`, `LocalFunctionStatementSyntax`, and `AnonymousMethodExpressionSyntax` from the walk. These have their own complexity. Use `DescendantNodes(n => !(n is ...))` to prune the traversal.
**Warning signs:** Simple methods with a lambda callback show unexpectedly high complexity.

### Pitfall 2: Switch Expression Arms Double-Counting
**What goes wrong:** Switch expressions using pattern matching generate both `SwitchExpressionArmSyntax` and potentially nested `BinaryExpressionSyntax` (for `when` clauses with `&&`/`||`), leading to double-counting.
**Why it happens:** The when clause's logical operators are descendants of the arm node.
**How to avoid:** This is actually correct behavior -- each `&&`/`||` IS an additional decision point per McCabe's definition. No special handling needed, but document this so thresholds are calibrated accordingly.
**Warning signs:** None -- this is correct. Just be aware when setting thresholds.

### Pitfall 3: Tags with Hash Prefix in YAML
**What goes wrong:** Writing `- "#method"` in YAML frontmatter tags array causes Obsidian to not recognize the tag properly.
**Why it happens:** Confusion between inline tag syntax (`#method` in body text) and YAML frontmatter tag syntax (bare `method`).
**How to avoid:** In YAML `tags:` list, use bare strings without `#` prefix: `- method`, `- danger/high-fan-in`, `- pattern/service`.
**Warning signs:** Tags don't appear in Obsidian's tag pane; Dataview tag queries fail.

### Pitfall 4: Wikilink Format Mismatch Between Notes
**What goes wrong:** Method note file is named `Namespace.Class.Method.md` but wikilinks in class notes use `[[Class.Method]]` (without namespace), creating broken links.
**Why it happens:** Inconsistent use of `ContainingTypeName` (which is fully qualified, e.g., `Code2Obsidian.Analysis.MethodAnalyzer`) versus the simple class name.
**How to avoid:** The wikilink text must exactly match the sanitized file name (minus `.md`). Since method note files use `Sanitize($"{method.ContainingTypeName}.{method.Name}.md")` and `ContainingTypeName` is fully qualified, wikilinks must also use the fully qualified form: `[[Sanitize(ContainingTypeName.MethodName)]]`. The user's decision says `[[ClassName.MethodName]]` but in practice this means the fully qualified containing type name (which IS how the file is named).
**Warning signs:** Clicking wikilinks in Obsidian creates new empty notes instead of navigating.

### Pitfall 5: Forgetting to Update CliOptions Record
**What goes wrong:** New CLI flags (`--fan-in-threshold`, `--complexity-threshold`) are added to the `RootCommand` but not passed through to the emitter.
**Why it happens:** The current `CliOptions` record only has `SolutionPath` and `OutputDirectory`. Adding new options requires updating the record, the `SetAction` handler, and the emitter's method signature.
**How to avoid:** Update `CliOptions` first, then wire through `Program.cs`, then use in emitter. Test by running `--help` to verify flags appear.
**Warning signs:** Flags parse but have no effect; default values always used.

### Pitfall 6: Inconsistent Frontmatter Between Note Types
**What goes wrong:** Method notes use some frontmatter keys, class notes use others, interface notes use yet others. Dataview queries that assume a field exists break on note types that lack it.
**Why it happens:** Different render methods evolve independently.
**How to avoid:** Define a shared base frontmatter rendering method that emits the common fields (`namespace`, `project`, `source_file`, `access_modifier`, `tags`), then add type-specific fields in the specific render methods. The context decision specifies: "Metrics that can't be computed use `0` rather than omitting."
**Warning signs:** Dataview `TABLE` queries show blanks for some notes; `WHERE` filters exclude notes unexpectedly.

### Pitfall 7: Expression-Bodied Methods Have No Body Block
**What goes wrong:** `public int Foo => 42;` has no `Body` (block statement), only `ExpressionBody`. A complexity calculator that only walks `Body` returns 0.
**Why it happens:** C# expression-bodied members use `ArrowExpressionClauseSyntax` instead of a `BlockSyntax`.
**How to avoid:** Check both `declaration.Body` and `declaration.ExpressionBody` when computing complexity. Expression-bodied methods typically have complexity 1 (just a return), but could contain ternary operators or null-coalescing.
**Warning signs:** Expression-bodied methods show complexity 0 instead of 1.

## Code Examples

Verified patterns from official sources and existing codebase:

### Cyclomatic Complexity Calculation
```csharp
// Source: Based on dotnet/roslyn-analyzers MetricsHelper.cs approach,
// adapted to syntax-tree walking for simplicity
// https://github.com/dotnet/roslyn-analyzers/blob/main/src/Utilities/Compiler/CodeMetrics/MetricsHelper.cs
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public static int ComputeCyclomaticComplexity(BaseMethodDeclarationSyntax declaration)
{
    int complexity = 1; // Every method has at least one path

    // Get the syntax nodes to walk -- handle both block body and expression body
    IEnumerable<SyntaxNode> nodes;
    if (declaration.Body is not null)
        nodes = declaration.Body.DescendantNodes(ShouldDescend);
    else if (declaration is MethodDeclarationSyntax { ExpressionBody: not null } method)
        nodes = method.ExpressionBody.DescendantNodes(ShouldDescend);
    else
        return complexity; // abstract/extern: just 1

    foreach (var node in nodes)
    {
        switch (node)
        {
            case IfStatementSyntax:
            case WhileStatementSyntax:
            case ForStatementSyntax:
            case ForEachStatementSyntax:
            case DoStatementSyntax:
            case CaseSwitchLabelSyntax:
            case CasePatternSwitchLabelSyntax:
            case CatchClauseSyntax:
            case ConditionalExpressionSyntax:
            case ConditionalAccessExpressionSyntax:
                complexity++;
                break;

            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.LogicalAndExpression)
                  || binary.IsKind(SyntaxKind.LogicalOrExpression)
                  || binary.IsKind(SyntaxKind.CoalesceExpression):
                complexity++;
                break;

            case SwitchExpressionArmSyntax arm
                when arm.Pattern is not DiscardPatternSyntax:
                complexity++;
                break;
        }
    }

    return complexity;
}

// Exclude nested lambdas and local functions from the walk
private static bool ShouldDescend(SyntaxNode node) =>
    node is not (SimpleLambdaExpressionSyntax
        or ParenthesizedLambdaExpressionSyntax
        or LocalFunctionStatementSyntax
        or AnonymousMethodExpressionSyntax);
```

### Fan-In / Fan-Out Computation
```csharp
// Source: Existing CallGraph API from Phase 1
// Already in: Analysis/Models/CallGraph.cs
int fanOut = analysis.CallGraph.GetCallees(methodId).Count;
int fanIn = analysis.CallGraph.GetCallers(methodId).Count;
```

### Pattern Detection
```csharp
// Source: Context decision -- suffix matching
private static readonly (string Suffix, string Pattern)[] PatternSuffixes =
{
    ("Repository", "repository"),
    ("Controller", "controller"),
    ("Service", "service"),
    ("Middleware", "middleware"),
    ("Factory", "factory"),
    ("Handler", "handler"),
};

public static string? DetectPattern(string typeName)
{
    foreach (var (suffix, pattern) in PatternSuffixes)
    {
        if (typeName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return pattern;
    }
    return null;
}
```

### Method Note Frontmatter (Expanded)
```csharp
// Source: Context decisions + Dataview best practices
// https://blacksmithgu.github.io/obsidian-dataview/annotation/add-metadata/
private static string RenderMethodFrontmatter(
    MethodInfo method,
    int complexity,
    int fanIn,
    int fanOut,
    List<string> tags)
{
    var sb = new StringBuilder();
    sb.AppendLine("---");
    sb.AppendLine($"namespace: \"{method.Namespace}\"");
    sb.AppendLine($"project: \"{method.ProjectName}\"");
    sb.AppendLine($"source_file: \"{method.FilePath}\"");
    sb.AppendLine($"access_modifier: \"{method.AccessModifier}\"");
    sb.AppendLine($"complexity: {complexity}");
    sb.AppendLine($"fan_in: {fanIn}");
    sb.AppendLine($"fan_out: {fanOut}");
    if (tags.Count > 0)
    {
        sb.AppendLine("tags:");
        foreach (var tag in tags)
            sb.AppendLine($"  - {tag}");
    }
    else
    {
        sb.AppendLine("tags: []");
    }
    sb.AppendLine("---");
    return sb.ToString();
}
```

### Class Note Frontmatter (Expanded)
```csharp
private static string RenderClassFrontmatter(
    TypeInfo typeInfo,
    string? pattern,
    int memberCount,
    int dependencyCount,
    List<string> tags)
{
    var sb = new StringBuilder();
    sb.AppendLine("---");
    sb.AppendLine($"namespace: \"{typeInfo.Namespace}\"");
    sb.AppendLine($"project: \"{typeInfo.ProjectName}\"");
    sb.AppendLine($"source_file: \"{typeInfo.FilePath}\"");
    sb.AppendLine($"access_modifier: \"{typeInfo.AccessModifier}\"");
    sb.AppendLine(typeInfo.BaseClassFullName is not null
        ? $"base_class: \"{typeInfo.BaseClassFullName}\""
        : "base_class: ~");
    if (typeInfo.InterfaceFullNames.Count > 0)
    {
        sb.AppendLine("interfaces:");
        foreach (var iface in typeInfo.InterfaceFullNames)
            sb.AppendLine($"  - \"{iface}\"");
    }
    else
    {
        sb.AppendLine("interfaces: []");
    }
    sb.AppendLine($"member_count: {memberCount}");
    sb.AppendLine($"dependency_count: {dependencyCount}");
    if (pattern is not null)
        sb.AppendLine($"pattern: \"{pattern}\"");
    else
        sb.AppendLine("pattern: ~");
    if (tags.Count > 0)
    {
        sb.AppendLine("tags:");
        foreach (var tag in tags)
            sb.AppendLine($"  - {tag}");
    }
    else
    {
        sb.AppendLine("tags: []");
    }
    sb.AppendLine("---");
    return sb.ToString();
}
```

### Danger Callout Rendering
```csharp
// Source: Obsidian Callouts documentation
// https://help.obsidian.md/Editing+and+formatting/Callouts
private static string RenderDangerCallouts(int fanIn, int complexity, int fanInThreshold, int complexityThreshold)
{
    var sb = new StringBuilder();

    if (fanIn >= fanInThreshold)
    {
        sb.AppendLine($"> [!danger] High Fan-In ({fanIn})");
        sb.AppendLine($"> This method is called by {fanIn} other methods. Changes here have wide impact.");
        sb.AppendLine();
    }

    if (complexity >= complexityThreshold)
    {
        sb.AppendLine($"> [!danger] High Complexity ({complexity})");
        sb.AppendLine($"> Cyclomatic complexity: {complexity}. Consider refactoring into smaller methods.");
        sb.AppendLine();
    }

    return sb.ToString();
}
```

### Wikilink Fix for Calls/Called-by
```csharp
// CURRENT (broken for OUTP-01):
// Uses ExtractMethodName() which strips to bare method name
// e.g., "MyNamespace.MyClass.Process(string)" -> "Process"

// NEW (OUTP-01 compliant):
// Look up the full MethodInfo and use ContainingTypeName.Name format
private static string FormatCallWikilink(MethodId calleeId, AnalysisResult analysis)
{
    if (analysis.Methods.TryGetValue(calleeId, out var calleeInfo))
    {
        return $"[[{Sanitize($"{calleeInfo.ContainingTypeName}.{calleeInfo.Name}")}]]";
    }
    // Fallback: extract from MethodId string (external methods not in analysis)
    return $"[[{ExtractMethodName(calleeId)}]]";
}
```

### CLI Option Wiring
```csharp
// Source: System.CommandLine 2.0.3 patterns from Phase 1
var fanInThresholdOption = new Option<int>("--fan-in-threshold")
{
    Description = "Fan-in threshold for danger tagging (default: 10)",
    DefaultValueFactory = _ => 10
};

var complexityThresholdOption = new Option<int>("--complexity-threshold")
{
    Description = "Cyclomatic complexity threshold for danger tagging (default: 15)",
    DefaultValueFactory = _ => 15
};
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| IL-based complexity (FxCop) | Syntax/IOperation-based (Roslyn analyzers) | Roslyn 1.0 (2015) | Source analysis avoids async/await IL inflation; more accurate for developer-visible code |
| CaseSwitchLabelSyntax only | CaseSwitchLabelSyntax + CasePatternSwitchLabelSyntax + SwitchExpressionArmSyntax | C# 7.0+ pattern matching, C# 8.0 switch expressions | Must handle modern C# syntax for accurate complexity |
| Obsidian inline tags only | YAML frontmatter `tags:` property (Obsidian 1.4+, Properties feature) | 2023 | Frontmatter tags are native Obsidian properties; no plugin needed |
| Dataview inline fields | YAML frontmatter fields | Obsidian Properties (2023) | Frontmatter is the standard; inline fields still work but frontmatter is preferred |

**No deprecated APIs in use.** All Roslyn syntax node types referenced (IfStatementSyntax, BinaryExpressionSyntax, etc.) have been stable since Roslyn 1.0 and remain current in 4.14.0.

## Open Questions

1. **Class-level complexity aggregation**
   - What we know: The context decision specifies `complexity` as a method-level field. Class notes have `member_count` and `dependency_count` but no complexity field.
   - What's unclear: Should class notes have an aggregated complexity score (sum/max/average of member complexities)?
   - Recommendation: Do NOT add class-level complexity. The context schema specifies `member_count` and `dependency_count` for classes, not complexity. Method-level complexity is where the signal is. If needed later, Dataview can aggregate: `TABLE sum(complexity) WHERE file.name = "MyClass"`.

2. **Namespace collision frequency in practice**
   - What we know: The context specifies `[[Namespace.ClassName]]` fallback for same-name classes in different namespaces.
   - What's unclear: How common this is in real codebases; whether to proactively detect collisions or just use fully-qualified names always.
   - Recommendation: The vault already uses `Sanitize(FullName)` (fully qualified) for type note file names and `Sanitize(ContainingTypeName.MethodName)` for method note file names. Since `FullName` and `ContainingTypeName` are already fully qualified, namespace collisions are already handled. The "fallback" is the default. No extra collision detection logic needed.

3. **Project name for methods in multi-project solutions**
   - What we know: `project.Name` is available from the Roslyn `Project` during analysis but not stored in domain models.
   - What's unclear: Whether `project.Name` or `compilation.Assembly.Name` is the better value for the `project` frontmatter field.
   - Recommendation: Use `project.Name` (the MSBuild project name), not the assembly name. Project name is what developers recognize (e.g., "MyApp.Core" not "MyApp.Core.dll"). Pass it from the analyzer to the domain model.

4. **Method note access modifier extraction**
   - What we know: `MethodInfo` currently lacks an `AccessModifier` field. The access modifier IS embedded in `DisplaySignature` but not as a separate queryable value.
   - What's unclear: Whether to parse it from `DisplaySignature` or extract it from the Roslyn symbol during analysis.
   - Recommendation: Extract from `IMethodSymbol.DeclaredAccessibility` during analysis using the existing `AnalysisHelpers.AccessibilityToString()`. Do NOT parse from the display string.

5. **Type access modifier for TypeInfo**
   - What we know: `TypeInfo` currently lacks an `AccessModifier` field. It needs one for the frontmatter `access_modifier` field.
   - What's unclear: None -- straightforward addition.
   - Recommendation: Add `AccessModifier` to `TypeInfo` record, populated from `INamedTypeSymbol.DeclaredAccessibility` during `TypeAnalyzer.ExtractTypeInfo()`.

## Sources

### Primary (HIGH confidence)
- [dotnet/roslyn-analyzers MetricsHelper.cs](https://github.com/dotnet/roslyn-analyzers/blob/main/src/Utilities/Compiler/CodeMetrics/MetricsHelper.cs) - Official cyclomatic complexity computation approach using IOperation; confirms which constructs count as decision points
- [CA1502 rule documentation](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1502) - Microsoft's cyclomatic complexity rule; default threshold 25; lists counted constructs
- [Dataview metadata documentation](https://blacksmithgu.github.io/obsidian-dataview/annotation/add-metadata/) - YAML frontmatter field naming, types, and query compatibility
- [Obsidian wikilink resolution rules](https://gist.github.com/dhpwd/9bb86c53b69cb63e09ccca42e3bf924c) - Exact filename (case-insensitive) -> normalized -> path if provided; shortest distinguishing path for disambiguation
- Existing project codebase (Phase 1 & 2) - CallGraph API, AnalysisHelpers, ObsidianEmitter structure, domain model patterns

### Secondary (MEDIUM confidence)
- [Obsidian Callouts documentation](https://help.obsidian.md/Editing+and+formatting/Callouts) - `> [!danger]` syntax for callout blocks; supported types include danger, warning, info, tip
- [Obsidian tags in YAML](https://forum.obsidian.md/t/can-we-use-the-front-matter-for-tags/8813) - Bare strings in `tags:` list (no `#` prefix); nested tags like `danger/high-fan-in` work in YAML
- [System.CommandLine documentation](https://learn.microsoft.com/en-us/dotnet/standard/commandline/) - Option definition with DefaultValueFactory for threshold flags

### Tertiary (LOW confidence)
- SwitchExpressionArmSyntax complexity counting: Included based on logical consistency with CaseSwitchLabelSyntax, but not explicitly listed in CA1502 docs (which predate C# 8.0 switch expressions). The roslyn-analyzers IOperation approach handles this via `CaseClause`, which maps to switch expression arms.
- ConditionalAccessExpressionSyntax (`?.`) as a decision point: Included in roslyn-analyzers' `hasConditionalLogic` function. Some complexity metrics exclude this. Our inclusion matches Microsoft's approach.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - No new dependencies; all APIs already available
- Cyclomatic complexity: HIGH - Approach verified against roslyn-analyzers source; syntax node types confirmed
- Fan-in/fan-out: HIGH - Trivially computable from existing CallGraph
- Pattern detection: HIGH - Simple string suffix matching; no complex logic
- YAML/frontmatter format: HIGH - Dataview docs confirm snake_case fields, list format, numeric types
- Wikilink format: HIGH - Current codebase already uses Sanitize(FullName) pattern; just needs consistency fix
- Danger callout syntax: MEDIUM - Obsidian callout syntax confirmed but exact rendering tested informally
- Nested lambda exclusion from complexity: MEDIUM - Logical but not explicitly called out in CA1502 docs; roslyn-analyzers handles this through IOperation scoping

**Research date:** 2026-02-25
**Valid until:** 2026-03-25 (all APIs stable; Roslyn syntax nodes unchanged since C# 12)
