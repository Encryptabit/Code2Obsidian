using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Code2Obsidian.Enrichment.Config;

/// <summary>
/// Optional Serena MCP configuration for Codex-backed enrichment runs.
/// Defaults are tuned for headless batch analysis: no dashboard, no onboarding,
/// and project auto-detection from the working directory.
/// </summary>
public sealed record SerenaMcpConfig(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("args")] string[]? Args = null,
    [property: JsonPropertyName("projectFromCwd")] bool ProjectFromCwd = true,
    [property: JsonPropertyName("openWebDashboard")] bool OpenWebDashboard = false,
    [property: JsonPropertyName("skipOnboarding")] bool SkipOnboarding = false,
    [property: JsonPropertyName("context")] string? Context = "codex",
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null,
    [property: JsonPropertyName("startupTimeoutSec")] int? StartupTimeoutSec = null,
    [property: JsonPropertyName("toolTimeoutSec")] int? ToolTimeoutSec = null
);

public static class SerenaMcpSettings
{
    private const string UvInstallScriptUrl = "https://astral.sh/uv/install.sh";
    private const string UvInstallScriptUrlWindows = "https://astral.sh/uv/install.ps1";
    private const string DefaultUvxCommand = "uvx";
    private const string DefaultPackageSource = "git+https://github.com/oraios/serena";
    private const string DirectExecutable = "serena";
    private const string ServerName = "serena";
    private static readonly string[] DefaultModes = ["interactive", "editing"];

    public static bool IsEnabled(LlmConfig config) => config.Serena?.Enabled == true;

    public static SerenaMcpConfig? Normalize(SerenaMcpConfig? config)
    {
        if (config is null)
            return null;

        var command = string.IsNullOrWhiteSpace(config.Command)
            ? DefaultUvxCommand
            : config.Command.Trim();

        string[]? args = null;
        if (config.Args is { Length: > 0 })
        {
            args = config.Args
                .Where(arg => !string.IsNullOrWhiteSpace(arg))
                .Select(arg => arg.Trim())
                .ToArray();

            if (args.Length == 0)
                args = null;
        }

        Dictionary<string, string>? env = null;
        if (config.Env is { Count: > 0 })
        {
            env = config.Env
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                .ToDictionary(
                    entry => entry.Key.Trim(),
                    entry => entry.Value.Trim(),
                    StringComparer.Ordinal);

            if (env.Count == 0)
                env = null;
        }

        var context = string.IsNullOrWhiteSpace(config.Context)
            ? "codex"
            : config.Context.Trim();

        return config with
        {
            Command = command,
            Args = args,
            Context = context,
            Env = env
        };
    }

    public static IReadOnlyList<string> BuildCodexConfigOverrides(SerenaMcpConfig? config)
    {
        var normalized = Normalize(config);
        if (normalized?.Enabled != true)
            return Array.Empty<string>();

        var overrides = new List<string>
        {
            $"mcp_servers.{ServerName}.command={ToTomlString(normalized.Command!)}",
            $"mcp_servers.{ServerName}.args={ToTomlArray(ResolveArgs(normalized))}"
        };

        if (normalized.StartupTimeoutSec is > 0)
            overrides.Add($"mcp_servers.{ServerName}.startup_timeout_sec={normalized.StartupTimeoutSec.Value}");

        if (normalized.ToolTimeoutSec is > 0)
            overrides.Add($"mcp_servers.{ServerName}.tool_timeout_sec={normalized.ToolTimeoutSec.Value}");

        if (normalized.Env is { Count: > 0 })
        {
            foreach (var (key, value) in normalized.Env.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                overrides.Add($"mcp_servers.{ServerName}.env.{key}={ToTomlString(value)}");
            }
        }

        return overrides;
    }

    public static IReadOnlyList<string> ResolveArgs(SerenaMcpConfig config)
    {
        var normalized = Normalize(config) ?? throw new ArgumentNullException(nameof(config));
        if (normalized.Args is { Length: > 0 })
            return normalized.Args;

        var directExecutable = !string.Equals(normalized.Command, DefaultUvxCommand, StringComparison.OrdinalIgnoreCase);
        var args = new List<string>();

        if (!directExecutable)
        {
            args.Add("--from");
            args.Add(DefaultPackageSource);
            args.Add(DirectExecutable);
        }

        args.Add("start-mcp-server");

        if (!string.IsNullOrWhiteSpace(normalized.Context))
        {
            args.Add("--context");
            args.Add(normalized.Context);
        }

        if (normalized.ProjectFromCwd)
            args.Add("--project-from-cwd");

        if (normalized.SkipOnboarding)
        {
            foreach (var mode in DefaultModes)
            {
                args.Add("--mode");
                args.Add(mode);
            }

            args.Add("--mode");
            args.Add("no-onboarding");
        }

        args.Add("--open-web-dashboard");
        args.Add(normalized.OpenWebDashboard ? "true" : "false");

        return args;
    }

