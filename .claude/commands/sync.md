---
description: Update my local Driving Rogue install from the repo (repo-sync agent; dry run first, asks before closing the game)
argument-hint: "[--no-launch] [--close-game] [--all-installs | -GameDir <dir>] [-FromRev <sha>]"
---
Update the person's local game install to what is in the repo, using the **repo-sync** subagent
(`.claude/agents/repo-sync.md`, backed by `tools/sync-install.ps1` and `tools/car-setups.ps1`). Extra request from
the person: `$ARGUMENTS`

1. Launch the `repo-sync` agent, tell it to follow its own steps exactly, and pass, as plain sentences:
   - `--no-launch` -> "don't start the game".
   - `--close-game` -> "the person agreed to close the game".
   - `--all-installs` -> "all installs"; `-GameDir "<dir>"` -> "also sync <dir>". Without either, the agent syncs
     only `<GameDir>` and reports the other installs `NOT REQUESTED`.
   - `-FromRev <sha>` straight through.
   - Anything the person already said in this conversation that answers one of its questions (close the game,
     `-Force` for named uncommitted files, take the repo's car setups, which installs), quoted.
   Never pass `-Launch`, `-Force` or `-CloseGame` as raw flags: the agent decides them from the answers above.
2. The agent ends with one `RESULT:` line:
   - `BLOCKED`: ask the person its `NEEDS ANSWER:` question word for word, wait for their reply, then launch the agent
     again with everything from step 1 plus their answer quoted. Don't answer for them.
   - `OK` / `WARN` / `FAIL`: relay its `SYNC:` block (through the `RESULT:` line) verbatim, then one plain-English
     summary line and the "Needs your attention" items as a short list.
3. If it failed after installing, offer `/rollback <backup name> <install dir>` from that install's `Backup:` line,
   never `latest` after a multi-install sync. Never edit the person's `.cfg` / car setups or delete a duplicate DLL
   without a yes.
