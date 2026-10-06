# CurbFeel

BepInEx 6 IL2CPP plugin for **Driving Rogue** that changes how the car meets road edges and traffic. You can ride up onto the curb and onto the sidewalk instead of bouncing off an invisible wall about a metre before it, and you can lane split. It applies to every car body (and AI racers) on every road tile.

Current version: **0.8.3**. Background research (collision layers, offsets, decompiled damage formulas): [`RESEARCH.md`](RESEARCH.md).

## In Rogue Hub (0.6.0)

Every setting has a name in Rogue Hub; calibration and debug values sit under Advanced. A change made in the hub (or
anywhere else, e.g. the .cfg file plus F9) re-fits the walls, ramps and hit boxes half a second after the last change,
so F9 isn't needed. The hub card shows how many road edges were moved; its "Re-fit walls now" button does F9's re-fit.
While the hub or its quick menu is on screen, the status panel is hidden. These values now have enforced ranges (BepInEx
clamps a value outside them in an existing .cfg when the game loads it):

| Setting | Range |
|---|---|
| `General.OverlayScale` | 0.5 - 2 |
| `A.Hull.Margin` | -0.3 - 0.3 m |
| `B.Walls.AllowedOverCurb` | 0 - 6 m |
| `B.Walls.SidewalkMargin`, `B.Walls.MinOverCurb` | 0 - 1 m |
| `D.CurbRamp.FullHeightAfterCurb` | 0.1 - 1.5 m |
| `C.Scrape.ShallowAngle`, `E.Traffic.SideSwipeAngle` | 0 - 20 degrees |
| `C.Scrape.ShallowDamageMult`, `C.Scrape.ContinuousDamageMult`, `E.Traffic.SideSwipeDamageMult` | 0 - 1 |
| `C.Scrape.ShallowSpeedLoss`, `E.Traffic.SideSwipeSpeedLoss` | 0 - 0.1 |
| `C.Scrape.HardDamageMult`, `C.Scrape.HardSpeedLossMult`, `E.Traffic.HardHitDamageMult` | 0 - 2 |
| `E.Traffic.WidthScale` | 0.7 - 1 |
| `E.Traffic.LengthScale` | 0.7 - 1.2 |

`D.CurbRamp.Height` (0 = AUTO) and `C.Scrape.BounceOffGuardrail` (-1 = stock) keep their special values; the hub shows
them as an AUTO / GAME switch next to a 0.1-0.6 m and 0-1 slider.

## What it changes

