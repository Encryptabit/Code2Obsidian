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
using System.Xml.Linq; // added

namespace Code2Obsidian
{
    internal static class Program
    {
        private sealed record Options(
            string SolutionPath,
            string OutDir,
            bool PerFile,
            bool PerMethod
        );

        private sealed record MethodInfo(
            string FilePath,
            Microsoft.CodeAnalysis.Text.TextSpan Span,
            IMethodSymbol Symbol
        );

        private static async Task<int> Main(string[] args)
        {
            // --- Parse CLI args (no external packages) ---
            if (!TryParseArgs(args, out var opt, out var parseError))
            {
                Console.Error.WriteLine(parseError);
                PrintUsage();
                return 2;
            }

            // --- Robust MSBuild registration ---
            EnsureMsbuildRegistered();

            // --- Resolve I/O ---
            var slnPath = Path.GetFullPath(opt.SolutionPath);
            if (!File.Exists(slnPath))
            {
                Console.Error.WriteLine($"Solution not found: {slnPath}");
                return 2;
            }

            var outDir = Path.GetFullPath(opt.OutDir);
            Directory.CreateDirectory(outDir);

            Console.WriteLine($"Solution: {slnPath}");
            Console.WriteLine($"Output  : {outDir}");
            Console.WriteLine($"Mode    : {(opt.PerFile ? "--per-file" : "--per-method")}");

            // --- Roslyn load ---
            using var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(slnPath);

            var projectAssemblies = GetProjectAssemblies(solution);

            // Collect methods & call graph
            var methods = new Dictionary<IMethodSymbol, MethodInfo>(SymbolEqualityComparer.Default);
            var callsOut = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);

            foreach (var proj in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
            {
                var compilation = await proj.GetCompilationAsync();
                if (compilation is null) continue;

                foreach (var doc in proj.Documents)
                {
                    // skip non-source docs
                    if (string.IsNullOrWhiteSpace(doc.FilePath)) continue;

                    var tree = await doc.GetSyntaxTreeAsync();
                    if (tree is null) continue;
                    var model = await doc.GetSemanticModelAsync();
                    if (model is null) continue;

                    var root = await tree.GetRootAsync();
                    foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                    {
                        var symbol = model.GetDeclaredSymbol(decl) as IMethodSymbol;

                        if (symbol is null) continue;
                        if (!IsUserMethod(symbol, projectAssemblies)) continue;

                        if (!methods.ContainsKey(symbol))
                            methods[symbol] = new MethodInfo(doc.FilePath!, decl.Span, symbol);

                        foreach (var inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            var info = model.GetSymbolInfo(inv);
                            var target = (info.Symbol as IMethodSymbol)
                                         ?? (info.CandidateSymbols.FirstOrDefault() as IMethodSymbol);
                            if (target is null) continue;

                            // Normalize reduced/extension form to a canonical symbol
                            var canon = target.ReducedFrom ?? target.OriginalDefinition ?? target;

                            // Only keep edges to our own code
                            if (!IsUserMethod(canon, projectAssemblies)) continue;

                            callsOut.GetOrCreate(symbol).Add(canon);
                        }
                    }
                }
            }

            // Reverse edges
            var callsIn = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
            foreach (var (caller, callees) in callsOut)
            foreach (var callee in callees)
                callsIn.GetOrCreate(callee).Add(caller);

            // --- Emit markdown ---
            if (opt.PerFile)
            {
                var byFile = methods.GroupBy(kvp => kvp.Value.FilePath);
                int fileCount = 0, methodCount = 0;

                foreach (var group in byFile)
                {
                    
                    // ---
                    // tags:
                    //   - function
                    // ---
                    var mdPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(group.Key) + ".md");
                    var sb = new StringBuilder();
                    sb.AppendLine("---");
                    sb.AppendLine("tags:");
                    sb.AppendLine("  - file");
                    sb.AppendLine("---");
                    sb.AppendLine($"# {Path.GetFileName(group.Key)}");
                    sb.AppendLine();

                    foreach (var (method, info) in group.OrderBy(g => g.Key.Name))
                    {
                        sb.Append(RenderMethodSection(method, callsOut, callsIn));
                        methodCount++;
                    }

                    File.WriteAllText(mdPath, sb.ToString());
                    fileCount++;
                }

                Console.WriteLine($"Wrote {methodCount} methods into {fileCount} markdown files → {outDir}");
            }
            else // per-method
            {
                int fileCount = 0;
                foreach (var (method, info) in methods)
                {
                    var fileName = Sanitize($"{method.ContainingType?.Name}.{method.Name}.md");
                    var mdPath = Path.Combine(outDir, fileName);
                    File.WriteAllText(mdPath, RenderMethodNote(method, callsOut, callsIn, info.FilePath));
                    fileCount++;
                }

                Console.WriteLine($"Wrote {fileCount} method markdown files → {outDir}");
            }

