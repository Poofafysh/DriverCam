# Driving Rogue mods

Source for two BepInEx 6 IL2CPP plugins for **Driving Rogue** (Steam):

| Plugin | Version | What it does |
|---|---|---|
| **DriverCam** | 0.9.2 | First-person driver view with a fitted cockpit for all 10 cars, working mirrors, HUD layout and a controller Edit mode. See [`source/DriverCam`](source/DriverCam) and [`source/README.md`](source/README.md) |
| **CurbFeel** | 0.3.2 | Curbs, sidewalks and lane splitting: the road-edge walls move up to 3 m past the curb, wheels ride up onto the sidewalk, and shallow wall or traffic scrapes don't cost health or reset your drift. See [`source/CurbFeel/README.md`](source/CurbFeel/README.md) |

This repo is **source only**: no DLLs, BepInEx files or zips. Build the plugins yourself as described below.

## Setup

1. **BepInEx 6 IL2CPP.** Install BepInEx 6.0.0-be.788 (or newer) for Unity IL2CPP x64 from <https://builds.bepinex.dev/projects/bepinex_be> into the game folder (Steam: right-click Driving Rogue > Manage > Browse local files), so `winhttp.dll` and the `BepInEx` folder sit next to `Driving Rogue.exe`. Start the game once and let BepInEx finish its first run (a few minutes; it generates `BepInEx/interop`), then close it.
2. **.NET SDK** 6 or newer (8 works).
3. **Your game path.** Copy `source/local.props.example` to `source/local.props` and set `GameDir` to your Driving Rogue folder. `local.props` is git-ignored, so each of us keeps our own.
4. **Build.** With the game closed (it locks loaded plugin DLLs):
   ```
   cd source/DriverCam
   dotnet build -c Release
   cd ../CurbFeel
   dotnet build -c Release
   ```
   Each build copies its DLL (and DriverCam's cockpit files) straight into the game's `BepInEx/plugins`.
5. **Tuned DriverCam settings** come with the build: it installs `source/DriverCam/SavedSettings/DriverCam_cars/` as the shared setups in `BepInEx/plugins/DriverCam/cars/`. On the next start, every car you haven't tuned yourself (no settings file, or only the untouched defaults) uses them. Cars you have tuned are left alone; **Use shared setup** on the DriverCam panel's Seat & view tab switches the current car to the shared one.

## Controls

| Key | Plugin | Does |
|---|---|---|
| C / Y (Change Camera) | DriverCam | cycles Chase → Chase 2 → Hood → Driver |
| F6 / controller View | DriverCam | toggle Driver view |
| F7 / L3+R3 | DriverCam | Edit mode (move seat, cockpit parts, mirrors with the controller) |
| DriverCam button (left of screen) | DriverCam | settings panel |
| F8 | CurbFeel | status panel: full / compact / hidden |
| F9 | CurbFeel | reload `BepInEx/config/rogue.curbfeel.cfg` |
| F10 | CurbFeel | CurbFeel on/off |

Both plugins only change your own game. Nothing is sent over the network. CurbFeel does change gameplay, so try it solo or in a private lobby first.

## Updating your game from the repo

After the other person pushes, or any time you want the latest:

```
powershell -ExecutionPolicy Bypass -File tools/sync-install.ps1 -DryRun            # see what would change
powershell -ExecutionPolicy Bypass -File tools/sync-install.ps1 -CloseGame -Launch  # do it, then check it loads
powershell -ExecutionPolicy Bypass -File tools/sync-install.ps1 -Rollback latest    # undo the last update
```

In Claude Code, ask for the **repo-sync** agent (`.claude/agents/repo-sync.md`). It runs these steps, explains what changed, and asks before closing your game or touching your settings. Each update:
- backs up your installed plugins and configs (including `DriverCam_cars`) to `backup/<time>/`, keeping the last 10
- pulls only if it's a clean fast-forward (it never merges or rewrites), then builds and installs every plugin
- checks the installed DLLs and every shipped file (read from each project's deploy step) against the build
- flags leftover files and duplicate plugin DLLs that could load twice
- reports config changes: new settings, settings that were removed or renamed (your saved value is ignored), defaults that changed while you still have the old one saved, and tuned per-car settings that differ from yours (never copied without asking)
- with `-Launch`, confirms BepInEx loaded each plugin at the new version with no plugin errors

## Working on this repo (both of us)

- `git pull --rebase` before you start and before you push.
- Bump the plugin's version in `source/<Plugin>/Plugin.cs` and its README whenever you change its code. Never reuse a version number.
- **After a code change, run the code audit.** In Claude Code, ask for the **code-auditor** agent
  (`.claude/agents/code-auditor.md`). It's a strict, read-only PASS/FAIL review of your diff covering: scope and version
  bump, clean build, IL2CPP pitfalls, whether every change to the game is restored when switched off, all cars and maps,
  local-only and honest docs, clashes with the other plugin, and no game assets or personal paths.
- **Before every push, run the push check:**
  ```
  powershell -ExecutionPolicy Bypass -File tools/push-check.ps1
  ```
  In Claude Code, ask for the **push-check** agent (`.claude/agents/push-check.md`; `CLAUDE.md` tells Claude to use it automatically). It flags:
  - being behind `origin/main`, and files both of us changed
  - a plugin whose code changed without a version bump, a version lower than what's already pushed, or one already used by a tag
  - duplicate plugin GUIDs, names or DLL names
  - hotkey clashes, and two plugins patching the same game method
  - binaries, build output, `local.props` or game assets about to be committed
  - merge-conflict markers, and READMEs that state a different version than the code
- Hotkeys in use: F6/F7 (DriverCam), F8/F9/F10 (CurbFeel).
- Bump versions with `tools/bump-version.ps1 -Plugin <Name> -To patch|minor|major` (it counts up from the higher of your version and the pushed one, so we never collide). No bump for docs-only changes.

### Claude Code slash commands

Defined in `.claude/commands/`; `CLAUDE.md` has the full workflow and rules.

| Command | Does |
|---|---|
| `/whats-new` | what the other person pushed since your last sync (read-only) |
| `/sync` | update your game from the repo via repo-sync (dry run first, asks before closing the game) |
| `/status` | repo vs origin, plugin versions local / pushed / installed |
| `/build [Plugin]` | build one or all plugins (deploys into the game) |
| `/game-log [text]` | summarize `BepInEx/LogOutput.log`: loaded versions, errors, plugin diagnostics |
| `/audit` | code-auditor review of your diff |
| `/bump <Plugin> [patch\|minor\|major\|x.y.z]` | bump a version everywhere and show the diff |
| `/check` | run the push check and explain every FAIL/WARN |
| `/ship [message]` | pull --rebase, audit, bump, commit, push-check, push (never forced) |
| `/rollback [latest\|name]` | restore a game backup |
| `/research <topic>` | look up game types in RESEARCH.md and the BepInEx interop |
| `/new-plugin <Name>` | scaffold a new plugin to the repo conventions |
| `/test-tools` | run the push-check scenario tests |
| `/hot-reload` | see `source/HotReload/README.md` (coming) |

## Uninstall

Delete `BepInEx/plugins/DriverCam.dll`, `BepInEx/plugins/DriverCam/` and `BepInEx/plugins/CurbFeel.dll`. To remove BepInEx itself, also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx` and `dotnet` folders.

Unofficial fan mods. Not affiliated with Gravity Works or Cosmic Shift Studios. Game assets (ripped models, dumps) are not included.
