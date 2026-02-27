# Serena Tool Gotchas

## replace_symbol_body for Fields
- For fields, the body returned by `find_symbol` starts at the **field name** (e.g., `KnownProviders = ...`), NOT the modifiers
- When calling `replace_symbol_body`, provide the body in the same format (starting from field name)
- Do NOT include `private static readonly Type` prefix — Serena keeps that and prepends it, causing duplication
- The body does NOT include the trailing semicolon — Serena adds it

## IChatClient (Microsoft.Extensions.AI)
- Interface methods use `IEnumerable<ChatMessage>`, not `IList<ChatMessage>`
