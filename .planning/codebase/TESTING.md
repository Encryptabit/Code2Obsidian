# Testing Patterns

**Analysis Date:** 2026-02-25

## Test Framework

**Runner:**
- Not detected - no test framework configured

**Assertion Library:**
- Not applicable - no unit testing infrastructure present

**Run Commands:**
- No test commands available
- Project is CLI tool with no automated test suite

## Test File Organization

**Location:**
- Not applicable - no test files found

**Naming:**
- No test files detected in project

**Structure:**
- No test file structure observed

## Test Coverage

**Requirements:**
- Not enforced - no test coverage tools configured

**Current Status:**
- Zero test coverage - codebase contains no unit tests, integration tests, or test fixtures

## Test Approach

**Manual Testing:**
- Tool is CLI-based and tested via command-line execution
- Input: C# solution files (.sln)
- Output: markdown files generated in output directory
- Expected invocation patterns:
  ```bash
  Code2Obsidian <solution.sln> --per-file [--out <folder>]
  Code2Obsidian <solution.sln> --per-method [--out <folder>]
  ```

**Testing Surfaces:**

**1. CLI Argument Parsing:**
- File: `C:\Projects\Code2Obsidian\Program.cs` (lines 281-344)
- Method: `TryParseArgs()`
- Untested scenarios:
  - Valid solution path with --per-file flag
  - Valid solution path with --per-method flag
  - Missing solution path (should error)
  - Both --per-file and --per-method specified (should error)
  - --out flag with valid directory
  - --out flag without value (should error)
  - Invalid flags (e.g., --invalid)
  - Help flag variations (-h, --help, /?)

**2. MSBuild Registration:**
- File: `C:\Projects\Code2Obsidian\Program.cs` (lines 243-277)
- Method: `EnsureMsbuildRegistered()`
- Untested scenarios:
  - VS installation detection and registration
  - Fallback to dotnet SDK when VS not found
  - Missing dotnet SDK (throws InvalidOperationException)
  - Multiple SDK versions (should pick newest)
  - Environment variable overrides (DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR, DOTNET_ROOT)

**3. Roslyn Symbol Analysis:**
- File: `C:\Projects\Code2Obsidian\Program.cs` (lines 58-111)
- Untested scenarios:
  - Method symbol collection from compilation
  - Invocation expression resolution
  - Call graph construction (callsOut/callsIn dictionaries)
  - Filtering of user methods vs. library methods
  - Handling of property accessors and operators (should skip)
  - Implicit method filtering
  - Extension method normalization (ReducedFrom handling)

**4. Method Filtering:**
- File: `C:\Projects\Code2Obsidian\Program.cs` (lines 397-417)
- Method: `IsUserMethod()`
- Untested scenarios:
  - Methods from project assemblies (should include)
  - Methods from external libraries (should exclude)
  - Generated/implicit methods (should exclude)
  - Property accessors: PropertyGet, PropertySet (should exclude)
  - Event methods: EventAdd, EventRemove (should exclude)
  - Operators: BuiltinOperator, UserDefinedOperator (should exclude)
  - Methods without source location (metadata-only)

**5. XML Documentation Extraction:**
- File: `C:\Projects\Code2Obsidian\Program.cs` (lines 420-492)
- Methods: `GetMethodDocstring()`, `NormalizeSpaces()`
- Untested scenarios:
  - Methods with summary documentation (should extract)
  - Methods with parameter documentation (should format as list)
  - Methods with return documentation
  - Methods with remarks
  - Methods without documentation (should return null/default)
  - Malformed XML documentation (caught by try/catch, line 466)
  - Whitespace normalization in docstrings
  - Multi-line documentation handling

**6. Markdown Rendering:**
- File: `C:\Projects\Code2Obsidian\Program.cs` (lines 172-239)
- Methods: `RenderMethodSection()`, `RenderMethodNote()`
- Untested scenarios:
  - Per-file mode: all methods grouped by source file
  - Per-method mode: individual markdown per method
  - YAML frontmatter with tags (file vs. method)
  - Call graph rendering (Calls → section)
  - Called-by rendering (Called-by ← section)
  - Method signatures in codeblock
  - File path inclusion in per-method notes
  - TODOs for missing documentation

**7. File I/O:**
- File: `C:\Projects\Code2Obsidian\Program.cs` (lines 44-52, 147, 160)
- Untested scenarios:
  - Solution file existence validation (line 45)
  - Output directory creation (line 52)
  - Markdown file writing (lines 147, 160)
  - File path sanitization for invalid characters (lines 363-368)
  - File name collisions in per-method mode

## Error Handling in Code

**Strategy:**
- Defensive validation with early returns
- Try/catch for external library interactions (Roslyn, XML parsing)
- Graceful fallback behavior rather than crashing

**Key Error Paths:**

1. **CLI Validation (lines 33-38):**
   ```csharp
   if (!TryParseArgs(args, out var opt, out var parseError))
   {
       Console.Error.WriteLine(parseError);
       PrintUsage();
       return 2;  // Exit code for CLI error
   }
   ```

2. **File Validation (lines 45-49):**
   ```csharp
   if (!File.Exists(slnPath))
   {
       Console.Error.WriteLine($"Solution not found: {slnPath}");
       return 2;
   }
   ```

3. **XML Parsing Resilience (lines 422-471):**
   ```csharp
   try
   {
       var xml = method.GetDocumentationCommentXml(...);
       // parse and format XML
   }
   catch
   {
       var fallback = method.GetDocumentationCommentXml(expandIncludes: false, ...);
       return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
   }
   ```

4. **MSBuild Registration (lines 247-276):**
   - First tries Visual Studio detection
   - Falls back to dotnet SDK
   - Throws `InvalidOperationException` if neither available

## Fragile/Untested Areas

**High Risk - No Test Coverage:**
- Roslyn API interactions (symbol collection, call graph building)
- MSBuild workspace initialization and solution loading
- XML documentation comment extraction and parsing
- File system I/O and path handling
- Multi-project solution analysis

**Mock Recommendations (if unit tests were added):**
- Mock `MSBuildWorkspace` for compilation loading
- Mock `IMethodSymbol` for symbol analysis tests
- Mock file system for I/O tests
- Mock XML documentation for docstring tests

---

*Testing analysis: 2026-02-25*
