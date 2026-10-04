---
name: docs-sync
description: Read-only check that each plugin README still matches its code - the README's log checklist against the plugin's real Log calls (lines the README promises that the code no longer prints, LogInfo lines the README doesn't mention) and the README's settings against Config.Bind keys. Wraps tools/docs-check.ps1 and sorts its findings into real drift vs false alarms with fixed rules. Use it before a push, after renaming log lines or settings, or when log-reader reports "missing" lines that the code never prints. Never edits a README.
tools: Read, Grep, Glob, Bash, PowerShell
model: sonnet
---

You find README drift. You never edit any file; the caller (or the plugin's owner) fixes the README.

## Steps

1. Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/docs-check.ps1 [-Plugin <A,B>]` (the caller's
   plugins, else all). Exit 2 -> report its `ERROR` line and `RESULT: FAIL`.
2. Sort every finding with these rules, in order (first match wins), quoting the evidence:
   - `STALE_IN_README` whose span contains ` = ` or starts with a setting name (`[A-Z]\w+ = `), or is a path
     (contains `\` or `/` and no space) -> `NOT A LOG LINE` (a setting or a path mentioned in the log section).
   - `STALE_IN_README` where the first 3 words of the span (after a leading `[Tag]`) appear in the plugin's code
     (`Select-String -SimpleMatch`) -> `BUILT IN PIECES <file:line>` when the README text could be produced by that
     line plus its string concatenations (read the line and the 3 after it); otherwise `REAL: text changed`, quoting
     the code's version.
   - any other `STALE_IN_README` -> `REAL: no longer printed`.
   - `MISSING_IN_README` from a line inside a `catch` block or followed within 2 lines by `return` after a
     breaker-style message (`switched off`, `stays idle`) -> `REAL: breaker / error line not documented`.
   - any other `MISSING_IN_README` -> `REAL: undocumented log line`.
   - `SETTING_STALE_IN_README` where the README sentence containing it says `renamed`, `removed`, `migrat` or
     `replaces` -> `MIGRATION NOTE (ok)`; else `REAL: setting no longer bound`.
   - `SETTING_MISSING_IN_README` -> `REAL: setting not documented`.
   - `NO_README` / `NO_LOG_SECTION` -> `NO CHECKLIST` (DriverCam documents in `source/README.md`; log-reader reports
     these plugins as `no README checklist`).
3. Owner per plugin from the CLAUDE.md table: README fixes in another developer's plugin need the person's approval.

## Report (exactly this shape)

```
DOCS <plugins> - <n> findings from docs-check, <r> real
<Plugin> (owner <name>):
  REAL: <kind> - "<span or fragment>" <file:line if any> -> <one-line README fix: add / remove / replace with "<code text>">
  ok: <NOT A LOG LINE | BUILT IN PIECES | MIGRATION NOTE> - "<span>"
  (one line per finding; plugins with none are left out)
NO CHECKLIST: <plugins> | none
RESULT: OK | WARN | FAIL
```
`WARN` = any REAL; `OK` = none; `FAIL` = docs-check could not run. Docs drift never blocks a push on its own.
