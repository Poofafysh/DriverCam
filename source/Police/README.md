# Police

BepInEx 6 IL2CPP plugin for **Driving Rogue**: police patrols that react to how you drive. Crash near a patrol car,
or blast past one much faster than it's going, and it chases you. Get away and you've escaped. Get caught and you
lose a few seconds off the race timer.

Current version: **0.0.2** (preview: "pursuit lite")

Design doc (claude.ai): "Police Pursuit - v1 Concept" (revised). The police only react to what you do. Nothing
escalates at random, there are no heat levels, and nothing carries over from one race to the next. 0.0.1 covers the
doc's phases 1-3 (patrols, noticing, a basic chase). **Single-player only:** in multiplayer, or if the game mode can't
be read, the plugin does nothing.

## What it does (0.0.2)

1. **Patrols.** About once every `PatrolSpacing` metres you drive (800 by default; the first patrol of each race comes early), an ordinary traffic car 300-700 m
   ahead of you becomes a patrol, up to `MaxPatrols` at a time (2 by default). Cars in your lane within 100 m are never
   picked, and neither are crashed ones. A patrol gets a lightbar on its roof: a red and a blue block plus two lights.
   - The lights are off until it chases you, then they flash red and blue at 2 Hz.
   - A marker floats above each patrol, with a **POLICE** tag once you're within about 450 m: **blue** = idle, **amber** = you're inside its notice zone, **red** =
     it's chasing you.

   A patrol is let go when it's more than 150 m behind you, when the game despawns or reuses the car, or when the race
   ends.
