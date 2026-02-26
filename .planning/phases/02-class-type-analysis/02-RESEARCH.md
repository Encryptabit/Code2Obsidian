# Phase 2: Class & Type Analysis - Research

**Researched:** 2026-02-25
**Domain:** Roslyn semantic analysis (INamedTypeSymbol), Obsidian markdown generation, YAML frontmatter
**Confidence:** HIGH

## Summary

Phase 2 extends the existing pipeline (IAnalyzer -> IEnricher -> IEmitter) to extract class-level and interface-level information from Roslyn compilations, then emit new "hub" Obsidian notes alongside existing per-method notes. The core technical challenge is building a new `TypeAnalyzer` (implementing `IAnalyzer`) that collects type-level data -- inheritance, interface implementation, properties, fields, constructors, and DI dependencies -- and a new domain model (`TypeInfo`) to carry this data from Roslyn-land into the emitter without symbol leakage.

The Roslyn APIs for this are well-documented and stable. `INamedTypeSymbol` exposes `BaseType`, `Interfaces`/`AllInterfaces`, `InstanceConstructors`, `GetMembers()`, and `TypeKind` -- all needed for the seven STRC requirements. The primary design decisions are: (1) whether TypeAnalyzer is a separate IAnalyzer or merged into MethodAnalyzer, (2) how to guarantee declaration-order member listing, and (3) how AnalysisResult carries type data alongside existing method data.

**Primary recommendation:** Add a new `TypeAnalyzer : IAnalyzer` as a separate pipeline stage, introduce `TypeInfo` and `PropertyFieldInfo` domain models, extend `AnalysisResult`/`AnalysisResultBuilder` with a type dictionary, and update `ObsidianEmitter` to emit class/interface notes using `[[ClassName]]` and `[[ClassName.MethodName]]` wikilinks.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- Class notes are NEW hub pages -- they link to per-method notes, not replace them
- Both class notes and method notes exist in the vault simultaneously
- Class notes create hub nodes in Graph View: class -> methods, class -> base class, class -> interfaces
- Interface notes include a "Known Implementors" section with wikilinks to all implementing classes
- New class/interface notes use correct `[[ClassName.MethodName]]` format for member links
- New class/interface notes use `[[ClassName]]` format for type relationship links (base class, interfaces)
- Existing method note link rewrites (Calls/Called-by sections) are Phase 3 scope (OUTP-01)
- Per-method notes contain complete signatures: access modifier, return type, method name, and all parameter names with types
- Class note member index is a compact navigation listing (wikilinks to method notes)
- Inheritance and interface information goes in YAML frontmatter
- Phase 2 frontmatter is minimal: base_class, interfaces, namespace, source_file fields only
- Phase 3 expands frontmatter with metrics, tags, and Dataview-compatible fields
- Base class and implemented interfaces stored as type names that match note file names
- Members listed in declaration order (same order as source file)
- No access modifier prefixes in the compact member index
- Full signatures with access modifiers appear in individual method notes (STRC-08)
- Interface "Known Implementors" section is body content, not just frontmatter

### Claude's Discretion
- DI dependency presentation: whether constructor-injected types get a dedicated section or just appear in the constructor entry
- Member index density: formatting choice only (wikilink + return type vs wikilink + full params)
- Exact YAML frontmatter field names and structure
- How to handle generic type parameters in wikilinks

### Deferred Ideas (OUT OF SCOPE)
None -- discussion stayed within phase scope
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| STRC-01 | Tool generates one note per class with purpose summary, member index, and source path | TypeAnalyzer collects all INamedTypeSymbol with TypeKind.Class; TypeInfo domain model carries data; ObsidianEmitter generates class notes |
| STRC-02 | Class notes show inheritance chain (base classes) as wikilinks | ITypeSymbol.BaseType provides direct base; walk chain for full hierarchy; store as type name string matching note file name |
| STRC-03 | Class notes show implemented interfaces as wikilinks | ITypeSymbol.Interfaces (directly declared) provides interface list; convert to type name strings |
| STRC-04 | Interface notes link to all known implementors | Build reverse index: for each class, record which interfaces it implements; interface TypeInfo gets list of implementing class TypeIds |
| STRC-05 | Class notes extract and list properties and fields with types | GetMembers().OfType<IPropertySymbol>() and OfType<IFieldSymbol>(); filter out compiler-generated backing fields |
| STRC-06 | Constructor parameters extracted and linked as DI dependencies | INamedTypeSymbol.InstanceConstructors -> IMethodSymbol.Parameters; parameter types become wikilinks to type notes |
| STRC-08 | Rich method signatures include return type, parameters with types, and access modifiers | Custom SymbolDisplayFormat with IncludeAccessibility, IncludeType, IncludeModifiers, and full parameter options; stored in MethodInfo.DisplaySignature |
</phase_requirements>

