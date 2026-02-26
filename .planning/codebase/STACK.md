# Technology Stack

**Analysis Date:** 2026-02-25

## Languages

**Primary:**
- C# - .NET language used for all source code in `Program.cs`

## Runtime

**Environment:**
- .NET 8.0 (LTS)
- Runtime: dotnet SDK 10.0.103

**Package Manager:**
- NuGet - Default .NET package manager
- Lockfile: `Code2Obsidian.csproj` contains package references

## Frameworks

**Core:**
- Microsoft.NET.Sdk - Standard .NET SDK for executable applications

**Code Analysis:**
- Microsoft.CodeAnalysis 4.14.0 - Roslyn compiler API for C# syntax tree analysis
- Microsoft.CodeAnalysis.CSharp.Workspaces 4.14.0 - Roslyn support for C# parsing and semantic analysis
- Microsoft.CodeAnalysis.Workspaces.MSBuild 4.14.0 - MSBuild integration for project/solution loading

**Build/Dev:**
- MSBuild - Microsoft Build System (via Microsoft.Build.Locator 1.9.1)
- Visual Studio or dotnet SDK for building

## Key Dependencies

**Critical:**
- Microsoft.Build.Locator 1.9.1 - Locates and registers MSBuild on the system; essential for loading C# projects and solutions
- Microsoft.CodeAnalysis.CSharp.Workspaces 4.14.0 - Parses C# code, extracts syntax trees and semantic information
- Microsoft.CodeAnalysis.Workspaces.MSBuild 4.14.0 - Loads entire solutions using MSBuild, enables project-wide analysis

## Configuration

**Environment:**
- No external environment configuration required beyond .NET SDK installation
- Optional: Visual Studio installation (auto-detected for MSBuild)
- Fallback: Uses DOTNET_ROOT or default installation paths (`C:\Program Files\dotnet`)

**Build:**
- `Code2Obsidian.csproj` - Project file defining build configuration, target framework, and dependencies
- ImplicitUsings enabled - Automatic System namespace imports
- Nullable enabled - Strict null checking in C#

## Platform Requirements

**Development:**
- .NET 8.0 SDK or later
- Windows, macOS, or Linux with dotnet CLI
- Visual Studio 2022+ (optional, for IDE support)
- C# 12 language features support

**Production:**
- .NET 8.0 runtime minimum
- Command-line execution: `dotnet Code2Obsidian.dll <solution.sln> [--per-file|--per-method] [--out <folder>]`
- Read/write access to solution files and output directory

---

*Stack analysis: 2026-02-25*
