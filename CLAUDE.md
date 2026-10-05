# Driving Rogue mods (shared repo) - instructions for Claude

**What this is.** Source for BepInEx 6 IL2CPP plugins for the Unity game **Driving Rogue** (Steam), shared by two
developers who both push to `main` of `github.com/Poofafysh/DriverCam`:

| Developer | Owns | Git author |
|---|---|---|
| **Poofafysh** | `source/CurbFeel/` (curbs, sidewalks, lane splitting), `source/TrafficDensity/` (NPC traffic multiplier), `source/RacingLine/` (racing-line points), `source/Police/` (police patrols and chases, daredevil rivals), `source/PitStop/` (refill-health key), `source/EngineAudio/` (engine sound, tyre squeal), `source/HeadLook/` (look around), `source/RogueHub/` (one menu for every mod) | Poofafysh |
| **Aste-risks** | `source/DriverCam/` (first-person camera, cockpits, mirrors, shared car setups) | Alec DeMilt |

Repo tooling (`tools/`, `.claude/`, docs) is shared. Don't edit the other developer's plugin without the person asking.
The repo is **source only**: no DLLs, no BepInEx, no zips. Everyone builds locally; the build deploys into their game.

## First-time setup (once per machine)

1. **BepInEx 6 IL2CPP x64, be.788 or newer** (<https://builds.bepinex.dev/projects/bepinex_be>) unzipped into the game
   folder, so `winhttp.dll` and `BepInEx/` sit next to `Driving Rogue.exe`.
2. **Start the game once** and wait for BepInEx's first run to finish (minutes): it generates `BepInEx/interop/`, which
   every plugin compiles against. Close the game.
3. **.NET SDK 6+** (8 is fine). Nothing is downloaded from NuGet; projects reference the game's own `dotnet/` folder.
4. **`source/local.props`**: copy `source/local.props.example`, set `<GameDir>` to the game folder. It is git-ignored and
   loaded for every plugin by `source/Directory.Build.props`. Never put personal paths in a `.csproj`.
5. **Build**: `/build` (or `dotnet build -c Release` in each `source/<Plugin>`), game closed. Then `/game-log` to confirm
   the plugins load. More than one copy of the game? List the others in `<ExtraGameDirs>` in `source/local.props`
   (semicolon-separated): `/sync`, `/status` and the log-reader cover them too.

## Daily workflow

1. **Sync** - `/whats-new` to see what the other developer pushed, `/sync` to pull + build + install with a backup.
2. **Change** - edit only what the task needs. Verify game types against the interop (`/research`).
3. **Audit** - `/audit` after any plugin code change; fix and re-run until **PASS**. Tell it the plugins in scope and
   anything the person approved (another session may have changes in the same working tree).
4. **Bump** - `/bump <Plugin> patch|minor|major` (see version rules).
5. **Ship** - `/ship`: `git pull --rebase`, audit, bump check, commit, push-check, then push only if you said
   "push" (otherwise it prints the push command). **Never force-push.**

Mandatory rules even without the commands: run the **code-auditor** agent after plugin code changes, run the
**push-check** agent before every `git push` and push only on OK (or warnings the person accepted), use **repo-sync**
(never hand-copy DLLs) to update a game install, and `git pull --rebase` (never merge, reset or rewrite history).
A build only to check code uses `-p:SkipDeploy=true`: a plain DriverCam build copies the repo's car setups over the
installed ones. A commit that changes the other developer's plugin says in its message who approved it.

## Version rules

- SemVer `MAJOR.MINOR.PATCH` per plugin. PATCH = fix / tuning / asset change; MINOR = new feature or setting;
  MAJOR = breaking change to config or behaviour.
- **Bump** on any change to a plugin's code, its shipped assets (e.g. DriverCam cockpits) or its shipped
  `SavedSettings/` (e.g. `DriverCam_cars/*.cfg`). **No bump** for docs-only changes.
- **Never reuse a version or go at/below the one on `origin/main`**, and never one that's already tagged.
- Always use `tools/bump-version.ps1 -Plugin <Name> -To patch|minor|major|x.y.z`. It counts up from the **higher** of
  the local and `origin/main` version and updates `Plugin.cs`, `.csproj` version tags, the plugin README
  "Current version" and the root README table. It does not touch hard-coded strings: DriverCam's
  `"DriverCam x.y.z loaded"` log line must be updated by hand (push-check warns). New code should log `{Version}`.
- **Collisions** (both of you bumped the same plugin): after `git pull --rebase`, resolve any conflict in favour of
  the higher version, then re-run `bump-version` - it lands above both. push-check FAILs on equal or lower versions.

## Agents and commands

| | What it does |
|---|---|
| agent `code-auditor` | strict read-only PASS/FAIL review of the diff, 9 questions (scope + approvals, build + `il2cpp-check`, IL2CPP with a `NAME` interop check per string-named game member (common names need evidence on their type), reversible + cheap with a `PERF` line per per-frame body, lifecycle exit-path table, all cars/maps, local + leaderboard-fair (`LB` line per guarded write) + honest, cross-plugin registry, clean content incl. no game-exported models); FAILs if the scoped files change during the audit (another session) |
| agent `push-check` | pre-push gate: wraps `tools/push-check.ps1` + `tools/il2cpp-check.ps1` + config-break / cross-plugin judgement; uncommitted / untracked-file warnings are listed as "not in this push", every other WARN is accepted only by the person's yes to its exact text; never pushes |
| agent `repo-sync` | backup, fast-forward pull, build, install, verify (SHA256 per DLL/asset), rollback (wraps `tools/sync-install.ps1`); syncs `<GameDir>`, and `<ExtraGameDirs>` only when asked; every run gets `-GameDir` and the pre-pull `-FromRev`; wraps every run in `tools/car-setups.ps1` (installed DriverCam car setups saved and put back; repo car setups never go into the frozen copy); writes `install.txt` into each backup; skips an install whose game is running |
| agent `log-reader` | read-only BepInEx log report per install: loads vs repo versions, load failures, grouped errors, each plugin README's expected log lines; `since=<n>:<hash>` for only new lines during a playtest (detects a game restart) |
| agent `playtest-analyst` | read-only playtest report: log since `since=`, fixed per-feature stats (chases, daredevil races, Racing Line live scoring, PitStop, EngineAudio, Perf), repeats counted across checks in a state JSON, each repeated issue with its printing `file:line`, a proposed fix and whether log-watch autonomy allows it (never daredevils / DriverCam / HeadLook) |
| agent `asset-builder` | Blender headless rebuild of DriverCam interiors (`build_interior.py`), gauge faces (`gauges.py`) and Police models (`police_models.py`) from snapshots into a scratch folder; budget table old vs new (`tools/asset-budget.ps1`); writes into `source/` only when asked, with ownership check and (DriverCam) approval; never deploys or touches car setups |
| agent `perf-budget` | read-only perf report: `tools/perf-scan.ps1` hits classified per-frame / while-missing / throttled / once with quoted evidence, `[Perf]` log timings, model budgets, fixed score per plugin, 5%-fps arithmetic |
| agent `gauge-check` | read-only: each cockpit's `n` / `dg` / `db` gauge lines vs `specs.py` DIALS, both face atlases present, HUD speed multipliers in `Gauges.cs`, real top speed per car vs its dial and the RPM source from the log |
| agent `release-notes` | read-only fixed-format notes per plugin between two revs (default `origin/main..HEAD`, `worktree` adds uncommitted): versions, commit subjects, settings added / removed / default changed, registry hotkey / screen changes, bump-level checks |
| agent `docs-sync` | read-only: `tools/docs-check.ps1` (README log checklist vs real Log calls, settings vs `Config.Bind`) sorted into REAL drift vs false alarms by fixed rules |
| `/playtest [since=] [new]` | playtest-analyst with a per-session state file; says which autonomous fixes it will make, with ETA |
| `/assets [what] [cars] [budget]` | asset-builder (scratch rebuild + budgets), or just `tools/asset-budget.ps1` with `budget` |
| `/perf [plugins] [scan]` | perf-budget agent, or just `tools/perf-scan.ps1` with `scan` |
| `/gauges [cars]` | gauge-check agent |
| `/release-notes [since <rev>]` | release-notes agent |
| `/docs-sync [plugins]` | docs-sync agent |
| `/claim <paths> -- <task>`, `/release [paths]`, `/claims [check <paths>]` | session ownership ledger `.claude/ownership.md` via `tools/ownership.ps1`: claim before editing, refused on overlap with another session; `/audit` and `/ship` check it |
| `/sync [--all-installs]` | update my game from the repo (dry run first, asks before closing the game; extra installs only with `--all-installs` or `-GameDir`) |
| `/whats-new` | read-only: incoming commits, version changes, config impact |
| `/status` | repo vs origin, versions local / origin / installed, game running |
| `/build [Plugin] [--no-deploy] [--deploy-drivercam]` | `dotnet build -c Release` one or all plugins, then `il2cpp-check`; deploys into `<GameDir>` only when that game is closed; DriverCam builds with SkipDeploy unless `--deploy-drivercam` (then its car setups are backed up and put back) |
| `/game-log [plugins/topic] [since=n]` | log-reader agent on `BepInEx/LogOutput.log` of each install |
| `/audit [plugins] [approved: ...]` | run code-auditor on the current diff, with scope and approvals |
| `/bump <Plugin> [level]` | `tools/bump-version.ps1`, then show the diff |
| `/check [flags]` | runs the push-check agent (flags passed through) and explains every FAIL/WARN in plain words |
| `/ship [message]` | full ship flow up to a commit and push-check; pushes only on OK and only if the person said "push" |
| `/rollback [latest\|name] [install]` | restore a `backup/` snapshot into an install (always with `-GameDir`; `latest` only with one install; refuses a backup whose `install.txt` names another install) |
| `/research <topic>` | look up game types: RESEARCH.md, then the real interop assemblies; dump.cs / IDA / AssetRipper facts stay `UNVERIFIED` until found in the interop |
| `/new-plugin <Name>` | scaffold `source/<Name>/` to repo conventions, then push-check |
| `/test-tools [words]` | run `tools/push-check.tests.ps1` (44 scenarios) |
| `/hot-reload` | build a plugin as a hot module (`-p:Hot=true`) into `BepInEx\hot\` and confirm HotReload picked it up, no game restart (see `source/HotReload/README.md`) |

Scripts (run from the repo root with `powershell -NoProfile -ExecutionPolicy Bypass -File <script>`):
`tools/push-check.ps1 [-NoFetch] [-NoGitHub] [-AllowIdentityChange]`,
`tools/il2cpp-check.ps1 [-Plugin A,B] [-NoBuild] [-GameDir <dir>]` (builds with SkipDeploy, then `tools/IlCheck`: stripped
methods BepInEx couldn't rebuild, MonoBehaviours that can't be injected),
`tools/bump-version.ps1 -Plugin <Name> -To <level>`,
`tools/sync-install.ps1 [-DryRun] [-CloseGame] [-Launch] [-Rollback latest|<name>] [-FromRev <sha>] [-GameDir <dir>] [-Force]`,
`tools/car-setups.ps1 -GameDir <dir> -Save | -Restore <folder> [-MoveNew]` (saves / puts back and hash-checks an
install's `plugins\DriverCam\cars\*.cfg` around anything that deploys DriverCam),
`tools/push-check.tests.ps1 [-Only <words>]`,
`tools/asset-budget.ps1 [-Path <files or folders>]` (tris / draws / mirrors per `.dcm` / `.pcm` against fixed budgets),
`tools/perf-scan.ps1 [-Plugin A,B] [-Depth 3]` (code reached from per-frame entry points: SCAN, GETCOMP, ALLOC, LINQ,
STRING, LAMBDA hits), `tools/docs-check.ps1 [-Plugin A,B]` (README vs code),
`tools/ownership.ps1 -List | -Claim -Paths <p> -Task <t> [-Hours n] | -Check -Paths <p> | -Release [-Paths <p>]`
(exit 1 = held by another session). Exit code 0 = OK; output ends with a `RESULT:` line (il2cpp-check:
exit 2 = could not run, never a pass, whatever it printed: read `$LASTEXITCODE`). A dry run is
`sync-install.ps1 -DryRun -Force -GameDir <dir> -FromRev <base>` (without `-Force` it stops at uncommitted changes
before reporting anything; without `-FromRev` an install synced after the pull compares against the new HEAD; a dry
run changes nothing).
Every agent's and command's report also ends with one line `RESULT: OK | WARN | FAIL | BLOCKED` (BLOCKED = it
stopped with a `NEEDS ANSWER:` question for the person; agents can't ask the person themselves, the caller does).
`sync-install.ps1` writes every install's backup into the same `backup/`, so after syncing several installs roll
back by name with `-GameDir`, never `latest`.

**Several sessions share this working tree.** Files this conversation didn't create or edit are another session's:
never stage, revert, stash, format or audit them, and leave them out of `/ship` (stage by name). If a file you edit
also has someone else's hunks, ask before shipping it. Before editing a plugin's files, `/claim` them (refused when
another session holds them; `/claims` lists who holds what) and `/release` when done.

## Registries (keep unique; push-check enforces)

| Plugin | GUID | DLL | Config file | Hotkeys | Screen area |
|---|---|---|---|---|---|
| DriverCam | `drivingrogue.drivercam` | `DriverCam.dll` (+ `plugins/DriverCam/`) | `drivingrogue.drivercam.cfg`, `plugins/DriverCam/cars/*.cfg` | F6 driver view, F7 Edit mode (also C/Y cycle, View button, L3+R3) | button on the left (dev tool, off by default since 0.11.1: `UI.ShowButton`, also in Rogue Hub > Camera); reads AppDomain data `rogue.headlook` (HeadLook) and `rogue.engineaudio` (EngineAudio's RPM, for its working tachometer); publishes AppDomain data `rogue.drivercam` (+ `rogue.drivercam.car`): seat / eye / wheel for Driver (DriverLink.cs) |
| CurbFeel | `rogue.curbfeel` | `CurbFeel.dll` | `rogue.curbfeel.cfg` | F8 panel, F9 reload config, F10 on/off | panel top-right; Harmony: optional skip-only prefix on `LeaderboardsManager.PublishEntry` (cooperates with PitStop and Sandbox) for F.Drift runs (`F.Drift.KeepOffLeaderboards`, default off) |
| TrafficDensity | `rogue.trafficdensity` | `TrafficDensity.dll` | `rogue.trafficdensity.cfg` | Ctrl+PageUp/PageDown step, Ctrl+Home stock, F4 perf overlay (when [Perf] Enabled) | toast top-centre (in RogueHub's notifications when the hub is installed), perf overlay top-left (when [Perf] Enabled); Harmony: optional skip-only prefix on `LeaderboardsManager.PublishEntry` (cooperates with PitStop and Sandbox) |
| RacingLine | `rogue.racingline` | `RacingLine.dll` | `rogue.racingline.cfg` | F5 show/hide line | line drawn on the road ahead, HUD card bottom-left (uGUI, sorting 480); score category `rogue.racingline` (RACING LINE) with a row on the results screen and the Victory screen (Shared/ScoreRows.cs rank 1: right after Near Miss); publishes the line as AppDomain data `rogue.racingline` (Police daredevils); Harmony: optional skip-only prefix on `LeaderboardsManager.PublishEntry` (cooperates with PitStop and Sandbox) for exit-boost runs (`ExitBoost.KeepOffLeaderboards`, default on) |
| Police | `rogue.police` | `Police.dll` (+ `plugins/Police/` car models) | `rogue.police.cfg` | F3 patrols on/off | pursuit panel + banners top-centre below TrafficDensity's toast (uGUI, sorting 490), 3D markers above patrols; score category `rogue.police` (PURSUIT) with a row on the results screen and the Victory screen (Shared/ScoreRows.cs rank 2: after RACING LINE); daredevils read RacingLine's `rogue.racingline` AppDomain data; multiplayer (0.8.0): its own Steam networking channel 7741 (never the game's Mirror state) |
| PitStop | `rogue.pitstop` | `PitStop.dll` | `rogue.pitstop.cfg` | F2 refill health (not while paused) | none of its own; results in RogueHub's notifications when the hub is installed |
| EngineAudio | `rogue.engineaudio` | `EngineAudio.dll` | `rogue.engineaudio.cfg` | F1 EngineAudio / game sound | one-line readout mid-left (when [Debug] Overlay); publishes AppDomain data `rogue.engineaudio` (simulated RPM, idle, redline, gear, live flag + timestamp; DriverCam gauges) |
| HeadLook | `rogue.headlook` | `HeadLook.dll` | `rogue.headlook.cfg` | right stick axes, hold right mouse (no key) | none; turns the game camera (DriverCam's driver view reads `rogue.headlook` AppDomain data). Harmony: prefixes `CameraControllerInGame.Update/LateUpdate/FixedUpdate`, which DriverCam postfixes (compatible; marked `cooperates with DriverCam` in HeadLook's `// harmony-target:` comment) |
| RogueHub | `rogue.hub` | `RogueHub.dll` | `rogue.hub.cfg` | backtick = quick menu, Shift+backtick = hub (keyboard); LB+RB = quick menu (controller); MODS button in the pause menu | hub full screen while paused (canvas 590; its backdrop blocks uGUI clicks; CurbFeel hides its IMGUI panel while the hub or quick menu is up (HubLink "open"); while the hub is up DriverCam's button is switched off through its own UI.ShowButton, not saved, and switched back on and saved when it closes); quick menu left at x 230, right of DriverCam's button (580); notifications top-right below the game's score stack (600). LB+RB counts only when both are pressed within 0.15 s (DriverCam's Edit mode holds one as a modifier). The quick menu takes the d-pad / face buttons / arrow keys from the game's Player and Dev maps while open. Reads every plugin's config through the chainloader; HubLink registry in AppDomain `rogue.hub.v1`. Harmony: postfix `PauseMenuPanel.Awake` |
| Sandbox | `rogue.sandbox` | `Sandbox.dll` | `rogue.sandbox.cfg` | none (SANDBOX button on the main menu, under Singleplayer) | SANDBOX tag top-left during sandbox races (uGUI, sorting 470); the shop's own hidden all-cards picker shown. Sandbox runs: free cards / rerolls, selling pays 0, locked mods offered, 10 + ExtraSlots mod slots, road length x1-x5 (default x2, one-time config migration `[State] DefaultsVersion`), sandbox maps (Maps*.cs, class `WideRoads`, `[Maps] WideRoads` default on, `Width` 30 m / `Lanes` 6: sets the global `RoadTileContainerSO` width / lane count per sandbox race and rebuilds the lanes, builds `fx_WideRoad` (road, guardrail, street-light poles) / `fx_WideStreet` / `fx_WideWall` per tile plus 8 pooled point lights `fx_WideLight`, hides the stock tile with `Renderer.forceRenderingOff`, disables its non-trigger colliders and Lights (all restored); Scenery skips those tiles). Harmony (all by hand, `// harmony-target:` in Guards.cs / State.cs / Shop.cs / RoadLength.cs / MenuButton.cs / Maps.cs; Maps: postfixes `RunWorldManager.ShouldSpawnGasStation` / `ShouldSpawnCarWash` (false only in sandbox races), prefix `LevelGenerator.SpawnTilesCoroutine` (sets the width, never skips); a multiplayer client's width is set by Multiplayer.cs's `GenerateMultiplayerLevelAsClient` prefix through `WideRoads.Apply`): skip-prefixes on every record write (leaderboards incl. `LeaderboardsManager.PublishEntry` (cooperates with PitStop), achievements, missions, XP, credits, boss progress, unlocks, statistics); a failed guard = no SANDBOX button. `Sandbox.Plugin.Active` (public static) is true during a sandbox race (read by Scenery*.cs), and during an agreed multiplayer sandbox race (`Plugin.Recompute` / `ActiveNow` call `Multiplayer.SandboxAgreed` / `GuardNow`; `WideRoads.Apply` / `ClampW` / `ClampN` read the host's map settings through `Multiplayer.Wide` / `Width` / `Lanes`); multiplayer lobby panel bottom-left in the main-menu lobby (uGUI, sorting 471); its own Steam networking channel 7742 (never the game's Mirror state); Multiplayer.cs Harmony: GameCoordinatorManager.StartMultiplayerFromHost / NextMultiplayerFromHost (prefix + postfix), LevelGenerator.GenerateMultiplayerLevelAsClient (prefix), StartNewSingleplayerGame / TryRestoreSingleplayerGame (prefix, cooperates with State.cs) |
| CarSkins | `rogue.carskins` | `CarSkins.dll` (+ `plugins/CarSkins/` models) | `rogue.carskins.cfg` | none | none; player car only (single-player): hides the game's body/wheel meshes with `Renderer.forceRenderingOff` (keeps `enabled`, so DriverCam's body measurement and HideCarBody are unaffected) and parents its model under the body node and the wheel spin pivots |
| Driver | `rogue.driver` | `Driver.dll` (+ `plugins/Driver/` driver.drm, driver_anims.dra, *.png) | `rogue.driver.cfg` | none | none on screen (3D driver in the player car; head-less body in DriverCam's driver view, hidden in chase views by default, `Renderer.forceRenderingOff` while hidden); reads AppDomain `rogue.drivercam` (DriverCam) and `rogue.headlook` (HeadLook); reads DriverCam's `cockpit_*.dcm` / `DriverCam_cars/*.cfg` read-only; no Harmony; on a Bikes motorcycle: Driver_Root parented under the bike's `Bikes.Lean` (reads node names Bikes.Lean / Bikes.Bars / Bikes.<Key>_Body), reads AppDomain `rogue.bikes.rider.<Key>` |
| Bikes | `rogue.bikes` | `Bikes.dll` (+ `plugins/Bikes/` models; `BMW_M2_G87.csm` tracked, CC BY-NC-SA) | `rogue.bikes.cfg` | none | none on screen; two bikes (S1000RR, Sport Bike) and one car (M2 G87, a car model on the donor's spin pivots) appended to GeneralReferencesData.VehicleContainer (single-player; removed in multiplayer / switched off / error breaker, deferred while one is selected or driven); car meshes under their bodies hidden with `Renderer.forceRenderingOff` (`enabled` kept for DriverCam); publishes AppDomain data `rogue.bikes.rider.S1000RR` / `.SportBike` (float[11] rider sockets; Driver) and the stable names `Bikes.<Key>_Body` / `Bikes.Lean` / `Bikes.Bars`; Harmony (by hand, `// harmony-target:` in Guards.cs, all-or-nothing): prefix `VehicleGarageManager.Awake`, postfix `Vehicle_SO.get_IsUnlocked`, postfixes `VehicleGarageManager.SaveData..ctor` / `SnapshotData..ctor`, skip-only prefixes `LeaderboardsManager.PublishEntry` (cooperates with PitStop, Sandbox, RacingLine, CurbFeel, TrafficDensity) and `SteamLeaderboardsManager.PublishEntry` (cooperates with Sandbox), prefix `NetworkPlayer.CMD_SetVehicleAndVynilIndex`, postfixes `VehicleSkinHolder.Awake` / `InitializeVehicleMeshes` / `SetVehiclePart` |
| Declutter | `rogue.declutter` | `Declutter.dll` | `rogue.declutter.cfg` | none | none on screen; hides Biomes renderers on road tiles with `Renderer.forceRenderingOff` (keeps `enabled`, so CurbFeel / DriverCam / LODGroups are unaffected) when under `SmallSize` and beyond `SmallDistance`, or beyond `FarDistance`, from the tile's PathWaypoints (sorted once the tile stands still); keeps layers 11/15/28 and road / curb / wall / tunnel / bridge names; no colliders; single-player and multiplayer (cosmetic, local); stands aside for a whole Sandbox run (reads `Sandbox.Plugin.Active` / `InSandboxRun` by reflection); no Harmony |
| Reverse | `rogue.reverse` | `Reverse.dll` | `rogue.reverse.cfg` | none (hold the brake at a standstill) | none; player car only (single-player): reverses by an extra `VelocityChange` on the rigidbody while the game's own speed stays 0; publishes AppDomain data `rogue.reverse` ([0] reversing, [1] speed m/s, [2] timestamp). Harmony (by hand, `// harmony-target:` in Hooks.cs): prefix + postfix `VehicleMovement.ApplyMovement`, prefix + finalizer `VehicleMovement.HandleCarRotation` (swaps TargetSpeed / SmoothTurnInput / CurrentTurnSpeed for that call only, restored in the finalizer) |
| HotReload (dev only) | `rogue.hotreload` | `HotReload.dll` (+ modules in `BepInEx\hot\`) | `rogue.hotreload.cfg` | F11 reload hot modules | toast top-centre, below TrafficDensity's |

All of F1-F11 are taken (avoid F12, Steam screenshot); new hotkeys need a modifier (e.g. Ctrl+F1). New plugins: GUID `rogue.<name>`, read keys through the Input
System (`Keyboard.current.f11Key` / `Key.F11`) so push-check can see clashes, and add a row here. Harmony patches
installed by hand (`harmony.Patch`) carry a `// harmony-target: Type.Method` comment, plus `(cooperates with <Plugin>)`
once an overlap has been checked, so push-check can compare them. Shared AppDomain data (`rogue.headlook`) is listed
in the row of the plugin that publishes it.

## Layout

```
source/Directory.Build.props   imports source/local.props (git-ignored) for every plugin
source/local.props.example     template for your GameDir
source/README.md               DriverCam architecture + build notes
source/DriverCam/              *.cs, DriverCam.csproj, Assets/ (cockpits .dcm, autofit.py), SavedSettings/ (shared car setups)
source/CurbFeel/               *.cs, CurbFeel.csproj, README.md (features, tuning table), RESEARCH.md (walls, curbs, damage)
source/TrafficDensity/         Plugin.cs (spawner patch + hotkeys, owns the shared Perf overlay), Fixes.cs (traffic AI fixes), README.md (how the game sizes traffic)
source/Shared/Perf.cs          shared timing helper, linked into plugins as source (<Compile Include="..\Shared\Perf.cs" Link="Perf.cs" />);
                               a change to it needs a bump of every plugin that links it (push-check enforces this)
source/Shared/Fx.cs, UiKit.cs  world-effect material/textures and the uGUI HUD toolkit (linked by RacingLine and Police; same bump rule)
source/Shared/ScoreRows.cs     a mod score category's rows on the results and Victory screens (ModScoreRows, one per category,
                               ordered by rank); linked by RacingLine and Police (same bump rule)
source/Shared/FastMath.cs      plain C# math on struct fields for per-frame loops (Unity's Mathf / Vector3 / Color helpers and even
                               `new Vector3` are slow il2cpp_runtime_invoke calls in the interop); linked by DriverCam, CurbFeel,
                               RacingLine, EngineAudio, HeadLook, Driver (same bump rule)
source/Shared/HubLink.cs       optional Rogue Hub hooks (Meta tags for ranges/labels, Action, Status, Toast); linked by TrafficDensity, CurbFeel, PitStop, RogueHub, Declutter
source/RacingLine/             Racing Line score category: line build + preview, corner scoring, native provider, row data for the results and Victory screens (design doc linked in Plugin.cs)
source/Police/                 police patrols (traffic cars), noticing, chase with lead bar, PURSUIT score category (PursuitScore, GameApi.Score), daredevils (design doc named in Plugin.cs)
source/PitStop/                F2 refills the player car's health (VehicleHealth.Heal), single-player only; Harmony prefix on
                               LeaderboardsManager.PublishEntry keeps refilled runs off the Steam leaderboards
source/EngineAudio/            engine sound from a simulated RPM (EngineModel), grain playback of the game's clips (Layer), traffic pitch patch; publishes AppDomain "rogue.engineaudio" for DriverCam's gauges (EngineLink.cs)
source/HeadLook/               head turn from right stick / right mouse: camera offset before render, restored before the game's camera code; publishes AppDomain "rogue.headlook" for DriverCam (HeadLookLink.cs)
source/RogueHub/               the hub: Catalog (every plugin's config + HubLink), HubView / QuickView / ToastView (own uGUI toolkit Ui.cs), Inputs, GameApi (pause, menu lock, driving-button overrides, MODS button)
source/Sandbox/                sandbox run mode: SANDBOX main-menu button (MenuButton), run flag + run entry points (State), record guards (Guards), free shop / 20 slots / all-cards picker (Shop), road length (RoadLength), sandbox maps: width + hooks (Maps.cs, class WideRoads), road build (Maps.Build), per-frame driver (Maps.Runner), HUD tag (Runner); Multiplayer.cs / MultiplayerNet.cs / MultiplayerLobby.cs = multiplayer sandbox (Steam channel 7742, lobby panel); Scenery.cs = sandbox scenery (switches off Biomes renderers via forceRenderingOff, non-trigger colliders and lights on non-wide tiles, restored after; CurbFeel reads these tiles)
source/CarSkins/               player car skins (cosmetic): CarModel (.csm loader), Skinner (fit, hide, restore), Assets/ (build_e46.py, BMW_E46.csm, texture; CC BY credit in the README)
source/Driver/                 3D driver in the player car: RigFile (.drm/.dra loader), Solver (fit, arm / leg IK, plain maths), SeatSource (DriverCam live / files / estimate), DriverRig (skinned mesh, CPU fallback), Runner; Assets/model/ (build_driver.py, original model + FBX for Unreal)
source/Bikes/                  sport bikes as new garage vehicles: Garage (Vehicle_SO clones + bike body template), Guards (save/snapshot/leaderboard/MP), Runner (wheels, lean, hiding), BikeModel (.csm loader), Assets/ (build_bike.py; models gitignored, licences unchecked)
source/Declutter/              hides far / small scenery on road tiles (Runner: tile poll, settle check, sliced sort, restore)
source/Reverse/                reverse gear (hold the brake at a standstill): Hooks (ApplyMovement / HandleCarRotation patches, state), Runner (car, exit paths), ReverseLink (AppDomain "rogue.reverse")
source/HotReload/              dev-only host: loads BepInEx\hot\*.dll from bytes, Unload/Load on change or F11 (DevOnly: /sync skips it)
tools/                         push-check, push-check.tests, bump-version, sync-install, il2cpp-check, car-setups,
                               asset-budget, perf-scan, docs-check, ownership (PowerShell 5.1);
                               IlCheck/ (C# console run by il2cpp-check; targets the installed SDK, no NuGet)
.claude/agents/ .claude/commands/
.claude/ownership.md           session ownership ledger (tools/ownership.ps1 writes it; local state, never commit)
backup/                        sync-install backups (git-ignored, last 10 kept)
```
Each `.csproj` targets net6.0 against `$(GameDir)\dotnet`, `BepInEx\core` and `BepInEx\interop`, with a `DeployToGame`
target that copies the DLL (DriverCam: also cockpits and `SavedSettings/DriverCam_cars/*.cfg`) into `BepInEx\plugins`.

## BepInEx / IL2CPP gotchas

- `ClassInjector.RegisterTypeInIl2Cpp<T>()` **before** `AddComponent<T>()`; every injected `MonoBehaviour` needs a
  `(IntPtr ptr) : base(ptr)` constructor.
- In an injected `MonoBehaviour`, **no local functions that use both locals/parameters and `this`**: they compile to an
  instance method taking a `ref` to a compiler-made struct, and `RegisterTypeInIl2Cpp` throws a NullReferenceException
  in `ConvertMethodInfo` (the plugin fails to load). Make them `static`, move them to a helper class, or mark them
  `[HideFromIl2Cpp]`. Lambdas and static methods are fine. `tools/il2cpp-check.ps1` finds these.
- **Stripped game methods**: the game build only kept the methods it uses. The interop still lists every method and
  BepInEx rebuilds the simple ones, but one it couldn't rebuild compiles fine and throws "Method unstripping failed"
  in game (e.g. `GUI.DrawTexture`, even the short overloads, which call the full one). `tools/il2cpp-check.ps1`
  follows the calls and finds these; run it after every build that adds engine calls.
- No `??`, `?.` or `??=` on Unity objects (they skip Unity's destroyed-object check); use `== null`. For generic
  Il2Cpp lookups use the `Il2CppType.Of<T>()` overloads plus `TryCast` (e.g. `Resources.FindObjectsOfTypeAll`).
- Anything a plugin starts in the game (a score action, a chase, a muted sound, a camera offset) must be ended on every
  exit path: level end, death, restart, quit to menu, car change, toggle off, error breaker, `OnDestroy`; and while
  paused (`Time.timeScale <= 0`) a plugin stops reading input and writing to the game.
- **Keep references** to Unity objects you create (GameObject, Mesh, Material, RenderTexture) in a field/list or
  DriverCam's `Keep.Hold`, or the GC collects their wrappers. Null-check Unity objects with `== null`, not `is null`.
- Copy Il2Cpp arrays to managed arrays before heavy loops. No whole-scene scans every frame; catch and throttle
  exceptions in `Update`.
- **Verify type and member names against `BepInEx/interop/*.dll`**, not only Il2Cpp dumps or AssetRipper exports;
  confirm runtime object names/layers in the game log.
- Restore every change to game state when a feature or the plugin is switched off; restore shared
  ScriptableObject edits made in a Harmony prefix in the postfix. Never touch Mirror-networked state.
- The game **locks loaded plugin DLLs**: a build while it runs compiles fine but the deploy fails with
  **MSB3021/MSB3027**. Close the game (or `/sync` with consent) and rebuild.
- **Tool scripts run on Windows PowerShell 5.1**: no `&&`/`||`, `??`, `?:` or `?.`; don't redirect native stderr with
  `2>&1` (use the scripts' `GitOut` pattern); `Get-Content` reads BOM-less UTF-8 as ANSI and `Set-Content` writes
  ANSI, so a file you rewrite must go through `[IO.File]::ReadAllText(path, UTF8)` / `WriteAllText` with the file's
  own BOM kept (`bump-version.ps1`'s `Read-Utf8`/`Set-File`); don't put literal non-ASCII in a `.ps1` (use
  `[char]0x2192`); wrap single results in `@()` before `.Count`; run with `-ExecutionPolicy Bypass`. If you change
  `push-check.ps1` or `bump-version.ps1`, add a scenario to `push-check.tests.ps1` and run `/test-tools`.

## Never commit

Built binaries (`*.dll`, `*.exe`, `*.pdb`, `bin/`, `obj/`), `BepInEx/`, `dotnet/`, `winhttp.dll`, doorstop files,
zips/releases, `source/local.props`, `backup/`, game-owned assets (AssetRipper output, Il2Cpp dumps, exported car models,
`source/DriverCam/Assets/cars/`, `cockpit.blend`), credentials. Facts and names about the game in docs are fine.
Stage files by name; `.gitignore` covers most of this, and push-check FAILs on the rest.
