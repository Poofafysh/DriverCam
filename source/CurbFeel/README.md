# CurbFeel

BepInEx 6 IL2CPP plugin for **Driving Rogue** that changes how the car meets road edges and traffic. You can ride up onto the curb and onto the sidewalk instead of bouncing off an invisible wall about a metre before it, and you can lane split. It applies to every car body (and AI racers) on every road tile.

Current version: **0.4.1**. Background research (collision layers, offsets, decompiled damage formulas): [`RESEARCH.md`](RESEARCH.md).

## What it changes

| | Stock | CurbFeel (defaults) |
|---|---|---|
| **A. Car wall hull** | Wall-contact capsules reach 1.6-1.7 m from the car's centre (~0.4 m outside the bodywork) | Trimmed to each car's measured body width (e.g. 1.28 m) |
| **B. Invisible walls** | Inner wall sits ~0.85-1.4 m *before* the visible curb | Moved up to **3 m past the curb** (about half a car length), stopping 0.5 m short of buildings, walls, cliffs and bus stops along each point of the road |
| **C. Wall damage** | Every touch: ~2.8 HP × speed curve + 8 % speed (25 % if > 20°), + 0.18 HP / 0.5 s while pressed | Contacts ≤ 5° are soft scrapes: no damage, 2 % speed. Steeper hits unchanged. Pressed-against-wall damage × 0.25 |
| **D. Curb** | No collision at all (flat road physics) | Invisible bevelled curb + sidewalk collider (Street layer): bevel 0.35 m before to 0.35 m after the curb face, height matched to the sidewalk (~0.24-0.41 m, fallback 0.32) |
| **E. Traffic (lane splitting)** | Every touch with a traffic car is a crash: damage, 10-20 % speed, **drift reset**, camera shake | Contacts ≤ 5° are side-swipes: 3 % speed, no damage, drift kept. Traffic hit boxes × 0.92 width. Near-miss range widened by the same amount |

Lanes are 5 m apart (traffic sits at ±2.5 / ±7.5 m), so two cars side by side leave ~2.5-2.8 m for your ~2.2 m traffic hit box.

## Install

