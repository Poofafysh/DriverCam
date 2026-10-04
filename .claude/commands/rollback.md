---
description: Restore a game install's plugins and configs from a sync backup (by name; latest only with one install)
argument-hint: "[latest | <backup folder name>] [install dir]"
---
Roll a local game install back to a backup made by `tools/sync-install.ps1`. Arguments: `$ARGUMENTS` (backup name
or `latest`, default `latest`; optional install folder, default `<GameDir>` from `source/local.props`).

1. **List** the folders in `backup/` at the repo root, newest first (the script keeps the last 10). For each show:
   name, the install from its `install.txt` (written by the repo-sync agent; `install unknown` if absent), and its
   top-level contents (`plugins\*.dll`, plugin folders, `config\*.cfg`). None -> say so, `RESULT: FAIL`, stop.
2. **Pick, by one rule:**
   - The target install = the folder given, else `<GameDir>`. It must be `<GameDir>` or listed in `<ExtraGameDirs>`;
     otherwise show both and stop.
   - `latest` is allowed only when `source/local.props` has no `<ExtraGameDirs>`. Otherwise show the newest 4 and ask
     for a name.
   - A named folder that doesn't exist -> show the list and ask.
   - The backup's `install.txt` names a different install -> refuse (`that backup is from <dir>`). `install unknown`
     -> say so and ask the person to confirm that backup belongs to the target install; continue only on a yes.
3. Say what will be restored (`plugins\` and `config\` of that backup copied over the install's `BepInEx\plugins` and
   `BepInEx\config`, overwriting the current files; files not in the backup stay). Check the game:
   `Get-Process "Driving Rogue" -ErrorAction SilentlyContinue | Select-Object Id, Path`. A process whose `Path` starts
   with the target install (or has an empty `Path`) is running there: **ask** before closing it; without a yes, stop.
4. Run from the repo root:
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-install.ps1 -Rollback <name> -GameDir "<install>"`
   (always pass `-GameDir`), plus `-CloseGame` only on a yes.
5. Report in exactly this shape:
   ```
   ROLLBACK <name> -> <install>: OK | FAILED
   <the script's info / WARN / FAIL lines>
   Installed now: the backup's plugins (versions show in /game-log after the next game start)
   Note: the repo is unchanged; the next /build or /sync installs the repo version again.
   RESULT: OK | FAIL
   ```