## Standard Stack

### Core (already in project)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.CodeAnalysis.CSharp.Workspaces | 4.14.0 | Roslyn semantic analysis APIs | Already used in Phase 1; provides INamedTypeSymbol, IPropertySymbol, IFieldSymbol |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 4.14.0 | Solution/project loading | Already used in Phase 1; SolutionLoader |
| Microsoft.Build.Locator | 1.9.1 | MSBuild SDK discovery | Already used in Phase 1 |
| System.CommandLine | 2.0.3 | CLI argument parsing | Already used in Phase 1; no changes needed |
| Spectre.Console | 0.54.0 | Progress display | Already used in Phase 1; no changes needed |

### No new dependencies needed
Phase 2 requires zero new NuGet packages. All Roslyn APIs for type analysis (INamedTypeSymbol, IPropertySymbol, IFieldSymbol, IMethodSymbol for constructors) are already available through the existing Microsoft.CodeAnalysis.CSharp.Workspaces 4.14.0 dependency. YAML frontmatter is simple enough to emit with string concatenation (no YAML library needed).

## Architecture Patterns

### Recommended Project Structure Additions
```
Analysis/
├── Models/
│   ├── TypeInfo.cs           # NEW: domain model for class/interface data
│   ├── PropertyFieldInfo.cs  # NEW: domain model for properties and fields
│   ├── ConstructorInfo.cs    # NEW: domain model for constructor + DI params
│   ├── ParameterInfo.cs      # NEW: domain model for method/constructor parameters
│   ├── MethodInfo.cs         # MODIFIED: add rich signature fields
│   ├── MethodId.cs           # unchanged
│   ├── TypeId.cs             # unchanged
│   └── CallGraph.cs          # unchanged
├── Analyzers/
│   ├── MethodAnalyzer.cs     # MODIFIED: update DisplaySignature format, populate new MethodInfo fields
│   ├── TypeAnalyzer.cs       # NEW: extracts class/interface type info
│   └── AnalysisHelpers.cs    # MODIFIED: add IsUserType() helper
├── AnalysisResult.cs          # MODIFIED: add Types dictionary
├── AnalysisResultBuilder.cs   # MODIFIED: add AddType(), type collection
└── IAnalyzer.cs               # unchanged

Emission/
├── ObsidianEmitter.cs         # MODIFIED: emit class notes + interface notes + updated method notes
├── IEmitter.cs                # unchanged
└── EmitResult.cs              # unchanged
```

### Pattern 1: TypeAnalyzer as Separate IAnalyzer
**What:** A new `TypeAnalyzer : IAnalyzer` that runs as a second analyzer in the pipeline, after `MethodAnalyzer`. It iterates over all type declarations per document, extracting class and interface metadata.
**When to use:** Always -- this is the recommended approach.
**Why separate from MethodAnalyzer:** TypeAnalyzer needs to iterate over `TypeDeclarationSyntax` / `InterfaceDeclarationSyntax` nodes, not `BaseMethodDeclarationSyntax`. The iteration granularity and extracted data are fundamentally different. Merging would create a God-class.
**Pipeline ordering:** MethodAnalyzer runs first (populating method data), TypeAnalyzer runs second (can reference already-discovered methods by their MethodIds).
**Example:**
```csharp
// Source: Roslyn official docs + project pattern from MethodAnalyzer.cs
public sealed class TypeAnalyzer : IAnalyzer
{
    public string Name => "TypeAnalyzer";

    public async Task AnalyzeAsync(
        AnalysisContext context,
        AnalysisResultBuilder builder,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        var csharpProjects = context.Solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .ToList();

        foreach (var project in csharpProjects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(document.FilePath)) continue;

                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;
                var model = await document.GetSemanticModelAsync(ct);
                if (model is null) continue;

                var root = await tree.GetRootAsync(ct);

                // Find all type declarations (classes, interfaces, records, structs)
                foreach (var typeDecl in root.DescendantNodes()
                    .OfType<TypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (symbol is null) continue;
                    if (!AnalysisHelpers.IsUserType(symbol, context.ProjectAssemblyNames))
                        continue;

                    var typeInfo = ExtractTypeInfo(symbol, document.FilePath!, model);
                    builder.AddType(typeInfo);
                }
            }
        }
    }
}
```

