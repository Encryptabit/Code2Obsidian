# Codebase Structure

**Analysis Date:** 2026-02-25

## Directory Layout

```
C:\Projects\Code2Obsidian/
├── Program.cs              # Main application code (single-file design)
├── Code2Obsidian.csproj    # Project configuration
├── .gitignore              # Git exclusions
├── bin/                    # Build outputs (Debug/Release)
├── obj/                    # Build intermediates
├── .idea/                  # JetBrains IDE settings
├── .planning/              # Documentation directory (for GSD)
│   └── codebase/           # Codebase analysis documents
└── .git/                   # Version control
```

## Directory Purposes

**Project Root (`C:\Projects\Code2Obsidian/`):**
- Purpose: Contains entire CLI application and configuration
- Contains: Source code, project file, build artifacts, documentation
- Key files: `Program.cs`, `Code2Obsidian.csproj`

**`bin/` Directory:**
- Purpose: Compiled executables and dependencies
- Contains: Debug and Release binaries, runtime configuration files
- Generated: Yes (by dotnet build)
- Committed: No (in .gitignore)

**`obj/` Directory:**
- Purpose: Intermediate build artifacts and metadata
- Contains: Generated code, assembly info, source link data, NuGet assets
- Generated: Yes (by dotnet compiler)
- Committed: No (in .gitignore)

**`.planning/codebase/` Directory:**
- Purpose: GSD codebase analysis documents
- Contains: ARCHITECTURE.md, STRUCTURE.md, CONVENTIONS.md, TESTING.md, CONCERNS.md
- Generated: No (manually authored by analysis tools)
- Committed: Yes (for future reference)

## Key File Locations

**Entry Points:**
- `C:\Projects\Code2Obsidian\Program.cs`: Application entry point (line 30, async Task<int> Main)

**Configuration:**
- `C:\Projects\Code2Obsidian\Code2Obsidian.csproj`: Project manifest (SDK, target framework, dependencies)

**Core Logic:**
- `C:\Projects\Code2Obsidian\Program.cs`: Monolithic implementation
  - CLI parsing (lines 281-359)
  - MSBuild registration (lines 243-277)
  - Analysis pipeline (lines 62-117)
  - Markdown emission (lines 119-165)
  - Helper functions (lines 363-492)

**Testing:**
- Not applicable (no test project in solution)

## Naming Conventions

**Files:**
- PascalCase for program entry: `Program.cs`
- Project name matches namespace: Code2Obsidian

**Directories:**
- Lowercase with leading dot for hidden/config: `.git`, `.idea`, `.planning`
- Lowercase for build: `bin`, `obj`

**Types:**
- Sealed records: `Options`, `MethodInfo` (immutable argument/data containers)
- Internal static class: `Program` (single responsibility: orchestration)

**Functions/Methods:**
- PascalCase: `Main`, `TryParseArgs`, `EnsureMsbuildRegistered`, `GetProjectAssemblies`, etc.
- Verb prefixes for operations: `Try*` (parse), `Get*` (retrieve), `Render*` (generate), `Ensure*` (validate)
- Private scope with static modifier (no instance state)

**Variables:**
- camelCase local variables: `opt`, `slnPath`, `outDir`, `methods`, `callsOut`, `callsIn`
- LINQ shorthand: `p` (project), `m` (method), `d` (document), `inv` (invocation), `g` (group)

**Records:**
- Immutable with positional constructor syntax
- Example: `private sealed record Options(string SolutionPath, string OutDir, bool PerFile, bool PerMethod);`

## Where to Add New Code

**New Feature (Code Analysis Enhancement):**
- Primary code: Add methods to `Program.cs` static class
- Analysis layer: Insert logic in the main analysis loop (lines 68-111)
- Emission layer: Add rendering method and call from RenderMethodSection or RenderMethodNote
- Example: To add parameter usage tracking, add new dictionary like `parameterUsages` and populate during invocation analysis

**New Helper Function:**
- Shared utility: Add to end of file (line 363+), before closing brace
- Pattern: `private static [return type] FunctionName([params]) { ... }`
- Example: To extract all type references, create `private static IEnumerable<ITypeSymbol> GetReferencedTypes(IMethodSymbol method)`

**New Output Format:**
- Markdown variant: Add new rendering method alongside `RenderMethodSection` and `RenderMethodNote`
- Emission logic: Add conditional branch in main (line 153+) after per-method block
- Example: For YAML output, add `if (opt.PerYaml) { ... emit YAML ... }`

**Configuration/Options:**
- CLI argument: Add to Options record (line 17-22)
- Parsing logic: Add case in `TryParseArgs` switch statement (line 291-324)
- Validation: Update mutual exclusivity check (line 334-339)

## Special Directories

**`_obsidian/` Directory:**
- Purpose: Default output directory for generated markdown files
- Generated: Yes (created by application on first run)
- Committed: No (in .gitignore line 15)
- Pattern: `_obsidian/[FileName.md]` for per-file mode, `_obsidian/[ClassName.MethodName.md]` for per-method mode

## Immutable Design Patterns

**Options Record (lines 17-22):**
```csharp
private sealed record Options(
    string SolutionPath,
    string OutDir,
    bool PerFile,
    bool PerMethod
);
```
- Used: Passed through pipeline from CLI parser to main logic
- Benefit: No side effects, thread-safe, pattern-matchable

**MethodInfo Record (lines 24-28):**
```csharp
private sealed record MethodInfo(
    string FilePath,
    Microsoft.CodeAnalysis.Text.TextSpan Span,
    IMethodSymbol Symbol
);
```
- Used: Stored alongside method symbols in analysis phase
- Benefit: Immutable metadata without separate storage structures

## Build and Dependencies

**Project File (`Code2Obsidian.csproj`):**
- SDK: Microsoft.NET.Sdk
- Target Framework: net8.0
- Language Features: ImplicitUsings enabled, Nullable enabled
- Dependencies:
  - Microsoft.Build.Locator 1.9.1 (MSBuild discovery)
  - Microsoft.CodeAnalysis.CSharp.Workspaces 4.14.0 (C# syntax/semantic analysis)
  - Microsoft.CodeAnalysis.Workspaces.MSBuild 4.14.0 (Roslyn MSBuild integration)

**Global Usings:**
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using System.Xml.Linq;
```

---

*Structure analysis: 2026-02-25*
