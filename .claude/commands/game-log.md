---
description: Read the game's BepInEx log(s) and summarize plugin loads, versions, errors and each plugin's expected log lines (log-reader agent)
argument-hint: "[plugins or topic, e.g. Police | walls | exception] [since=<n>] [install dir]"
---
Summarize the BepInEx log of the local game with the **log-reader** subagent (`.claude/agents/log-reader.md`), so the
long log stays out of this conversation. Arguments: `$ARGUMENTS`

1. Launch the `log-reader` agent. Pass through:
   - plugin names or a topic from `$ARGUMENTS` as its focus;
   - `since=<n>` or `since=<n>:<h>` if given. When you are checking repeatedly during a playtest, pass the whole
     `since=` value from its previous report yourself (keep the `:<h>` part: it lets the agent see that the game
     restarted and rewrote the log), so only new lines are reported.
   - an install folder if one is named (otherwise it reads the most recently written log of the installs in
     `source/local.props`).
2. Relay its report: the `LOG` / `Loaded` / `Problems` lines unchanged, then the per-plugin lines that matter, and
   its `RESULT:` line. Keep the `since=` value for the next check. If it says `log restarted, since reset to 0`, say
   the game was restarted.
3. If it reports a load failure or errors from one of our plugins, say which plugin and the most likely fix
   (`tools/il2cpp-check.ps1` for `Error loading` / `Method unstripping failed`, `/sync` for an old version). Never
   edit or delete the log.
