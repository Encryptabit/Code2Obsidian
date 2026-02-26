using System.Text;
using Code2Obsidian.Analysis;
using Code2Obsidian.Analysis.Models;
using Code2Obsidian.Enrichment;

namespace Code2Obsidian.Emission;

/// <summary>
/// Generates Obsidian markdown notes from enriched analysis data.
/// Emits one .md file per method, one per class/struct/record, and one per interface.
/// Class notes are hub pages linking to methods, base classes, interfaces, and DI dependencies.
/// Interface notes include "Known Implementors" sections.
/// Produces expanded YAML frontmatter with Dataview-compatible fields, collision-free wikilinks,
/// danger callouts for high-risk methods, and architectural pattern tags.
/// </summary>
public sealed class ObsidianEmitter : IEmitter
{
    private readonly int _fanInThreshold;
    private readonly int _complexityThreshold;

    /// <summary>
    /// Pattern suffix table for architectural pattern detection.
    /// </summary>
    private static readonly (string Suffix, string Pattern)[] PatternSuffixes =
    {
        ("Repository", "repository"),
        ("Controller", "controller"),
        ("Service", "service"),
        ("Middleware", "middleware"),
        ("Factory", "factory"),
        ("Handler", "handler"),
    };

    public ObsidianEmitter(int fanInThreshold = 10, int complexityThreshold = 15)
    {
        _fanInThreshold = fanInThreshold;
        _complexityThreshold = complexityThreshold;
    }

    public async Task<EmitResult> EmitAsync(
        EnrichedResult result,
        string outputDirectory,
        CancellationToken ct)
    {
        var analysis = result.Analysis;
        var warnings = new List<string>();
        int notesWritten = 0;

        Directory.CreateDirectory(outputDirectory);

        // Build collision set: short class names that appear more than once across different namespaces
        var collisionSet = BuildCollisionSet(analysis);

        // Emit method notes
        foreach (var (methodId, method) in analysis.Methods)
        {
            ct.ThrowIfCancellationRequested();

            var shortClassName = GetShortClassName(method.ContainingTypeName);
            var noteBaseName = IsCollision(shortClassName, collisionSet)
                ? $"{method.ContainingTypeName}.{method.Name}"
                : $"{shortClassName}.{method.Name}";
            var fileName = Sanitize($"{noteBaseName}.md");
            var filePath = Path.Combine(outputDirectory, fileName);

            try
            {
                var content = RenderMethodNote(method, analysis, collisionSet);
                await File.WriteAllTextAsync(filePath, content, ct);
                notesWritten++;
            }
            catch (IOException ex)
            {
                warnings.Add($"Failed to write '{filePath}': {ex.Message}");
            }
        }

        // Emit class and interface notes
        foreach (var (typeId, typeInfo) in analysis.Types)
        {
            ct.ThrowIfCancellationRequested();

            var shortClassName = GetShortClassName(typeInfo.FullName);
            var noteBaseName = IsCollision(shortClassName, collisionSet)
                ? typeInfo.FullName
                : shortClassName;
            var fileName = Sanitize($"{noteBaseName}.md");
            var filePath = Path.Combine(outputDirectory, fileName);

            try
            {
                var content = typeInfo.Kind == TypeKindInfo.Interface
                    ? RenderInterfaceNote(typeInfo, analysis, collisionSet)
                    : RenderClassNote(typeInfo, analysis, collisionSet);
                await File.WriteAllTextAsync(filePath, content, ct);
                notesWritten++;
            }
            catch (IOException ex)
            {
                warnings.Add($"Failed to write '{filePath}': {ex.Message}");
            }
        }

        return new EmitResult(notesWritten, warnings);
    }

    /// <summary>
    /// Builds a set of short class names that appear more than once across different namespaces.
    /// When a short name collides, wikilinks and file names must use the namespace-qualified form.
    /// </summary>
    private static HashSet<string> BuildCollisionSet(AnalysisResult analysis)
    {
        // Map short name -> set of namespaces it appears in
        var nameToNamespaces = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var typeInfo in analysis.Types.Values)
        {
            var shortName = typeInfo.Name;
            if (!nameToNamespaces.TryGetValue(shortName, out var namespaces))
            {
                namespaces = new HashSet<string>(StringComparer.Ordinal);
                nameToNamespaces[shortName] = namespaces;
            }
            namespaces.Add(typeInfo.Namespace);
        }

