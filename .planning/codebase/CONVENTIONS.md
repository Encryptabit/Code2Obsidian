# Coding Conventions

**Analysis Date:** 2026-02-25

## Naming Patterns

**Files:**
- PascalCase for main program file: `Program.cs`
- Matches namespace structure for .NET conventions

**Functions/Methods:**
- PascalCase for method names: `TryParseArgs()`, `EnsureMsbuildRegistered()`, `RenderMethodSection()`
- Private methods use `private static` modifiers and PascalCase
- Helper methods follow consistent naming: verb-noun pattern (`GetOrCreate`, `IsUserMethod`, `GetMethodDocstring`)
- Generic method parameters use PascalCase: `GetOrCreate<TKey, TValue>()`

**Variables:**
- camelCase for local variables: `slnPath`, `outDir`, `perFile`, `methodCount`
- camelCase for method parameters: `args`, `opt`, `parseError`, `callsOut`, `callsIn`
- camelCase for private fields and local state
- UPPERCASE with underscores for constants (implicit - not found in codebase)

**Types:**
- PascalCase for records: `Options`, `MethodInfo`
- sealed record keyword used for immutable data types
- Interface patterns: `IMethodSymbol`, `IAssemblySymbol` (from Microsoft.CodeAnalysis library)
- Type parameters in generics: `TKey`, `TValue`, `TItem`

## Code Style

**Formatting:**
- Uses explicit line breaks and indentation (4 spaces - standard .NET)
- Brace style: Allman style (opening brace on new line for methods, control structures)
- No explicit formatting tool (no .editorconfig or .prettierrc found)

**Linting:**
- No explicit linter configuration found
- Relies on IDE defaults (IntelliJ IDEA detected via .idea folder)
- Target framework: .NET 8.0
- Compiler options enabled:
  - `ImplicitUsings`: enabled (simplifies imports)
  - `Nullable`: enabled (strict null checking)

## Import Organization

**Order:**
1. System namespace imports first: `using System;`
2. System.Collections and System.IO: `using System.Collections.Generic;`, `using System.IO;`
3. System.Text and System.Xml: `using System.Text;`, `using System.Xml.Linq;`
4. LINQ: `using System.Linq;`
5. Third-party/external namespaces: `using Microsoft.Build.Locator;`, `using Microsoft.CodeAnalysis;`
6. Project-specific: `namespace Code2Obsidian`

**Path Aliases:**
- No path aliases detected
- Direct use of fully qualified names: `Microsoft.CodeAnalysis.Text.TextSpan`, `Microsoft.CodeAnalysis.CSharp.Syntax`

## Error Handling

**Patterns:**
- Defensive programming with null checks: `if (symbol is null) continue;`
- Pattern matching for type checking: `var target = (info.Symbol as IMethodSymbol) ?? (info.CandidateSymbols.FirstOrDefault() as IMethodSymbol);`
- Try/catch used sparingly in `GetMethodDocstring()` for XML parsing resilience (lines 422-471)
- Fallback behavior: catches exceptions and returns alternative values
- Explicit null coalescing: `docstring ?? fallback`
- Early returns for validation: `if (!TryParseArgs(...)) return 2;`

**Patterns by context:**
- MSBuild registration: throws `InvalidOperationException` for critical failures (lines 265-274)
- CLI parsing: returns boolean with output parameter for error details
- File operations: no explicit error handling (relies on caller handling)
- Roslyn compilation: null checks after async operations: `if (compilation is null) continue;`

## Logging

**Framework:** `Console` class

**Patterns:**
- `Console.WriteLine()` for standard output
- `Console.Error.WriteLine()` for error messages (lines 35, 47)
- Informational messages printed to track progress (lines 54-56, 151, 164)
- No structured logging framework used

## Comments

**When to Comment:**
- Section separators with dashes: `// ------ Rendering helpers ------` (line 170)
- Explanatory comments for complex logic: `// Normalize reduced/extension form to a canonical symbol` (line 101)
- Purpose comments for logic groups: `// Only keep edges to our own code` (line 104)
- Inline comments for non-obvious decisions: `// skip non-source docs` (line 75)
- Comments clarify intent, not repeat code

**JSDoc/TSDoc:**
- Not applicable to C# (uses XML documentation comments)
- XML documentation comments present in extracted methods: `GetMethodDocstring()` parses `<summary>`, `<param>`, `<returns>`, `<remarks>` (lines 424-461)
- Output markdown preserves docstring information formatted as readable text

## Function Design

**Size:**
- Functions range from 2 lines (accessors) to ~50 lines (main method, RenderMethodSection)
- Most helper functions: 5-20 lines
- Longer functions: decomposed into logical sections with comment headers

**Parameters:**
- Methods use explicit parameters rather than collection passing
- Generic constraints used when appropriate: `where TKey : notnull`, `where TValue : class`
- Dictionaries and HashSets passed for graph operations (callsOut, callsIn)
- No out parameters except in TryParse pattern: `TryParseArgs(args, out var opt, out var parseError)`

**Return Values:**
- Boolean return for validation methods: `TryParseArgs()`, `IsUserMethod()`
- Nullable strings for optional data: `GetMethodDocstring()` returns `string?`
- Tuples used in LINQ groups: `group.OrderBy(g => g.Key.Name)`
- void for console output methods
- string for markdown rendering: `RenderMethodSection()`, `RenderMethodNote()`

## Module Design

**Exports:**
- Internal sealed static class `Program` (line 15) - single entry point pattern
- No public classes (all contained within sealed static class)
- All methods are `private static`
- Records are `private sealed` (immutable and encapsulated)

**Barrel Files:**
- Not applicable - single file codebase

---

*Convention analysis: 2026-02-25*
