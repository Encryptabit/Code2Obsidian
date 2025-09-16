
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

class Program
{
    static async Task<int> Main(string[] args)
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        var slnPath = args.Length > 0 ? args[0] : FindSolutionUpwards();
        var outDir  = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "_obsidian");

        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Solution: {slnPath}");
        Console.WriteLine($"Output  : {outDir}");

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(slnPath);

        // map method → info
        var methods = new Dictionary<IMethodSymbol, MethodInfo>(SymbolEqualityComparer.Default);
        var callsOut = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);

        foreach (var proj in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            var compilation = await proj.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var doc in proj.Documents)
            {
                var tree = await doc.GetSyntaxTreeAsync();
                if (tree is null) continue;
                var model = await doc.GetSemanticModelAsync();
                if (model is null) continue;

                var root = await tree.GetRootAsync();

                foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(decl) as IMethodSymbol;
                    if (symbol is null) continue;

                    if (!methods.ContainsKey(symbol))
                        methods[symbol] = new MethodInfo(doc.FilePath, decl.Span, symbol);

                    foreach (var inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var target = model.GetSymbolInfo(inv).Symbol as IMethodSymbol
                                     ?? (model.GetSymbolInfo(inv).CandidateSymbols.FirstOrDefault() as IMethodSymbol);
                        if (target is null) continue;
                        callsOut.GetOrCreate(symbol).Add(target.OriginalDefinition ?? target);
                    }
                }
            }
        }

        // reverse edges
        var callsIn = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var (caller, callees) in callsOut)
            foreach (var callee in callees)
                callsIn.GetOrCreate(callee).Add(caller);

        // group by source file
        var byFile = methods
            .GroupBy(kvp => kvp.Value.FilePath)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (filePath, methodPairs) in byFile)
        {
            var mdPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(filePath) + ".md");
            var sb = new StringBuilder();

            sb.AppendLine($"# {Path.GetFileName(filePath)}");
            sb.AppendLine();

            foreach (var (method, info) in methodPairs.OrderBy(p => p.Key.Name))
            {
                sb.AppendLine($"#### [[{method.Name}]]");
                sb.AppendLine("##### What it does:");
                sb.AppendLine("- _TODO: Plain-English walkthrough._");
                sb.AppendLine();
                sb.AppendLine("##### Improvements:");
                sb.AppendLine("- _TODO: Suggested optimizations._");
                sb.AppendLine();

                sb.AppendLine("```csharp");
                sb.AppendLine(method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                sb.AppendLine("```");
                sb.AppendLine();

                // Calls →
                if (callsOut.TryGetValue(method, out var outs) && outs.Any())
                {
                    sb.AppendLine("**Calls →**");
                    foreach (var callee in outs.OrderBy(m => m.Name))
                        sb.AppendLine($"- [[{callee.Name}]]");
                    sb.AppendLine();
                }

                // Called-by ←
                if (callsIn.TryGetValue(method, out var ins) && ins.Any())
                {
                    sb.AppendLine("**Called-by ←**");
                    foreach (var caller in ins.OrderBy(m => m.Name))
                        sb.AppendLine($"- [[{caller.Name}]]");
                    sb.AppendLine();
                }
            }

            File.WriteAllText(mdPath, sb.ToString());
        }

        Console.WriteLine($"Wrote {methods.Count} methods into {byFile.Count} markdown files → {outDir}");
        return 0;
    }

    static string FindSolutionUpwards()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            var sln = Directory.GetFiles(dir, "*.sln").FirstOrDefault();
            if (sln != null) return sln;
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException("No .sln found in parent folders.");
    }

    record MethodInfo(string FilePath, Microsoft.CodeAnalysis.Text.TextSpan Span, IMethodSymbol Symbol);
}

static class DictExt
{
    public static HashSet<TValue> GetOrCreate<TKey, TValue>(
        this IDictionary<TKey, HashSet<TValue>> dict, TKey key) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var set))
            dict[key] = set = new HashSet<TValue>(EqualityComparer<TValue>.Default);
        return set;
    }
}
