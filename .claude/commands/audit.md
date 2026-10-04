---
description: Run the strict code-auditor agent on the current diff (PASS/FAIL, read-only)
argument-hint: "[plugin names in scope, optional] [approved: <what the person approved>]"
---
Run the **code-auditor** subagent (`.claude/agents/code-auditor.md`) on the current change. Arguments: `$ARGUMENTS`

1. Show `git status --short` and `git diff HEAD --stat` plus `git log origin/main..HEAD --oneline` so the scope is clear.
   If there is no change at all (clean tree, nothing ahead of origin), say so and stop.
2. Work out the **scope**: the plugins named in `$ARGUMENTS`, or else the plugins whose files this conversation
   created or edited (from your own tool calls), plus `source/Shared/*.cs` files you edited. If the working tree also
   holds changes you didn't make (another session, the other developer's unfinished work), keep them out of the scope
   and list them in one line. Nothing in `$ARGUMENTS` and nothing edited in this conversation -> ask which plugins.
   Then run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ownership.ps1 -Check -Paths "<scoped files,
   ;-separated>"`: exit 1 = another session claimed some of them; tell the person (quote the `CONFLICT` lines) and
   audit them only if they say so.
3. Work out the **approvals**: edits the person explicitly approved in chat that the auditor would otherwise flag,
   e.g. "the person approved adding the HeadLook hook to DriverCam's DriverView.cs". Only what the person actually
   said, quoted or closely paraphrased. Never invent one.
4. Say `ETA: ~3-5 min per plugin in scope` (it builds each one and runs il2cpp-check). Launch the `code-auditor` agent. Tell it the scope, the approvals, and to review everything in scope that is not
   yet on `origin/main` (uncommitted changes and local commits). Do not tell it the change is fine or explain why you
   made it; it must judge independently.
5. Relay its whole report unchanged, from the `PASS`/`FAIL` line through the `RESULT:` line. For its one-line stops:
   `FAIL: source/local.props missing` -> add "copy `source/local.props.example` to `source/local.props` and set
   `GameDir`"; `FAIL: tree changed during audit (concurrent session)` -> fix nothing, say another session is editing
   the scoped files, and run `/audit` again only when the person says that session is done.
6. If FAIL, ask the person whether to fix the listed items now. If they say yes, fix them and re-run this audit until
   PASS. The auditor itself never edits files.
