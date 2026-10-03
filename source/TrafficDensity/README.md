# TrafficDensity

BepInEx 6 IL2CPP plugin for **Driving Rogue** that multiplies how many NPC traffic cars are on the road.

Current version: **0.1.0**

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

A short message at the top-centre of the screen shows the new multiplier and car count. The value is saved to `BepInEx/config/rogue.trafficdensity.cfg`.

## Settings (`rogue.trafficdensity.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | |
| `General.Multiplier` | 1.5 | 1 = stock, 2 = twice the cars, 0.5 = half |
| `General.MaxCars` | 60 | hard upper limit (pool max is 100); lots of cars cost frame rate |
| `General.AllowInMultiplayer` | false | off = stock traffic in multiplayer; if you host with it on, everyone gets the extra traffic |
| `Keys.Steps` | 0.5,0.75,1,1.25,1.5,2,2.5,3,4 | values the hotkeys step through |
| `Keys.ShowToast` | true | |

## Notes

- Lowering the multiplier mid-race doesn't delete cars; it stops new ones spawning until enough have driven off.
- Pairs well with CurbFeel's traffic side-swipes (lane splitting).
