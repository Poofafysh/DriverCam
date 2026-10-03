---
description: Run the strict code-auditor agent on the current diff (PASS/FAIL, read-only)
argument-hint: "[plugin name or focus, optional]"
---
Run the **code-auditor** subagent (`.claude/agents/code-auditor.md`) on the current change. Optional focus: `$ARGUMENTS`

1. Show `git status --short` and `git diff HEAD --stat` plus `git log origin/main..HEAD --oneline` so the scope is clear.
   If there is no change at all (clean tree, nothing ahead of origin), say so and stop.
2. Launch the `code-auditor` agent. Tell it to review everything not yet on `origin/main` (uncommitted changes and local
   commits), and, if `$ARGUMENTS` names a plugin or topic, to pay extra attention to it. Do not tell it the change is
   fine or explain why you made it; it must judge independently.
3. Relay its verdict: the `PASS`/`FAIL` line, the eight answers, and the numbered fix list, unchanged.
4. If FAIL, ask the person whether to fix the listed items now. If they say yes, fix them and re-run this audit until
   PASS. The auditor itself never edits files.