        // Also scan methods for containing type names that might not have their own TypeInfo
        foreach (var method in analysis.Methods.Values)
        {
            var shortName = GetShortClassName(method.ContainingTypeName);
            if (!nameToNamespaces.TryGetValue(shortName, out var namespaces))
            {
                namespaces = new HashSet<string>(StringComparer.Ordinal);
                nameToNamespaces[shortName] = namespaces;
            }
            namespaces.Add(method.Namespace);
        }

        var collisionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (shortName, namespaces) in nameToNamespaces)
        {
            if (namespaces.Count > 1)
                collisionSet.Add(shortName);
        }

        return collisionSet;
    }

    /// <summary>
    /// Returns true if the short class name is in the collision set.
    /// </summary>
    private static bool IsCollision(string shortClassName, HashSet<string> collisionSet)
    {
        return collisionSet.Contains(shortClassName);
    }

    /// <summary>
    /// Extracts the short class name from a potentially fully-qualified name.
    /// E.g., "Namespace.ClassName" -> "ClassName", "ClassName" -> "ClassName"
    /// </summary>
    private static string GetShortClassName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot < 0 ? fullName : fullName.Substring(lastDot + 1);
    }

    /// <summary>
    /// Detects an architectural pattern based on type name suffix.
    /// Returns the pattern name (e.g., "repository") or null if no pattern matches.
    /// </summary>
    private static string? DetectPattern(string typeName)
    {
        foreach (var (suffix, pattern) in PatternSuffixes)
        {
            if (typeName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return pattern;
        }
        return null;
    }

    /// <summary>
    /// Renders a complete markdown note for a single method with expanded YAML frontmatter,
    /// danger callouts for high-risk methods, header, signature, doc comment, and call graph links.
    /// </summary>
    private string RenderMethodNote(MethodInfo method, AnalysisResult analysis, HashSet<string> collisionSet)
    {
        var sb = new StringBuilder();
        var callGraph = analysis.CallGraph;

        // Compute fan-in and fan-out from the call graph
        var fanIn = callGraph.GetCallers(method.Id).Count;
        var fanOut = callGraph.GetCallees(method.Id).Count;

        // YAML frontmatter with expanded fields
        sb.AppendLine("---");
        sb.AppendLine($"namespace: \"{method.Namespace}\"");
        sb.AppendLine($"project: \"{method.ProjectName}\"");
        sb.AppendLine($"source_file: \"{method.FilePath}\"");
        sb.AppendLine($"access_modifier: \"{method.AccessModifier}\"");
        sb.AppendLine($"complexity: {method.CyclomaticComplexity}");
        sb.AppendLine($"fan_in: {fanIn}");
        sb.AppendLine($"fan_out: {fanOut}");
        sb.AppendLine("tags:");
        sb.AppendLine("  - method");
        if (fanIn >= _fanInThreshold)
            sb.AppendLine("  - danger/high-fan-in");
        if (method.CyclomaticComplexity >= _complexityThreshold)
            sb.AppendLine("  - danger/high-complexity");
        sb.AppendLine("---");

        // Header
        var shortClassName = GetShortClassName(method.ContainingTypeName);
        sb.AppendLine($"# {shortClassName}::{method.Name}");
        sb.AppendLine($"**Path**: `{method.FilePath}`");
        sb.AppendLine();

        // Danger callouts (before method section)
        if (fanIn >= _fanInThreshold)
        {
            sb.AppendLine($"> [!danger] High Fan-In ({fanIn})");
            sb.AppendLine($"> This method is called by {fanIn} other methods. Changes here have wide impact.");
            sb.AppendLine();
        }

        if (method.CyclomaticComplexity >= _complexityThreshold)
        {
            sb.AppendLine($"> [!danger] High Complexity ({method.CyclomaticComplexity})");
            sb.AppendLine($"> Cyclomatic complexity: {method.CyclomaticComplexity}. Consider refactoring into smaller methods.");
            sb.AppendLine();
        }

        // Method section (signature, doc, calls)
        sb.Append(RenderMethodSection(method, analysis, collisionSet));

        return sb.ToString();
    }

    /// <summary>
    /// Renders a method section with signature, doc comment, and call graph links.
    /// Uses collision-free wikilinks: default [[ClassName.MethodName]], fallback [[Namespace.ClassName.MethodName]].
    /// </summary>
    private static string RenderMethodSection(MethodInfo method, AnalysisResult analysis, HashSet<string> collisionSet)
    {
        var sb = new StringBuilder();
        var callGraph = analysis.CallGraph;

        // Self-link header with collision-aware wikilink
        var shortClassName = GetShortClassName(method.ContainingTypeName);
        var selfLink = IsCollision(shortClassName, collisionSet)
            ? Sanitize($"{method.ContainingTypeName}.{method.Name}")
            : Sanitize($"{shortClassName}.{method.Name}");

        sb.AppendLine();
        sb.AppendLine($"#### [[{selfLink}]]");
        sb.AppendLine("##### What it does:");

        if (!string.IsNullOrWhiteSpace(method.DocComment))
        {
            foreach (var line in method.DocComment.Split('\n'))
                sb.AppendLine(line.TrimEnd());
        }
        else
        {
            sb.AppendLine("- _TODO: Plain-English walkthrough._");
        }
        sb.AppendLine();
        sb.AppendLine("##### Improvements:");
        sb.AppendLine("- _TODO: Suggested optimizations._");
        sb.AppendLine();

        // Signature code block
        sb.AppendLine("```csharp");
        sb.AppendLine(method.DisplaySignature);
        sb.AppendLine("```");
        sb.AppendLine();

        // Outgoing calls
        var callees = callGraph.GetCallees(method.Id);
        if (callees.Count > 0)
        {
            sb.AppendLine("**Calls ->**");
            foreach (var callee in callees.OrderBy(c => c.Value))
            {
                var wikilink = ResolveMethodWikilink(callee, analysis, collisionSet);
                sb.AppendLine($"- [[{wikilink}]]");
            }
            sb.AppendLine();
        }

        // Incoming calls
        var callers = callGraph.GetCallers(method.Id);
        if (callers.Count > 0)
        {
            sb.AppendLine("**Called-by <-**");
            foreach (var caller in callers.OrderBy(c => c.Value))
            {
                var wikilink = ResolveMethodWikilink(caller, analysis, collisionSet);
                sb.AppendLine($"- [[{wikilink}]]");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves a MethodId to a collision-free wikilink string.
    /// Default: ClassName.MethodName. Fallback for collisions: Namespace.ClassName.MethodName.
    /// Falls back to ExtractMethodName for external methods not in the analysis.
    /// </summary>
    private static string ResolveMethodWikilink(MethodId methodId, AnalysisResult analysis, HashSet<string> collisionSet)
    {
        if (analysis.Methods.TryGetValue(methodId, out var methodInfo))
        {
            var shortClassName = GetShortClassName(methodInfo.ContainingTypeName);
            return IsCollision(shortClassName, collisionSet)
                ? Sanitize($"{methodInfo.ContainingTypeName}.{methodInfo.Name}")
                : Sanitize($"{shortClassName}.{methodInfo.Name}");
        }

        // External method: fall back to just the method name
        return ExtractMethodName(methodId);
    }

    /// <summary>
    /// Resolves a type to a collision-free wikilink string.
    /// Default: ClassName. Fallback for collisions: Namespace.ClassName.
    /// </summary>
    private static string ResolveTypeWikilink(TypeInfo typeInfo, HashSet<string> collisionSet)
    {
        return IsCollision(typeInfo.Name, collisionSet)
            ? Sanitize(typeInfo.FullName)
            : Sanitize(typeInfo.Name);
    }

    /// <summary>
    /// Extracts the method name portion from a fully qualified MethodId.
    /// E.g., "Namespace.Class.Method(params)" -> "Method"
    /// </summary>
    private static string ExtractMethodName(MethodId id)
    {
        var value = id.Value;
        // Find the opening paren
        var parenIndex = value.IndexOf('(');
        if (parenIndex < 0) parenIndex = value.Length;

        // Find the last dot before the paren
        var dotIndex = value.LastIndexOf('.', parenIndex - 1);
        if (dotIndex < 0) return value;

        return value.Substring(dotIndex + 1, parenIndex - dotIndex - 1);
    }

    /// <summary>
    /// Renders a complete markdown note for a class, record, or struct type.
    /// Includes expanded YAML frontmatter with pattern detection, purpose summary,
    /// inheritance wikilinks, DI dependencies, properties/fields, and member index.
    /// </summary>
    private static string RenderClassNote(TypeInfo typeInfo, AnalysisResult analysis, HashSet<string> collisionSet)
    {
        var sb = new StringBuilder();

        // Build a set of known type FullNames for wikilink resolution
        var knownTypes = new HashSet<string>(
            analysis.Types.Values.Select(t => t.FullName));

        // Compute DI dependencies (needed for frontmatter and body)
        var diDeps = typeInfo.Constructors
            .SelectMany(c => c.Parameters)
            .Where(p => p.TypeNoteFullName is not null)
            .GroupBy(p => p.TypeNoteFullName!)
            .Select(g => g.First())
            .ToList();

        // Pattern detection
        var detectedPattern = DetectPattern(typeInfo.Name);

        // YAML frontmatter with expanded fields
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
        sb.AppendLine($"member_count: {typeInfo.MethodIds.Count}");
        sb.AppendLine($"dependency_count: {diDeps.Count}");
        sb.AppendLine(detectedPattern is not null
            ? $"pattern: \"{detectedPattern}\""
            : "pattern: ~");
        sb.AppendLine("tags:");
        sb.AppendLine("  - class");
        if (detectedPattern is not null)
            sb.AppendLine($"  - pattern/{detectedPattern}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Title
        sb.AppendLine($"# {typeInfo.Name}");
        sb.AppendLine();

        // Purpose summary (DocComment blockquote or kind/namespace fallback)
        if (!string.IsNullOrWhiteSpace(typeInfo.DocComment))
        {
            sb.AppendLine($"> {typeInfo.DocComment}");
        }
        else
        {
            sb.AppendLine($"> {typeInfo.Kind} in `{typeInfo.Namespace}`");
        }
        sb.AppendLine();

        // Source path
        sb.AppendLine($"**Path**: `{typeInfo.FilePath}`");
        sb.AppendLine();

        // Type relationships
        if (typeInfo.BaseClassFullName is not null)
        {
            if (knownTypes.Contains(typeInfo.BaseClassFullName))
            {
                // Find the base type to resolve its wikilink
                var baseType = analysis.Types.Values.FirstOrDefault(t => t.FullName == typeInfo.BaseClassFullName);
                var baseWikilink = baseType is not null
                    ? ResolveTypeWikilink(baseType, collisionSet)
                    : Sanitize(typeInfo.BaseClassFullName);
                sb.AppendLine($"**Inherits from**: [[{baseWikilink}]]");
            }
            else
            {
                sb.AppendLine($"**Inherits from**: {typeInfo.BaseClassName}");
            }
            sb.AppendLine();
        }

        if (typeInfo.InterfaceFullNames.Count > 0)
        {
            sb.AppendLine("**Implements**:");
            for (int i = 0; i < typeInfo.InterfaceFullNames.Count; i++)
            {
                var fullName = typeInfo.InterfaceFullNames[i];
                var displayName = typeInfo.InterfaceNames[i];
                if (knownTypes.Contains(fullName))
                {
                    var ifaceType = analysis.Types.Values.FirstOrDefault(t => t.FullName == fullName);
                    var ifaceWikilink = ifaceType is not null
                        ? ResolveTypeWikilink(ifaceType, collisionSet)
                        : Sanitize(fullName);
                    sb.AppendLine($"- [[{ifaceWikilink}]]");
                }
                else
                {
                    sb.AppendLine($"- {displayName}");
                }
            }
            sb.AppendLine();
        }

        // Dependencies section (DI from all constructors, deduped by TypeNoteFullName)
        if (diDeps.Count > 0)
        {
            sb.AppendLine("## Dependencies");
            foreach (var dep in diDeps)
            {
                sb.AppendLine($"- [[{Sanitize(dep.TypeNoteFullName!)}]] (`{dep.Name}`)");
            }
            sb.AppendLine();
        }

        // Properties and Fields
        if (typeInfo.Properties.Count > 0 || typeInfo.Fields.Count > 0)
        {
            sb.AppendLine("## Properties");
            foreach (var prop in typeInfo.Properties)
                sb.AppendLine($"- `{prop.Name}`: {prop.TypeName}");
            foreach (var field in typeInfo.Fields)
                sb.AppendLine($"- `{field.Name}`: {field.TypeName}");
            sb.AppendLine();
        }

        // Members section (compact wikilink index with collision awareness)
        if (typeInfo.MethodIds.Count > 0)
        {
            sb.AppendLine("## Members");
            foreach (var methodId in typeInfo.MethodIds)
            {
                if (analysis.Methods.TryGetValue(methodId, out var methodInfo))
                {
                    var wikilink = ResolveMethodWikilink(methodId, analysis, collisionSet);
                    sb.AppendLine($"- [[{wikilink}]]");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a complete markdown note for an interface type.
    /// Same structure as class notes, but includes "Known Implementors" section
    /// and sets dependency_count to 0 (interfaces have no constructors with DI).
    /// </summary>
    private static string RenderInterfaceNote(TypeInfo typeInfo, AnalysisResult analysis, HashSet<string> collisionSet)
    {
        var sb = new StringBuilder();

        var knownTypes = new HashSet<string>(
            analysis.Types.Values.Select(t => t.FullName));

        // Pattern detection (interfaces can have patterns too, e.g., IRepository)
        var detectedPattern = DetectPattern(typeInfo.Name);

        // YAML frontmatter with expanded fields
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
        sb.AppendLine($"member_count: {typeInfo.MethodIds.Count}");
        sb.AppendLine("dependency_count: 0");
        sb.AppendLine(detectedPattern is not null
            ? $"pattern: \"{detectedPattern}\""
            : "pattern: ~");
        sb.AppendLine("tags:");
        sb.AppendLine("  - interface");
        if (detectedPattern is not null)
            sb.AppendLine($"  - pattern/{detectedPattern}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Title
        sb.AppendLine($"# {typeInfo.Name}");
        sb.AppendLine();

        // Purpose summary
        if (!string.IsNullOrWhiteSpace(typeInfo.DocComment))
        {
            sb.AppendLine($"> {typeInfo.DocComment}");
        }
        else
        {
            sb.AppendLine($"> {typeInfo.Kind} in `{typeInfo.Namespace}`");
        }
        sb.AppendLine();

        // Source path
        sb.AppendLine($"**Path**: `{typeInfo.FilePath}`");
        sb.AppendLine();

        // Type relationships (interfaces can extend other interfaces)
        if (typeInfo.InterfaceFullNames.Count > 0)
        {
            sb.AppendLine("**Extends**:");
            for (int i = 0; i < typeInfo.InterfaceFullNames.Count; i++)
            {
                var fullName = typeInfo.InterfaceFullNames[i];
                var displayName = typeInfo.InterfaceNames[i];
                if (knownTypes.Contains(fullName))
                {
                    var ifaceType = analysis.Types.Values.FirstOrDefault(t => t.FullName == fullName);
                    var ifaceWikilink = ifaceType is not null
                        ? ResolveTypeWikilink(ifaceType, collisionSet)
                        : Sanitize(fullName);
                    sb.AppendLine($"- [[{ifaceWikilink}]]");
                }
                else
                {
                    sb.AppendLine($"- {displayName}");
                }
            }
            sb.AppendLine();
        }

        // Properties (interfaces can have property declarations)
        if (typeInfo.Properties.Count > 0)
        {
            sb.AppendLine("## Properties");
            foreach (var prop in typeInfo.Properties)
                sb.AppendLine($"- `{prop.Name}`: {prop.TypeName}");
            sb.AppendLine();
        }

        // Members section with collision-aware wikilinks
        if (typeInfo.MethodIds.Count > 0)
        {
            sb.AppendLine("## Members");
            foreach (var methodId in typeInfo.MethodIds)
            {
                if (analysis.Methods.TryGetValue(methodId, out var methodInfo))
                {
                    var wikilink = ResolveMethodWikilink(methodId, analysis, collisionSet);
                    sb.AppendLine($"- [[{wikilink}]]");
                }
            }
            sb.AppendLine();
        }

        // Known Implementors section
        var typeIdForLookup = typeInfo.Id;
        if (analysis.Implementors.TryGetValue(typeIdForLookup, out var implementorIds)
            && implementorIds.Count > 0)
        {
            sb.AppendLine("## Known Implementors");
            foreach (var implId in implementorIds)
            {
                if (analysis.Types.TryGetValue(implId, out var implType))
                {
                    var implWikilink = ResolveTypeWikilink(implType, collisionSet);
                    sb.AppendLine($"- [[{implWikilink}]]");
                }
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Known Implementors");
            sb.AppendLine("_No known implementors in this solution._");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sanitizes a string for use as a file name by replacing invalid characters.
    /// </summary>
    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
