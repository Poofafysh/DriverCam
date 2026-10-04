---
description: List the ownership ledger (which session holds which files, until when), or check paths against it
argument-hint: "[check <path>;<path>...]"
---
Arguments: `$ARGUMENTS`

- No arguments: run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ownership.ps1 -List` and show its
  `CLAIM` lines as a table (session, task, paths, until, ACTIVE / EXPIRED); mark this session's rows (session = the
  first 6 characters of `$env:CLAUDE_CODE_SESSION_ID`). `CLAIMS: none` -> say no session has claimed anything.
- `check <paths>`: run `tools/ownership.ps1 -Check -Paths "<paths>"` the same way. Exit 0 -> free to edit; exit 1 ->
  relay the `CONFLICT` lines and don't edit those paths.
- Read-only: never edit `.claude/ownership.md` by hand, never stage or commit it.
