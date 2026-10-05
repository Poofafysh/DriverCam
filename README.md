# Driving Rogue mods

Source for several BepInEx 6 IL2CPP plugins for **Driving Rogue** (Steam): DriverCam, CurbFeel, TrafficDensity, RacingLine, Police, PitStop, EngineAudio, HeadLook, RogueHub (one menu for all of them), and the dev-only HotReload.

| Plugin | Version | What it does |
|---|---|---|
| **DriverCam** | 0.11.1 | First-person driver view with a fitted cockpit for all 10 cars, working mirrors, HUD layout and a controller Edit mode. See [`source/DriverCam`](source/DriverCam) and [`source/README.md`](source/README.md) |
| **CurbFeel** | 0.8.0 | Curbs, sidewalks and lane splitting: the road-edge walls move up to 3 m past the curb (measured on each tile's own sidewalk) and stop at the map's visible railings, fences, planters and buildings, wheels ride up onto the sidewalk, and shallow wall or traffic scrapes don't cost health or reset your drift. See [`source/CurbFeel/README.md`](source/CurbFeel/README.md) |
| **TrafficDensity** | 0.4.0 | Multiplies the NPC traffic on the road (stacks with the game's traffic hazard, applies live). Ctrl+PageUp/PageDown to change, Ctrl+Home = stock. On by default since 0.4.0: **safe lane changes** (NPCs only change lanes when the lane is clear ahead and behind, allowing for how fast you or another car are closing in; no weaving, no two-lane jumps) and **speed limits** (traffic drives the road's limit in mph: highway 70, open road 55, city 35, curvy 40, each driver a little over or under; daredevils keep their speed). See [`source/TrafficDensity/README.md`](source/TrafficDensity/README.md) |
| **RacingLine** | 0.7.1 | New score category **Racing Line** (v2): grades every corner on line, speed vs a reference, grip and pedals (brake straight, lift in, power out); points count up live like the game's own categories and keep the combo alive through grip corners; Grip line x2 for no drifting; coins; RACING LINE rows on the results screen and the end-of-run Victory screen; the line routes around NPC traffic in the way. Counts in single-player and multiplayer (your own score; `Scoring.InMultiplayer`). F5 lays the line on the road ahead (green / amber / red by pace, like Forza) with a HUD card. See [`source/RacingLine/README.md`](source/RacingLine/README.md) |
| **Police** | 0.9.0 | Preview. Police patrols, drawn as the plugin's own police cars (Interceptor / Pursuit / Utility, modelled in Blender; `Look.CarModels` can pick the game's boss cars in a police livery or the traffic look instead) with a flashing lightbar, engage only when you pass them recklessly (35 km/h faster, cutting close, or crashing / near-missing / drifting as you go by) and chase you at your own car's top speed (a little more when they fall far behind); patrols you pass recklessly mid-chase join as backup; a BUSTED / EVADE meter decides (busted = caught slow or boxed in; -5 s off the race timer). New score category **PURSUIT**: points count up live during a chase (lead gained, time at speed), an escape adds a bonus (longer, closer chases with more units pay more) and coins, getting busted loses that chase's points; PURSUIT rows on the results and Victory screens. Police only react to what you do: no random wanted level, no heat levels or escalation (which cars become patrols is random). Daredevils (the red cars with the devil icon) become rivals: boss cars racing RacingLine's optimal line on your own car's pace scaled by a per-boss skill (0.90-1.10: some quicker than you, some slower), with slipstream, corner-aware overtakes and clean defending; grip cars hold the line, drift cars slide through corners; they never try to hit you. Multiplayer (0.8.0): the host drives police and daredevils for every player, guests draw them and score their own chases (same build for everyone; no caught time penalty in multiplayer, the race countdown is shared). F3 turns patrols off / on. See [`source/Police/README.md`](source/Police/README.md) |
| **PitStop** | 0.3.0 | Press F2 to refill your car's health (the game's own heal; in multiplayer your own car; a run that used a refill is kept off the Steam leaderboards). See [`source/PitStop/README.md`](source/PitStop/README.md) |
| **EngineAudio** | 0.3.0 | Realistic engine sound from the game's own recordings: a simulated RPM follows the game's gearbox and your throttle (shift drops, rev limiter, idle), the recordings play at the matching RPM with throttle-based on/off-load blending; at top speed (most of a race) a steady, living high note instead of the limiter; tyre squeal on drifts and hard corners (synthesized, follows your slip angle); traffic engines change pitch with speed. F1 compares with the game's own sound. See [`source/EngineAudio/README.md`](source/EngineAudio/README.md) |
| **HeadLook** | 0.1.1 | Look around like turning your head: right stick, or hold the right mouse button and move. Turns in place in hood view, orbits the car in chase views, and turns the driver's head in DriverCam's driver view. See [`source/HeadLook/README.md`](source/HeadLook/README.md) |
| **RogueHub** | 0.1.0 | One menu for every mod: every plugin's settings (sliders with a number box, toggles, lists, key binds, buttons, descriptions) in the game's own style, a quick menu while driving (LB+RB or backtick) and one notification stack. MODS in the pause menu or Shift+backtick opens it. See [`source/RogueHub/README.md`](source/RogueHub/README.md) |
| **Sandbox** | 0.2.0 | Preview. A SANDBOX button on the main menu starts a run where every card is free, locked mods are offered and you get 20 mod slots. Every sandbox race is on its own map: a 6-lane, 30 m road (width and lanes adjustable), twice as long (x1-x5), with every building and prop removed; only the road with its curbs, sidewalks, guardrails and street lights remains. Multiplayer sandbox runs use the host's road and settings (every player needs the same Sandbox version). Sandbox runs never reach the leaderboards, achievements, missions, XP, credits, unlocks or statistics. Back up `player.dat` before the first try. See [`source/Sandbox/README.md`](source/Sandbox/README.md) |
| **CarSkins** | 0.1.0 | Cosmetic, single-player: draws your Saber as a BMW E46 (1998), a CC BY Sketchfab model cut to about 5k triangles; it follows the game car's suspension, and its wheels spin and steer with the real ones. The game's own car is only hidden. See [`source/CarSkins/README.md`](source/CarSkins/README.md) |
| **Driver** | 0.3.0 | Cosmetic: a 3D racing driver (original model) sits in your car, hands on DriverCam's steering wheel and turning it with you, head following HeadLook. Shown in DriverCam's driver view (body, arms and hands; the head is left out at the eyes); chase views optional. On a Bikes motorcycle it rides the bike instead: a sport-bike tuck with hands on the grips and feet on the pegs, leaning with the bike and hanging off in corners. See [`source/Driver/README.md`](source/Driver/README.md) |
| **Reverse** | 0.1.0 | A reverse gear: with the car completely stopped, keep holding the brake and after a moment the car rolls backwards (up to 25 km/h by default), steering like a real car in reverse; let go of the brake to stop, press the throttle to drive off. Player car, single-player only. See [`source/Reverse/README.md`](source/Reverse/README.md) |
| **Bikes** | 0.1.0 | Two sport motorcycles as new garage vehicles: the BMW S1000RR and a blue Sport Bike, after the game's cars, each with its own name and stats. Underneath, each drives on a hidden copy of a donor car (default Saber), so everything else keeps working; the bike model spins and steers its wheels with the game's and leans into corners. Single-player only; bikes never reach your save (stored as the donor car), the Steam leaderboards or other players. The models stay local (licences unchecked). See [`source/Bikes/README.md`](source/Bikes/README.md) |
| **HotReload** (dev only) | 0.1.0 | Developer tool: reloads hot-module builds of plugins (CurbFeel so far) while the game runs, no restart. Not installed by `/sync`. See [`source/HotReload/README.md`](source/HotReload/README.md) |

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
| Ctrl+PageUp / Ctrl+PageDown | TrafficDensity | more / less NPC traffic |
| Ctrl+Home | TrafficDensity | stock traffic |
| F4 | TrafficDensity | perf overlay on/off (only while [Perf] Enabled) |
| F5 | RacingLine | show / hide the racing line on the road and its HUD card |
| F3 | Police (preview) | patrols off / on for this session |
| F2 | PitStop | refill your car's health (in multiplayer: your own car) |
| F1 | EngineAudio | EngineAudio / the game's own engine sound |
| Right stick / hold right mouse | HeadLook | look around (springs back on release) |
| MODS (pause menu) / Shift + backtick | RogueHub | the hub: every mod's settings |
| LB + RB / backtick | RogueHub | quick menu while driving (favourite settings and buttons) |
| F11 | HotReload (dev only) | reload all hot modules now |

