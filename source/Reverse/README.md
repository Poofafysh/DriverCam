# Reverse

BepInEx 6 IL2CPP plugin for **Driving Rogue**: a reverse gear. The game has none, so a car stuck nose-first against a
wall or turned the wrong way can't back out.

Current version: **0.1.0**

## What it does

- **Hold the brake to reverse.** Brake the car to a complete stop and keep holding the brake. After `HoldDelay`
  (0.3 s) the car starts rolling backwards, building up to `MaxSpeedKmh` (25 km/h) at `Accel` (4 m/s per second).
- **Steering works like a real car in reverse.** Steer right and the back of the car swings right. The game's slower
  steering while braking is undone while reversing (`NoBrakeTurnPenalty`).
- **Let go of the brake to stop.** The reverse slows to a stop at `Decel` (10 m/s per second). Press the brake again
  before it stops and it carries on reversing.
- **Press the throttle to drive off.** Reverse ends at once and the game drives forwards as usual.
- **Walls and cars:** if something stops the car, the reverse speed drops to what the car is really doing, so no speed
  builds up against a wall.
- **Reverse also ends** when the car leaves the ground, starts drifting, the handbrake is pulled, the game pauses, the
  race ends, the car is wrecked, the car changes or respawns, the game takes away control, or the plugin is switched
  off.
- **Where it works:** the player's car, in single-player. Multiplayer and AI racers are never touched.

## How it works

Everything below comes from the game's code in `GameAssembly.dll`.

- **The game has no reverse.** Its speed is a number (`VehicleMovement.TargetSpeed`), clamped every frame to between
  the car's minimum speed (`MinSpeed`, normally 0) and its top speed. Braking at a standstill just holds it at 0.
- **The game overwrites the car's velocity.** Every physics step `VehicleMovement.ApplyMovement` sets the car's flat
  velocity to forward x speed, with one `AddForce(target - velocity, VelocityChange)`. At speed 0 that stops the car
  dead.
- **Reverse adds one more push in the same step.** The game's speed stays 0, so its own push cancels all flat motion.
  A Harmony postfix then adds `-forward x reverse speed` as a `VelocityChange`, and the car ends the step moving
  backwards at exactly that speed. The game's gravity and downforce are kept. Nothing is pushed while airborne.
- **Steering.** The game turns the car by steering input x turn speed, scaled to nothing at speed 0. For that one call
  (`VehicleMovement.HandleCarRotation`) the plugin gives the game the reverse speed and a mirrored steering input, then
  puts both back right after (a Harmony finalizer, so they come back even on an error).
- **Nothing is saved or left changed in the game.** Every change lasts one physics step or one call.

## What else sees a reversing car

The game's own speed stays 0 while reversing, so:

- **Scores:** Drift, Slipstream, Top Speed and Near Miss earn nothing. The race progress goes backwards (you lose
  distance), and the race timer keeps running.
- **Game HUD and engine sound:** 0 km/h and idle.
- **Other plugins:** EngineAudio idles, and DriverCam's gauges read 0.
- **RacingLine:** from RacingLine 0.7.1 nothing scores while reversing, nor afterwards until the car is back past the
  furthest point it had reached. Metres driven again after backing up never pay twice, so reversing can't inflate the
  run total the game uploads to the Steam leaderboard. With an older RacingLine, keep Reverse off for runs that should
  count.
- **Police:** a chaser sees the reversing car as stopped, so backing away from a cop fills the BUSTED meter. Police
  could read `rogue.reverse` and use the reverse speed in its stopped check (not done yet).

## For other plugins: `rogue.reverse`

AppDomain data `rogue.reverse` is a `float[3]`, written once a frame:

| Index | Meaning |
|---|---|
| 0 | 1 = reversing, 0 = not |
| 1 | reverse speed, m/s (0 or more) |
| 2 | `Time.unscaledTime` of the last write: treat it as live only when it is less than about 0.25 s old |

RacingLine reads it (no scoring while reversing). EngineAudio could play a reverse whine, and DriverCam's gauges could show
the speed and "R".

## Settings (`rogue.reverse.cfg`)

| Setting | Default | Meaning |
|---|---|---|
| `General.Enabled` | true | Master switch (applies at once) |
| `General.MaxSpeedKmh` | 25 | Top reverse speed, km/h (5-60) |
| `General.HoldDelay` | 0.3 | Seconds the brake must be held with the car fully stopped before it reverses (0-2) |
| `Tuning.Accel` | 4 | How quickly reverse speed builds while the brake is held, m/s per second (1-15) |
| `Tuning.Decel` | 10 | How quickly the car stops after you let go of the brake, m/s per second (2-40) |
| `Tuning.NoBrakeTurnPenalty` | true | Undo the game's slower steering while braking, while reversing |

Rogue Hub shows them all, with the Tuning ones under Advanced, plus a live status line.

## Log (`/game-log Reverse`)

- At startup: `[Reverse] hooks installed (VehicleMovement.ApplyMovement, VehicleMovement.HandleCarRotation with finalizer)`
  (or, after the warning `[Reverse] finalizer on HandleCarRotation refused (...); using a postfix`,
  `[Reverse] hooks installed (VehicleMovement.ApplyMovement, VehicleMovement.HandleCarRotation with postfix)`), then
  `Reverse 0.1.0 loaded. Hold the brake with the car stopped to reverse (up to 25 km/h; single-player).`
- Once per car: `[Reverse] player car found (game minimum speed 0.0 km/h)`. If the minimum speed is above 0, it adds
  `: above 0, so reverse can't start on this car`, because the car never fully stops.
- Each reverse: `[Reverse] reversing (brake held at a standstill)`, then `[Reverse] stopped: <why> (top 25 km/h, 6.3 m back)`.
  The reasons are `brake released`, `throttle`, `handbrake`, `airborne`, `drifting`, `no control`, `car on its side`,
  `car moved (respawn)`, `paused`, `switched off`, `multiplayer`, `race over`, `car wrecked`, `car change`, `no car`,
  `no physics steps`, `not allowed now`, `error`, `error breaker` and `unloaded`.
- If something fails: `[Reverse] game check: missing ...` / `[Reverse] hooks failed to install: ...` /
  `[Reverse] reverse stays off (game check or hooks failed).` mean a game update removed something it needs.
  `[Reverse] error (n/5, then Reverse stays off for this session): ...` and
  `[Reverse] runner error (n/5, then Reverse stays off for this session): ...` are the error breakers.

## Limits

- **A speed boost blocks it.** A card's flat speed bonus keeps the game's speed above 0, so the car never counts as
  stopped and reverse can't start while one is active.
- **Not tested in game yet.** Not tested: whether `MinSpeed` really is 0 on every car (see the log line) and how the
  steering feels.
- **No reverse lights.** The cars' "tail lights" are speed trails (`TrailRenderer`), not lamps, so there is nothing
  cheap to light up.

No other plugin patches `VehicleMovement`.
