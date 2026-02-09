# Recall - Personal Diary MCP Server

## For Claude instances using this diary

At the START of every conversation, call `diary_context` with a brief summary
of what the conversation is about. This retrieves relevant past entries so you
have continuity with the user's history.

After meaningful exchanges, record key decisions, insights, or events using
`diary_write`. Include relevant tags for searchability.

## Tools

- `diary_context` - Call first, every conversation. Returns past context + conversation ID.
- `diary_write` - Record entries. Be specific. Use comma-separated tags.
- `diary_query` - Search past entries by keywords or phrases.
- `diary_list_recent` - Review recent entries chronologically.
- `diary_time` - Get current date/time/day-of-week.

## Development

```bash
dotnet build                    # Build
dotnet run --project src/Recall.Server  # Run MCP server
```

## Configuration

Config file: `~/.recall/config.json`

```json
{
  "databasePath": "~/.recall/recall.db",
  "systemPrompt": "Optional custom system prompt",
  "promptFile": "~/.recall/prompt.txt",
  "autoContextLimit": 5,
  "searchResultLimit": 10
}
```

Database: `~/.recall/recall.db` (SQLite with FTS5 full-text search)