### Pattern 2: TypeInfo Domain Model (No Roslyn Symbol Leakage)
**What:** A pure domain record that carries all extracted type data using strings and TypeId/MethodId references. No Microsoft.CodeAnalysis types.
**Why:** Maintains the Roslyn boundary established in Phase 1 (only `MethodId.FromSymbol()` and `TypeId.FromSymbol()` touch Roslyn symbols).
**Example:**
```csharp
// Domain model -- NO Microsoft.CodeAnalysis references
public sealed record TypeInfo(
    TypeId Id,
    string Name,                           // Simple name: "MyClass"
    string FullName,                       // Fully qualified: "MyNamespace.MyClass"
    string Namespace,                      // "MyNamespace"
    TypeKindInfo Kind,                     // Class, Interface, Record, Struct
    string FilePath,                       // Source file path
    string? BaseClassName,                 // Name matching note file name, null if System.Object or none
    IReadOnlyList<string> InterfaceNames,  // Names matching note file names
    IReadOnlyList<PropertyFieldInfo> Properties,
    IReadOnlyList<PropertyFieldInfo> Fields,
    IReadOnlyList<ConstructorInfo> Constructors,
    IReadOnlyList<MethodId> MethodIds,     // References to methods already in AnalysisResult.Methods
    string? DocComment                     // XML doc summary
);

public enum TypeKindInfo { Class, Interface, Record, Struct }

public sealed record PropertyFieldInfo(
    string Name,
    string TypeName,
    string AccessModifier,  // "public", "private", etc.
    bool IsStatic
);

public sealed record ConstructorInfo(
    string DisplaySignature,    // Full rich signature
    string AccessModifier,
    IReadOnlyList<ParameterInfo> Parameters
);

public sealed record ParameterInfo(
    string Name,
    string TypeName,            // Display name of the parameter type
    string? TypeNoteName        // Name matching an existing type note, null if external type
);
```

### Pattern 3: Declaration Order via Syntax Span
**What:** Sort members by their `DeclaringSyntaxReferences[0].Span.Start` to guarantee source-file order.
**Why:** `GetMembers()` does not guarantee declaration order. The user explicitly requested declaration-order listing.
**Example:**
```csharp
// Source: Roslyn API -- ISymbol.DeclaringSyntaxReferences, verified via official docs
private static int GetDeclarationOrder(ISymbol symbol)
{
    var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
    return syntaxRef?.Span.Start ?? int.MaxValue;
}

// Usage: sort members by source position
var orderedMembers = typeSymbol.GetMembers()
    .Where(m => !m.IsImplicitlyDeclared)
    .OrderBy(GetDeclarationOrder);
```

### Pattern 4: Reverse Implementor Index
**What:** After collecting all types, build a dictionary mapping each interface TypeId to the list of classes that implement it.
**Why:** STRC-04 requires interface notes to list all known implementors. This must be a post-pass after all types are collected.
**Example:**
```csharp
// In AnalysisResultBuilder or as a post-processing step
private Dictionary<TypeId, List<TypeId>> _implementors = new();

public void RegisterImplementor(TypeId interfaceId, TypeId classId)
{
    if (!_implementors.TryGetValue(interfaceId, out var list))
    {
        list = new List<TypeId>();
        _implementors[interfaceId] = list;
    }
    list.Add(classId);
}

// Emitter can then query: who implements IFoo?
public IReadOnlyList<TypeId> GetImplementors(TypeId interfaceId)
{
    return _implementors.TryGetValue(interfaceId, out var list)
        ? list : Array.Empty<TypeId>();
}
```

