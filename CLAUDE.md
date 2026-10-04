# Driving Rogue mods (shared repo) - instructions for Claude

**What this is.** Source for BepInEx 6 IL2CPP plugins for the Unity game **Driving Rogue** (Steam), shared by two
developers who both push to `main` of `github.com/Poofafysh/DriverCam`:

| Developer | Owns | Git author |
|---|---|---|
| **Poofafysh** | `source/CurbFeel/` (curbs, sidewalks, lane splitting), `source/TrafficDensity/` (NPC traffic multiplier), `source/RacingLine/` (racing-line points) | Poofafysh |
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
   both plugins load.

## Daily workflow

1. **Sync** - `/whats-new` to see what the other developer pushed, `/sync` to pull + build + install with a backup.
2. **Change** - edit only what the task needs. Verify game types against the interop (`/research`).
3. **Audit** - `/audit` after any plugin code change; fix and re-run until **PASS**.
4. **Bump** - `/bump <Plugin> patch|minor|major` (see version rules).
5. **Ship** - `/ship`: `git pull --rebase`, audit, bump check, commit, push-check, push. **Never force-push.**

Mandatory rules even without the commands: run the **code-auditor** agent after plugin code changes, run the
**push-check** agent before every `git push` and push only on OK (or warnings the person accepted), use **repo-sync**
(never hand-copy DLLs) to update a game install, and `git pull --rebase` (never merge, reset or rewrite history).

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
| agent `code-auditor` | strict read-only PASS/FAIL review of the diff (scope, build, IL2CPP, reversibility, all cars/maps, local-only, cross-plugin, clean content) |
| agent `push-check` | pre-push gate: wraps `tools/push-check.ps1` + config-break / cross-plugin judgement; never pushes |
| agent `repo-sync` | backup, fast-forward pull, build, install, verify, rollback (wraps `tools/sync-install.ps1`) |
| `/sync` | update my game from the repo (dry run first, asks before closing the game) |
| `/whats-new` | read-only: incoming commits, version changes, config impact |
| `/status` | repo vs origin, versions local / origin / installed, game running |
| `/build [Plugin]` | `dotnet build -c Release` one or all plugins (deploys into the game) |
| `/game-log [text]` | summarize `BepInEx/LogOutput.log`: loads, versions, errors, CurbFeel/DriverCam diagnostics |
| `/audit` | run code-auditor on the current diff |
| `/bump <Plugin> [level]` | `tools/bump-version.ps1`, then show the diff |
| `/check [flags]` | `tools/push-check.ps1`, every FAIL/WARN explained with its fix |
| `/ship [message]` | full ship flow, ends with a push only on OK |
| `/rollback [latest\|name]` | restore a `backup/` snapshot into the game |
| `/research <topic>` | look up game types: RESEARCH.md, then the real interop assemblies |
| `/new-plugin <Name>` | scaffold `source/<Name>/` to repo conventions, then push-check |
| `/test-tools [words]` | run `tools/push-check.tests.ps1` (33 scenarios) |
| `/hot-reload` | build a plugin as a hot module (`-p:Hot=true`) into `BepInEx\hot\` and confirm HotReload picked it up, no game restart (see `source/HotReload/README.md`) |

Scripts (run from the repo root with `powershell -NoProfile -ExecutionPolicy Bypass -File <script>`):
`tools/push-check.ps1 [-NoFetch] [-NoGitHub] [-AllowIdentityChange]`,
`tools/bump-version.ps1 -Plugin <Name> -To <level>`,
`tools/sync-install.ps1 [-DryRun] [-CloseGame] [-Launch] [-Rollback latest|<name>] [-FromRev <sha>] [-GameDir <dir>] [-Force]`,
`tools/push-check.tests.ps1 [-Only <words>]`. Exit code 0 = OK; output ends with a `RESULT:` line.

## Registries (keep unique; push-check enforces)

| Plugin | GUID | DLL | Config file | Hotkeys | Screen area |
|---|---|---|---|---|---|
| DriverCam | `drivingrogue.drivercam` | `DriverCam.dll` (+ `plugins/DriverCam/`) | `drivingrogue.drivercam.cfg`, `plugins/DriverCam/cars/*.cfg` | F6 driver view, F7 Edit mode (also C/Y cycle, View button, L3+R3) | button on the left |
| CurbFeel | `rogue.curbfeel` | `CurbFeel.dll` | `rogue.curbfeel.cfg` | F8 panel, F9 reload config, F10 on/off | panel top-right |
| TrafficDensity | `rogue.trafficdensity` | `TrafficDensity.dll` | `rogue.trafficdensity.cfg` | Ctrl+PageUp/PageDown step, Ctrl+Home stock, F4 perf overlay (when [Perf] Enabled) | toast top-centre, perf overlay top-left (when [Perf] Enabled) |
| RacingLine | `rogue.racingline` | `RacingLine.dll` | `rogue.racingline.cfg` | F5 show/hide line | status line bottom-left, dots on the road |
| HotReload (dev only) | `rogue.hotreload` | `HotReload.dll` (+ modules in `BepInEx\hot\`) | `rogue.hotreload.cfg` | F11 reload hot modules | toast top-centre, below TrafficDensity's |

Not used by our plugins: F1-F3 (avoid F12, Steam screenshot). New plugins: GUID `rogue.<name>`, read keys through the Input
System (`Keyboard.current.f11Key` / `Key.F11`) so push-check can see clashes, and add a row here.

## Layout

```
source/Directory.Build.props   imports source/local.props (git-ignored) for every plugin
source/local.props.example     template for your GameDir
source/README.md               DriverCam architecture + build notes
source/DriverCam/              *.cs, DriverCam.csproj, Assets/ (cockpits .dcm, autofit.py), SavedSettings/ (shared car setups)
source/CurbFeel/               *.cs, CurbFeel.csproj, README.md (features, tuning table), RESEARCH.md (walls, curbs, damage)
source/TrafficDensity/         Plugin.cs (spawner patch + hotkeys, owns the shared Perf overlay), Fixes.cs (traffic AI fixes), README.md (how the game sizes traffic)
source/Shared/Perf.cs          shared timing helper, linked into plugins as source (<Compile Include="..\Shared\Perf.cs" Link="Perf.cs" />);
                               a change to it needs a bump of every plugin that links it (push-check doesn't see source/Shared)
source/RacingLine/             Racing Line score category: line build + preview, corner scoring, native provider, results row (design doc linked in Plugin.cs)
source/HotReload/              dev-only host: loads BepInEx\hot\*.dll from bytes, Unload/Load on change or F11 (DevOnly: /sync skips it)
tools/                         push-check, push-check.tests, bump-version, sync-install (PowerShell 5.1)
.claude/agents/ .claude/commands/
backup/                        sync-install backups (git-ignored, last 10 kept)
```
Each `.csproj` targets net6.0 against `$(GameDir)\dotnet`, `BepInEx\core` and `BepInEx\interop`, with a `DeployToGame`
target that copies the DLL (DriverCam: also cockpits and `SavedSettings/DriverCam_cars/*.cfg`) into `BepInEx\plugins`.

## BepInEx / IL2CPP gotchas

- `ClassInjector.RegisterTypeInIl2Cpp<T>()` **before** `AddComponent<T>()`; every injected `MonoBehaviour` needs a
  `(IntPtr ptr) : base(ptr)` constructor.
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
