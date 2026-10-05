# Sandbox maps

Every sandbox race is on a wide road: **30 m with 6 lanes** (the game's roads are 20 m with 4), **twice as long**
(`[Run] LengthMultiplier` = 2), with every building and prop removed. Only the road and what belongs to it remains:
asphalt and lane lines, curbs, sidewalks, guardrails and street lights. The road is **built at runtime along each tile's
own path** (`Maps.cs` (class `WideRoads`, `[Maps] WideRoads` default on), `Maps.Build.cs`, `Maps.Runner.cs`, Sandbox 0.2.0). Status: **written, builds, not played yet.**

An earlier design (Blender-built `.wtl` tiles riding on stock "carrier" tiles) is kept at the end for reference only;
the runtime build replaced it because it covers all 54 tiles, needs no tile-choice hook and ships no game-derived data.

## How the game builds a road (from the research)

- A race is a chain of tile scenes, each a 300, 400 or 500 m square. They're loaded additively through Addressables
  during the loading screen (`LevelGenerator.SpawnTilesCoroutine`), then placed (`SetScenePositionAndRotation`).
- **Tile frame:** the tile spans `0..size` on x and z. The road enters at `(size/2, 0, 0)`. A Top exit leaves at
  `(size/2, 0, size)`, a Left exit at `(0, 0, size/2)`.
- **The path:** `RoadPathGenerator` joins every tile's `EasyRoad_PathWaypoint_Lite` children (under `PathWaypoints`,
  about 10 m apart) into one spline. Traffic, AI racers, obstacles, the timer, RacingLine and Police all use it.
- **Width and lanes are global:** `RoadTileContainerSO.roadWidth` 20 (+0x58) and `roadLaneCount` 4 (+0x5C).
  `RunWorldManager.UpdateLaneInfo` turns them into lane centres at `-W/2 + W/2n + i·W/n`.
- **The road surface:** a 20 m EasyRoads3D mesh on a flat Street-layer (11) collider, 4-5 m wider than the road on
  each side. The walls are invisible Guardrail_* MeshColliders on layer 15. The stock tiles have no lightmaps.

## The runtime design (implemented)

1. **Width first.** Before any tile, racer, traffic car or obstacle exists, `RoadTileContainerSO.roadWidth` /
   `roadLaneCount` are set to `[Maps] Width` / `Lanes` (lanes at least 3.5 m) and `RunWorldManager.UpdateLaneInfo` is called. Lane centres:
   -12.5, -7.5, -2.5, 2.5, 7.5, 12.5 m. Hooks (all prefixes, idempotent, never skip the game's method):
   - `LevelGeneratorTileSelector.GetRandomTiles` (0x1807A9BB0): host and single-player (also before
     `RunWorldManager.ShouldSpawnGasStation` / `ShouldSpawnCarWash`, which answer false in sandbox races);
   - `LevelGenerator.GenerateMultiplayerLevelAsClient` (0x180795CF0, patched by Multiplayer.cs, which calls
     `WideRoads.Apply`): a multiplayer client, which never picks tiles (it loads the host's tile ids with
     `FindTileById` and starts the spawn coroutine itself); width from the host's run settings;
   - `LevelGenerator.SpawnTilesCoroutine` (0x180796BF0): every other level load (`GenerateLocalLevel`,
     `GenerateTestLevel`, `Start`), a safety net.
   AI racer behaviours (`AvoidancePathRacerBehaviour`, `SteerRacerBehaviour`) and `VehicleAutoInputHandler` read the
   container when they start, so it stays wide for the whole race and is restored on every exit path.
2. **Centre line.** `RoadPathGenerator.GeneratePath` (0x1807B2260) postfix, on the loading screen after every tile
   scene loaded and was placed (the finish line exists by then): each tile's `WaypointParentReference` children
   (sibling order) joined into one line, Catmull-Rom resampled every 2.5 m, right vectors and normals computed over the
   whole line (so tile seams share a sample), max(40 m, 1.5 W) of straight road added at both ends. GeneratePath drops
   some waypoints (`ignoreTileLastWaypoint`, close / direction skips), so the line is checked against
   `RoadPathGenerator.RegularPath` at 20 points; more than 0.5 m off and the race spline itself is sampled instead.
3. **Hairpins.** A sample whose reach (W/2 + 6 m each side) meets a part of the road more than max(60 m, 2 W) further
   along it is "pinched": no wall, guardrail, raised sidewalk or street light there.
4. **Build, all at once on the loading screen.** Per tile, under `fx_WideRoad` parented to its "Road Network":
   - one visual mesh, 9 submeshes: asphalt, white lines (edges and 3 m / 9 m dashes), yellow double centre line, curb,
     sidewalk, ground (out to 80 m, sloping 2 m down), guardrail (0.45-0.8 m, both faces, posts every 5 m), poles,
     lamp heads (emissive);
   - `fx_WideStreet`: Street-layer (11) MeshCollider, flat to +-(W/2 + 6) with a bevelled 0.15 m curb, the stock road's
     physics material;
   - `fx_WideWall`: Guardrail-layer (15) MeshCollider at +-(W/2 + 4.5), 3 m high, both faces, end caps, the stock
     guardrail's physics material (not a "Guardrail" name, so CurbFeel's WallShifter leaves it alone);
   - then every non-trigger collider and every `Light` under Biomes and Road Network is switched off.
   - and every renderer there is listed (still the loading screen). Only setting `forceRenderingOff` on that list is
     spread over frames (`[Maps] BudgetMs`, no scan during the race); the first two tiles are hidden at once.
   Near-car check (`NearCheck`): the Road Network colliders of the camera's tile and the next one, both every 0.25 s
   from the build until 10 s after the race is ready, then alternately every 0.5 s (each about every 1 s; covers
   CurbFeel's F9 / F10 reset). Round-robin re-check of every tile, one tile per 0.15 s at most (so each tile about every
   N x 0.15 s, ~3 s with ~20 tiles): colliders only for 15 s after the build, then each tile at most every 5 s for
   colliders, renderers and lights. Our own objects are recognised by pointer, not by name (no string per component).
