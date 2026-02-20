# Recall

> **Note:** This is a personal learning project. I'm using it to learn C# and .NET by building something I actually use daily. It works on **Linux only** (the ONNX native library resolver is Linux-specific). PRs welcome but expect rough edges.

A personal diary MCP server with persistent memory and semantic search.

Recall gives Claude (or any MCP-compatible AI) access to your past conversations. Every new conversation starts with relevant context retrieved automatically, so the AI always knows what you've discussed before.

## How it works

Recall is an [MCP server](https://modelcontextprotocol.io/) that stores diary entries in SQLite with vector embeddings for semantic search (all-MiniLM-L6-v2 via ONNX Runtime). Tools:

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

## Authentication and access control

Recall has two layers of auth: **transport-level** (who can connect) and **tool-level** (what they can see).

### Transport: local vs. remote

**Local (stdio):** Claude Code connects via stdio — no transport auth needed.

**Remote (HTTP):** OAuth 2.1 with PKCE or API keys control who can connect at all.

```bash
# OAuth setup (for claude.ai)
dotnet run -- oauth setup
# Prompts for a passphrase and your server's public URL

# API keys (for other clients)
dotnet run -- key create "my-client"    # Generate a key (shown once)
dotnet run -- key list                  # List all keys
dotnet run -- key revoke 3              # Revoke by ID
```

Both methods work simultaneously. Without any configured, the server runs open.

### Tool-level: three-tier access

Every tool call (except `diary_time`) requires a `secret` parameter. The server hashes it and compares against two configured hashes to determine the access level:

| Level | Diary | Health | Write restricted |
|-------|-------|--------|------------------|
| **Guardian** | all entries | yes | yes |
| **Coding** | unrestricted only | no | no |
| **None** (no/bad secret) | rejected | rejected | rejected |

Configure in `~/.recall/config.json`:

```json
{
  "guardianSecretHash": "<sha256 of guardian passphrase>",
  "codingSecretHash": "<sha256 of coding passphrase>"
}
```

Generate hashes with: `echo -n "your-passphrase" | sha256sum`

**Injecting the secret into Claude Code** — use a [PreToolUse hook](https://docs.anthropic.com/en/docs/claude-code/hooks) that adds the coding secret to every Recall tool call. Example `~/.claude/hooks/recall-secret.sh`:

```bash
#!/bin/bash
INPUT=$(cat)
TOOL_NAME=$(echo "$INPUT" | jq -r '.tool_name')

# diary_time needs no secret
if [ "$TOOL_NAME" = "mcp__claude_ai_Recall__diary_time" ]; then
    exit 0
fi

TOOL_INPUT=$(echo "$INPUT" | jq -r '.tool_input')
UPDATED=$(echo "$TOOL_INPUT" | jq '. + {"secret": "YOUR_CODING_SECRET"}')

jq -n --argjson updated "$UPDATED" '{
  "hookSpecificOutput": {
    "hookEventName": "PreToolUse",
    "permissionDecision": "allow",
    "updatedInput": $updated
  }
}'
```

Register it in `~/.claude/settings.json`:

```json
{
  "hooks": {
    "PreToolUse": [{
      "matcher": "mcp__claude_ai_Recall__.*",
      "hooks": [{"type": "command", "command": "/path/to/recall-secret.sh"}]
    }]
  }
}
```

For claude.ai (guardian), include the guardian secret in the system prompt with instructions to pass it as the `secret` parameter on every tool call.

## Health data integration

Recall can store daily health summaries from Fitbit (sleep, heart rate, activity, SpO2) and menstrual cycle tracking. The `tools/` directory contains:

| Script | Purpose |
|--------|---------|
| `tools/fitbit-sync.py` | Fetch Fitbit data via API, write to recall.db |
| `tools/fitbit-cron.sh` | Hourly cron job: sync + push to remote |
| `tools/cycle.py` | Menstrual cycle tracking with predictions |

Health data appears alongside diary entries through the `health_query` and `health_recent` MCP tools.

## Deployment

### Local (stdio)

Just register with Claude Code as shown in Quick Start. No server process needed.

### Remote (HTTP)

Publish a **self-contained** binary and run it as a service. Framework-dependent builds won't work unless the exact .NET runtime is installed on the server.

```bash
# Build (self-contained for Linux x64)
dotnet publish src/Recall.Server/Recall.Server.csproj \
    -c Release --self-contained -r linux-x64 -o publish/

# Copy to server
rsync -av publish/ server:~/recall-server/

# Run
./publish/Recall.Server --http --port 3000
```

**systemd service** (`/etc/systemd/system/recall.service`):

```ini
[Unit]
Description=Recall MCP Server
After=network.target

[Service]
Type=exec
User=recall
WorkingDirectory=/opt/recall
ExecStart=/opt/recall/Recall.Server --http --port 3000
Restart=on-failure
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now recall
```

Put a reverse proxy (Apache/nginx/Caddy) in front for TLS. Claude.ai requires HTTPS.

### Updating a running server

```bash
dotnet publish src/Recall.Server/Recall.Server.csproj \
    -c Release --self-contained -r linux-x64 -o publish/
rsync -av publish/ server:~/recall-server/
# On the server:
sudo systemctl restart recall
```

The SQLite database is preserved across restarts. Schema migrations run automatically on startup.

### Proxying external MCP servers

Recall can act as an OAuth gateway for other MCP servers. Any stdio-based MCP server wrapped in [supergateway](https://www.npmjs.com/package/supergateway) can be proxied through Recall's existing auth — no additional OAuth setup needed.

**How it works:**

```
claude.ai ──HTTPS──▶ reverse proxy ──▶ Recall ──▶ supergateway ──▶ stdio MCP server
                                    (OAuth gate)   (Streamable HTTP)
```

**Step 1: Set up the external MCP server with supergateway**

```bash
# Install on the server
mkdir -p ~/my-mcp && cd ~/my-mcp
npm init -y
npm install supergateway @some/mcp-server

# Create credentials file (if needed)
cat > ~/.config/my-mcp.env << 'EOF'
MY_USERNAME=user@example.com
MY_PASSWORD=secret
EOF
chmod 600 ~/.config/my-mcp.env
```

**Step 2: Create a systemd user unit**

```ini
# ~/.config/systemd/user/my-mcp.service
[Unit]
Description=My MCP Server (supergateway)
After=network.target

[Service]
Type=exec
EnvironmentFile=%h/.config/my-mcp.env
ExecStart=/usr/bin/npx --prefix %h/my-mcp supergateway \
    --stdio "node %h/my-mcp/node_modules/@some/mcp-server/dist/index.js" \
    --port 8385 \
    --outputTransport streamableHttp \
    --streamableHttpPath /mcp \
    --logLevel info
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
```

```bash
systemctl --user daemon-reload
systemctl --user enable --now my-mcp
loginctl enable-linger  # persist after logout
```

**Step 3: Add the proxy to Recall's config**

In `~/.recall/config.json`:

```json
{
  "mcpProxies": [
    { "prefix": "my-mcp", "target": "http://127.0.0.1:8385" }
  ]
}
```

Restart Recall. Tools are now available at `https://your-server/recall/my-mcp/mcp` using the same OAuth token.

**Step 4: Add a claude.ai connector**

In claude.ai settings, add an MCP connector with URL:

```
https://your-server/recall/my-mcp/mcp
```

It uses the same OAuth passphrase as Recall — no separate auth setup.

**Important notes:**

- Use `--outputTransport streamableHttp` in supergateway — claude.ai prefers Streamable HTTP over SSE
- No proxy config is needed in Apache/nginx — the existing `/recall/` proxy rule covers all subpaths
- When `mcpProxies` is absent or empty, zero proxy code runs — no impact on Recall
- Credentials for external services go in env files, never in Recall's config or repo

## Architecture

```
┌──────────────────────────────────────┐
│          Recall MCP Server           │
│  SQLite + vector search + OAuth 2.1  │
│                                      │
│  /sse, /message  →  diary & health   │
│  /{prefix}/*     →  proxy to backend │
└─────────┬────────────────┬───────────┘
          │                │
    MCP protocol     reverse proxy
          │                │
  ┌───────┴───────┐  ┌─────┴──────────┐
  │  Claude Code  │  │  supergateway  │
  │   claude.ai   │  │  (port 8385)   │
  │   any client  │  │       │        │
  └───────────────┘  │  stdio MCP     │
                     │  server        │
                     └────────────────┘
```

## Building

```bash
dotnet build              # Build
dotnet test               # Run tests (when available)
```

## License

MIT
