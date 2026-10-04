# TrafficDensity

BepInEx 6 IL2CPP plugin for **Driving Rogue** that multiplies how many NPC traffic cars are on the road, and makes
them drive more like real traffic.

Current version: **0.4.0**

What it does:

- **Traffic multiplier**: more (or fewer) NPC cars than the game's own amount.
- **Traffic AI fixes** (`[Fixes]`): fewer NPC-NPC crashes and jams with dense traffic.
- **Safe lane changes** (`[SafeLanes]`, 0.4.0): NPCs no longer swerve into you. A lane change only goes ahead when the
  target lane is clear ahead and behind, allowing for how fast you (or another car) are closing in. No weaving, no
  two-lane jumps. See [Safe lane changes](#safe-lane-changes).
- **Speed limits** (`[SpeedLimits]`, 0.4.0): traffic drives the limit of the road it is on, in mph: highway 70, open
  road 55, city 35, curvy 40, each driver a little over or under. See [Speed limits](#speed-limits).

In **Rogue Hub** (0.3.0): every setting has a name, a slider range and a step there, the multiplier and car limit are clamped by BepInEx (0.25-4x, 10-100 cars), the `[Fixes]` values show -1 as a GAME switch, TrafficDensity's card shows the live multiplier and car count, a "Back to stock traffic" button sets x1, and the multiplier message goes to the hub's notification stack when the hub is installed.

## How the game decides traffic

`DefaultAISpawner.Start` sets the race's traffic cap once per race:

```
aiSpawnCount = RoundToInt(race NPC count x spawnCountMultiplier)
```

- **Race NPC count** comes from the race data (`RunRaceSO` NPC behaviour counts): 6 cars in stage 1, rising by one per stage to 14 in stage 9.
- **`spawnCountMultiplier`** is set by the game's own "more traffic" hazard (`TrafficMultiplierHazardEffect`).
- **`spawnCountMultiplierDebug`** is only applied in debug builds, so it does nothing in the released game.
- The spawner then keeps topping traffic up to `aiSpawnCount` all race long, spawning 110-435 m ahead or 90-110 m behind.
- The traffic pool holds at most **100** cars.

TrafficDensity takes the game's final count (so hazards still stack) and multiplies it. Changes apply live, mid-race.

## Controls

| Key | Does |
|---|---|
| Ctrl+PageUp / Ctrl+PageDown | step the multiplier through `Steps` (default 0.5 → 4) |
| Ctrl+Home | back to stock (x1) |
| F4 | show / hide the perf overlay (only while `Perf.Enabled` is on) |

A short message at the top-centre of the screen shows the new multiplier and car count. The value is saved to `BepInEx/config/rogue.trafficdensity.cfg`.

## Settings (`rogue.trafficdensity.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch: the multiplier and the AI fixes |
| `General.Multiplier` | 1.5 | 1 = stock, 2 = twice the cars, 0.5 = half |
| `General.MaxCars` | 60 | hard upper limit (pool max is 100); lots of cars cost frame rate |
| `General.AllowInMultiplayer` | false | off = stock traffic in multiplayer. On + you host = everyone gets the extra traffic and the changed NPC behaviour (the host runs all traffic). As a client nothing is changed |
| `General.KeepOffLeaderboards` | false | on = a run in which safe lane changes or speed limits changed the traffic (a lane change stopped, a speed limit written) isn't uploaded to the Steam leaderboards; off = runs upload as usual. On while the leaderboard guard couldn't be installed = those two features stay off |
| `General.UsedThisRun` | false | written by the plugin (that mark, kept across a quit and continue, cleared when a new run starts); don't edit |
| `Keys.Steps` | 0.5,0.75,1,1.25,1.5,2,2.5,3,4 | values the hotkeys step through |
| `Keys.ShowToast` | true | |
| `Fixes.Enabled` | true | the traffic AI fixes below; off = the game's own AI values |
| `Fixes.WreckClearDistance` | 40 | NPC-NPC crashes further than this ahead of you are removed at once (game: 150) |
| `Fixes.LaneChangeCheckBehind` | 25 | metres an NPC checks behind before changing lanes (game: 6) |
| `Fixes.MinBrake` | 0 | lowest throttle behind a close car, 0-1 (game: 0.2, Daredevil 0.6) |
| `Fixes.ObstructionCheckInterval` | 0.2 | seconds between checks for a car ahead (game: 0.5) |
| `Fixes.SpawnGap` | 30 | clear road the spawner wants around a new NPC in its lane (game: 20) |
| `Fixes.RubberBandSpeedFactor` | -1 | speed of NPCs far ahead as a fraction of normal (game: 0.6); higher = less bunching but changes pacing |
| `SafeLanes.Enabled` | true | NPC lane changes check you and other cars ahead and behind, with closing speed; off = the game's own check |
| `SafeLanes.YourGapSeconds` | 3 | seconds of closing speed kept clear around you (3 s at 140 mph against a 60 mph car = about 110 m) |
| `SafeLanes.YourGapMetres` | 15 | clear road always left in front of and behind you, on top of the closing time |
| `SafeLanes.CarGapSeconds` | 1.5 | the closing-time margin around other NPC cars |
| `SafeLanes.CarGapMetres` | 8 | clear road always left around other NPC cars |
| `SafeLanes.CooldownSeconds` | 4 | wait between two lane changes of the same car |
| `SafeLanes.OneLaneAtATime` | true | never jump two lanes in one move |
| `SafeLanes.LogEach` | false | log every stopped lane change (tuning only) |
| `SpeedLimits.Enabled` | true | traffic follows the speed limit of the road; off = the game's speeds (68 mph everywhere) |
| `SpeedLimits.HighwayMph` | 70 | long straight stretches outside the city, tiles with freeway signs |
| `SpeedLimits.OpenRoadMph` | 55 | the rest of the road outside the city (park, industrial areas) |
| `SpeedLimits.CityMph` | 35 | residential and commercial areas, tiles with slow-down 40 signs |
| `SpeedLimits.CurvyMph` | 40 | curvy stretches (only where lower than the area's limit) |
| `SpeedLimits.DriverVariation` | 0.08 | each driver up to 8% over or under the limit (Rogue Hub shows %) |
| `SpeedLimits.SlowDriverFactor` | 0.85 | the game's slow ("grandma") cars drive this share of the limit |
| `SpeedLimits.CurvyDegrees` | 45 | curvy = the road turns at least this many degrees within 200 m |
| `SpeedLimits.HighwayMaxDegrees` | 12 | outside the city, highway = at most this many degrees within 400 m |
| `SpeedLimits.CityBiomes` | Residential,Commercial | the game's area types that count as city (others: Park, Industrial) |
| `SpeedLimits.UseSignHints` | true | follow the map's freeway / slow-down signs (see [Speed limits](#speed-limits)) |
| `SpeedLimits.MatchSpeedometer` | true | limits as the game's speedometer shows them (it reads about 10% high); off = real mph |
| `SpeedLimits.LogZones` | true | log the zones of each race and the speeds in use once a minute |
| `Perf.Enabled` | false | shared timing overlay for all Rogue mods (see [Perf overlay](#perf-overlay)); F4 toggles the overlay while on; one summary line in the log every 10 s |

Every `Fixes` value accepts **-1** to leave the game's value. `Fixes` edits apply at the next game start;
`SafeLanes` and `SpeedLimits` edits apply live (within a second; a driver's own over / under factor at its next spawn).

## Perf overlay

`Perf.Enabled = true` turns on a shared timing helper (`source/Shared/Perf.cs`) for **all** Rogue mods. Each mod times
its own work with `using (Perf.Scope("Mod.Part")) { ... }`; TrafficDensity owns the display:

- **Overlay** in the top-left corner (12 px in, scaled with screen height, up to 12 rows; above DriverCam's button).
  One row per timed part: average ms per frame, max ms in the last second, calls per frame. Footer: total ms of all
  mods per frame, managed memory allocated per frame (KB) and .NET gen0 garbage collections. **F4** hides / shows it.
- **Log**: one `[Perf]` line every 10 s in `BepInEx/LogOutput.log` with the totals and the five most expensive parts.

TrafficDensity's own parts are `Traffic.Update` (whole Update), `Traffic.Fixes` (AI fixes pass, 4x a second),
`Traffic.Apply` (spawn cap check, once a second) and `Traffic.Speed` (speed limits, twice a second). Off (the default) costs next to nothing: every scope is a single flag check.

## Why extra traffic crashed and jammed

The game's traffic AI has no real collision avoidance. It works at stock density only because the road is mostly empty. With 2-4x the cars:

- **Wrecks block lanes.** Two NPCs that touch both stop for good with hazard lights. The game only removes them if they're more than 150 m ahead of you; otherwise they sit there until you've driven past. Cars behind them pile in, and that's a jam.
- **Cut-ins.** An NPC changing lanes looks 50 m ahead but only 6 m behind, and can jump two lanes at once.
- **No full stop.** NPC braking never drops below 20% throttle (60% for Daredevils), so they creep into slow or stopped cars.
- **Spawning.** A new car only needs 20 m of clear lane. After 3 failed tries the game spawns it anyway.
- **Bunching.** NPCs further ahead of you drive slower (the game's rubber banding), so cars behind keep catching up.

The `Fixes` settings address the first four. A few times a second TrafficDensity writes these values onto NPCs: a car is checked when it appears (new or back from the pool), when a `Fixes` value changes, and every 2 s after that. It uses no extra Harmony patches. When the fixes are switched off, or in multiplayer without `AllowInMultiplayer`, the game's original values are written back, including on cars waiting in the pool.

## Safe lane changes

How the game decides a lane change (GameAssembly, checked in IDA):

- A car closer than ~45 m behind a slower car takes the first "free" lane **up to two lanes away** and moves over.
  A few seconds later it moves back towards its own lane whenever that lane is "free".
- "Free" only compares road positions: nothing within 50 m ahead or **6 m behind** (25 m with `Fixes.LaneChangeCheckBehind`).
  You count as an obstacle, but **your speed is ignored**: at 140 mph, 30 m back counts as free, so the car pulls out
  in front of you.

What `SafeLanes` adds: the game still picks when and where to go, but a Harmony prefix on
`AIVehicleLaneHandler.MoveToLaneIndex` cancels the move unless the target lane (and every lane crossed on the way) is
clear by **gap + closing speed x seconds**, ahead and behind:

- **You** (and every player in multiplayer): `YourGapMetres` + closing speed x `YourGapSeconds`. A lane you are
  drifting into counts too (your sideways movement over the next second).
- **Other cars** (oncoming ones included, closing at both speeds): `CarGapMetres` + closing speed x `CarGapSeconds`,
  including cars already changing into that lane.
- **Cooldown**: `CooldownSeconds` between two changes of the same car. **One lane at a time** (`OneLaneAtATime`).

A cancelled change leaves the car in its lane, where the game's obstruction braking slows it behind the car ahead (with
`Fixes.MinBrake` 0 it can slow right down). It writes nothing to the game, so switching it off is instant. Cars Police
drives (its daredevil rivals and chasers) are never touched.

## Speed limits

The game gives every normal traffic car the same top speed on every road: 27.8 m/s, which the speedometer shows as
**68 mph** (slow "grandma" cars 41, daredevils 123). `SpeedLimits` replaces that with the limit of the road the car is on.

Once per race it reads the road and gives every 10 m a zone:

| Zone | Default | When |
|---|---|---|
| highway | 70 mph | outside the city: the road turns at most `HighwayMaxDegrees` (12) within 400 m; or a tile with freeway signs |
| open road | 55 mph | outside the city, the rest |
| city | 35 mph | the race's area type is Residential or Commercial (`CityBiomes`); or a tile with slow-down 40 signs |
| curvy | 40 mph | the road turns at least `CurvyDegrees` (45) within 200 m (only where lower than the area's limit) |

- **Area type**: the game picks one per race (`BiomeType` Residential, Commercial, Park, Industrial; every tile scene
  carries all of them). All roads have 4 lanes / 20 m in this game, so lane count can't tell a highway apart.
- **Signs** (`UseSignHints`): the tiles that carry the map's freeway signs or its round slow-down signs (40 / 80, the
  numbers are km/h) are listed by tile name. A slow-down 80 tile is never a highway.
- A slower zone starts 60 m early (cars slow down before the bend) and ends 60 m late.
- Each car: its zone's limit x its own driver factor (`DriverVariation`: +/-8%), slow drivers x `SlowDriverFactor`. Set
  where the game sets a car's speed (spawn and pool reuse, `AIPathFollower.SetVehicle` postfix; the car also starts at
  that speed) and updated twice a second as it drives into another zone. The game's own corner slowdown and obstruction
  braking still apply on top.
- **mph as the speedometer shows it** (`MatchSpeedometer`): the game's speedometer shows m/s x 2.237 x 1.1 (10% high),
  so 70 here sits next to your own 140 the way it looks.
- Kept: daredevils (Police's rivals), cars Police drives, the game's slow-motion slowdown (kept as an offset), rubber
  banding (cars far ahead of you x0.6, behind you x0.5: change it with `Fixes.RubberBandSpeedFactor`).
- Switched off (or multiplayer without `AllowInMultiplayer`): every car gets the game's speed back. A chaser Police is
  driving at that moment gets it as soon as Police lets go of it. After 5 errors the feature switches itself off the same way.
- A car coming back from the traffic pool is noticed at spawn (the `SetVehicle` hook), and also by its jump in road
  distance, so the limits keep working even if that hook ever fails.

### Speed-limit signs on the map (mph)

The maps have no km/h text: the speed limit signs are European round signs with only a number (40, 60, 80, 100 on
the shared `Road_Signs` texture). On the road tiles only `sign_SlowDown40` (5 tiles) and `sign_SlowDown80` (1 tile) use
them; `sign_SpeedLimit100` appears only in the car intro scenes. The speedometer unit is the game's own setting
(Settings > Gameplay: `CurrentUnitSystem`, mph by default). Showing mph on those signs would mean drawing our own sign
face (a texture made by the plugin, never a game file) and putting it on those renderers; not done yet.

## Multiplayer: who can change traffic

The host simulates every NPC and syncs them to the other players. TrafficDensity checks its role through Mirror, the game's own multiplayer flag (`GameState.IsMultiplayerMode`) and the loaded scene names:

- **Offline:** everything applies.
- **Host:** applies only with `AllowInMultiplayer = true`, and then affects everyone in the lobby.
- **Client** (connected to someone else's game): never changes anything. The hotkeys just say the host controls traffic.

In multiplayer the host's traffic comes from `MultiplayerAISpawner` (not `DefaultAISpawner`), with the same AI
components. Safe lane changes and speed limits work on it (host with `AllowInMultiplayer`); they check every player's
car. The multiplier and the `[Fixes]` values still only reach `DefaultAISpawner`, so in multiplayer they don't apply yet.

## What to check in the log (`/game-log TrafficDensity`)

- `TrafficDensity 0.4.0 loaded: x2.5 (Ctrl+PageUp/PageDown to change, Ctrl+Home = stock).` at startup (the multiplier shown is the saved one).
- `[Traffic] race start: game wants N NPC cars -> M (xK)` at each race start: the game's own count, the count now used, and the multiplier in effect.
- `[Traffic] fixes on (game values: spawn gap ...): wrecks cleared beyond ..., lane-change check behind ..., min brake ..., obstruction check every ..., spawn gap ..., rubber band ...` when the jam fixes are applied, and `[Traffic] fixes off: game values restored on N car(s)` when they're switched off.
- `[Traffic] multiplier set to xK ...` each time you change the multiplier with the keys or in Rogue Hub.
- `[Traffic] restore skipped a car: ...` (warning): a car couldn't be put back to the game's values.
- `[Traffic] safe lane changes (last 60 s): N allowed, M stopped (near you A, near another car B, cooldown C, two-lane jumps D)`
  once a minute while lane changes happen. With LogEach on in [SafeLanes]: `[Traffic] lane change stopped (you): car at 1234 m to lane 2, only 40 m clear`.
- `[Traffic] speed limits on` when the limits start (each race), and `[Traffic] speed limits off: game speeds restored on N car(s)`
  (`, K more once Police lets go of them` for chasers Police was driving; each of those later logs
  `[Traffic] speed limits off: game speed given back to a car Police let go of`).
- `[Traffic] leaderboard upload skipped: safe lane changes or speed limits changed the traffic in this run (General.KeepOffLeaderboards)`
  at the end of such a run (only with `KeepOffLeaderboards` on), and `[Traffic] new run: leaderboard mark cleared` when a new run starts.
- Warnings `[Traffic] leaderboard guard not installed: ...` at startup, and (with `KeepOffLeaderboards` on) once
  `[Traffic] safe lane changes and speed limits stay off: the leaderboard guard isn't installed and KeepOffLeaderboards is on`.
- Warning `[Traffic] speed limits: update failed (n/5 before switching off; ...)` / `speed at spawn failed (...)` (at most one per
  10 s), and after 5 errors `[Traffic] speed limits switched off after repeated errors (...); game speeds given back` (error;
  Rogue Hub's card then says "speed limits off after errors"): tell me.
- `[Traffic] speed limits for this race (Park biome, 4 lanes, 4.6 km road, 9 tiles, speedometer mph): highway 70 mph 38%, open road 55 mph 22%, city 35 mph 0%, curvy 40 mph 40%; ...`
  at each race start, then one line per tile: `[Traffic]   tile 3/9 2ed654ce (Medium, game curvature 0.31, 900-1400 m, sharpest 12 deg/100 m): open road 60%, curvy 40%; slow-down 80 sign`.
- `[Traffic] speed limits: N car(s) limited to 33-76 mph (average 52)` once a minute (with `LogZones`).
- Warnings `[Traffic] speed limits: tiles add up to ... m but the road is ... m; tile hints not used`, `... biome unreadable`,
  `... tile list unreadable`: the zones then come from the road's shape only.
- Errors `[Traffic] safe lane changes unavailable (... hook failed)` / `[Traffic] speed limits at spawn unavailable (...)` at
  startup, and `[Traffic] safe lane changes switched off after repeated errors`: tell me.
- `[Perf] ...` lines: `[Perf] timing on for all Rogue mods (F4 toggles the overlay)` / `timing off`, or `[Perf] overlay owned by <plugin>; [Perf] settings here are ignored` when another mod runs the overlay.

## Notes

- 0.4.0: safe lane changes (`[SafeLanes]`), speed limits by road (`[SpeedLimits]`), perf part `Traffic.Speed`
  (speed limits, twice a second). Harmony: prefix `AIVehicleLaneHandler.MoveToLaneIndex`, postfix `AIPathFollower.SetVehicle`,
  and (optional `General.KeepOffLeaderboards`, off by default: the user chose "Add a toggle, default: uploads count") a prefix on
  `LeaderboardsManager.PublishEntry` that only ever skips the upload (same pattern as PitStop, Sandbox, CurbFeel, RacingLine).
- 0.3.1: the toast / perf overlay `OnGUI` skips Unity's IMGUI Layout pass (`useGUILayout = false`; it only uses `GUI.*`).
- Lowering the multiplier mid-race doesn't delete cars; it stops new ones spawning until enough have driven off.
- Pairs well with CurbFeel's traffic side-swipes (lane splitting).
