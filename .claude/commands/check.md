---
description: Pre-push check (push-check agent - tools/push-check.ps1 + il2cpp-check), every FAIL/WARN explained with its fix
argument-hint: "[-NoFetch] [-NoGitHub] [-AllowIdentityChange]"
---
Run the pre-push gate and explain the result. Extra flags: `$ARGUMENTS`

1. Launch the **push-check** subagent (`.claude/agents/push-check.md`). Tell it to follow its own steps exactly and
   pass only the flags in `$ARGUMENTS` (`-NoFetch` offline, `-NoGitHub` skip the GitHub releases lookup,
   `-AllowIdentityChange` for an intentional GUID / name / DLL rename). The agent owns the commands, the plugin list
   for il2cpp-check, the fix for each line and the verdict rules: don't run the scripts yourself as well.
2. Relay its report unchanged, from the `PUSH CHECK:` line through the `RESULT:` line. After it, add one plain-words
   bullet per `Blocking` and `Warnings` line (what it means for the person; the fix is already in the report).
3. A warning counts as accepted only when the person says yes to its exact text after seeing it. Don't push, commit
   or edit anything unless the person asks; `/ship` does the full flow.
