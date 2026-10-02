DriverCam 0.8.1 - first-person driver view for Driving Rogue
==============================================================

INSTALL
1. In Steam: right-click Driving Rogue > Manage > Browse local files.
2. Copy EVERYTHING from this zip into that folder, so that winhttp.dll and the
   BepInEx folder sit right next to "Driving Rogue.exe".
3. Start the game. The FIRST launch takes a few minutes (BepInEx builds its files)
   and may look frozen - let it finish. A console window may open; that's normal.

CONTROLS
- Change Camera (C on keyboard / Y on controller) now cycles:
  Chase -> Chase 2 -> Hood -> Driver
- F6 or the controller View button (two little squares) toggles Driver view.
- Click the "DriverCam" button on the left of the screen for the settings panel:
    Seat & view - seat position, look angle, field of view, cockpit look
    Parts       - move/turn/resize each cockpit part (including the side mirrors)
    Mirror      - aim and zoom the rear-view and side mirrors
    HUD         - move/shrink the health bar, speedometer and ability bar
  Settings are saved per car automatically.

NOTES
- The Saber has a fully fitted cockpit. Other cars use a generic cockpit for now;
  line their side mirrors up with the car's own mirrors on the Parts tab.
- DriverCam only changes your own camera, cockpit and HUD. Nothing is sent over
  the network, so it doesn't change the game for other players. Try it in a
  private session first.

UNINSTALL
Delete winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt and the
BepInEx and dotnet folders from the game folder. (Steam's "Verify integrity of
game files" won't remove them, because they aren't the game's own files.)
