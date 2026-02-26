using System.Text;
using Code2Obsidian.Analysis.Models;
using Code2Obsidian.Enrichment;

namespace Code2Obsidian.Emission;

/// <summary>
/// Generates Obsidian markdown notes from enriched analysis data.
/// Emits one .md file per method (per-method mode is the default for Phase 1).
///
/// Ported from Program.cs RenderMethodSection (lines 172-219),
/// RenderMethodNote (lines 222-239), and Sanitize (lines 362-368).
/// Adapted to use domain MethodInfo and CallGraph instead of IMethodSymbol dictionaries.
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
