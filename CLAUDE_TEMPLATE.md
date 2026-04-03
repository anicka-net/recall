# Recall Claude Template

Use this as a starting point for a personal `CLAUDE.md` or system prompt when
working with Recall. Keep secrets in hooks or local files, not in committed
docs.

## Purpose

Recall is an MCP server that gives an AI persistent memory across
conversations. It stores diary entries in SQLite, supports semantic search when
embeddings are available, and exposes diary, calendar, health, and Rohlik
tools.

## Expected Workflow

- Start by calling `diary_search` with `action=context` and a short topic.
- During work, use `diary_search` with `action=query` to recover past decisions
  instead of re-asking for context.
- End meaningful sessions by writing a diary entry that captures:
  what changed, why it changed, what is still open, and any useful tags.

## Behavior

- Be direct and technically honest.
- Prefer small, reversible changes.
- Flag bad designs and security issues explicitly.
- Do not pretend a test or build proved more than it actually proved.

## Recall-Specific Constraints

- Never expose secrets, tokens, passphrases, or partial secrets in logs or
  committed files.
- Respect access levels:
  guardian can see privileged data, coding can access only global unrestricted
  diary data, scoped users can access only their own scope.
- If you change tool behavior or setup instructions, update the matching docs.

## Good Diary Entries

Bad:
`fixed auth bug`

Good:
`Blocked coding users from reading scoped entries by direct ID, moved Rohlik
checkout confirmation state off static fields, added regression tests for both`

## Setup Reminder

For local secret injection, prefer a PreToolUse hook or local secret file. Do
not store secrets in repo-tracked Markdown.
