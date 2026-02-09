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
| `diary_list_recent` | List recent entries chronologically |
| `diary_time` | Current date/time (so the AI knows when it is) |

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

## Architecture

```
┌──────────────────────────┐
│     Recall MCP Server    │
│ SQLite + vector search   │
│  stdio / HTTP transport  │
└──────────┬───────────────┘
           │ MCP protocol
    ┌──────┴──────┐
    │ Claude Code │
    │  claude.ai  │
    │   any MCP   │
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
