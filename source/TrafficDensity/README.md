# TrafficDensity

BepInEx 6 IL2CPP plugin for **Driving Rogue** that multiplies how many NPC traffic cars are on the road.

Current version: **0.3.1**

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
| `Keys.Steps` | 0.5,0.75,1,1.25,1.5,2,2.5,3,4 | values the hotkeys step through |
| `Keys.ShowToast` | true | |
| `Fixes.Enabled` | true | the traffic AI fixes below; off = the game's own AI values |
| `Fixes.WreckClearDistance` | 40 | NPC-NPC crashes further than this ahead of you are removed at once (game: 150) |
| `Fixes.LaneChangeCheckBehind` | 25 | metres an NPC checks behind before changing lanes (game: 6) |
| `Fixes.MinBrake` | 0 | lowest throttle behind a close car, 0-1 (game: 0.2, Daredevil 0.6) |
| `Fixes.ObstructionCheckInterval` | 0.2 | seconds between checks for a car ahead (game: 0.5) |
| `Fixes.SpawnGap` | 30 | clear road the spawner wants around a new NPC in its lane (game: 20) |
| `Fixes.RubberBandSpeedFactor` | -1 | speed of NPCs far ahead as a fraction of normal (game: 0.6); higher = less bunching but changes pacing |
| `Perf.Enabled` | false | shared timing overlay for all Rogue mods (see [Perf overlay](#perf-overlay)); F4 toggles the overlay while on; one summary line in the log every 10 s |

Every `Fixes` value accepts **-1** to leave the game's value. Config edits apply at the next game start.

## Perf overlay

`Perf.Enabled = true` turns on a shared timing helper (`source/Shared/Perf.cs`) for **all** Rogue mods. Each mod times
its own work with `using (Perf.Scope("Mod.Part")) { ... }`; TrafficDensity owns the display:

- **Overlay** in the top-left corner (12 px in, scaled with screen height, up to 12 rows; above DriverCam's button).
  One row per timed part: average ms per frame, max ms in the last second, calls per frame. Footer: total ms of all
  mods per frame, managed memory allocated per frame (KB) and .NET gen0 garbage collections. **F4** hides / shows it.
- **Log**: one `[Perf]` line every 10 s in `BepInEx/LogOutput.log` with the totals and the five most expensive parts.

TrafficDensity's own parts are `Traffic.Update` (whole Update), `Traffic.Fixes` (AI fixes pass, 4x a second) and
`Traffic.Apply` (spawn cap check, once a second). Off (the default) costs next to nothing: every scope is a single flag check.

## Why extra traffic crashed and jammed

The game's traffic AI has no real collision avoidance. It works at stock density only because the road is mostly empty. With 2-4x the cars:

- **Wrecks block lanes.** Two NPCs that touch both stop for good with hazard lights. The game only removes them if they're more than 150 m ahead of you; otherwise they sit there until you've driven past. Cars behind them pile in, and that's a jam.
- **Cut-ins.** An NPC changing lanes looks 50 m ahead but only 6 m behind, and can jump two lanes at once.
- **No full stop.** NPC braking never drops below 20% throttle (60% for Daredevils), so they creep into slow or stopped cars.
- **Spawning.** A new car only needs 20 m of clear lane. After 3 failed tries the game spawns it anyway.
- **Bunching.** NPCs further ahead of you drive slower (the game's rubber banding), so cars behind keep catching up.

The `Fixes` settings address the first four. A few times a second TrafficDensity writes these values onto NPCs: a car is checked when it appears (new or back from the pool), when a `Fixes` value changes, and every 2 s after that. It uses no extra Harmony patches. When the fixes are switched off, or in multiplayer without `AllowInMultiplayer`, the game's original values are written back, including on cars waiting in the pool.

## Multiplayer: who can change traffic

The host simulates every NPC and syncs them to the other players. TrafficDensity checks its role through Mirror, the game's own multiplayer flag (`GameState.IsMultiplayerMode`) and the loaded scene names:

- **Offline:** everything applies.
- **Host:** applies only with `AllowInMultiplayer = true`, and then affects everyone in the lobby.
- **Client** (connected to someone else's game): never changes anything. The hotkeys just say the host controls traffic.

## What to check in the log (`/game-log TrafficDensity`)

- `TrafficDensity 0.3.1 loaded: x2.5 (Ctrl+PageUp/PageDown to change, Ctrl+Home = stock).` at startup (the multiplier shown is the saved one).
- `[Traffic] race start: game wants N NPC cars -> M (xK)` at each race start: the game's own count, the count now used, and the multiplier in effect.
- `[Traffic] fixes on (game values: spawn gap ...): wrecks cleared beyond ..., lane-change check behind ..., min brake ..., obstruction check every ..., spawn gap ..., rubber band ...` when the jam fixes are applied, and `[Traffic] fixes off: game values restored on N car(s)` when they're switched off.
- `[Traffic] multiplier set to xK ...` each time you change the multiplier with the keys or in Rogue Hub.
- `[Traffic] restore skipped a car: ...` (warning): a car couldn't be put back to the game's values.
- `[Perf] ...` lines: `[Perf] timing on for all Rogue mods (F4 toggles the overlay)` / `timing off`, or `[Perf] overlay owned by <plugin>; [Perf] settings here are ignored` when another mod runs the overlay.

## Notes

- 0.3.1: the toast / perf overlay `OnGUI` skips Unity's IMGUI Layout pass (`useGUILayout = false`; it only uses `GUI.*`).
- Lowering the multiplier mid-race doesn't delete cars; it stops new ones spawning until enough have driven off.
- Pairs well with CurbFeel's traffic side-swipes (lane splitting).