2. **Noticing.** A patrol only notices you within `NoticeRange` metres (60 by default) along the road, ahead or behind.
   It doesn't matter which lane you're in. It notices when:
   - you crash into anything (the game's own collision count goes up), or
   - you pass it more than `OverspeedKmh` (80 km/h) faster than it's going.

   Only one chase can run at a time. After a chase ends, no patrol can notice you for `Cooldown` seconds (20).
   In `Mode = Chill`, patrols drive around with their lightbars but never notice you.
3. **Chase ("pursuit lite").** The patrol that noticed you chases you, driven by the game's own traffic AI. It stays in
   lanes and changes lanes with its normal avoidance. For the chase, two of its AI settings change: rubber banding is
   switched off, and its top speed is set to `SpeedFactor` × your car's top speed (0.97 by default). So it can keep up,
   but it can never out-run your car. Both settings are put back exactly when the chase ends, when the car is despawned,
   and when the plugin switches off.

   The **lead bar** (top-centre, under TrafficDensity's message) starts at 50%:

   | Raises it | Lowers it |
   |---|---|
   | being more than 20 m ahead of the chaser (up to +6%/s at 80 m) | the chaser within 20 m, alongside or ahead of you (−8%/s) |
   | each near miss (+5%) | each collision (−15%) |
   | driving above 80% of your top speed (+2%/s) | driving below 60% of your top speed (−4%/s) |

   - **ESCAPED:** the bar reaches 100%, the chaser falls more than 130 m behind, or the game despawns it.
   - **CAUGHT:** the bar empties. `CaughtPenaltySeconds` (5) come off the race timer.
   - **Time up:** after `Duration` seconds (40) the bar decides: above 50% means you escaped.

   Either way the patrol switches its lights off, gets its AI settings back and goes back to being normal traffic.

**F3** turns patrols off and on for the current session (a short message confirms it). The `.cfg` file isn't changed.

### The caught penalty

The game's `TimerManager.AddTimerSeconds(-5)` can't take time away: in single-player it passes the negative value to
`RaceTimer.RemoveCountdownTime`, which clamps it to 0, so nothing happens. Police instead calls
`RaceTimer.RemoveCountdownTime(5)`, the game's own way of taking time off, directly. It only does this while a
countdown timer is running, and it always leaves at least 3 s on the clock: a police catch never ends your race by
itself. If no time can be taken, the message just says `CAUGHT` and the log says why. The game can carry time left
over into the next race, so a penalty can also show up there.

## Settings (`BepInEx/config/rogue.police.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch |
| `General.Mode` | Normal | `Normal` = patrols notice and chase. `Chill` = patrols, but they never notice you. `Off` = no patrols |
| `Patrols.MaxPatrols` | 2 | most patrols at once (0-4) |
| `Patrols.PatrolSpacing` | 800 | on average one new patrol per this many metres driven (300-10000) |
| `Notice.NoticeRange` | 60 | metres along the road, ahead or behind, in which a patrol can notice you (10-200) |
| `Notice.OverspeedKmh` | 80 | passing a patrol this much faster than it gets you noticed (10-300) |
| `Chase.Duration` | 40 | longest chase in seconds; then the bar decides (10-300) |
| `Chase.CaughtPenaltySeconds` | 5 | seconds off the race timer when caught (0-30, 0 = none; always leaves 3 s) |
| `Chase.Cooldown` | 20 | seconds after a chase before any patrol can notice you again (0-300) |
| `Chase.SpeedFactor` | 0.97 | the chaser's top speed as a fraction of yours (0.5-0.99) |
| `Debug.LogEvents` | true | log patrols, notices and chase results |

Edits to the `.cfg` file apply at the next game start. The plugin reads every value live, so an in-game config
manager can change them while you play.

## This is a gameplay change

- It changes how up to `MaxPatrols` traffic cars drive. During a chase, one of them follows you at nearly your top
  speed. The game's own traffic code is used as-is. Police never touches traffic cars' colliders, lanes, obstruction or
  collision values: CurbFeel and TrafficDensity tune those, and Police leaves them alone.
- Getting caught can take seconds off your race timer.
- The lightbar blocks have no colliders (they're removed the moment each block is created), so they don't change any
  physics. Nothing is attached to the car itself: the lightbar follows the car every frame, so a car the game recycles
  never carries it along. Everything Police creates is destroyed when the patrol is let go, when the scene changes, or
  when the plugin switches off.
- Single-player only. Nothing is ever sent to other players; Police reads `GameState.IsMultiplayerMode` and stays off
  in multiplayer.

## What to check in the log (`/game-log Police`)

- `Police 0.0.2 loaded` (the version you installed) and `[Police] game check OK: ...`. A `game check: missing ...` line lists what a game update
  removed and which features are off.
- `[Police] lightbar material: shader '...'`, logged once. If the colour says `none`, the blocks keep their default
  colour, but the lights still flash.
- `[Police] active`, `idle: ...` (why it's off: config, F3, multiplayer) and `waiting for a race`.
- With `LogEvents`:
  - `patrol picked: N m ahead ...` and `patrol released: <why>`
  - `noticed: crash N m from it` or `noticed: passed it N km/h faster`, with the chaser's top speed before → after
  - `chase over: ESCAPED / CAUGHT (<why>) after N s, lead N%, -5 s` or `no time penalty (<why>)`
- `... switched off for this session after an error` means one feature stopped. `switched off for this session after
  repeated errors` means the whole plugin stopped. Either way the cars are restored and Police's own objects removed.

> **Upgrading from 0.0.1:** the default `PatrolSpacing` changed from 1500 to 800, but an existing `rogue.police.cfg` keeps its old value. Set `PatrolSpacing = 800` there yourself.

## Known limits (0.0.1)

- The chaser is a traffic car. It stays in lanes, brakes for traffic ahead of it (the game's obstruction logic), and
  slows down in curves like all traffic. A wall of cars can stop it.
- A patrol you crash into is wrecked: the game stops crashed traffic for good, so it can't chase you, and you'll
  escape it.
- No siren sound yet.
- No score category yet (PURSUIT is planned for 0.0.2). Escapes and catches only show as messages and log lines.
- The lightbar height comes from the car's own collider (fallback 1.6 m). On unusual models it may float a little or
  sit low.
- The lead bar and messages sit at y = 112-178 px (at 1080p), which overlaps HotReload's toast (dev only) when both
  show at once.
