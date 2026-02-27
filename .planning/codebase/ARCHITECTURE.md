# Architecture

**Analysis Date:** 2026-02-25

## Pattern Overview

**Overall:** Monolithic CLI application with sequential analysis pipeline

**Key Characteristics:**
- Single-file, procedural architecture (all logic in `Program.cs`)
- Three-stage pipeline: Parse → Analyze → Emit
- Roslyn-based static code analysis
- Call graph construction from syntax analysis
- YAML-prefixed markdown generation

## Layers

**CLI Layer:**
- Purpose: Parse command-line arguments and validate user input
- Location: `Program.cs` (lines 281-359)
- Contains: Argument parsing, usage help, option validation
- Depends on: None (built-in .NET libraries)
- Used by: Main entry point

**MSBuild/Roslyn Layer:**
- Purpose: Load solution, compile projects, and access semantic symbols
- Location: `Program.cs` (lines 40-61, 243-277)
- Contains: MSBuild registration, workspace creation, project compilation
- Depends on: Microsoft.CodeAnalysis.* and Microsoft.Build.Locator
- Used by: Analysis layer

**Analysis Layer:**
- Purpose: Extract methods, build call graphs, filter user code
- Location: `Program.cs` (lines 62-117)
- Contains: Method symbol collection, invocation tracking, graph reversal
- Depends on: Roslyn layer, filtering helpers
- Used by: Emission layer

**Emission Layer:**
- Purpose: Generate Obsidian-compatible markdown with frontmatter
- Location: `Program.cs` (lines 119-165, 170-239)
- Contains: Per-file and per-method markdown renderers, metadata extraction
- Depends on: Analysis layer, rendering helpers
- Used by: Main entry point

**Helper Layer:**
- Purpose: Utility functions for filtering, rendering, and data manipulation
- Location: `Program.cs` (lines 363-492)
- Contains: Symbol validation, graph extension methods, sanitization, docstring extraction
- Depends on: Roslyn symbols, LINQ
- Used by: Analysis and Emission layers

## Data Flow

**Main Analysis Pipeline:**

1. **Input**: User provides `.sln` path + output directory + mode (--per-file or --per-method)
2. **CLI Parse**: `TryParseArgs()` validates arguments, returns Options record
3. **MSBuild Setup**: `EnsureMsbuildRegistered()` locates and registers MSBuild
4. **Solution Load**: `MSBuildWorkspace.Create()` opens solution and loads all projects
5. **Assembly Collection**: `GetProjectAssemblies()` extracts compiled assemblies for filtering
6. **Method Extraction**: For each project/document:
   - Parse syntax tree → find all `BaseMethodDeclarationSyntax` nodes
   - Resolve semantic symbols via model
   - Filter via `IsUserMethod()` (only user code, not generated)
   - Store in `methods` dictionary keyed by symbol
7. **Invocation Tracking**: For each method body:
   - Find all `InvocationExpressionSyntax` nodes
   - Resolve target method symbol
   - Normalize symbols (reduce extension methods to canonical form)
   - Add edge to `callsOut` if target is user code
8. **Graph Reversal**: Build `callsIn` by inverting `callsOut` edges
9. **Markdown Emission**:
   - If --per-file: Group methods by source file, emit one `.md` per file
   - If --per-method: Emit one `.md` per method
10. **Output**: Write to disk, log summary

**State Management:**
- Immutable records for Options, MethodInfo
- Dictionaries with symbol equality comparison for graph edges
- StringBuilder for incremental markdown construction
- No mutable shared state across methods

## Key Abstractions

**Options Record:**
- Purpose: Encapsulate parsed CLI arguments
- Located at: `Program.cs` (lines 17-22)
- Pattern: Sealed record with immutable properties
- Fields: SolutionPath, OutDir, PerFile, PerMethod

**MethodInfo Record:**
- Purpose: Store location and symbol metadata for a method
- Located at: `Program.cs` (lines 24-28)
- Pattern: Sealed record with source path, span, and symbol reference
- Used by: Emission layer to track file origins

**Call Graph:**
- Purpose: Directed graph of method invocations
- Located at: `Program.cs` (lines 65-117)
- Pattern: Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>
- Implementation: `callsOut` (outgoing edges), `callsIn` (incoming edges)
- Keyed by: SymbolEqualityComparer.Default (identity-based)

**Symbol Filtering:**
- Purpose: Distinguish user code from library/generated code
- Located at: `Program.cs` (lines 397-417)
- Pattern: Multi-condition validator checking assembly, source, implicit declarations, and method kind
- Used by: Analysis layer to exclude metadata-only, generated, and operator symbols

## Entry Points

**Main Async Method:**
- Location: `Program.cs` (line 30)
- Triggers: CLI invocation: `Code2Obsidian.exe <solution.sln> --per-file|--per-method [--out dir]`
- Responsibilities: Orchestrate entire pipeline, return exit code

**Workflow Stages:**
1. Parse arguments and handle errors (returns 2 on invalid input)
2. Register MSBuild (throws if not found)
3. Resolve file paths and create output directory
4. Load solution via Roslyn
5. Perform analysis
6. Generate and write markdown
7. Print summary to console
8. Return 0 on success

## Error Handling

**Strategy:** Early validation with error messages to stderr, exceptions for environment issues

**Patterns:**

- **CLI Errors** (lines 33-38): Invalid arguments logged to `Console.Error`, usage printed, return code 2
- **File System Errors** (lines 45-49): Missing solution file detected, error to stderr, return code 2
- **MSBuild Registration** (lines 243-277): Fallback to dotnet SDK if VS not found; throws `InvalidOperationException` if both fail
- **Roslyn Parse Errors** (lines 70-81): Gracefully skip documents/projects where semantic model fails
- **Symbol Resolution** (lines 86-109): Null checks after symbol resolution; unresolvable invocations skipped
- **XML Documentation** (lines 420-471): Try-catch wrapper around docstring parsing; falls back to raw XML on parse failure

## Cross-Cutting Concerns

**Logging:** Console output only (no file logging)
- Progress: Solution/output paths printed to stdout before analysis
- Summary: File/method counts printed to stdout after emission
- Errors: All errors to `Console.Error`

**Validation:** Multi-layered
- CLI: Argument type checking, mutual exclusivity (exactly one of --per-file or --per-method)
- File paths: Resolved to absolute paths, existence checked
- Symbols: Assembly membership, source location, implicit declarations, method kind
- Invocations: Symbol resolution with fallback to candidate symbols

**Symbol Canonicalization:** `ReducedFrom ?? OriginalDefinition ?? target` (lines 102)
- Normalizes extension method reduced forms and definitions to canonical symbols
- Ensures call graph edges are deduplicated and consistent

---

*Architecture analysis: 2026-02-25*
