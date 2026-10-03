---
description: Update my local Driving Rogue install from the repo (repo-sync agent; dry run first, asks before closing the game)
argument-hint: "[--no-launch] [--close-game] [extra sync-install.ps1 flags]"
---
Update the person's local game install to what is in the repo, using the **repo-sync** subagent
(`.claude/agents/repo-sync.md`, backed by `tools/sync-install.ps1`). Extra request from the person: `$ARGUMENTS`

1. Launch the `repo-sync` agent and tell it to follow its own steps exactly, and include anything from `$ARGUMENTS`:
   - `--no-launch` means leave out `-Launch` (don't start the game afterwards).
   - `--close-game` means the person has already agreed to closing a running game, so `-CloseGame` may be used without asking.
   - Pass any other `-Flag` (for example `-GameDir "..."`, `-FromRev <sha>`, `-Force`) straight through to `tools/sync-install.ps1`.
2. The agent must run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-install.ps1 -DryRun` **first** and
   report incoming commits (who and what), plugin version changes and config impact **before** changing anything.
3. If the dry run reports FAIL in preflight (diverged branch, uncommitted changes, BepInEx/interop missing), stop and
   explain the fix. Do not pull, rebase, stash, reset or discard anything.
4. If the game is running and the person did not pass `--close-game`, **ask the person in chat** before closing it.
   Wait for a clear yes. Then run the real update
   (`tools/sync-install.ps1 -CloseGame -Launch`, minus `-CloseGame` / `-Launch` as agreed).
5. Never edit the person's `.cfg` or `DriverCam_cars/*.cfg` files and never delete a duplicate DLL without a yes.
6. Relay the agent's final `SYNC:` report block to the person verbatim, followed by a one-line plain-English summary
   and any "needs your attention" items as a short list. If it failed after installing, offer `/rollback`.
