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
/// </summary>
public sealed class ObsidianEmitter : IEmitter
{
    public async Task<EmitResult> EmitAsync(
        EnrichedResult result,
        string outputDirectory,
        CancellationToken ct)
    {
        var analysis = result.Analysis;
        var warnings = new List<string>();
        int notesWritten = 0;

        Directory.CreateDirectory(outputDirectory);

        // Emit method notes
        foreach (var (methodId, method) in analysis.Methods)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Sanitize($"{method.ContainingTypeName}.{method.Name}.md");
            var filePath = Path.Combine(outputDirectory, fileName);

            try
            {
                var content = RenderMethodNote(method, analysis.CallGraph);
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

            var fileName = Sanitize($"{typeInfo.FullName}.md");
            var filePath = Path.Combine(outputDirectory, fileName);

            try
            {
                var content = typeInfo.Kind == TypeKindInfo.Interface
                    ? RenderInterfaceNote(typeInfo, analysis)
                    : RenderClassNote(typeInfo, analysis);
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
    /// Renders a complete markdown note for a single method with YAML frontmatter,
    /// header, signature, doc comment, and call graph links.
    /// Ported from Program.cs RenderMethodNote (lines 222-239).
    /// </summary>
    private static string RenderMethodNote(MethodInfo method, CallGraph callGraph)
    {
        var sb = new StringBuilder();

        // YAML frontmatter
        sb.AppendLine("---");
        sb.AppendLine("tags:");
        sb.AppendLine("  - method");
        sb.AppendLine("---");

        // Header
        sb.AppendLine($"# {method.ContainingTypeName}::{method.Name}");
        sb.AppendLine($"**Path**: `{method.FilePath}`");
        sb.AppendLine();

        // Method section (signature, doc, calls)
        sb.Append(RenderMethodSection(method, callGraph));

        return sb.ToString();
    }

    /// <summary>
    /// Renders a method section with signature, doc comment, and call graph links.
    /// Ported from Program.cs RenderMethodSection (lines 172-219).
    /// Adapted to use domain MethodInfo and CallGraph with string-based IDs.
    /// </summary>
    private static string RenderMethodSection(MethodInfo method, CallGraph callGraph)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine($"#### [[{method.Name}]]");
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
                // Extract just the method name from the full ID for the wiki link
                var calleeName = ExtractMethodName(callee);
                sb.AppendLine($"- [[{calleeName}]]");
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
                var callerName = ExtractMethodName(caller);
                sb.AppendLine($"- [[{callerName}]]");
            }
            sb.AppendLine();
        }

        return sb.ToString();
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
    /// Includes YAML frontmatter, purpose summary, inheritance wikilinks,
    /// DI dependencies, properties/fields, and member index.
    /// </summary>
    private static string RenderClassNote(TypeInfo typeInfo, AnalysisResult analysis)
    {
        var sb = new StringBuilder();

        // Build a set of known type FullNames for wikilink resolution
        var knownTypes = new HashSet<string>(
            analysis.Types.Values.Select(t => t.FullName));

        // YAML frontmatter (Phase 2 minimal set)
        sb.AppendLine("---");
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
        sb.AppendLine($"namespace: \"{typeInfo.Namespace}\"");
        sb.AppendLine($"source_file: \"{typeInfo.FilePath}\"");
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
                sb.AppendLine($"**Inherits from**: [[{Sanitize(typeInfo.BaseClassFullName)}]]");
            else
                sb.AppendLine($"**Inherits from**: {typeInfo.BaseClassName}");
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
                    sb.AppendLine($"- [[{Sanitize(fullName)}]]");
                else
                    sb.AppendLine($"- {displayName}");
            }
            sb.AppendLine();
        }

        // Dependencies section (DI from all constructors, deduped by TypeNoteFullName)
        var diDeps = typeInfo.Constructors
            .SelectMany(c => c.Parameters)
            .Where(p => p.TypeNoteFullName is not null)
            .GroupBy(p => p.TypeNoteFullName!)
            .Select(g => g.First())
            .ToList();

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

        // Members section (compact wikilink index)
        if (typeInfo.MethodIds.Count > 0)
        {
            sb.AppendLine("## Members");
            foreach (var methodId in typeInfo.MethodIds)
            {
                if (analysis.Methods.TryGetValue(methodId, out var methodInfo))
                {
                    var wikilink = Sanitize($"{methodInfo.ContainingTypeName}.{methodInfo.Name}");
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
    /// and omits the Dependencies section (interfaces have no constructors with DI).
    /// </summary>
    private static string RenderInterfaceNote(TypeInfo typeInfo, AnalysisResult analysis)
    {
        var sb = new StringBuilder();

        var knownTypes = new HashSet<string>(
            analysis.Types.Values.Select(t => t.FullName));

        // YAML frontmatter
        sb.AppendLine("---");
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
        sb.AppendLine($"namespace: \"{typeInfo.Namespace}\"");
        sb.AppendLine($"source_file: \"{typeInfo.FilePath}\"");
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
                    sb.AppendLine($"- [[{Sanitize(fullName)}]]");
                else
                    sb.AppendLine($"- {displayName}");
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

        // Members section
        if (typeInfo.MethodIds.Count > 0)
        {
            sb.AppendLine("## Members");
            foreach (var methodId in typeInfo.MethodIds)
            {
                if (analysis.Methods.TryGetValue(methodId, out var methodInfo))
                {
                    var wikilink = Sanitize($"{methodInfo.ContainingTypeName}.{methodInfo.Name}");
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
                    sb.AppendLine($"- [[{Sanitize(implType.FullName)}]]");
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
    /// Ported from Program.cs Sanitize() (lines 362-368).
    /// </summary>
    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
