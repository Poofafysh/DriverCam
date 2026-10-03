---
description: Read the game's BepInEx LogOutput.log and summarize plugin loads, versions, errors and CurbFeel/DriverCam diagnostics
argument-hint: "[search text, e.g. Walls | Traffic | exception]"
---
Summarize the BepInEx log of the local game. Optional filter / question: `$ARGUMENTS`

1. Find the game folder: `<GameDir>` from `source/local.props` (`<GameDir>...</GameDir>`); if that file is missing, use
   the default `C:\Program Files (x86)\Steam\steamapps\common\Driving Rogue` and say so. The log is
   `<GameDir>\BepInEx\LogOutput.log` (rewritten on every game start; `ErrorLog.log` may also exist next to it).
   If it doesn't exist, say the game hasn't been started with BepInEx yet and stop.
2. Report the file's last-write time and whether the game is running now
   (`Get-Process "Driving Rogue" -ErrorAction SilentlyContinue`), so it's clear which session the log is from.
3. Read the log (it can be large: use Grep for the patterns below, then Read around interesting hits). Summarize:
   - **BepInEx / Unity**: the BepInEx version line and the Unity version line.
   - **Plugins loaded**: every `Loading [<Name> <version>]` line. Compare each version with `source/<Name>/Plugin.cs`;
     flag mismatches (installed DLL is older/newer than the repo: suggest `/build` or `/sync`). Flag a plugin that
     appears twice, `Skipping [` / `duplicate` / `already loaded` lines (duplicate DLL), or a repo plugin that never
     appears. If the log ends before any `Loading [` line, BepInEx is still starting (first run takes minutes).
   - **Errors**: every `[Error` / `[Fatal` line and every `Exception` with its first stack lines, grouped and counted
     (the same exception repeated every frame = one entry with a count). The source name is space-padded, so match
     with a regex like `^\[(Error|Fatal)\s*:\s*CurbFeel\]`. Say which plugin each belongs to (CurbFeel, DriverCam,
     HarmonyX / Il2CppInterop, or the game itself).
   - **Warnings** from our plugins (`^\[Warning\s*:\s*(CurbFeel|DriverCam)\]`), grouped.
   - **CurbFeel diagnostics**: lines containing `[CurbFeel]` (start/state line), `[Walls]`, `[Traffic]`, `[Hull]`
     (wall shifts, sidewalk map, traffic tuning, hull trimming). Give the latest state and anything that went wrong.
   - **DriverCam diagnostics**: the `DriverCam x.y.z loaded` line, cockpit / car preset / shared-setup / mirror messages
     and anything from `Source: DriverCam`.
   - If `$ARGUMENTS` is given, also list every line matching it (case-insensitive) with line numbers, trimmed.
4. Finish with a 2-4 line verdict: did both plugins load at the repo versions, are there plugin errors, and the most
   likely next step. Never edit or delete the log.