Our plugins only change your own game, with three exceptions:
- **TrafficDensity** with `AllowInMultiplayer` on: while you host a lobby, the extra traffic and the changed NPC behaviour apply to everyone in it (the host runs all traffic). Off by default; as a client it never changes anything.
- **RacingLine** points (`NativeCategory` on by default; also in multiplayer with `Scoring.InMultiplayer`, your own score) count toward the run total that the game itself uploads to its Steam leaderboard. This was chosen deliberately (2026-10-02); turn `NativeCategory` off to keep Racing Line display-only.
- **Police** PURSUIT points (`Pursuit.Enabled` on by default; in multiplayer each player's own with `Multiplayer.Enabled`) count toward the run total that the game itself uploads to its Steam leaderboard. This was chosen deliberately, like RacingLine; set `Pursuit.Enabled = false` to keep chases out of the score.

CurbFeel does change gameplay, so try it solo or in a private lobby first.

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
  bump, clean build plus `tools/il2cpp-check.ps1`, IL2CPP pitfalls, whether every change to the game is restored when
  switched off, every exit path (level end, death, pause, breaker), all cars and maps, local-only / leaderboard-fair and
  honest docs, clashes with the other plugins, and no game assets or personal paths.
- **IL2CPP check** (no game run needed): finds game / Unity methods that were stripped from the game build (they
  compile, then throw "Method unstripping failed", e.g. `GUI.DrawTexture`) and MonoBehaviours that can't be injected
  (the plugin never loads). Builds without deploying into the game:
  ```
  powershell -ExecutionPolicy Bypass -File tools/il2cpp-check.ps1 [-Plugin A,B] [-NoBuild]
  ```
- **Before every push, run the push check:**
  ```
  powershell -ExecutionPolicy Bypass -File tools/push-check.ps1
  ```
  In Claude Code, ask for the **push-check** agent (`.claude/agents/push-check.md`; `CLAUDE.md` tells Claude to use it automatically). It flags:
  - being behind `origin/main`, and files both of us changed
  - a plugin whose code changed without a version bump, a version lower than what's already pushed, or one already used by a tag
  - duplicate plugin GUIDs, names or DLL names
  - a change to a shared file in `source/Shared/` without bumping every plugin that links it
  - hotkey clashes, and two plugins patching the same game method (hand-installed patches need a
    `// harmony-target: Type.Method` comment; `(cooperates with <Plugin>)` marks an overlap that was checked)
  - a commit that changes the other person's plugin without saying in its message who approved it
  - binaries, build output, `local.props` or game assets about to be committed
  - merge-conflict markers, and READMEs that state a different version than the code
- Hotkeys in use: F6/F7 (DriverCam), F8/F9/F10 (CurbFeel), F11 (HotReload, dev only), Ctrl+PageUp/PageDown/Home and F4 (TrafficDensity), F5 (RacingLine), F3 (Police), F2 (PitStop), F1 (EngineAudio), right stick / hold right mouse (HeadLook). All of F1-F11 are taken: new hotkeys need a modifier.
- Bump versions with `tools/bump-version.ps1 -Plugin <Name> -To patch|minor|major` (it counts up from the higher of your version and the pushed one, so we never collide). No bump for docs-only changes.

### Claude Code slash commands

Defined in `.claude/commands/`; `CLAUDE.md` has the full workflow and rules.

| Command | Does |
|---|---|
| `/whats-new` | what the other person pushed since your last sync (read-only) |
| `/sync` | update your game from the repo via repo-sync (dry run first, asks before closing the game) |
| `/status` | repo vs origin, plugin versions local / pushed / installed |
| `/build [Plugin] [--no-deploy]` | build one or all plugins (deploys into the game unless `--no-deploy`), then the IL2CPP check |
| `/game-log [plugins/topic] [since=n]` | log-reader agent: loaded versions, load failures, errors, and each plugin's expected log lines, for every install |
| `/audit [plugins] [approved: ...]` | code-auditor review of your diff |
| `/bump <Plugin> [patch\|minor\|major\|x.y.z]` | bump a version everywhere and show the diff |
| `/check` | run the push check and explain every FAIL/WARN |
| `/ship [message]` | pull --rebase, audit, bump, commit, push-check, push (never forced) |
| `/rollback [latest\|name]` | restore a game backup |
| `/research <topic>` | look up game types in RESEARCH.md and the BepInEx interop |
| `/new-plugin <Name>` | scaffold a new plugin to the repo conventions |
| `/test-tools` | run the push-check scenario tests |
| `/hot-reload` | build a plugin as a hot module (`-p:Hot=true`) into `BepInEx\hot\` and confirm HotReload picked it up, no game restart (see `source/HotReload/README.md`) |

## Credits

- CarSkins' BMW E46 model: **BMW E46 1998 low-poly car** by **TODO: author name** on Sketchfab (**TODO: model link**), [CC Attribution](https://creativecommons.org/licenses/by/4.0/). Decimated for this repo.

## Uninstall

Delete `BepInEx/plugins/DriverCam.dll`, `BepInEx/plugins/DriverCam/` and the other plugins' DLLs (`CurbFeel.dll`, `TrafficDensity.dll`, `RacingLine.dll`, `Police.dll`, `PitStop.dll`, `EngineAudio.dll`, `HeadLook.dll`, `RogueHub.dll`, `CarSkins.dll` with its `BepInEx/plugins/CarSkins/` folder, `Driver.dll` with its `BepInEx/plugins/Driver/` folder, `Reverse.dll`, `Bikes.dll` with its `BepInEx/plugins/Bikes/` folder). To remove BepInEx itself, also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx` and `dotnet` folders.

Unofficial fan mods. Not affiliated with Gravity Works or Cosmic Shift Studios. Game assets (ripped models, dumps) are not included.
