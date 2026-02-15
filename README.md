# Recall

> **Note:** This is a personal learning project. I'm using it to learn C# and .NET by building something I actually use daily. It works on **Linux only** (the ONNX native library resolver is Linux-specific). PRs welcome but expect rough edges.

A personal diary MCP server with persistent memory and semantic search.

Recall gives Claude (or any MCP-compatible AI) access to your past conversations. Every new conversation starts with relevant context retrieved automatically, so the AI always knows what you've discussed before.

## How it works

Recall is an [MCP server](https://modelcontextprotocol.io/) that stores diary entries in SQLite with vector embeddings for semantic search (all-MiniLM-L6-v2 via ONNX Runtime). It exposes five tools:

| Tool | Purpose |
|------|---------|
| `diary_context` | Auto-fetch relevant past entries at conversation start |
| `diary_write` | Record an entry with optional tags |
| `diary_query` | Search past entries by meaning (semantic search) |
| `diary_update` | Edit an existing entry |
| `diary_list_recent` | List recent entries chronologically |
| `diary_time` | Current date/time (so the AI knows when it is) |
| `health_query` | Search health/fitness data (sleep, HR, steps, SpO2) |
| `health_recent` | Recent health summaries |

## Quick start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### Install and connect to Claude Code

```bash
git clone https://github.com/anicka-net/recall.git
cd recall
dotnet build

# Register with Claude Code
claude mcp add --transport stdio --scope user recall -- \
    dotnet run --project /path/to/recall/src/Recall.Server/Recall.Server.csproj
```

The diary tools are now available in your Claude Code conversations.

### Configuration

Config file: `~/.recall/config.json`

```json
{
  "databasePath": "~/.recall/recall.db",
  "systemPrompt": "Custom instructions for the AI",
  "promptFile": "~/.recall/prompt.txt",
  "autoContextLimit": 5,
  "searchResultLimit": 10
}
```

All fields are optional. Defaults work out of the box.

### Data storage

All data is stored locally at `~/.recall/recall.db` (SQLite). Nothing leaves your machine unless you configure a remote transport.

### Semantic search model

Recall uses [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) for semantic search. Download the model files:

```bash
mkdir -p ~/.recall/models/all-MiniLM-L6-v2
curl -L -o ~/.recall/models/all-MiniLM-L6-v2/model.onnx \
    https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
curl -L -o ~/.recall/models/all-MiniLM-L6-v2/vocab.txt \
    https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt
```

Without the model files, Recall falls back to substring (LIKE) search.

## Authentication

### Local (Claude Code)

No auth needed. Claude Code connects via stdio transport — it's a local process.

### Remote (claude.ai)

Recall supports OAuth 2.1 with PKCE for remote access. Claude.ai discovers and negotiates auth automatically.

**Setup:**

```bash
# Set a passphrase for the login form
dotnet run -- oauth setup

# You'll be prompted for:
#   1. A passphrase (used when Claude.ai redirects you to authorize)
#   2. Your server's public URL (e.g. https://example.com)
```

When Claude.ai connects, it:
1. Discovers OAuth endpoints via `/.well-known/oauth-protected-resource`
2. Registers itself as a client
3. Redirects your browser to a login form
4. You enter the passphrase once
5. Claude.ai gets a token and refreshes it automatically

**API keys** (simpler, for non-Claude.ai clients):

```bash
dotnet run -- key create "my-client"    # Generate a key (shown once)
dotnet run -- key list                  # List all keys
dotnet run -- key revoke 3              # Revoke by ID
```

Both auth methods work simultaneously. Without any keys or OAuth configured, the server runs open (fine for local-only use, not for internet-facing).

## Health data integration

Recall can store daily health summaries from Fitbit (sleep, heart rate, activity, SpO2) and menstrual cycle tracking. The `tools/` directory contains:

| Script | Purpose |
|--------|---------|
| `tools/fitbit-sync.py` | Fetch Fitbit data via API, write to recall.db |
| `tools/fitbit-cron.sh` | Hourly cron job: sync + push to remote |
| `tools/cycle.py` | Menstrual cycle tracking with predictions |

Health data appears alongside diary entries through the `health_query` and `health_recent` MCP tools.

## Architecture

```
┌──────────────────────────┐
│     Recall MCP Server    │
│ SQLite + vector search   │
│ OAuth 2.1 / API keys     │
│  stdio / HTTP transport  │
└──────────┬───────────────┘
           │ MCP protocol
    ┌──────┴──────┐
    │ Claude Code │  (stdio, no auth)
    │  claude.ai  │  (HTTP + OAuth 2.1)
    │   any MCP   │  (HTTP + Bearer token)
    │    client   │
    └─────────────┘
```

## Building

```bash
dotnet build              # Build
dotnet test               # Run tests (when available)
```

## License

MIT
