---
description: Restore my game's plugins and configs from the latest (or a named) sync backup
argument-hint: "[latest | <backup folder name>]"
---
Roll the local game install back to a backup made by `tools/sync-install.ps1`. Which backup: `$ARGUMENTS`
(empty = `latest`).

1. List the available backups: the folders in `backup/` at the repo root (git-ignored, newest first, the script keeps
   the last 10). For each show the folder name (timestamp) and, if present, what it contains (plugin DLLs, plugin
   folders, configs). If there are none, say so and stop.
2. Pick the target: `latest` or the named folder (if the name doesn't match a folder, show the list and ask).
3. Tell the person what will be restored and that their current plugins/configs get replaced. Check whether the game is
   running (`Get-Process "Driving Rogue" -ErrorAction SilentlyContinue`); if it is, **ask** before closing it.
4. Run from the repo root:
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-install.ps1 -Rollback <latest|name>`
   adding `-CloseGame` only if the person agreed, and `-GameDir "..."` only if they gave one.
5. Report the `RESULT:` line, which plugin versions are installed now, and any FAIL/WARN with the fix. Note that a
   rollback restores the game install only; the repo checkout is unchanged, so the next `/build` or `/sync` will
   install the repo version again.
