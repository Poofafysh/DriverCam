# DriverCam source

| Path | What |
|---|---|
| `DriverCam/*.cs`, `DriverCam.csproj` | the BepInEx 6 IL2CPP plugin (net6.0) |
| `DriverCam/Assets/cockpit.dcm` | generic cockpit, used by cars without a fitted one |
| `DriverCam/Assets/cockpits/cockpit_<Car>.dcm` | cockpits fitted to each player car (car body coordinates) |
| `DriverCam/Assets/autofit.py` | Blender script that fits a cockpit to a car from the game's dumped prefabs |
| `DriverCam/SavedSettings/` | example config / part-layout files |
| `Directory.Build.props`, `local.props.example` | shared build settings; your game path goes in `local.props` (git-ignored) |
| `CurbFeel/` | the CurbFeel plugin (curbs, sidewalks, lane splitting): source, `README.md` (features, tuning, build) and `RESEARCH.md` (how the game's walls, curbs and traffic collisions work) |

## Build

Needs the .NET 6+ SDK and the game with BepInEx installed and launched once, so that `BepInEx/interop` exists.

Set your game folder once: copy `local.props.example` to `local.props` (git-ignored) and edit `GameDir`. `Directory.Build.props` loads it for every plugin. You can also pass `-p:GameDir="..."` on the command line.

```
cd DriverCam
dotnet build -c Release
```

The build copies `DriverCam.dll` and the cockpit files into the game's `BepInEx/plugins`. Close the game first, because it locks the DLL while running.

## How it fits together

- `Plugin.cs` holds the config entries, registers the behaviour and installs the Harmony patches.
- `DriverMode.cs` creates the "Driver" `CameraModeSO`, cloned from the game's Hood view.
- `Patches.cs` and `DriverCamBehaviour.cs` add Driver to Change Camera (C / Y) cycling, handle the F6 / View button toggle and the IMGUI settings panel, and strip the car outline.
- `DriverView.cs` places the camera, cockpit and mirrors in `Application.onBeforeRender`, from the rendered (shaken) body pose.
- `Cockpit.cs` and `CockpitModel.cs` load the `.dcm` cockpit format and build movable part groups.
- `PartLayout.cs` stores per-car part tweaks. `CarPresets.cs` stores per-car view and mirror settings.
- `EditMode.cs` is the controller Edit mode (L3+R3).
- `MirrorView.cs` runs the rear and side mirrors, each with its own camera and render texture.
- `HudLayout.cs` moves and scales the health bar, speedometer and ability bar.
- `CarExporter.cs`, `GpuMeshReader.cs` and `TextureSaver.cs` export the driven car's mesh and textures in-game, as a fitting reference.

IL2CPP note: wrap Unity objects the mod creates with `Keep.Hold`, or the GC collects their managed wrappers.

## Not included

The Blender working file (`cockpit.blend`) and the reference car model are not in this repo. The .blend contains the game's own car mesh, and the reference model is third-party. Game assets (the AssetRipper/Il2Cpp dumps) are not included either.
