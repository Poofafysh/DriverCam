---
description: Build one or all plugins in Release (deploys into the main game only when it is closed; DriverCam never deploys unless --deploy-drivercam), then il2cpp-check
argument-hint: "[Plugin | all] [--no-deploy] [--deploy-drivercam]"
---
Build plugin(s). Argument: `$ARGUMENTS` (a plugin folder name under `source/`, or empty / `all` for every plugin;
`--no-deploy` = compile only, copy nothing into the game; `--deploy-drivercam` = also deploy DriverCam, see step 4).
A deploying build only installs into `<GameDir>`; other installs (`<ExtraGameDirs>`) are updated with `/sync`.

1. Work out the targets: every `source/<Name>/` that contains a `.csproj` (empty or `all`), or just the named one
   (match case-insensitively; if it doesn't exist, list the valid names and stop). Skip a project with
   `<DevOnly>true</DevOnly>` (HotReload) unless it was named. Say `ETA: ~1 min per plugin (<n> plugins)` before
   building.
2. Check `source/local.props` exists and its `<GameDir>` folder contains `BepInEx/interop/Assembly-CSharp.dll`. If
   `local.props` is missing, say: copy `source/local.props.example` to `source/local.props` and set `GameDir`. If
   `interop` is missing, BepInEx hasn't finished its first run: start the game once, wait, close it.
3. **Decide deploy or not** (one rule, no judgement):
   - `--no-deploy` given -> no deploy.
   - Otherwise check the game:
     `Get-Process "Driving Rogue" -ErrorAction SilentlyContinue | Select-Object Path`. If any process's `Path` starts
     with `<GameDir>` or is empty -> no deploy, and say `game running: compile only (no deploy)`. Do not close the
     game unless the person asks.
   - Otherwise deploy.
4. **DriverCam never deploys by default.** Its `DeployToGame` copies the cockpits and
   `SavedSettings/DriverCam_cars/*.cfg` over the installed per-car setups, which hold the person's tuning. So DriverCam
   always builds with `-p:SkipDeploy=true`, unless `--deploy-drivercam` was given and step 3 says deploy. Then, around
   that one build:
   - before: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/car-setups.ps1 -GameDir "<GameDir>" -Save`
     (note `<folder>` from `CARS SAVED <n> <folder>`);
   - after, even if the build failed:
     `powershell -NoProfile -ExecutionPolicy Bypass -File tools/car-setups.ps1 -GameDir "<GameDir>" -Restore "<folder>"`.
     Exit 1 or any `CARS MISMATCH` -> `RESULT: FAIL`, report `<folder>` (the saved copies). `CARS NEW` lines go into
     the report.
   DriverCam is Aste-risks' plugin: building it is fine, editing it needs the person's approval.
5. For each target run `dotnet build -c Release source/<Name>`, adding `-p:SkipDeploy=true` when step 3 or 4 says no
   deploy. A deploying build's `DeployToGame` target copies the DLL into `<GameDir>\BepInEx\plugins`.
6. After the builds, run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/il2cpp-check.ps1 -NoBuild
   -Plugin <targets that built, comma-separated>`. It catches stripped game methods and classes that can't be
   injected, which compile fine but fail in game. Read `$LASTEXITCODE`: exit 2 = the check did not run, whatever it
   printed; report its last line, never "OK". Skip it when no target built.
7. Report in exactly this shape:
   ```
   BUILD <Plugin>: OK | FAILED (<n> errors) | WARN (<n> warnings); deployed: yes | no (--no-deploy | game running | SkipDeploy default | build failed)
   (one BUILD line per target)
   il2cpp-check: <its RESULT or last line> (exit <n>) | skipped (nothing built)
   CARS: <only for a DriverCam deploy: RESULT line of car-setups.ps1 -Restore, plus any CARS NEW / MISMATCH lines>
   RESULT: OK | WARN | FAIL
   ```
   `RESULT: FAIL` = any build failed, il2cpp-check exit 1 or 2, or a car-setups restore failed; `WARN` = builds OK
   but with warnings in a file changed since `origin/main` (`git diff --name-only origin/main` plus untracked), or
   MSB3021/MSB3027 (built, not installed); otherwise `OK`.
   - **MSB3021 / MSB3027** ("Unable to copy file", "being used by another process") = the game has the DLL locked.
     The build succeeded; close the game and build again (or use `/sync`) to install it.
   - **CS0246 / CS0234** for `Il2Cpp*`, `UnityEngine.*`, `BepInEx.*` types = wrong `GameDir` or missing interop.
   - Other errors are real; show them (file:line + message) and offer to fix.
