# Code2Obsidian

Code2Obsidian analyzes a C# solution and generates an Obsidian vault of notes.

## Claude subscription-backed setup (`claude-code`)

`claude-code` uses your local Claude CLI session (`claude -p`) instead of an API endpoint URL.

1. Authenticate your local Claude CLI session:

```bash
claude auth login
claude auth status
```

2. Create `code2obsidian.llm.json` next to your `.sln` file:

```json
{
  "provider": "claude-code",
  "model": "claude-sonnet-4-5"
}
```

3. Run enrichment:

```bash
dotnet run -- ./YourSolution.sln --enrich
```

You can override via flags when needed:

```bash
dotnet run -- ./YourSolution.sln \
  --enrich \
  --llm-provider claude-code \
  --llm-model claude-sonnet-4-5
```

## Managed lanes and Serena

Use `--pool-size` to launch managed Claude process lanes:

```bash
dotnet run -- ./YourSolution.sln --enrich --pool-size 2
```

Optional Serena config in `code2obsidian.llm.json`:

```json
{
  "provider": "claude-code",
  "model": "claude-sonnet-4-5",
  "serena": {
    "enabled": true,
    "context": "claude-code"
  }
}
```

## Auth-precedence note

If `ANTHROPIC_API_KEY` or `CLAUDE_API_KEY` is set (or you pass `--llm-api-key`), Claude may prefer API-key auth over your logged-in subscription session. Remove those API-auth settings when you intend to use CLI login-based auth.

## Unsupported Claude path

`claude mcp serve` is an MCP tool transport, not a supported model backend transport for `claude-code`.

## Endpoint-backed providers

`--llm-endpoint` is for endpoint-backed/custom providers (for example `codex`, `ollama`, or custom OpenAI-compatible endpoints). Do not set endpoint fields for `claude-code`.
