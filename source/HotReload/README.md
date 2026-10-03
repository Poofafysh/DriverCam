# HotReload

A developer tool for **Driving Rogue** mods. It's a BepInEx 6 IL2CPP host plugin that loads mod code from `BepInEx\hot\` and **reloads it while the game runs**. Rebuild a module and the new code is live about a second later, with no restart.

Current version: **0.1.0**. Players don't need it. It's only for developing plugins. Its `.csproj` is marked `<DevOnly>true</DevOnly>`, so repo-sync (`tools/sync-install.ps1`) never builds, installs or requires it.

| Plugin | Version | GUID | Hotkeys | What it does |
|---|---|---|---|---|
| **HotReload** | 0.1.0 | `rogue.hotreload` | F11 (reload all now) | Loads `IHotModule` DLLs from `BepInEx\hot\` from bytes, reloads them when they change, drives their Update/OnGUI |
| **CurbFeel** (hot build) | 0.4.0 | `rogue.curbfeel` | F8 / F9 / F10 (unchanged) | `dotnet build -c Release -p:Hot=true` in `source/CurbFeel` builds it as a hot module |

## How it works

1. **The host** (`HotReload.dll`) is a normal plugin in `BepInEx\plugins\`. It is installed once and needs one game restart. It owns the only IL2CPP-injected MonoBehaviour (`HotHost`). Il2CppInterop can't unregister an injected type or register the same name twice, so reloadable code must not inject types. The host's `Update`/`OnGUI` call the modules instead.
2. **The contract** (`HotModule.cs`) is `IHotModule { string Id; void Load(HotContext); void Unload(); void Update(); void OnGUI(); }`. `HotContext` gives a module:
   - a `ManualLogSource` named after its DLL
   - a fresh `ConfigFile` at `BepInEx\config\<Id>.cfg`, the same file the normal plugin uses
   - a Harmony id unique to this load (`<Id>.hot<n>`)
   - a load counter, and the hot, config and plugins paths
3. **Modules** live in `BepInEx\hot\`, outside `BepInEx\plugins\`, so the chainloader never loads or locks them. The host reads each DLL (and a matching `.pdb`, for line numbers) into memory and loads it from those bytes. Because the file on disk is never held open, a build can overwrite it at any time.
   - `LoadMode=Collectible` (the default) puts each version in its own collectible `AssemblyLoadContext`. Old versions can then be freed, unless Harmony or Il2CppInterop still hold a reference, in which case they just stay in memory.
   - If the runtime refuses a collectible load, the host switches that DLL to `Individual` (`Assembly.Load(bytes)`) for the rest of the session. Old versions then stay in memory, which is a small leak and fine for a dev tool.
   - Shared assemblies (game interop, BepInEx, Harmony, HotReload) always resolve to the copies that are already loaded, so `IHotModule` is the same type on both sides.
4. **Reload triggers**:
   - A `FileSystemWatcher` on the hot folder. After the last change the host waits `DebounceMs` (400 ms), then opens the file without allowing writers, retrying while the build is still copying, and checks that it's a complete DLL.
   - `ReloadKey` (**F11**) reloads every module, even ones that haven't changed.
   - Writing an identical build again does nothing.
5. **A reload** runs in this order:
   1. Load the new assembly next to the old one and create its `IHotModule` classes. If that fails, the old build keeps running.
   2. Check for conflicts (below).
   3. Call the old module's `Unload()` inside try/catch, then `new Harmony(oldId).UnpatchSelf()` as a safety net. The host then checks that no patch with that id is left. If one is, it logs the methods, shows a red message, and blocks new builds of that module until the game restarts, so old and new prefixes can never run together.
   4. Unload the old load context.
   5. Call the new module's `Load(ctx)`.

   Each result is written to `BepInEx\LogOutput.log` and shown in a short message at the top centre of the screen, below TrafficDensity's message (green when it worked, red on an error).
6. **Errors never reach Unity.** Every call into a module is wrapped. A module that throws 10 times in a row in `Update` or `OnGUI` is suspended until its next reload.
7. **Conflicts** are refused, with the reason logged:
   - a hot module whose `Id` is already loaded by the chainloader as a normal plugin (for example `CurbFeel.dll` still in `plugins\`)
   - two hot DLLs with the same `Id`
   - a `HotFolder` inside `plugins\`

## Install (once)

With the game **closed**:

```
cd source/HotReload
dotnet build -c Release          # copies HotReload.dll into <GameDir>\BepInEx\plugins
```

Start the game. `LogOutput.log` should show `HotReload 0.1.0 loaded` and `Watching ...\BepInEx\hot`.

## Using it with CurbFeel

1. With the game closed, delete `BepInEx\plugins\CurbFeel.dll`. The hot build replaces it, and the host refuses to run both.
2. `cd source/CurbFeel` and run `dotnet build -c Release -p:Hot=true`. This writes `BepInEx\hot\CurbFeel.dll` (and its `.pdb`).
3. Start the game. The log shows `Loaded CurbFeel.dll [rogue.curbfeel] #1`.
4. Edit CurbFeel and run the same build again while the game runs. About a second later you'll see `Reloaded CurbFeel.dll ... #2` in the log and a green message on screen. The old build reverted all its changes first.
5. To go back to the normal plugin: delete `BepInEx\hot\CurbFeel.dll` (the host unloads it live), then `dotnet build -c Release` with the game closed.

