---
description: Playtest check - per-feature stats, repeated issues across checks and a fix plan from the game log (playtest-analyst agent, read-only)
argument-hint: "[since=<n>:<h>] [install dir] [new]"
---
Analyse the playtest log with the **playtest-analyst** subagent (`.claude/agents/playtest-analyst.md`). Arguments:
`$ARGUMENTS`

1. State file: `$env:TEMP\rogue-playtest-<first 6 characters of $env:CLAUDE_CODE_SESSION_ID>.json` (one per
   session, so two sessions never share counts). `new` in `$ARGUMENTS` -> delete that file first (a new play session).
2. `since=`: the value from `$ARGUMENTS`, else the one from this conversation's last `/playtest` report, else none
   (the agent then takes it from the state file).
3. Launch `playtest-analyst` with `state=<file>`, the `since=` value and the install folder if one was named. Don't
   tell it what you expect to see.
4. Relay its report unchanged from `PLAYTEST` to `RESULT:`. Keep its `since=` for the next check.
5. Then, for each `ISSUE` marked `autonomous: YES`, say in one line whether you will implement it now (only when it
   is repeated and the fix line names a concrete change; memory `log-watch-autonomy`), with an ETA
   (`~10 min per fix: build SkipDeploy, /audit, install when the game is closed`). Issues marked `NO` are listed for
   the person to decide; never edit those files.