            return 0;
        }

        // ------ Rendering helpers ------

        private static string RenderMethodSection(
            IMethodSymbol method,
            Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> callsOut,
            Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> callsIn)
        {
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine($"#### [[{method.Name}]]");
            sb.AppendLine("##### What it does:");
            var doc = GetMethodDocstring(method);
            if (!string.IsNullOrWhiteSpace(doc))
            {
                foreach (var line in doc.Split('\n'))
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

            sb.AppendLine("```csharp");
            sb.AppendLine(method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            sb.AppendLine("```");
            sb.AppendLine();

            if (callsOut.TryGetValue(method, out var outs) && outs.Any())
            {
                sb.AppendLine("**Calls →**");
                foreach (var callee in outs.OrderBy(m => m.Name))
                    sb.AppendLine($"- [[{callee.Name}]]");
                sb.AppendLine();
            }

            if (callsIn.TryGetValue(method, out var ins) && ins.Any())
            {
                sb.AppendLine("**Called-by ←**");
                foreach (var caller in ins.OrderBy(m => m.Name))
                    sb.AppendLine($"- [[{caller.Name}]]");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Standalone file per method (includes file context header)
        private static string RenderMethodNote(
            IMethodSymbol method,
            Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> callsOut,
            Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> callsIn,
            string sourcePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("tags:");
            sb.AppendLine("  - method");
            sb.AppendLine("---");
            sb.AppendLine($"# {method.ContainingType?.ToDisplayString()}::{method.Name}");
            sb.AppendLine($"**Path**: `{sourcePath}`");
            sb.AppendLine();

            sb.Append(RenderMethodSection(method, callsOut, callsIn));
            return sb.ToString();
        }

        // ------ MSBuild registration ------

        private static void EnsureMsbuildRegistered()
        {
            if (MSBuildLocator.IsRegistered) return;

            // Try Visual Studio first
            var vs = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();
            if (vs is not null)
            {
                MSBuildLocator.RegisterInstance(vs);
                return;
            }

            // Fallback to dotnet SDK
            string dotnetRoot =
                Environment.GetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR")
                ?? Environment.GetEnvironmentVariable("DOTNET_ROOT")
                ?? (Environment.Is64BitProcess ? @"C:\Program Files\dotnet" : @"C:\Program Files (x86)\dotnet");

            var sdkDir = Path.Combine(dotnetRoot, "sdk");
            if (!Directory.Exists(sdkDir))
                throw new InvalidOperationException(
                    $"dotnet SDK folder not found at '{sdkDir}'. Install .NET SDK or set DOTNET_ROOT.");

            var candidate = Directory.GetDirectories(sdkDir)
                .OrderByDescending(Path.GetFileName)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Microsoft.Build.dll")));

            if (candidate is null)
                throw new InvalidOperationException(
                    $"No SDK with Microsoft.Build.dll found under '{sdkDir}'. Reinstall/repair the .NET SDK.");

            MSBuildLocator.RegisterMSBuildPath(candidate);
        }

        // ------ CLI parsing ------

        private static bool TryParseArgs(string[] args, out Options opt, out string error)
        {
            string? sln = null;
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "_obsidian");
            bool perFile = false, perMethod = false;

            var queue = new Queue<string>(args);
            while (queue.Count > 0)
            {
                var tok = queue.Dequeue();
                switch (tok)
                {
                    case "--per-file":
                        perFile = true; break;
                    case "--per-method":
                        perMethod = true; break;
                    case "--out":
                        if (queue.Count == 0)
                        {
                            error = "--out requires a value";
                            opt = default!;
                            return false;
                        }

                        outDir = queue.Dequeue();
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        error = "";
                        opt = default!;
                        return false;
                    default:
                        if (tok.StartsWith("-", StringComparison.Ordinal))
                        {
                            error = $"Unknown option: {tok}";
                            opt = default!;
                            return false;
                        }

                        // positional = solution path
                        sln ??= tok;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(sln))
            {
                error = "Missing required <solution.sln> argument.";
                opt = default!;
                return false;
            }

            if (perFile == perMethod) // either both false or both true
            {
                error = "Specify exactly one of --per-file OR --per-method.";
                opt = default!;
                return false;
            }

            opt = new Options(sln, outDir, perFile, perMethod);
            error = "";
            return true;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"
Usage:
  Code2Obsidian <solution.sln> --per-file  [--out <folder>]
  Code2Obsidian <solution.sln> --per-method [--out <folder>]

Options:
  --per-file      Emit one Markdown per source .cs file (sections per method).
  --per-method    Emit one Markdown per method.
  --out <folder>  Output directory (default: ./_obsidian)
  -h, --help      Show this help.
");
        }

        // ------ helpers ------

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static HashSet<TValue> GetOrCreate<TKey, TValue>(
            this IDictionary<TKey, HashSet<TValue>> dict, TKey key) where TKey : notnull
        {
            if (!dict.TryGetValue(key, out var set))
            {
                set = new HashSet<TValue>(EqualityComparer<TValue>.Default);
                dict[key] = set;
            }

            return set;
        }


// Cache the set of assemblies that belong to the loaded solution
        private static HashSet<IAssemblySymbol> GetProjectAssemblies(Solution solution)
        {
            var set = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            foreach (var p in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
            {
                var comp = p.GetCompilationAsync().GetAwaiter().GetResult();
                if (comp?.Assembly != null) set.Add(comp.Assembly);
            }

            return set;
        }

// Decide if a method is "ours" (i.e., defined in source within this solution)
        private static bool IsUserMethod(IMethodSymbol m, HashSet<IAssemblySymbol> projectAssemblies)
        {
            if (m == null) return false;

            // Must come from one of our project assemblies
            if (!projectAssemblies.Contains(m.ContainingAssembly)) return false;

            // Must have source (exclude metadata-only)
            if (m.Locations.All(l => !l.IsInSource)) return false;

            // Skip generated/implicit bits
            if (m.IsImplicitlyDeclared) return false;

            // Optional: skip property/event accessors and operators
            if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
                or MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator)
                return false;

            return true;
        }

        // --- New: Extract and format XML doc comments for methods ---
        private static string? GetMethodDocstring(IMethodSymbol method)
        {
            try
            {
                var xml = method.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: default);
                if (string.IsNullOrWhiteSpace(xml)) return null;

                // Wrap to ensure single root for parsing
                var root = XElement.Parse($"<root>{xml}</root>");

                var sb = new StringBuilder();

                var summary = root.Element("summary")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(summary))
                    sb.AppendLine(NormalizeSpaces(summary));

                var parameters = root.Elements("param").ToList();
                if (parameters.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Parameters:");
                    foreach (var p in parameters)
                    {
                        var name = p.Attribute("name")?.Value ?? "";
                        var text = NormalizeSpaces(p.Value?.Trim() ?? "");
                        sb.AppendLine($"- {name}: {text}");
                    }
                }

                var returns = root.Element("returns")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(returns))
                {
                    sb.AppendLine();
                    sb.AppendLine($"Returns: {NormalizeSpaces(returns)}");
                }

                var remarks = root.Element("remarks")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(remarks))
                {
                    sb.AppendLine();
                    sb.AppendLine(NormalizeSpaces(remarks));
                }

                var result = sb.ToString().Trim();
                return string.IsNullOrWhiteSpace(result) ? xml.Trim() : result;
            }
            catch
            {
                // If the XML isn't well-formed, just return the raw text
                var fallback = method.GetDocumentationCommentXml(expandIncludes: false, cancellationToken: default);
                return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
            }
        }

        private static string NormalizeSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var b = new StringBuilder(s.Length);
            bool ws = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!ws) { b.Append(' '); ws = true; }
                }
                else
                {
                    b.Append(ch);
                    ws = false;
                }
            }
            return b.ToString().Trim();
        }
    }
}