Copy `CurbFeel.dll` into `Driving Rogue\BepInEx\plugins\` (BepInEx 6 IL2CPP must already be installed, e.g. from DriverCam). Start the game once to create `BepInEx\config\rogue.curbfeel.cfg`.

## In game

A status panel in the top-right corner shows every feature (green = on), live counters (walls moved, ramps, scrapes, side-swipes, traffic boxes, lane offsets), and flashes on each scrape / side-swipe. With the mouse cursor visible, each row is a button that toggles that feature.

| Key | Does |
|---|---|
| **F8** | panel: Full → Compact → Hidden |
| **F9** | reload the config file and reapply |
| **F10** | CurbFeel on/off (instant comparison with stock) |

All keys are configurable. F6/F7 are left free for DriverCam.

## Tuning (rogue.curbfeel.cfg)

| Section | Key | Default | Notes |
|---|---|---|---|
| B.Walls | `AllowedOverCurb` | 3.0 | Max distance past the curb face before the wall |
| B.Walls | `SidewalkCap` / `SidewalkMargin` / `MinOverCurb` / `CapSmoothing` | true / 0.5 / 0.2 / 3 | Stop short of building-sized scenery (nothing beyond the curb has collision in this game) |
| B.Walls | `CurbReference` / `WiderBehindCurb` / `CurbFromRegular` | Wider / 0.15 / 1.1 | Curb-face estimate (stock outer wall sits ~0.17 m behind the curb) |
| D.CurbRamp | `Height` | 0 (auto) | Fixed ramp height in m, or 0 to match the sidewalk |
| D.CurbRamp | `StartBeforeCurb` / `FullHeightAfterCurb` / `TopExtend` | 0.35 / 0.35 / 0.3 | Longer bevel = gentler climb |
| D.CurbRamp | `ShowRamps` | false | Draw the ramp colliders in pink (debug) |
| A.Hull | `HalfWidth` / `Margin` / `AllVehicles` | 0 (auto) / 0 / true | |
| C.Scrape | `ShallowAngle`, `ShallowDamageMult`, `ShallowSpeedLoss`, `HardDamageMult`, `HardSpeedLossMult`, `ContinuousDamageMult`, `BounceOffGuardrail` | 5, 0, 0.02, 1, 1, 0.25, -1 | |
| E.Traffic | `WidthScale` / `LengthScale` | 0.92 / 1.0 | |
| E.Traffic | `SideSwipeAngle`, `SideSwipeSpeedLoss`, `SideSwipeDamageMult`, `SideSwipeCooldown`, `HardHitDamageMult` | 5, 0.03, 0, 0.3, 1 | |
| E.Traffic | `NearMissExtraRange` / `IncludeRacers` / `LogLanes` | -1 (auto) / false / true | |

## Build

Needs the .NET SDK (8 is fine) and a game folder where BepInEx has already generated `BepInEx\interop`. It compiles against the game's bundled .NET 6 runtime, so no extra packages are downloaded.

```
dotnet build -c Release -o bin -p:GameDir="D:\SteamLibrary\steamapps\common\Driving Rogue"
```

`-p:SkipDeploy=true` builds without copying anything into the game.

### Hot reload (developers)

The same source also builds as a hot module for the [HotReload](../HotReload/README.md) host, so you can change CurbFeel while the game runs:

```
dotnet build -c Release -p:Hot=true
```

This build has no BepInEx plugin class and no injected MonoBehaviour (`HotModule.cs` is its entry point, compiled only with `HOT`). It goes to `BepInEx\hot\CurbFeel.dll` instead of `BepInEx\plugins\`. The host reloads it within a second of the build finishing: the old build's `Unload` reverts every change (hull, walls, ramps, traffic boxes, near-miss range) and removes its Harmony patches, then the new build starts with the same `rogue.curbfeel.cfg`. To switch to hot mode, delete `BepInEx\plugins\CurbFeel.dll` once and restart. If both are installed, the host refuses the hot module and logs why. Players always use the normal build. Its behaviour doesn't change.

## Performance

0.4.1 changes no gameplay values or settings. It does the same work less often and with fewer allocations:

| Area | Before | Now |
|---|---|---|
| Finding road walls | searched every MeshCollider in the game 4 times a second | walks only a road tile's own scene, from its root objects, when the tile finishes loading, plus 2 re-checks (1 s and 4 s later). While no wall pair exists at all, every loaded scene is rescanned every 5 s. At most 2 scenes are walked per tick. Each mesh's first vertex is read once and cached, not copied out with `.vertices` for every comparison |
| Applying walls | 2 wall pairs per tick | 1 wall pair per tick, so loading a tile causes smaller hitches. A tile with many walls takes a little longer to finish |
| Sidewalk map | searched every MeshRenderer in the game for each tile | walks only that tile's root objects. The whole-game search is kept as a fallback. A tile's map is dropped when the tile unloads |
| Wall probes / ramps | new HashSet, List, Dictionary and iterator objects for every probe and triangle | the same buffers are reused each time |
| Ticks | walls, hull and traffic could all run on the same frame | staggered: walls at 0 s, hull at +0.17 s, traffic at +0.33 s |
| Car hull | searched all CapsuleColliders twice a second | does a full scan every tick for 10 s after the player car or the set of loaded scenes changes, and otherwise every 3 s as a safety check. Already-handled capsules are skipped by instance ID before any other Unity call |
| Traffic boxes | searched all AIVehicleControllers twice a second | reads the spawner's own list (`DefaultAISpawner.activeAiCars`) and also searches the whole game every 5 s for any car not on it. Without a single-player spawner it searches every tick, as before. The near-miss handler lookup is cached for each player car |
| Wall-contact patches | `CheckWallContinuousDamage` was always patched | patched the first time C.Scrape is enabled, which is at startup with the default settings. It is never unpatched at runtime; with Scrape off the prefix does nothing, as before. Traffic checks cache their `GetComponentInParent` result per collider. The `VehicleBaseParameters` search is throttled to once every 3 s while none are loaded |
| Keys / panel | key names parsed every frame; panel text rebuilt on every OnGUI call | key names are parsed only when the setting changes. The panel only works on Repaint and mouse events, its text is rebuilt at most 5 times a second (immediately after a click or event), and the plugin build has no GUILayout pass |

Optional timing: CurbFeel opens `CurbFeel.Walls` / `.Hull` / `.Traffic` / `.Overlay` scopes in the shared perf helper (`source/Shared/Perf.cs`). TrafficDensity shows them in its perf overlay when its `[Perf]` setting is on. When timing is off, a scope costs one bool check.

### Future work

- Tile-load hitches: mesh reads and ramp builds still run on the main thread, one wall pair per tick. Candidates: `AsyncGPUReadback` for the sidewalk meshes (now read synchronously with `GraphicsBuffer.GetData`), and `Physics.BakeMesh` in a job for the new wall and ramp colliders. Both are left out of this pass on purpose: they need in-game checks that the APIs survive IL2CPP stripping.
- Settle the safety-scan intervals (walls 5 s while unpaired, hull 3 s, traffic 5 s) from in-game timings.

## Notes

- Local physics only, nothing is sent over the network. It does change gameplay (fewer wall and traffic penalties), so try it solo or in private lobbies first.
- Walls are paired per tile by name (`Guardrail_Regular*` ↔ `Guardrail_Wider*`), vertex count and position. A wall without a partner is left stock and logged.
- `GpuMeshReader.cs` is adapted from DriverCam's reader.
