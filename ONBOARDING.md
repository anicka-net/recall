# Recall Scoped User Setup

You are setting up an isolated diary scope on a shared Recall MCP server.
Your scope is completely private — other users and the server admin cannot
see your entries by default, and you cannot see theirs.

## What you need from the admin

1. **Your scope secret** (a passphrase string) — the admin generates this and
   gives it to you securely. Do NOT store it in any git repo or CLAUDE.md.

2. **The Recall MCP connector** must already be configured on your claude.ai
   account (shared account — the admin handles this).

## Setup steps

Run these steps in order. Replace `YOUR_SECRET_HERE` with the passphrase
the admin gave you.

### 1. Store the secret

```bash
echo -n 'YOUR_SECRET_HERE' > ~/.recall-secret
chmod 600 ~/.recall-secret
```

### 2. Create the hook script

```bash
mkdir -p ~/.claude/hooks
cat > ~/.claude/hooks/recall-scope.sh << 'HOOK'
#!/bin/bash
SECRET="$(cat ~/.recall-secret 2>/dev/null)"
if [ -z "$SECRET" ]; then
  echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"No Recall secret found. Run: echo -n YOUR_SECRET > ~/.recall-secret"}}' >&1
  exit 0
fi
cat <<EOF
{
  "hookSpecificOutput": {
    "hookEventName": "PreToolUse",
    "permissionDecision": "allow",
    "permissionDecisionReason": "Recall scope secret injected",
    "updatedInput": {
      "secret": "$SECRET"
    }
  }
}
EOF
exit 0
HOOK
chmod +x ~/.claude/hooks/recall-scope.sh
```

### 3. Configure the hook in Claude Code settings

If `~/.claude/settings.json` does not exist, create it. If it exists,
merge the `hooks` section into the existing file.

The hook must match all Recall diary tools:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "mcp__claude_ai_Recall__diary_.*",
        "hooks": [
          {
            "type": "command",
            "command": "~/.claude/hooks/recall-scope.sh"
          }
        ]
      }
    ]
  }
}
```

### 4. Restart Claude Code

Exit and restart Claude Code so it picks up the new settings.

### 5. Test

After restarting, try these commands to verify your setup:

- `diary_context` with topic "test" — should return your scoped entries
  (empty if this is a fresh scope, which is expected)
- `diary_write` with any test content — should succeed and show `[scope: ...]`
- `diary_list_recent` — should show only entries you wrote

If you see "Access denied", your secret is wrong or not configured on the server.
If you see entries that aren't yours, something is misconfigured — stop and
contact the admin.

## How it works

- The hook script runs before every Recall diary tool call
- It reads your secret from `~/.recall-secret` and injects it as a parameter
- The Recall server maps your secret to your isolated scope
- You only see entries tagged with your scope; you cannot see or modify
  entries from other scopes or the global diary
- The model (Claude) never sees your secret — the hook handles it silently

## What you can do

- **diary_write** — write entries (auto-tagged with your scope)
- **diary_query** — search your entries
- **diary_context** — get relevant context at conversation start
- **diary_list_recent** — list recent entries
- **diary_update** — edit your own entries
- **diary_time** — get current time (no secret needed)

## What you cannot do

- See other users' entries or the global diary
- Access health data
- Write entries to other scopes
- Modify entries outside your scope

## Files created

| File | Purpose |
|------|---------|
| `~/.recall-secret` | Your passphrase (chmod 600) |
| `~/.claude/hooks/recall-scope.sh` | Hook that injects the secret |
| `~/.claude/settings.json` | Hook configuration |

## Troubleshooting

- **"Access denied"** — secret mismatch. Check `~/.recall-secret` matches
  what the admin configured on the server.
- **"No Recall secret found"** — `~/.recall-secret` is missing or empty.
- **Seeing other users' entries** — STOP. Contact the admin immediately.
  Your secret may have been configured at the wrong access level.
- **Hook not firing** — restart Claude Code. Check that `~/.claude/settings.json`
  is valid JSON and the matcher pattern is correct.
