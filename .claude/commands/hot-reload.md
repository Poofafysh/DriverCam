---
description: Build a plugin as a hot module (-p:Hot=true) and confirm the running game's HotReload host picked it up
argument-hint: "[Plugin folder under source/, default CurbFeel]"
---

Hot-reload the plugin `$ARGUMENTS` (if empty, use `CurbFeel`) into the running game via the HotReload host.
See `source/HotReload/README.md` for how it works. Run from the repo root (or worktree root).

Rules: never close, kill or restart the game. Never delete or overwrite anything in `BepInEx/plugins/` or
`BepInEx/config/` without asking the person first. Only `BepInEx/hot/` is written (by the build).

1. **Preflight**
   - Read `GameDir` from `source/local.props`. If it's missing, stop and say so.
   - Check that `source/<Plugin>/` has a `HotModule.cs` and its `.csproj` supports `-p:Hot=true`. If not, stop: the
     plugin hasn't been made hot-loadable yet. DriverCam isn't hot-loadable yet; the README says what it needs.
   - Check that `<GameDir>/BepInEx/plugins/HotReload.dll` exists. If it doesn't, the host isn't installed. Building
     `source/HotReload` with `dotnet build -c Release` while the game is closed installs it, then the game needs one
     restart. Ask before doing that.
   - Check for `<GameDir>/BepInEx/plugins/<Plugin>.dll`. If it exists, the host will refuse the hot module because
     the normal plugin with the same GUID is loaded. Tell the person to delete it (with the game closed) and restart
     once. Don't delete it yourself unless they say so.
   - See whether the game is running (`Get-Process "Driving Rogue"`). If it isn't, the build still works and the host
     loads the module at the next start. Say that, and skip step 3.
2. **Build.** Note how many lines the newest `<GameDir>/BepInEx/LogOutput*.log` has, then run in `source/<Plugin>`:
   `dotnet build -c Release -p:Hot=true`
   Report errors and stop if it fails. It deploys `<Plugin>.dll` and `.pdb` to `<GameDir>/BepInEx/hot/`. If the copy
   warns that the file is in use, something other than the host has it open; say so.
3. **Confirm.** Poll the new lines of that log for up to 20 seconds. BepInEx buffers the disk log unless
   `[Logging.Disk] InstantFlushing = true`, so lines can arrive late. Look for:
   - `[Info   : HotReload] Reloaded <Plugin>.dll [<guid>] #N, built HH:mm:ss, ...` (or `Loaded`): success. The
     `built` time should match the DLL you just built.
   - The plugin's own lines, e.g. `[Info   :  CurbFeel] CurbFeel x.y.z loaded as a hot module (load #N)`.
   - `[Error  : HotReload] <Plugin>.dll: ... not loaded`, `Load threw`, `Unload threw` or `suspended`: report the
     message and the stack trace's top frames.
   - Nothing within 20 s: the log may just be buffered. Suggest looking for the green message at the top of the
     screen, pressing F11 in game (reload all), or setting `InstantFlushing = true` in `BepInEx/config/BepInEx.cfg`
     (takes effect after a restart).
4. **Report** in a few lines: build result, the HotReload log line(s) found (quoted), the load number, any errors or
   warnings, and what the person should see in game (a green "<Plugin> reloaded #N" message at the top centre).
