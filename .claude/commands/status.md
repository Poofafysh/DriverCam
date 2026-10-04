---
description: One-screen status - repo vs origin, local changes, plugin versions (local / origin / installed in game), game running
---
Give a quick, read-only status of the repo and the local game install(s). Change nothing (no pull, build or install).

1. `git fetch origin --tags --quiet`, then `git status -sb` (branch, ahead/behind) and `git status --short`.
2. Plugins: every `source/<Plugin>/Plugin.cs` with a `[BepInPlugin]`. Local version = the third attribute argument
   (literal, or the `const string` it names); origin version = the same from
   `git show origin/main:source/<Plugin>/Plugin.cs` (`-` if the plugin is not on origin). "Unbumped" = a non-`.md`
   file under `source/<Plugin>/` differs from `origin/main` (`git diff --name-only origin/main -- source/<Plugin>`
   plus untracked) while local == origin version.
3. Installed versions, for `<GameDir>` and every `<ExtraGameDirs>` entry in `source/local.props` (missing file ->
   say so and skip this step): the last `Loading [<Name> <version>]` per plugin in `<dir>\BepInEx\LogOutput.log`
   (the log-reader agent's version snippet does exactly this), `ERROR` if there is an `Error loading [<Name>`, `-` if
   absent, and the log's LastWriteTime. A `*.dll` in `<dir>\BepInEx\plugins` whose name contains a repo plugin's
   name but isn't `<Name>.dll` (e.g. `CurbFeel (1).dll`) = possible duplicate.
4. Game running: `Get-Process "Driving Rogue" -ErrorAction SilentlyContinue | Select-Object Path`, matched to an
   install by `Path` prefix.
5. Newest folder in `backup/` and its `install.txt` if present.
6. Output exactly:
   ```
   Repo: <branch> <ahead> ahead / <behind> behind origin/main; <n> changed, <m> untracked
   | Plugin | local | origin | <install 1 leaf> | <install 2 leaf> | note |
   (one row per plugin; note = unbumped | OLD install | ERROR | duplicate <file> | blank)
   Logs: <install leaf> written <time>, one per install; Game running: <install leaf> | no
   Newest backup: <name> (<install or "install unknown">) | none
   Next: 1-3 of /whats-new, /sync, /audit, /ship, /build, each with a 3-6 word reason
   ```
