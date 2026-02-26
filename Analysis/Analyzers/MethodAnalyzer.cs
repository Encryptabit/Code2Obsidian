using Code2Obsidian.Analysis.Models;
using Code2Obsidian.Loading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Code2Obsidian.Analysis.Analyzers;

/// <summary>
/// Extracts methods and call graph edges from the Roslyn compilation in a single pass.
/// Ported from Program.cs lines 68-117: method extraction, call graph construction,
/// and reverse edge computation.
///
/// Design decision: MethodAnalyzer and CallGraphAnalyzer are merged into this single class
/// because they share the same iteration over syntax trees. A separate CallGraphAnalyzer
/// would require a redundant second pass over all documents. The CallGraph.AddEdge() method
/// automatically maintains reverse edges, so no separate reverse-edge computation step is needed.
/// </summary>
public sealed class MethodAnalyzer : IAnalyzer
{
    public string Name => "MethodAnalyzer";

    public async Task AnalyzeAsync(AnalysisContext context, AnalysisResultBuilder builder, CancellationToken ct)
    {
        foreach (var project in context.Solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp))
        {
            ct.ThrowIfCancellationRequested();

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            builder.IncrementProjectCount();

            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();

                // Skip non-source docs
                if (string.IsNullOrWhiteSpace(document.FilePath)) continue;

                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;

                var model = await document.GetSemanticModelAsync(ct);
                if (model is null) continue;

                builder.IncrementFileCount();

                var root = await tree.GetRootAsync(ct);

                foreach (var declaration in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(declaration) as IMethodSymbol;
                    if (symbol is null) continue;
                    if (!AnalysisHelpers.IsUserMethod(symbol, context.ProjectAssemblyNames)) continue;

                    // Convert Roslyn symbol to domain model
                    var methodId = MethodId.FromSymbol(symbol);
                    var containingType = symbol.ContainingType;
                    var typeId = containingType is not null
                        ? TypeId.FromSymbol(containingType)
                        : new TypeId("global");

                    var methodInfo = new Models.MethodInfo(
                        Id: methodId,
                        Name: symbol.Name,
                        ContainingTypeName: containingType?.ToDisplayString() ?? "global",
                        ContainingTypeId: typeId,
                        FilePath: document.FilePath!,
                        DisplaySignature: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        DocComment: AnalysisHelpers.GetMethodDocstring(symbol)
                    );

                    builder.AddMethod(methodInfo);

                    // Extract call graph edges from this method's body
                    foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var info = model.GetSymbolInfo(invocation);
                        var target = (info.Symbol as IMethodSymbol)
                                     ?? (info.CandidateSymbols.FirstOrDefault() as IMethodSymbol);
                        if (target is null) continue;

                        // Normalize reduced/extension form to a canonical symbol
                        var canon = target.ReducedFrom ?? target.OriginalDefinition ?? target;

                        // Only keep edges to our own code
                        if (!AnalysisHelpers.IsUserMethod(canon, context.ProjectAssemblyNames)) continue;

                        var calleeId = MethodId.FromSymbol(canon);
                        builder.AddCallEdge(methodId, calleeId);
                    }
                }
            }
        }
    }
}
