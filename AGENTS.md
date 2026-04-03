# Recall Agent Guide

This repository accepts AI-assisted changes, but the bar is simple: extend the
project without breaking the diary, auth, or storage behavior that already
works.

## Core Rules

1. Preserve working behavior first.
   Before changing behavior, read the affected code path and its tests. Prefer
   small edits over rewrites.

2. Treat auth and scope isolation as invariants.
   Coding access must stay limited to global unrestricted data. Scoped users
   must never see or mutate other scopes. Do not add logs that expose secrets,
   tokens, passphrases, or partial secrets.

3. Keep the source of truth clear.
   Runtime behavior lives in `src/Recall.Server/` and `src/Recall.Storage/`.
   If you change a tool contract or setup flow, update the docs in the same
   change.

4. Make regressions harder.
   Every bug fix in access control, diary retrieval, calendar logic, OAuth, or
   checkout should come with a regression test.

5. Do not “clean up” by broad refactor.
   This is a personal learning project. Prefer readable, local improvements.
   Only extract abstractions when they remove repeated logic on a hot path.

## Repository Map

- `src/Recall.Server/`
  MCP server entrypoint, tool handlers, OAuth endpoints, Rohlik integration.
- `src/Recall.Storage/`
  SQLite schema, access control, search, calendar, health, token storage.
- `src/Recall.Tests/`
  Regression coverage. Add tests here when behavior changes.
- `README.md`
  User-facing setup, auth model, and tool behavior.
- `ONBOARDING.md`
  Scoped-user setup and hook instructions.

## Change Checklist

- Read the affected runtime path before editing.
- Preserve access-control invariants:
  no secret leakage, no scoped-data leakage, no direct-ID bypasses.
- Update tests when fixing a bug or changing externally visible behavior.
- Run `dotnet test Recall.slnx`.
- If config shape, hooks, or tool semantics changed, update `README.md` and/or
  `ONBOARDING.md`.

## Project-Specific Notes

- `DiaryTools`, `HealthTools`, and Rohlik tools are user-facing APIs. Keep
  parameter semantics stable unless there is a strong reason to change them.
- `DiaryDatabase.AccessFilter` and related access checks are security-critical.
  Reuse central checks instead of duplicating per-call access logic.
- Fallback behavior matters. If embeddings are unavailable, text search must
  still work.
- Payment and checkout flows must remain session-safe. Avoid static mutable
  state for per-user confirmation or cart data.

## CLAUDE.md

`CLAUDE.md` is intentionally a symlink to this file so default agent guidance
stays short and current. If you want a longer, customizable starter prompt,
use `CLAUDE_TEMPLATE.md`.