repo-sync (`/sync`) always installs the normal `CurbFeel.dll` into `plugins\`. After a sync, delete it again (game closed) to stay in hot mode. Until you do, the host refuses the hot build and logs why.

In Claude Code, `/hot-reload` builds a module with `-p:Hot=true` and confirms in `LogOutput.log` that the host picked it up.

`-p:SkipDeploy=true` builds without copying into the game (both projects).

## Config (`BepInEx\config\rogue.hotreload.cfg`)

| Key | Default | |
|---|---|---|
| `ReloadKey` | F11 | reload every hot module now |
| `WatchFolder` | true | reload automatically when a DLL changes |
| `HotFolder` | hot | relative to `BepInEx\`, or an absolute path (never inside `plugins\`) |
| `DebounceMs` | 400 | quiet time after the last change before reading the DLL |
| `LoadMode` | Collectible | `Collectible` or `Individual` (see above) |
| `ShowToast` | true | on-screen message after each reload |

## Writing a hot module

- Implement `HotReload.IHotModule` in a public class with a parameterless constructor.
- Reference `HotReload` with `<Private>false</Private>`, and never copy `HotReload.dll` into `hot\`.
- Build with no `[BepInPlugin]` class. CurbFeel does this with `#if HOT`.
- Don't use `ClassInjector`, custom MonoBehaviours or `AddComponent<YourType>()`. Do your per-frame work in `Update()`/`OnGUI()`.
- Patch with `new Harmony(ctx.HarmonyId)` and call `UnpatchSelf()` in `Unload()`.
- `Unload()` must undo everything:
  - restore changed values
  - destroy created GameObjects, meshes and materials
  - remove Unity or Il2Cpp callbacks (`Application.onBeforeRender`, `SceneManager.sceneLoaded`, delegates converted with `DelegateSupport`)
  - clear static caches that hold game objects

  A callback left behind keeps calling old code, and in a collectible context it could call into freed code. If a module needs such callbacks, set `LoadMode=Individual`.
- Statics start fresh on every load, because each load is a new assembly. Keep anything that must survive a reload in the config file.

## Limitations

- **Installing or updating HotReload itself needs a restart.** It's a normal plugin, and the running game keeps the DLL it started with.
- **Injected IL2CPP types can't be hot-reloaded.** That includes custom MonoBehaviours registered with `ClassInjector`, so modules must be driven by the host.
- **DriverCam isn't hot-reloadable yet.** It injects `DriverCamBehaviour`, hooks `Application.onBeforeRender` and creates cameras, render textures and cockpit objects. Its code is unchanged.
- Harmony patches on IL2CPP methods go through Il2CppInterop's native detours. Patch, unpatch and patch-again use the normal Harmony API, the same way runtime hook tools do on IL2CPP games, but **this hasn't been tested in Driving Rogue yet**. Watch the first reloads in `LogOutput.log`. If you see problems, set `LoadMode=Individual`. The old trampolines stay in memory, so collectible contexts are often not freed.
- Not tested in game yet. Built and checked outside the game: the hot CurbFeel DLL loads into a collectible context, its `IHotModule` binds to the host's type, and three load/unload cycles were all collected.
- CurbFeel's wall-mesh clones aren't destroyed when it reverts, as in the plugin's own F9/F10 reapply. Each reload leaks a few small meshes until the game restarts.
- Hot modules can't depend on other hot modules. A module can only reference assemblies that are already loaded (game, BepInEx, Harmony, HotReload, normal plugins).

## How DriverCam could adopt it later

1. Move the work out of `DriverCamBehaviour` into a plain static class, as CurbFeel did with `CurbFeelCore`. Keep the injected behaviour in the plugin build only (`#if !HOT`).
2. Add a `HotModule.cs` (`#if HOT`) whose `Load` binds the config and applies the Harmony patches with `ctx.HarmonyId`, and whose `Update`/`OnGUI` call the static class.
3. Give `Unload` a full teardown:
   - remove the `Application.onBeforeRender` hook
   - destroy the cockpit, mirror cameras and render textures
   - restore the camera modes it added and the car outline
   - restore the HUD layout
4. Add the same `Hot` / `SkipDeploy` properties to `DriverCam.csproj`. The hot build should copy its cockpit and cars assets to `BepInEx\plugins\DriverCam\` as before, since those are data files, not code.
5. Test in `LoadMode=Individual` first. DriverCam passes managed delegates to Il2Cpp callbacks (`Application.onBeforeRender`), which is the case where collectible contexts are riskiest.
