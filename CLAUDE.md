# Recall - Personal Diary MCP Server

## For Claude instances using this diary

At the START of every conversation, call `diary_context` with a brief summary
of what the conversation is about. This retrieves relevant past entries so you
have continuity with the user's history.

After meaningful exchanges, record key decisions, insights, or events using
`diary_write`. Include relevant tags for searchability.

### Privilege separation

Recall has two access levels:

- **Stdio (Claude Code)** — unprivileged. Sees only unrestricted diary entries.
  Health tools are not registered at all. All writes are automatically unrestricted.
- **HTTP/OAuth (claude.ai)** — privileged. Sees all entries including restricted
  (personal/health/practice). Has health_query and health_recent tools.

As a coding instance, you only see technical/implementation entries.
Do not worry about restricted entries — they're invisible to you by design.

## Tools (stdio)

- `diary_context` - Call first, every conversation. Returns past context + conversation ID.
- `diary_write` - Record entries. Be specific. Use comma-separated tags. Entries are always unrestricted.
- `diary_update` - Update an existing entry by ID.
- `diary_query` - Search past entries by keywords or phrases.
- `diary_list_recent` - Review recent entries chronologically.
- `diary_time` - Get current date/time/day-of-week.

## Architecture

- **Transport**: stdio (Claude Code) or HTTP/SSE (remote/claude.ai)
- **Search**: Vector search using ONNX embeddings (all-MiniLM-L6-v2), with FTS5 fallback
- **Storage**: SQLite (`~/.recall/recall.db`), schema version 4
- **Auth (HTTP mode)**: OAuth 2.1 or API keys. Authenticated sessions get PrivilegeContext.IsPrivileged = true.

## Development

```bash
dotnet build                              # Build
dotnet run --project src/Recall.Server    # Run stdio (for Claude Code)
dotnet run --project src/Recall.Server -- --http          # Run HTTP on port 3000
dotnet run --project src/Recall.Server -- --http --port N # Custom port
dotnet run --project src/Recall.Server -- oauth setup     # Configure OAuth passphrase
dotnet run --project src/Recall.Server -- key create "name"  # Create API key
```

## Configuration

Config file: `~/.recall/config.json`

```json
{
  "databasePath": "~/.recall/recall.db",
  "modelPath": "~/.recall/model.onnx",
  "autoContextLimit": 5,
  "searchResultLimit": 10,
  "oAuthPassphraseHash": "<sha256 hex>",
  "oAuthBaseUrl": "https://example.com"
}
```

## Key source files

- `src/Recall.Server/Program.cs` — Entry point, transport setup, auth middleware
- `src/Recall.Server/Tools/DiaryTools.cs` — Diary MCP tools
- `src/Recall.Server/Tools/HealthTools.cs` — Health MCP tools (HTTP only)
- `src/Recall.Server/PrivilegeContext.cs` — Per-request privilege tracking
- `src/Recall.Server/OAuthEndpoints.cs` — OAuth 2.1 flow
- `src/Recall.Storage/DiaryDatabase.cs` — SQLite data access, vector search
- `src/Recall.Storage/EmbeddingService.cs` — ONNX embedding generation (P/Invoke)
- `src/Recall.Storage/Schema.cs` — Database migrations (v1-v4)
