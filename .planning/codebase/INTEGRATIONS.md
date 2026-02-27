# External Integrations

**Analysis Date:** 2026-02-25

## APIs & External Services

Not detected - Code2Obsidian does not consume external APIs or cloud services.

## Data Storage

**Databases:**
- Not applicable - No database connectivity

**File Storage:**
- Local filesystem only
  - Reads: C# solution files (`.sln`, `.csproj`, `.cs`)
  - Writes: Markdown files to output directory (default: `./_obsidian/`)
  - Client: System.IO for file operations

**Caching:**
- None - No caching layer

## Authentication & Identity

**Auth Provider:**
- Not applicable - No authentication required
- No API keys, secrets, or credentials needed

## Monitoring & Observability

**Error Tracking:**
- None - No error tracking service configured

**Logs:**
- Console output only
  - Uses Console.WriteLine for informational logging
  - Uses Console.Error.WriteLine for error messages
  - No structured logging framework

## CI/CD & Deployment

**Hosting:**
- Not applicable - Standalone console application
- Run locally or as part of documentation generation pipeline

**CI Pipeline:**
- Not configured - Manual build and execution

## Environment Configuration

**Required env vars:**
- None - Application requires no environment variables for basic operation

**Optional env vars:**
- `DOTNET_ROOT` - Specifies .NET SDK location (auto-detected if not set)
- `DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR` - Override MSBuild SDK resolver directory
- Windows defaults to `C:\Program Files\dotnet` if not found

**Secrets location:**
- Not applicable - No secrets are used or stored

## Webhooks & Callbacks

**Incoming:**
- Not applicable - Console application, not a web service

**Outgoing:**
- Not applicable - No external service integration

## MSBuild Integration

**How it Works:**
- Locates Visual Studio or .NET SDK installations via MSBuildLocator
- Registers MSBuild instance programmatically
- Loads C# solutions and projects using MSBuild workspace
- Extracts compilation metadata (symbols, syntax trees) from loaded projects

**Entry Point:** `EnsureMsbuildRegistered()` in `Program.cs` (lines 243-277)

**Fallback Chain:**
1. Checks if MSBuild already registered
2. Queries installed Visual Studio instances
3. Falls back to dotnet SDK in DOTNET_ROOT
4. Searches SDK directories for Microsoft.Build.dll

## System Dependencies

**Build/Compilation:**
- MSBuild (included with Visual Studio or dotnet SDK)
- C# compiler (via Roslyn)

**Runtime:**
- .NET Runtime 8.0+
- System standard library components

---

*Integration audit: 2026-02-25*