| | Stock | CurbFeel (defaults) |
|---|---|---|
| **A. Car wall hull** | Wall-contact capsules reach 1.6-1.7 m from the car's centre (~0.4 m outside the bodywork) | Trimmed to each car's measured body width (e.g. 1.28 m) |
| **B. Invisible walls** | Walls sit wherever the tile put them: on city streets the stock wall is ~1 m *behind* the visible curb, on park / industrial roads ~1 m *before* it, and nothing stops you at visible railings | Moved up to **3 m past the curb** (about half a car length), the curb measured on each tile's own sidewalk mesh at every point, and stopping 0.15 m short of the first visible guardrail, rail, fence, rock wall, barricade, wood bar, bollard, planter, tree trunk, bus stop, building, cliff or tunnel wall. Where such a railing stands right at the curb, the wall can sit up to 1 m *closer to the road than stock* (a gameplay change: you now hit the railing you see); nowhere else does a wall move inward |
| **C. Wall damage** | Every touch: ~2.8 HP × speed curve + 8 % speed (25 % if > 20°), + 0.18 HP / 0.5 s while pressed | Contacts ≤ 5° are soft scrapes: no damage, 2 % speed. Steeper hits unchanged. Pressed-against-wall damage × 0.25 |
| **D. Curb** | No collision at all (flat road physics) | Invisible bevelled curb + sidewalk collider (Street layer): bevel 0.35 m before to 0.35 m after the curb face, height matched to the sidewalk (~0.24-0.41 m, fallback 0.32) |
| **F. Drift** (0.8.0) | Drifting has no start cost | The moment a drift starts you lose 5 % of your speed (`F.Drift.StartSpeedLoss`), unless a card negates drift speed loss (the game's drift speed-loss multiplier is 0). Your own car only, also in multiplayer |
| **E. Traffic (lane splitting)** | Every touch with a traffic car is a crash: damage, 10-20 % speed, **drift reset**, camera shake | Contacts ≤ 5° are side-swipes: 3 % speed, no damage, drift kept. Traffic hit boxes × 0.92 width. Near-miss range widened by the same amount |

Lanes are 5 m apart (traffic sits at ±2.5 / ±7.5 m), so two cars side by side leave ~2.5-2.8 m for your ~2.2 m traffic hit box.

### Walls follow the map (0.5)

A census of all 54 road tiles (every biome) showed where the old fixed rules went wrong:

- **City streets** (Residential / Commercial, `Sides_*` / `Base_*` / `Sidewalk_*` meshes): the curb is ~1.70 m *in front of* the stock outer wall, not 0.15 m. The old estimate put the wall ~4.5 m past the curb, and inside a building, planter or railing at 20-28% of points.
- **Visible guardrails were ignored** (their name contains "Guardrail", like the invisible walls), long rails and fences (100-200 m, one mesh) were dropped by the size filter, and the box test skipped any building whose (unrotated) bounding box already contained the curb, so the wall went straight through them. Lamp cones, tree canopies and bushes did the opposite: they stopped walls where nothing solid stands.

Now, at every point of every wall:

1. **Curb:** a ray just above the road finds the curb face on the tile's own sidewalk mesh (fallback where a tile has none: the street-style constants above).
2. **Obstacles:** from the curb outward, rays at bumper height (0.6 / 1.1 m, and 0.5 m to either side) find the first thing a car can't pass: rotated boxes from each object's own mesh bounds (exact for railing segments along curves), the real triangles of long rails, fences, cliffs and tunnels, and 0.6 m trunks for trees. Lamps, signs, cones, foliage, billboards and decals are ignored.
3. **Wall:** curb + `AllowedOverCurb`, stopped `SidewalkMargin` short of that obstacle, smoothed along the road (it only ever lowers). The 2 m thick wall slabs move as one piece, measured from their road face.
4. **No sidewalk, no move:** a tile with no sidewalk or curb mesh at all (river and bridge tiles, where the edge drops to an embankment) keeps its stock walls.

Each tile logs one line (always) when its map is read: `[Sidewalk] <tile>: N obstacle boxes, N obstacle triangles, N sidewalk triangles (city / park style), N meshes unreadable, N ms over N frame(s)` (0.7: the map is read over several frames, `MapBudgetMs` per frame, and that tile's walls move once it is done). With `VerboseLog = true` each wall also logs `curb +0.4 m from the stock wall (31/32 points measured on the sidewalk mesh), wall 1.85 m past the curb (40% stopped by railings / walls / buildings)`. A wall never moves into the road past its stock place, except up to 1 m where a railing or wall was measured right at the curb. Turn on `ShowWalls` to see exactly where every wall stands.

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
| B.Walls | `SidewalkCap` / `SidewalkMargin` / `MinOverCurb` / `CapSmoothing` | true / 0.15 / 0.2 / 3 | Stop short of railings, walls, planters, buildings... (nothing beyond the curb has collision in this game); the wall never goes into one |
| B.Walls | `CurbReference` | Auto | `Auto` = measured on the tile's sidewalk / curb mesh; `Wider` / `Regular` = the old fixed rules |
| B.Walls | `CurbFromWiderCity` / `CurbFromWiderPark` | -1.70 / 0.10 | Auto fallback where a tile has no curb mesh: curb relative to the stock outer wall on city / park streets (measured on all 54 tiles) |
| B.Walls | `WiderBehindCurb` / `CurbFromRegular` | 0.15 / 1.1 | Only for `CurbReference = Wider / Regular` |
| B.Walls | `ShowWalls` | false | Debug: a 1 m cyan strip where each moved wall stands (F9 to reload) |
| B.Walls | `MapBudgetMs` | 3 | 0.7: milliseconds per frame spent reading a newly loaded tile's sidewalks and obstacles (0 = the whole tile in one frame, as before: a hitch of up to ~1 s on big tiles). The tile's walls move once its map is read |
| General | `ConfigVersion` | (written) | Settings migration marker (0.5: `CurbReference` Wider -> Auto, `SidewalkMargin` 0.5 -> 0.15 if you never changed them). Don't edit |
| D.CurbRamp | `Height` | 0 (auto) | Fixed ramp height in m, or 0 to match the sidewalk |
| D.CurbRamp | `StartBeforeCurb` / `FullHeightAfterCurb` / `TopExtend` | 0.35 / 0.35 / 0.3 | Longer bevel = gentler climb |
| D.CurbRamp | `ShowRamps` | false | Draw the ramp colliders in pink (debug) |
| A.Hull | `HalfWidth` / `Margin` / `AllVehicles` | 0 (auto) / 0 / true | |
| C.Scrape | `ShallowAngle`, `ShallowDamageMult`, `ShallowSpeedLoss`, `HardDamageMult`, `HardSpeedLossMult`, `ContinuousDamageMult`, `BounceOffGuardrail` | 5, 0, 0.02, 1, 1, 0.25, -1 | |
| E.Traffic | `WidthScale` / `LengthScale` | 0.92 / 1.0 | |
| E.Traffic | `SideSwipeAngle`, `SideSwipeSpeedLoss`, `SideSwipeDamageMult`, `SideSwipeCooldown`, `HardHitDamageMult` | 5, 0.03, 0, 0.3, 1 | |
| E.Traffic | `NearMissExtraRange` / `IncludeRacers` / `LogLanes` | -1 (auto) / false / true | |
| F.Drift | `Enabled` / `StartSpeedLoss` / `KeepOffLeaderboards` | true / 0.05 / false | speed lost the moment a drift starts (skipped when a card negates drift speed loss); `KeepOffLeaderboards` = don't upload a run in which a drift start cost speed (a prefix on `LeaderboardsManager.PublishEntry`, combines with PitStop's and Sandbox's) |

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

0.7.0 (stutter fix, no gameplay values changed):

| Area | Before | Now |
|---|---|---|
| Sidewalk map of a new tile | read in one frame: 20+ tiles over 200 ms in one log, worst 1254 ms (124k obstacle triangles) | read over several frames, `MapBudgetMs` (3 ms) per frame; `WallShifter` waits for the map before it moves that tile's walls |
| Map maths | Unity's `Mathf.Min/Max/FloorToInt`, `Matrix4x4.MultiplyPoint3x4`, `Vector3` operators: each a slow IL2CPP interop call, about 15 per triangle | plain C# on the struct fields (`Shared/FastMath.cs`), also in the wall probes (`Ray`, `RayTri`, `RayBox`) and the GPU vertex read |

Optional timing: CurbFeel opens `CurbFeel.Walls` / `.Hull` / `.Traffic` / `.Overlay` / `.Map` scopes in the shared perf helper (`source/Shared/Perf.cs`). TrafficDensity shows them in its perf overlay when its `[Perf]` setting is on. When timing is off, a scope costs one bool check.

### Future work

- Tile-load hitches: mesh reads and ramp builds still run on the main thread, one wall pair per tick. Candidates: `AsyncGPUReadback` for the sidewalk meshes (now read synchronously with `GraphicsBuffer.GetData`), and `Physics.BakeMesh` in a job for the new wall and ramp colliders. Both are left out of this pass on purpose: they need in-game checks that the APIs survive IL2CPP stripping.
- Settle the safety-scan intervals (walls 5 s while unpaired, hull 3 s, traffic 5 s) from in-game timings.

## What to check in the log (`/game-log CurbFeel`)

- `[Drift] first drift: the game's drift speed-loss multiplier is N (0 = a card negates it)` (once per session); with `VerboseLog`, `[Drift] drift started: -5% speed (multiplier N)` per drift; `[Drift] leaderboard upload skipped: this run used the drift start speed loss` with `KeepOffLeaderboards`.
- `CurbFeel 0.8.0 loaded. F9 = reload config, F10 = toggle on/off, F8 = status panel.` and, just before it, `[CurbFeel] start: master=ON hull=ON walls=ON ramp=ON scrape=ON traffic=ON overCurb=3 ...` (the state of every switch).
- `[CurbFeel] config reloaded: <state>` after F9, and `[CurbFeel] <why>: <state>` when you toggle it.
- `[Sidewalk] <tile>: 314 obstacle boxes, 0 obstacle triangles, 2882 sidewalk triangles (city style), 0 meshes unreadable, 96 ms over 8 frame(s)` once per tile when its sidewalks and obstacles have been read (`park style` for park tiles; the ms and frames show what `MapBudgetMs` costs).
- `[Walls] <tile>: no sidewalk on this tile (bridge / river edge): stock walls kept` for tiles where the walls are left alone.
- `[Walls] <name>: mesh not readable, skipped` and `[Walls] DIAG: driving but no wall pairs. ...` are warnings: the walls of that mesh or tile weren't changed.
- `[Hull] <name>: body measurement implausible (...), using 1.25 m` (warning): the car's size couldn't be measured.
- `[Traffic] lane offsets in use: ...` when the traffic side-swipe lanes are applied.
- In the hot-module build: `CurbFeel 0.8.0 loaded as a hot module (load #N). ...` and `[CurbFeel] hot module unloaded, game back to stock.`

## Notes

- Local physics only, nothing is sent over the network. It does change gameplay (fewer wall and traffic penalties), so try it solo or in private lobbies first.
- Walls are paired per tile by name (`Guardrail_Regular*` ↔ `Guardrail_Wider*`), vertex count and position. A wall without a partner is left stock and logged.
- `GpuMeshReader.cs` is adapted from DriverCam's reader.
