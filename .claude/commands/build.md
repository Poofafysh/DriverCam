---
description: Build one or all plugins in Release (each build deploys into the game via DeployToGame)
argument-hint: "[Plugin | all]"
---
Build plugin(s). Argument: `$ARGUMENTS` (a plugin folder name under `source/`, or empty / `all` for every plugin).

1. Work out the targets: every `source/<Name>/` that contains a `.csproj` (empty or `all`), or just the named one
   (match case-insensitively; if it doesn't exist, list the valid names and stop).
2. Check `source/local.props` exists and its `<GameDir>` folder contains `BepInEx/interop/Assembly-CSharp.dll`. If
   `local.props` is missing, say: copy `source/local.props.example` to `source/local.props` and set `GameDir`. If
   `interop` is missing, BepInEx hasn't finished its first run: start the game once, wait, close it.
3. Check whether the game is running (`Get-Process "Driving Rogue" -ErrorAction SilentlyContinue`). If it is, warn
   that the deploy copy will fail because BepInEx has the DLLs loaded; the compile itself still proves the code builds.
   Do not close the game unless the person asks.
4. For each target run `dotnet build -c Release` in `source/<Name>` (e.g. `dotnet build -c Release source/CurbFeel`).
   The `DeployToGame` target copies the DLL into `<GameDir>\BepInEx\plugins` (DriverCam also copies its cockpits and
   the shared `SavedSettings/DriverCam_cars/*.cfg` into `plugins\DriverCam\`).
5. Report per plugin: errors (file:line + message), new warnings, and whether it deployed.
   - **MSB3021 / MSB3027** ("Unable to copy file", "being used by another process") = the game is running and has the
     DLL locked. The build succeeded; close the game and build again (or use `/sync`) to install it.
   - **CS0246 / CS0234** for `Il2Cpp*`, `UnityEngine.*`, `BepInEx.*` types = wrong `GameDir` or missing interop.
   - Other errors are real; show them and offer to fix.
