---
description: One-screen status - repo vs origin, local changes, plugin versions (local / origin / installed in game), game running
---
Give a quick, read-only status of the repo and the local game install. Change nothing.

1. `git fetch origin --tags --quiet`, then `git status -sb` (branch, ahead/behind) and `git status --short`.
2. For each `source/<Plugin>/Plugin.cs`: the local version, the version on `origin/main`
   (`git show origin/main:source/<Plugin>/Plugin.cs`), and whether code changed since origin without a bump.
3. Installed versions: read `<GameDir>` from `source/local.props`, then the latest `Loading [<Name> <version>]` lines in
   `<GameDir>\BepInEx\LogOutput.log` (and its timestamp). Mention any `*.dll` in `BepInEx\plugins` that looks like a
   duplicate of a repo plugin (e.g. `CurbFeel (1).dll`).
4. Whether the game is running (`Get-Process "Driving Rogue" -ErrorAction SilentlyContinue`).
5. Newest backup folder in `backup/`, if any.
6. Output a compact table (plugin | local | origin | installed) plus 1-3 suggested next commands
   (`/whats-new`, `/sync`, `/audit`, `/ship`, `/build`).
