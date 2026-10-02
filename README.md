# DriverCam

First-person driver view mod for **Driving Rogue** (Steam). BepInEx 6 IL2CPP plugin.

Current version: **0.8.1**

## Install

1. In Steam: right-click Driving Rogue > Manage > Browse local files.
2. Copy everything from this repo (or the release zip) into that folder, so that `winhttp.dll` and the `BepInEx` folder sit right next to `Driving Rogue.exe`.
3. Start the game. The first launch takes a few minutes while BepInEx builds its files and may look frozen. A console window may open; that's normal.

## Controls

- **Change Camera** (C on keyboard / Y on controller) cycles: Chase -> Chase 2 -> Hood -> Driver
- **F6** or the controller **View** button toggles Driver view.
- Click the **DriverCam** button on the left of the screen for the settings panel:
  - **Seat & view**: seat position, look angle, field of view, cockpit look
  - **Parts**: move/turn/resize each cockpit part (including the side mirrors)
  - **Mirror**: aim and zoom the rear-view and side mirrors
  - **HUD**: move/shrink the health bar, speedometer and ability bar

Settings are saved per car automatically.

## Notes

- The Saber has a fully fitted cockpit. Other cars use a generic cockpit for now; line their side mirrors up with the car's own mirrors on the Parts tab.
- DriverCam only changes your own camera, cockpit and HUD. Nothing is sent over the network, so it doesn't change the game for other players. Try it in a private session first.

## Uninstall

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx` and `dotnet` folders from the game folder. Steam's "Verify integrity of game files" won't remove them, because they aren't the game's own files.

## Contents

| Path | What |
|---|---|
| `BepInEx/plugins/DriverCam.dll` | the mod |
| `BepInEx/plugins/DriverCam/` | cockpit models (`cockpit.dcm`, `cockpit_Saber.dcm`) and the Saber interior texture |
| `BepInEx/core`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` | [BepInEx](https://github.com/BepInEx/BepInEx) 6.0.0-be.788 (IL2CPP) loader, LGPL-2.1 |
| `dotnet/` | .NET 6 runtime used by BepInEx IL2CPP (MIT) |

Unofficial fan mod. Not affiliated with Gravity Works or Cosmic Shift Studios.
