---
description: Claim files for this session in the ownership ledger (.claude/ownership.md) before editing them; refuses if another session holds any of them
argument-hint: "<path or glob>[;<path>...] -- <task in a few words> [hours <n>]"
---
Arguments: `$ARGUMENTS`

Several Claude sessions share this working tree. Claim before you edit a plugin's files, its assets or shared
tooling, so other sessions see it and skip them.

1. Split `$ARGUMENTS` at ` -- `: paths before (`;`-separated, repo-relative, `*` / `**` allowed, e.g.
   `source/Police/Runner.cs;source/Police/PursuitHud.cs` or `source/DriverCam/**`), task after (no `|`). `hours <n>`
   at the end sets the expiry (default 8, max 24). No ` -- ` or no paths -> show the argument hint and stop.
2. Run exactly:
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ownership.ps1 -Claim -Paths "<paths>" -Task "<task>" [-Hours <n>]`
   (the session id is taken from `$env:CLAUDE_CODE_SESSION_ID`).
3. Relay its lines. Exit 1 (`CONFLICT ...`) -> nothing was claimed: don't edit those paths; tell the person which
   session holds them and until when, and ask whether to wait or have that session `/release` them. Never edit the
   ledger by hand and never release another session's claim. Exit 2 -> show its `ERROR` line.
4. Claim narrowly (the files or one plugin folder you will touch), and `/release` when the work is shipped or handed
   over. A claim is a promise to other sessions, not a lock git enforces: `/audit`, `/ship` and the asset-builder
   agent check it; check by hand with `/claims check <paths>` before editing anything else.
