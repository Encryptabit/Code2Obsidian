using Code2Obsidian.Analysis.Models;
using Code2Obsidian.Loading;
using Code2Obsidian.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeInfo = Code2Obsidian.Analysis.Models.TypeInfo;

namespace Code2Obsidian.Analysis.Analyzers;

/// <summary>
/// Extracts class, interface, record, and struct metadata from the Roslyn compilation.
/// Populates TypeInfo domain models and builds the implementor reverse index.
/// Runs as a separate IAnalyzer after MethodAnalyzer so that method data is already available.
/// </summary>
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

        int projectIndex = 0;
        int totalFiles = csharpProjects.Sum(p => p.Documents.Count());
        int fileIndex = 0;

        foreach (var project in csharpProjects)
        {
            ct.ThrowIfCancellationRequested();

            projectIndex++;
            progress?.Report(new PipelineProgress(
                PipelineStage.Analyzing,
                $"Analyzing types in {project.Name}... ({projectIndex}/{csharpProjects.Count})",
                fileIndex,
                totalFiles));

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();

                fileIndex++;

                if (string.IsNullOrWhiteSpace(document.FilePath)) continue;

                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;

                var model = await document.GetSemanticModelAsync(ct);
                if (model is null) continue;

                progress?.Report(new PipelineProgress(
                    PipelineStage.Analyzing,
                    $"Analyzing types in {project.Name}/{Path.GetFileName(document.FilePath)}",
                    fileIndex,
                    totalFiles));

                var root = await tree.GetRootAsync(ct);

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (symbol is null) continue;
                    if (!AnalysisHelpers.IsUserType(symbol, context.ProjectAssemblyNames)) continue;

                    var typeInfo = ExtractTypeInfo(symbol, document.FilePath!, context.ProjectAssemblyNames, project.Name);
                    builder.AddType(typeInfo);

                    // Register implementors: only concrete types (class, record, struct) —
                    // interfaces should not appear as implementors (STRC-04).
                    // Uses AllInterfaces so base interfaces also list this type as implementor.
                    if (symbol.TypeKind != TypeKind.Interface)
                    {
                        foreach (var iface in symbol.AllInterfaces)
                        {
                            if (AnalysisHelpers.IsUserType(iface, context.ProjectAssemblyNames))
                            {
                                builder.RegisterImplementor(
                                    TypeId.FromSymbol(iface),
                                    TypeId.FromSymbol(symbol));
                            }
                        }
                    }
                }
            }
        }
    }

    private static TypeInfo ExtractTypeInfo(
        INamedTypeSymbol symbol,
        string documentFilePath,
        IReadOnlySet<string> projectAssemblyNames,
        string projectName)
    {
        var id = TypeId.FromSymbol(symbol);
        var name = symbol.Name;
        var fullName = symbol.ToDisplayString();
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        var kind = MapTypeKind(symbol);
        var filePath = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath
                       ?? documentFilePath;

        // Base class: skip System.Object
        string? baseClassFullName = null;
        string? baseClassName = null;
        if (symbol.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
        {
            baseClassFullName = baseType.ToDisplayString();
            baseClassName = baseType.Name;
        }

        // Directly declared interfaces (Interfaces, not AllInterfaces) for display
        var interfaceFullNames = symbol.Interfaces
            .Select(i => i.ToDisplayString())
            .ToList();
        var interfaceNames = symbol.Interfaces
            .Select(i => i.Name)
            .ToList();

        // Properties: non-implicit, sorted by declaration order
        var properties = symbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsImplicitlyDeclared)
            .OrderBy(AnalysisHelpers.GetDeclarationOrder)
            .Select(p => new PropertyFieldInfo(
                Name: p.Name,
                TypeName: p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                AccessModifier: AnalysisHelpers.AccessibilityToString(p.DeclaredAccessibility),
                IsStatic: p.IsStatic))
            .ToList();

        // Fields: non-implicit, exclude backing fields, sorted by declaration order
        var fields = symbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsImplicitlyDeclared && f.AssociatedSymbol is null)
            .OrderBy(AnalysisHelpers.GetDeclarationOrder)
            .Select(f => new PropertyFieldInfo(
                Name: f.Name,
                TypeName: f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                AccessModifier: AnalysisHelpers.AccessibilityToString(f.DeclaredAccessibility),
                IsStatic: f.IsStatic))
            .ToList();

        // Constructors: non-implicit, sorted by declaration order
        var constructors = symbol.InstanceConstructors
            .Where(c => !c.IsImplicitlyDeclared)
            .OrderBy(AnalysisHelpers.GetDeclarationOrder)
            .Select(ctor => new Models.ConstructorInfo(
                DisplaySignature: ctor.ToDisplayString(AnalysisHelpers.RichSignatureFormat),
                AccessModifier: AnalysisHelpers.AccessibilityToString(ctor.DeclaredAccessibility),
                Parameters: ctor.Parameters.Select(p => new ParameterInfo(
                    Name: p.Name,
                    TypeName: p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    TypeNoteFullName: AnalysisHelpers.IsUserType(p.Type as INamedTypeSymbol, projectAssemblyNames)
                        ? (p.Type as INamedTypeSymbol)!.ToDisplayString()
                        : null
                )).ToList()))
            .ToList();

        // Method IDs: user methods sorted by declaration order
        var methodIds = symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => AnalysisHelpers.IsUserMethod(m, projectAssemblyNames))
            .OrderBy(AnalysisHelpers.GetDeclarationOrder)
            .Select(MethodId.FromSymbol)
            .ToList();

        var docComment = AnalysisHelpers.GetTypeDocComment(symbol);

        return new TypeInfo(
            Id: id,
            Name: name,
            FullName: fullName,
            Namespace: ns,
            Kind: kind,
            FilePath: filePath,
            BaseClassFullName: baseClassFullName,
            BaseClassName: baseClassName,
            InterfaceFullNames: interfaceFullNames,
            InterfaceNames: interfaceNames,
            Properties: properties,
            Fields: fields,
            Constructors: constructors,
            MethodIds: methodIds,
            DocComment: docComment,
            ProjectName: projectName,
            AccessModifier: AnalysisHelpers.AccessibilityToString(symbol.DeclaredAccessibility));
    }

    private static TypeKindInfo MapTypeKind(INamedTypeSymbol symbol)
    {
        if (symbol.IsRecord) return TypeKindInfo.Record;

        return symbol.TypeKind switch
        {
            TypeKind.Interface => TypeKindInfo.Interface,
            TypeKind.Struct => TypeKindInfo.Struct,
            _ => TypeKindInfo.Class
        };
    }
}
