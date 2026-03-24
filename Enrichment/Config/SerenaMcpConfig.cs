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
        string? wslDistro,
        CancellationToken ct)
    {
        var normalized = Normalize(config);
        if (normalized?.Enabled != true)
            return (normalized, null);

        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(wslDistro))
            return await EnsureCommandAvailableInWslAsync(normalized, wslDistro.Trim(), ct);

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

    private static async Task<(SerenaMcpConfig Config, string? InstalledMessage)> EnsureCommandAvailableInWslAsync(
        SerenaMcpConfig config,
        string wslDistro,
        CancellationToken ct)
    {
        var command = config.Command!;
        if (!string.Equals(command, DefaultUvxCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (await IsCommandAvailableInWslAsync(command, wslDistro, config.Env, ct))
                return (config, null);

            throw new InvalidOperationException(
                $"Serena MCP command '{command}' was not found inside WSL distro '{wslDistro}'. " +
                "Install the command in that distro, ensure it is on PATH for Codex app-server, " +
                "or set 'serena.command' to a WSL-accessible executable path in code2obsidian.llm.json.");
        }

        var result = await RunWslBashAsync(
            BuildManagedSerenaBootstrapScript(),
            wslDistro,
            env: null,
            ct);

        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.Stderr)
                ? result.Stdout.Trim()
                : result.Stderr.Trim();
            throw new InvalidOperationException(
                $"Automatic Serena bootstrap in WSL distro '{wslDistro}' failed with exit code {result.ExitCode}: {details}");
        }

        var lines = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException(
                $"Automatic Serena bootstrap in WSL distro '{wslDistro}' did not return the installed command path.");
        }

        var serenaPath = lines[0];
        var installed = lines.Any(line => string.Equals(line, "installed", StringComparison.OrdinalIgnoreCase));

        return (
            config with { Command = serenaPath },
            installed
                ? $"Installed uv and Serena automatically in WSL distro '{wslDistro}' at {serenaPath}"
                : $"Prepared Serena MCP command in WSL distro '{wslDistro}' at {serenaPath}");
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

    private static async Task<bool> IsCommandAvailableInWslAsync(
        string command,
        string wslDistro,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken ct)
    {
        var result = await RunWslBashAsync(
            BuildCommandAvailabilityScript(command),
            wslDistro,
            env,
            ct);
        return result.ExitCode == 0;
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

    private static string BuildManagedSerenaBootstrapScript() => """
        set -e

        INSTALL_DIR="$HOME/.local/share/code2obsidian/tools/uv"
        UV_BIN="$INSTALL_DIR/uv"
        TOOL_DIR="$INSTALL_DIR/tools"
        TOOL_BIN_DIR="$INSTALL_DIR/bin"
        SERENA_BIN="$TOOL_BIN_DIR/serena"
        INSTALLED=0

        if [ ! -x "$UV_BIN" ]; then
          INSTALLED=1
          if command -v curl >/dev/null 2>&1; then
            curl -LsSf https://astral.sh/uv/install.sh | env UV_UNMANAGED_INSTALL="$INSTALL_DIR" UV_NO_MODIFY_PATH=1 sh
          elif command -v wget >/dev/null 2>&1; then
            wget -qO- https://astral.sh/uv/install.sh | env UV_UNMANAGED_INSTALL="$INSTALL_DIR" UV_NO_MODIFY_PATH=1 sh
          else
            echo "Neither curl nor wget is available in WSL to install uv." >&2
            exit 127
          fi
        fi

        mkdir -p "$TOOL_DIR" "$TOOL_BIN_DIR"

        if [ ! -x "$SERENA_BIN" ]; then
          INSTALLED=1
          UV_TOOL_DIR="$TOOL_DIR" \
          UV_TOOL_BIN_DIR="$TOOL_BIN_DIR" \
          UV_NO_MODIFY_PATH=1 \
          "$UV_BIN" tool install --quiet --no-progress --force --from git+https://github.com/oraios/serena serena-agent
        fi

        if [ ! -x "$SERENA_BIN" ]; then
          echo "Automatic Serena tool bootstrap completed without producing '$SERENA_BIN'." >&2
          exit 1
        fi

        printf '%s\n' "$SERENA_BIN"
        if [ "$INSTALLED" -eq 1 ]; then
          printf 'installed\n'
        else
          printf 'prepared\n'
        fi
        """;

    private static string BuildCommandAvailabilityScript(string command)
    {
        var quotedCommand = BashSingleQuote(command);
        if (command.Contains('/') || command.Contains('\\') || command.Contains(':'))
        {
            return $"[ -e {quotedCommand} ]";
        }

        return $"command -v {quotedCommand} >/dev/null 2>&1";
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWslBashAsync(
        string script,
        string wslDistro,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken ct)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"code2obsidian-serena-{Guid.NewGuid():N}.sh");
        var scriptBody = PrependWslExports(script, env).Replace("\r\n", "\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(scriptPath, scriptBody, ct);

        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(wslDistro))
        {
            psi.ArgumentList.Add("--distribution");
            psi.ArgumentList.Add(wslDistro);
        }

        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add(ToWslPath(scriptPath));

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, await stdoutTask, await stderrTask);
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

    private static string PrependWslExports(string script, IReadOnlyDictionary<string, string>? env)
    {
        if (env is null || env.Count == 0)
            return script;

        var exports = env
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry => $"export {entry.Key.Trim()}={BashSingleQuote(entry.Value)}");

        return string.Join("\n", exports) + "\n\n" + script;
    }

    private static string BashSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string ToWslPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length >= 3 && fullPath[1] == ':' &&
            (fullPath[2] == '\\' || fullPath[2] == '/'))
        {
            var drive = char.ToLowerInvariant(fullPath[0]);
            var remainder = fullPath[3..].Replace('\\', '/');
            return $"/mnt/{drive}/{remainder}";
        }

        return fullPath.Replace('\\', '/');
    }

    private static string ToTomlArray(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(ToTomlString)) + "]";

    private static string ToTomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
