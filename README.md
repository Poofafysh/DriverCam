# DriverCam

First-person driver view mod for **Driving Rogue** (Steam). BepInEx 6 IL2CPP plugin.

Current version: **0.9.1**

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

### Edit mode (controller, fast tuning)

Sit in the car in Driver view (best at the race start, standing still) and press **L3 + R3** (both sticks in) or **F7**.

| Input | Does |
|---|---|
| D-pad left / right | pick what to edit: seat, each cockpit part, the rear/left/right mirror views (the part blinks when picked) |
| Left stick | move left/right and back/forward |
| Right stick | move up/down and turn |
| Hold LB + right stick | tilt / roll |
| D-pad up / down | size (parts) or zoom (seat, mirror views) |
| Hold RB | fine adjustment |
| L3 + R3 | done |

Changes show live and save to the current car when you let go of the stick.

### Per-car settings

Each car has its own file in `BepInEx/config/DriverCam_cars/<Car>.cfg` with everything for that car: seat and view, the three mirrors and the cockpit part positions (`Part.` lines). This repo includes tuned files for the cars; copying the repo over your install replaces your own car files with these, so back up `DriverCam_cars` first if you want to keep yours.

## Notes

- All 10 player cars have a cockpit fitted to their own body (pillars, roof, side mirrors on the real mirror housings). Use Edit mode to fine-tune any car.
- DriverCam only changes your own camera, cockpit and HUD. Nothing is sent over the network, so it doesn't change the game for other players. Try it in a private session first.

## Uninstall

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx` and `dotnet` folders from the game folder. Steam's "Verify integrity of game files" won't remove them, because they aren't the game's own files.

## Contents

| Path | What |
|---|---|
| `BepInEx/plugins/DriverCam.dll` | the mod |
| `BepInEx/plugins/DriverCam/` | cockpit models (`cockpit.dcm` generic, `cockpit_<Car>.dcm` per car) and the interior texture |
| `BepInEx/core`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` | [BepInEx](https://github.com/BepInEx/BepInEx) 6.0.0-be.788 (IL2CPP) loader, LGPL-2.1 |
| `BepInEx/config/DriverCam_cars/` | per-car settings (seat, view, mirrors, cockpit parts) |
| `source/` | the mod's source code, see `source/README.md` for building |
| `dotnet/` | .NET 6 runtime used by BepInEx IL2CPP (MIT) |

Unofficial fan mod. Not affiliated with Gravity Works or Cosmic Shift Studios.