### Pattern 5: Rich Method Signature via Custom SymbolDisplayFormat
**What:** Define a custom `SymbolDisplayFormat` that produces complete signatures with access modifiers, return types, and full parameter info.
**Why:** STRC-08 requires "return type, parameter names with types, and access modifiers" in method notes. Phase 1's `MinimallyQualifiedFormat` omits access modifiers.
**Example:**
```csharp
// Source: Roslyn SymbolDisplayFormat API, verified via official docs
private static readonly SymbolDisplayFormat RichSignatureFormat = new(
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.MinimallyQualified,
    memberOptions:
        SymbolDisplayMemberOptions.IncludeAccessibility |
        SymbolDisplayMemberOptions.IncludeModifiers |
        SymbolDisplayMemberOptions.IncludeType |       // return type
        SymbolDisplayMemberOptions.IncludeParameters |
        SymbolDisplayMemberOptions.IncludeRef,
    parameterOptions:
        SymbolDisplayParameterOptions.IncludeType |
        SymbolDisplayParameterOptions.IncludeName |
        SymbolDisplayParameterOptions.IncludeDefaultValue |
        SymbolDisplayParameterOptions.IncludeParamsRefOut,
    genericsOptions:
        SymbolDisplayGenericsOptions.IncludeTypeParameters |
        SymbolDisplayGenericsOptions.IncludeTypeConstraints,
    miscellaneousOptions:
        SymbolDisplayMiscellaneousOptions.UseSpecialTypes
);

// Usage:
string richSig = methodSymbol.ToDisplayString(RichSignatureFormat);
// Result: "public async Task<bool> ProcessAsync(string input, int count = 0)"
```

### Pattern 6: Wikilink Name Extraction
**What:** Convert fully qualified type names to Obsidian-compatible note file names. Strip generic arity markers and angle brackets.
**Why:** Type names like `Dictionary<string, int>` produce invalid file names and broken wikilinks.
**Example:**
```csharp
/// <summary>
/// Converts a type display name to a note-compatible name.
/// "MyNamespace.MyClass" -> "MyClass"
/// "MyNamespace.MyClass<T>" -> "MyClass_T_" (or "MyClass{T}" depending on convention)
/// System types (System.Object, etc.) -> null (no note exists)
/// </summary>
private static string? ToNoteName(INamedTypeSymbol typeSymbol, IReadOnlySet<string> projectAssemblyNames)
{
    // Only create note links for user types (types in the solution)
    if (typeSymbol.ContainingAssembly is null ||
        !projectAssemblyNames.Contains(typeSymbol.ContainingAssembly.Name))
        return null;

    var name = typeSymbol.Name; // Simple name without namespace
    if (typeSymbol.IsGenericType)
    {
        // Append type parameters: MyClass<T, U> -> "MyClass_T_ U_"
        var typeParams = string.Join(", ",
            typeSymbol.TypeParameters.Select(tp => tp.Name));
        name = $"{name}<{typeParams}>";
    }
    return name;
}
```

