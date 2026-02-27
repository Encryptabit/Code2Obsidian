using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Code2Obsidian.Loading;

/// <summary>
/// Handles MSBuild registration and solution loading.
/// Ports EnsureMsbuildRegistered() and GetProjectAssemblies() from the original Program.cs,
/// but returns assembly name strings instead of IAssemblySymbol references.
/// </summary>
public sealed class SolutionLoader
{
    private readonly List<string> _diagnostics = new();
    private readonly HashSet<string> _seenDiagnostics = new(StringComparer.Ordinal);
    private int _suppressedPackageDiagnosticCount;

    /// <summary>
    /// Workspace diagnostics collected during solution loading.
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>
    /// Count of noisy package diagnostics suppressed from WorkspaceFailed output.
    /// </summary>
    public int SuppressedPackageDiagnosticCount => _suppressedPackageDiagnosticCount;

    /// <summary>
    /// Loads a solution from the given path and returns an AnalysisContext.
    /// Registers MSBuild if not already registered, creates a workspace,
    /// opens the solution, and collects project assembly names.
    /// </summary>
    public async Task<AnalysisContext> LoadAsync(string solutionPath, CancellationToken ct)
    {
        EnsureMsbuildRegistered();

        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            var normalized = NormalizeDiagnosticMessage(e.Diagnostic.Message);

            if (ShouldSuppressPackageDiagnostic(normalized))
            {
                _suppressedPackageDiagnosticCount++;
                return;
            }

            if (_seenDiagnostics.Add(normalized))
                _diagnostics.Add(normalized);
        };

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        var assemblyNames = await GetProjectAssemblyNamesAsync(solution, ct);

        return new AnalysisContext(workspace, solution, assemblyNames);
    }

    /// <summary>
    /// Ensures MSBuild is registered. Tries Visual Studio first, then falls back to dotnet SDK.
    /// Ported from Program.cs lines 242-277.
    /// </summary>
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
            ?? (Environment.Is64BitProcess
                ? @"C:\Program Files\dotnet"
                : @"C:\Program Files (x86)\dotnet");

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

    /// <summary>
    /// Collects assembly name strings from all C# projects in the solution.
    /// Uses name strings instead of IAssemblySymbol references for robustness across compilations.
    /// </summary>
    private static async Task<IReadOnlySet<string>> GetProjectAssemblyNamesAsync(
        Solution solution, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation?.Assembly is not null)
            {
                names.Add(compilation.Assembly.Name);
            }
        }

        return names;
    }

    private static string NormalizeDiagnosticMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "MSBuild reported an empty diagnostic.";

        var singleLine = string.Join(
            " ",
            message.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

        const string prefix = "Msbuild failed when processing the file '";
        const string middle = "' with message: ";

        if (singleLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var fileStart = prefix.Length;
            var fileEnd = singleLine.IndexOf('\'', fileStart);
            if (fileEnd > fileStart)
            {
                var middleStart = singleLine.IndexOf(middle, fileEnd + 1, StringComparison.OrdinalIgnoreCase);
                if (middleStart > fileEnd)
                {
                    var projectPath = singleLine[fileStart..fileEnd];
                    var details = singleLine[(middleStart + middle.Length)..];
                    return $"MSBuild warning in '{projectPath}': {details}";
                }
            }
        }

        return singleLine;
    }

    private static bool ShouldSuppressPackageDiagnostic(string message)
    {
        return message.Contains("Detected package version outside of dependency constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("was restored using '.NETFramework,Version", StringComparison.OrdinalIgnoreCase)
            || message.Contains("has a known critical severity vulnerability", StringComparison.OrdinalIgnoreCase)
            || message.Contains("has a known high severity vulnerability", StringComparison.OrdinalIgnoreCase)
            || message.Contains("has a known moderate severity vulnerability", StringComparison.OrdinalIgnoreCase)
            || message.Contains("has a known low severity vulnerability", StringComparison.OrdinalIgnoreCase);
    }
}