    public static async Task<(SerenaMcpConfig? Config, string? InstalledMessage)> EnsureCommandAvailableAsync(
        SerenaMcpConfig? config,
        CancellationToken ct)
    {
        var normalized = Normalize(config);
        if (normalized?.Enabled != true)
            return (normalized, null);

        var command = normalized.Command!;
        if (!string.Equals(command, DefaultUvxCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (IsCommandAvailable(command, normalized.Env))
                return (normalized, null);

            throw new InvalidOperationException(
                $"Serena MCP command '{command}' was not found. " +
                "Install the command and ensure it is on PATH for the Code2Obsidian process, " +
                "or set 'serena.command' to an absolute executable path in code2obsidian.llm.json.");
        }

        var installDir = GetManagedUvInstallDirectory();
        var uvPath = GetManagedUvBinaryPath(installDir, "uv");
        if (!File.Exists(uvPath))
            await InstallUvAsync(installDir, ct);

        if (!File.Exists(uvPath))
        {
            throw new InvalidOperationException(
                $"Automatic uv bootstrap completed without producing '{uvPath}'. " +
                "Install uv manually or set 'serena.command' to an absolute executable path.");
        }

        var toolBinDir = GetManagedUvToolBinDirectory(installDir);
        var toolDir = GetManagedUvToolDirectory(installDir);
        var serenaPath = GetManagedToolBinaryPath(toolBinDir, "serena");
        var didInstallSerena = !File.Exists(serenaPath);
        if (didInstallSerena)
            await InstallSerenaToolAsync(uvPath, toolDir, toolBinDir, ct);

        if (!File.Exists(serenaPath))
        {
            throw new InvalidOperationException(
                $"Automatic Serena tool bootstrap completed without producing '{serenaPath}'. " +
                "Install Serena manually or set 'serena.command' to an absolute executable path.");
        }

        return (
            normalized with { Command = serenaPath },
            didInstallSerena
                ? $"Installed uv and Serena automatically for MCP at {serenaPath}"
                : $"Prepared Serena MCP command at {serenaPath}");
    }

    private static bool IsCommandAvailable(string command, IReadOnlyDictionary<string, string>? env)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(command);
        }

        var pathValue = GetEnvironmentValue("PATH", env);
        if (string.IsNullOrWhiteSpace(pathValue))
            return false;

        var pathEntries = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (OperatingSystem.IsWindows())
        {
            var pathExtValue = GetEnvironmentValue("PATHEXT", env) ?? ".EXE;.BAT;.CMD;.COM";
            var suffixes = pathExtValue
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var entry in pathEntries)
            {
                var basePath = Path.Combine(entry, command);
                if (File.Exists(basePath))
                    return true;

                foreach (var suffix in suffixes)
                {
                    if (File.Exists(basePath + suffix))
                        return true;
                }
            }

            return false;
        }

        foreach (var entry in pathEntries)
        {
            if (File.Exists(Path.Combine(entry, command)))
                return true;
        }

        return false;
    }

    private static string? GetEnvironmentValue(string key, IReadOnlyDictionary<string, string>? env)
    {
        if (env is not null && env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;

        return Environment.GetEnvironmentVariable(key);
    }

    private static string GetManagedUvInstallDirectory()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }
        else
        {
            baseDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(baseDir, "code2obsidian", "tools", "uv");
    }

    private static string GetManagedUvBinaryPath(string installDir, string commandName)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{commandName}.exe" : commandName;
        return Path.Combine(installDir, fileName);
    }

    private static string GetManagedUvToolDirectory(string installDir) =>
        Path.Combine(installDir, "tools");

    private static string GetManagedUvToolBinDirectory(string installDir) =>
        Path.Combine(installDir, "bin");

    private static string GetManagedToolBinaryPath(string toolBinDir, string commandName)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{commandName}.exe" : commandName;
        return Path.Combine(toolBinDir, fileName);
    }

    private static async Task InstallUvAsync(string installDir, CancellationToken ct)
    {
        Directory.CreateDirectory(installDir);

        var scriptUrl = OperatingSystem.IsWindows() ? UvInstallScriptUrlWindows : UvInstallScriptUrl;
        var scriptExtension = OperatingSystem.IsWindows() ? ".ps1" : ".sh";
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"code2obsidian-uv-install-{Guid.NewGuid():N}{scriptExtension}");

        try
        {
            using var http = new HttpClient();
            var script = await http.GetStringAsync(scriptUrl, ct);
            await File.WriteAllTextAsync(scriptPath, script, ct);

            var psi = BuildUvInstallerStartInfo(scriptPath, installDir);
            using var process = new Process
            {
                StartInfo = psi
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                throw new InvalidOperationException(
                    $"Automatic uv bootstrap failed with exit code {process.ExitCode}: {details}");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Failed to download the official uv installer from '{scriptUrl}': {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static async Task InstallSerenaToolAsync(
        string uvPath,
        string toolDir,
        string toolBinDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(toolDir);
        Directory.CreateDirectory(toolBinDir);

        var psi = new ProcessStartInfo
        {
            FileName = uvPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("tool");
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("--quiet");
        psi.ArgumentList.Add("--no-progress");
        psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add("--from");
        psi.ArgumentList.Add(DefaultPackageSource);
        psi.ArgumentList.Add("serena-agent");
        psi.Environment["UV_TOOL_DIR"] = toolDir;
        psi.Environment["UV_TOOL_BIN_DIR"] = toolBinDir;
        psi.Environment["UV_NO_MODIFY_PATH"] = "1";

        using var process = new Process
        {
            StartInfo = psi
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            throw new InvalidOperationException(
                $"Automatic Serena tool install failed with exit code {process.ExitCode}: {details}");
        }
    }

    private static ProcessStartInfo BuildUvInstallerStartInfo(string scriptPath, string installDir)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            var powershell = IsCommandAvailable("pwsh", null) ? "pwsh" : "powershell";
            psi.FileName = powershell;
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add(scriptPath);
        }

        psi.Environment["UV_UNMANAGED_INSTALL"] = installDir;
        psi.Environment["UV_NO_MODIFY_PATH"] = "1";
        return psi;
    }

    private static string ToTomlArray(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(ToTomlString)) + "]";

    private static string ToTomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