5. **Street lights** (`[Maps] StreetLights`): a pole every 40 m, alternating sides, between the sidewalk and the
   guardrail, 8 m tall with a 2.5 m arm over the road and an emissive head; a pool of 8 point lights (range 22 m) moved
   to the poles nearest the camera every 0.5 s.
6. **Finish line.** Its trigger is measured and its root scaled sideways to W + 4 m (34 m), put back afterwards.
7. **Restore** (idempotent): sandbox race ends, `SetRun(false)`, the next race isn't a sandbox race, error breaker
   (3 errors), `OnDestroy`, a multiplayer client's wide race scene left. Renderers, colliders and lights back on, our
   objects, meshes and lights destroyed, finish scale back, container width back with `UpdateLaneInfo`.

## Multiplayer

- **Length and tiles** are the host's: the host runs `GetRandomTiles` (with the length multiplier) and the gas-station
  choice, and sends the tile ids; clients load the same tiles at the same places.
- **Width** is not in the tile list: the host's run settings (map on/off, width, lanes) travel on Sandbox's own Steam
  channel (Multiplayer*.cs) and `WideRoads.Apply` / `ClampW` / `ClampN` read them through `Multiplayer.Wide` /
  `Width` / `Lanes` (the local config when no multiplayer run is on). Each client reports the width it applied; the
  host logs a mismatch.
- **Sandbox state** in multiplayer: `Plugin.Recompute` calls `Multiplayer.SandboxAgreed` and `Plugin.ActiveNow` calls
  `Multiplayer.GuardNow`, so `Active`, the perks and the map treat an agreed multiplayer sandbox race as a sandbox race.
- The road geometry is deterministic (same tiles, same placement, same waypoint and spline maths), so every player
  drives the same road. Every player needs the same Sandbox version.

## Other plugins

- **RacingLine** and **TrafficDensity** read the live road width and lane list: they follow.
- **Police daredevils** follow RacingLine's line. **Police patrols** use a fixed `DriveLimit = 7.5` (Police's
  `Runner.cs`): on a 30 m road they keep to the middle lanes until Police reads `CurrentRoadWidth / 2 - 2.5`.
- **CurbFeel**: `WallShifter` only touches `*Guardrail*` colliders, so `fx_WideWall` is safe; its `CurbFeel_Ramp`
  colliders (built at a tile's first scan, +1 s, +4 s) under the old road collider are switched off by the near-car
  check within 0.25 s on the camera's tile and the next, elsewhere by the round-robin re-check. Optional (CurbFeel's file): skip a tile
  whose Road Network has an `fx_WideRoad` child, to save the sidewalk mapping work.
- **Scenery.cs** gives back anything it hid on a tile before the maps take it (`SceneryRunner.ReleaseTile`) and skips
  those tiles (`WideRoads.Owns`); with the maps on, it only strips tiles when the wide build failed.

## Reference: the carrier-tile design (superseded)

Built in Blender as `.wtl` text meshes (`Assets/maps/build_tile.py`, `extract_waypoints.py`; two local test tiles
`Wide_Easy_1.0.wtl` / `Wide_Easy_5.0.wtl`, git-ignored, never committed), each riding on one stock "carrier" tile with the same path: hide the carrier,
add ours. Its cross-section is the one the runtime build uses (road +-15 m, lines at +-5 / +-10 m, double yellow at 0,
edge lines at +-14.6 m, 0.15 m curb, sidewalk to +-19 m, walls at +-19.5 m, Street collider to +-21 m). Dropped because
it needs a tile-choice hook limited to tiles with a wide twin and ships meshes that follow game paths; the runtime build
covers every tile. The `.wtl` loader was never written.

```
python extract_waypoints.py "Easy_1.0 (T_300)" straight300.json
blender -b --factory-startup --python build_tile.py -- straight300.json <out dir> preview.png [seed]
```

## Clean content

The runtime road ships nothing from the game: it is built in the player's own game from the tiles' waypoints at run
time. Only the generator scripts (`build_tile.py`, `extract_waypoints.py`) are in the repo. The `.wtl` meshes they make
follow stock tile paths read from the export, so they stay local (`Assets/maps/.gitignore`), like the waypoint JSON.
No game meshes, textures, paths or scene data are committed.