### Anti-Patterns to Avoid
- **Leaking INamedTypeSymbol into domain models:** TypeInfo must use strings and TypeId, never Roslyn symbols. Same boundary as Phase 1 MethodInfo.
- **Using AllInterfaces instead of Interfaces:** `AllInterfaces` includes transitively inherited interfaces (e.g., `IDisposable` from a base class). Use `Interfaces` (directly declared) for the class note, as users want to see what the class explicitly declares.
- **Iterating compilation.GlobalNamespace with SymbolVisitor:** Overkill for this project. The existing pattern of iterating documents/syntax trees is simpler, consistent with MethodAnalyzer, and handles partial classes naturally (same symbol from multiple files).
- **Assuming GetMembers() order is declaration order:** It is not guaranteed. Always sort by syntax span position.
- **Creating notes for external types:** Only user types (in the solution's own assemblies) get notes. External type references become plain text, not wikilinks.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Method signature formatting | Custom string concatenation of access + return + name + params | `IMethodSymbol.ToDisplayString(customFormat)` | Handles generics, ref params, nullable, default values correctly |
| Type display names | Manual namespace stripping / generic arity calculation | `INamedTypeSymbol.ToDisplayString(format)` or `symbol.Name` + `symbol.TypeParameters` | Edge cases with nested types, generic specialization |
| Finding interface implementors | Manual tree walk looking for `: IFoo` in syntax | Collect from `INamedTypeSymbol.Interfaces` during type analysis pass | Syntax-based approach misses implicit interface implementations and type aliases |
| Access modifier string | Switch on DeclaredAccessibility enum | Map `Accessibility` enum to lowercase string (small utility method) | Only 6 cases, but easy to get `ProtectedOrInternal` wrong ("protected internal") |
| YAML frontmatter | Full YAML serialization library | Simple StringBuilder with known fields | Only 4 fields in Phase 2; library is overkill and adds a dependency |

**Key insight:** Roslyn's `SymbolDisplayFormat` system handles all the fiddly edge cases of C# signature rendering (nullable annotations, `ref`/`out`/`in` parameters, `params`, default values, generic constraints). Hand-rolling signature strings is a recipe for subtle bugs.

## Common Pitfalls

### Pitfall 1: Partial Classes Produce Duplicate TypeInfo
**What goes wrong:** A partial class declared across 3 files appears 3 times in the document iteration loop, creating duplicate TypeInfo entries.
**Why it happens:** Each file contains a `TypeDeclarationSyntax` for the same symbol, and `model.GetDeclaredSymbol()` returns the same `INamedTypeSymbol` for all of them.
**How to avoid:** Use the TypeId (fully qualified name) as a dedup key. `AnalysisResultBuilder.AddType()` should use `TryAdd()` semantics (first wins), same as `AddMethod()`.
**Warning signs:** Note count is higher than expected; duplicate notes with same content.
**Additional nuance:** For the file path, use the first `DeclaringSyntaxReferences` location. For members, they are already consolidated on the symbol regardless of which partial file you get the symbol from -- `GetMembers()` returns all members across all partial declarations.

### Pitfall 2: Compiler-Generated Members Pollute Member Lists
**What goes wrong:** GetMembers() returns compiler-generated backing fields (for auto-properties), default constructors, and other implicit members.
**Why it happens:** Roslyn symbols include everything the compiler sees, not just what the developer wrote.
**How to avoid:** Filter with `!member.IsImplicitlyDeclared` for most cases. For backing fields specifically, check `IFieldSymbol.AssociatedSymbol != null` (backing field of auto-property) or `IFieldSymbol.IsImplicitlyDeclared`.
**Warning signs:** Seeing `<PropertyName>k__BackingField` entries or parameterless constructors the user never wrote.

### Pitfall 3: BaseType Returns System.Object for All Classes
**What goes wrong:** Every class has `BaseType == System.Object`, so the note shows "Inherits from: [[Object]]" for everything.
**Why it happens:** In C#, all classes implicitly inherit from System.Object.
**How to avoid:** Skip BaseType if it is `SpecialType.System_Object`. Check: `typeSymbol.BaseType?.SpecialType != SpecialType.System_Object`.
**Warning signs:** Every class note has "Base class: Object" in frontmatter.

### Pitfall 4: Generic Types in Wikilinks Break File Names
**What goes wrong:** `[[Dictionary<string, int>]]` is not a valid Obsidian wikilink. Angle brackets `<>` are invalid in file names on Windows.
**Why it happens:** Roslyn's `ToDisplayString()` produces standard C# syntax with angle brackets.
**How to avoid:** Sanitize generic type names for file names. Replace `<` and `>` with safe characters. Recommendation: use the simple `Name` property (which excludes type parameters for the file name) and include generic parameters in the note title/body only.
**Warning signs:** Obsidian shows broken links; files with `<` in names fail to create on Windows.

### Pitfall 5: Interfaces vs AllInterfaces Confusion
**What goes wrong:** Class note lists interfaces it doesn't explicitly declare (inherited from base class).
**Why it happens:** Using `AllInterfaces` instead of `Interfaces`. `AllInterfaces` is transitive.
**How to avoid:** Use `Interfaces` (directly declared only) for the class note's interface list. `AllInterfaces` is useful only if you want the full transitive set.
**Warning signs:** A derived class shows `IDisposable` even though only its base declares it.

### Pitfall 6: Constructor Parameter Types Link to Non-Existent Notes
**What goes wrong:** DI dependency links like `[[ILogger]]` point to notes that don't exist because `ILogger` is from Microsoft.Extensions.Logging (external).
**Why it happens:** Emitter creates wikilinks for all parameter types, including external framework types.
**How to avoid:** Only create wikilinks for types that are in the user's solution (check against `projectAssemblyNames`). External types should render as plain text with their type name but no `[[ ]]` wrapping.
**Warning signs:** Obsidian shows many "unresolved link" warnings in Graph View; clicking DI links goes nowhere.

### Pitfall 7: MethodAnalyzer and TypeAnalyzer Double-Iterate Documents
**What goes wrong:** Both analyzers independently iterate all projects/documents, causing the compilation to be requested twice and documents to be parsed twice.
**Why it happens:** Each IAnalyzer is independently structured with its own iteration loop.
**How to avoid:** This is acceptable for Phase 2. The compilation is cached by Roslyn (calling `GetCompilationAsync` twice returns the same object). Document syntax trees and semantic models are also cached. The performance cost is minimal -- it's iteration overhead, not recompilation. If it becomes a concern in Phase 4 (incremental mode), analyzers can be refactored to share a single document iteration.
**Warning signs:** Analysis phase takes noticeably longer than Phase 1 (unlikely given caching).

## Code Examples

Verified patterns from official sources:

### Extracting Type Relationships from INamedTypeSymbol
```csharp
// Source: Microsoft.CodeAnalysis.ITypeSymbol official docs
// https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.itypesymbol

// Base class (skip System.Object)
string? baseClassName = null;
if (typeSymbol.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
{
    baseClassName = baseType.Name; // or ToNoteName() for user types
}

// Directly implemented interfaces (not transitive)
var interfaceNames = typeSymbol.Interfaces
    .Select(i => i.Name)
    .ToList();

// Access modifier
string accessModifier = typeSymbol.DeclaredAccessibility switch
{
    Accessibility.Public => "public",
    Accessibility.Internal => "internal",
    Accessibility.Protected => "protected",
    Accessibility.ProtectedOrInternal => "protected internal",
    Accessibility.ProtectedAndInternal => "private protected",
    Accessibility.Private => "private",
    _ => ""
};
```

### Extracting Properties and Fields
```csharp
// Source: Roslyn GetMembers() API + IPropertySymbol/IFieldSymbol
// https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.ipropertysymbol

var properties = typeSymbol.GetMembers()
    .OfType<IPropertySymbol>()
    .Where(p => !p.IsImplicitlyDeclared)
    .OrderBy(GetDeclarationOrder)
    .Select(p => new PropertyFieldInfo(
        Name: p.Name,
        TypeName: p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        AccessModifier: AccessibilityToString(p.DeclaredAccessibility),
        IsStatic: p.IsStatic
    ))
    .ToList();

var fields = typeSymbol.GetMembers()
    .OfType<IFieldSymbol>()
    .Where(f => !f.IsImplicitlyDeclared && f.AssociatedSymbol is null) // exclude backing fields
    .OrderBy(GetDeclarationOrder)
    .Select(f => new PropertyFieldInfo(
        Name: f.Name,
        TypeName: f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        AccessModifier: AccessibilityToString(f.DeclaredAccessibility),
        IsStatic: f.IsStatic
    ))
    .ToList();
```

### Extracting Constructor Parameters for DI
```csharp
// Source: INamedTypeSymbol.InstanceConstructors + IMethodSymbol.Parameters
// https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.inamedtypesymbol

var constructors = typeSymbol.InstanceConstructors
    .Where(c => !c.IsImplicitlyDeclared)
    .OrderBy(GetDeclarationOrder)
    .Select(ctor => new ConstructorInfo(
        DisplaySignature: ctor.ToDisplayString(RichSignatureFormat),
        AccessModifier: AccessibilityToString(ctor.DeclaredAccessibility),
        Parameters: ctor.Parameters.Select(p => new ParameterInfo(
            Name: p.Name,
            TypeName: p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            TypeNoteName: IsUserType(p.Type, projectAssemblyNames)
                ? GetNoteName(p.Type)
                : null
        )).ToList()
    ))
    .ToList();
```

### Collecting MethodIds for a Type's Methods
```csharp
// Link to already-extracted methods from MethodAnalyzer
var methodIds = typeSymbol.GetMembers()
    .OfType<IMethodSymbol>()
    .Where(m => AnalysisHelpers.IsUserMethod(m, context.ProjectAssemblyNames))
    .OrderBy(GetDeclarationOrder)
    .Select(MethodId.FromSymbol)
    .ToList();
```

### Emitting Class Note Markdown
```csharp
// Example output structure for a class note
var sb = new StringBuilder();

// YAML frontmatter (Phase 2 minimal set)
sb.AppendLine("---");
sb.AppendLine($"base_class: {typeInfo.BaseClassName ?? "~"}");  // YAML null
sb.AppendLine("interfaces:");
foreach (var iface in typeInfo.InterfaceNames)
    sb.AppendLine($"  - \"{iface}\"");
sb.AppendLine($"namespace: \"{typeInfo.Namespace}\"");
sb.AppendLine($"source_file: \"{typeInfo.FilePath}\"");
sb.AppendLine("---");
sb.AppendLine();

// Title
sb.AppendLine($"# {typeInfo.Name}");
sb.AppendLine();

// Source path
sb.AppendLine($"**Path**: `{typeInfo.FilePath}`");
sb.AppendLine();

// Type relationships (as wikilinks)
if (typeInfo.BaseClassName is not null)
    sb.AppendLine($"**Inherits from**: [[{typeInfo.BaseClassName}]]");

if (typeInfo.InterfaceNames.Count > 0)
{
    sb.AppendLine("**Implements**:");
    foreach (var iface in typeInfo.InterfaceNames)
        sb.AppendLine($"- [[{iface}]]");
}
sb.AppendLine();

// DI Dependencies section
if (hasDiDependencies)
{
    sb.AppendLine("## Dependencies");
    foreach (var param in primaryCtor.Parameters.Where(p => p.TypeNoteName is not null))
        sb.AppendLine($"- [[{param.TypeNoteName}]] (`{param.Name}`)");
    sb.AppendLine();
}

// Member index (compact, declaration order, no access modifiers)
sb.AppendLine("## Members");
sb.AppendLine();
foreach (var methodId in typeInfo.MethodIds)
{
    // Extract ClassName.MethodName for the wikilink
    var noteName = ExtractClassMethodName(methodId);
    var returnType = GetReturnTypeForMethod(methodId); // optional for compact index
    sb.AppendLine($"- [[{noteName}]]");
}
```

### Emitting Interface Note with Known Implementors
```csharp
// Interface-specific section
sb.AppendLine("## Known Implementors");
sb.AppendLine();
var implementors = analysisResult.GetImplementors(typeInfo.Id);
if (implementors.Count > 0)
{
    foreach (var implTypeId in implementors.OrderBy(t => t.Value))
    {
        var implName = ExtractSimpleName(implTypeId);
        sb.AppendLine($"- [[{implName}]]");
    }
}
else
{
    sb.AppendLine("_No known implementors in this solution._");
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| SymbolVisitor to traverse all types | Document iteration + GetDeclaredSymbol per TypeDeclarationSyntax | Roslyn patterns stabilized ~2018 | Document iteration is simpler, consistent with MethodAnalyzer, handles partial classes |
| Reflection-based analysis | Roslyn semantic model | .NET Compiler Platform 1.0 (2015) | Full semantic info without loading assemblies at runtime |
| GetMembers() ordering assumed stable | Sort by DeclaringSyntaxReferences.Span.Start | Always needed | Guarantees declaration order |

**No deprecated APIs in use.** All Roslyn APIs referenced (INamedTypeSymbol, IPropertySymbol, IFieldSymbol, ITypeSymbol.BaseType, .Interfaces, .GetMembers()) have been stable since Roslyn 1.0 and remain current in 4.14.0.

## Open Questions

1. **GetMembers() declaration order in practice**
   - What we know: The Roslyn API does not explicitly guarantee source-order from GetMembers(). DeclaringSyntaxReferences.Span.Start provides guaranteed ordering.
   - What's unclear: Whether GetMembers() is reliably ordered in the current Roslyn implementation (4.14.0) even without the guarantee.
   - Recommendation: Always sort by syntax span position. The cost is negligible and provides a hard guarantee.

2. **Generic type parameter rendering in wikilinks**
   - What we know: `MyClass<T>` produces invalid file names (`<` and `>` are illegal on Windows).
   - What's unclear: Best convention for generic type names in Obsidian note names (e.g., `MyClass_T_`, `MyClass{T}`, `MyClass-T-`).
   - Recommendation: Use the bare `Name` property (no type parameters) for file names. Generic types are rare in typical business logic; most classes are non-generic. For the rare generic class, the note title and body can show full generic syntax while the file name uses the simple name. If name collisions occur (e.g., `MyClass` and `MyClass<T>` both exist), append arity: `MyClass`, `MyClass`1`.

3. **Structs and records: should they get notes?**
   - What we know: The requirements say "class" and "interface" but the user's domain says "every class and interface in the analyzed solution." Records are classes. Structs are value types.
   - What's unclear: Whether struct notes are desired (e.g., custom value objects, Akka.NET message types).
   - Recommendation: Include records (they ARE classes). Include structs too -- the user wants comprehensive Graph View coverage. Filter to TypeKind.Class, TypeKind.Interface, TypeKind.Struct for now. Enums and delegates can be deferred.

4. **MethodInfo.DisplaySignature upgrade**
   - What we know: Phase 1 uses `MinimallyQualifiedFormat` which omits access modifiers. STRC-08 requires access modifiers.
   - What's unclear: Whether changing the existing format string is a breaking change for existing method notes.
   - Recommendation: Update MethodAnalyzer to use the new `RichSignatureFormat`. This changes the signature display in method notes but that IS the requirement. Old vault outputs will be regenerated.

## Sources

### Primary (HIGH confidence)
- [INamedTypeSymbol official docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.inamedtypesymbol?view=roslyn-dotnet-4.9.0) - Properties, constructors, type members, GetMembers()
- [ITypeSymbol official docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.itypesymbol?view=roslyn-dotnet-4.9.0) - BaseType, Interfaces, AllInterfaces, TypeKind
- [SymbolDisplayFormat official docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.symboldisplayformat?view=roslyn-dotnet-4.9.0) - Member options, parameter options for signature formatting
- [INamedTypeSymbol.cs source](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/Symbols/INamedTypeSymbol.cs) - InstanceConstructors, Arity, TypeParameters
- [Dataview metadata docs](https://blacksmithgu.github.io/obsidian-dataview/annotation/add-metadata/) - YAML frontmatter array format for Obsidian compatibility

### Secondary (MEDIUM confidence)
- [Roslyn issue #6138](https://github.com/dotnet/roslyn/issues/6138) - Discussion on getting all INamedTypeSymbols, SymbolVisitor vs document iteration approaches
- [SymbolDisplayFormat.cs source](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/SymbolDisplay/SymbolDisplayFormat.cs) - Member display option flags
- [Roslyn discussion #75619](https://github.com/dotnet/roslyn/discussions/75619) - Interfaces vs AllInterfaces behavior clarification
- Existing project codebase (Phase 1) - MethodAnalyzer.cs, ObsidianEmitter.cs, domain model patterns

### Tertiary (LOW confidence)
- WebSearch on GetMembers() ordering: No official guarantee found. Sort-by-span recommendation is based on inference from DeclaringSyntaxReferences docs rather than explicit Roslyn team guidance.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - No new dependencies; all APIs already in project via Roslyn 4.14.0
- Architecture: HIGH - Follows established Phase 1 patterns (IAnalyzer, domain models, string-based IDs, builder pattern)
- Roslyn API usage: HIGH - INamedTypeSymbol/ITypeSymbol APIs verified against official Microsoft docs
- Pitfalls: HIGH - Based on Roslyn API docs + practical experience documented in GitHub issues
- YAML frontmatter format: MEDIUM - Based on Dataview docs; Phase 3 will expand this
- Declaration order guarantee: MEDIUM - Sort-by-span approach is sound but "GetMembers order" behavior is undocumented

**Research date:** 2026-02-25
**Valid until:** 2026-03-25 (Roslyn APIs are extremely stable; 30 days is conservative)
