# Police

BepInEx 6 IL2CPP plugin for **Driving Rogue**: police patrols that engage **only when you pass them recklessly**: much
faster than they're going, cutting close, or crashing, near-missing or drifting as you go by. Pass one recklessly while
you're already being chased and it joins in as backup. Get away and you've escaped. Get busted and you lose a few
seconds off the race timer.

Current version: **0.5.0** (preview)

Design doc (claude.ai): "Police Pursuit - v1 Concept" (revised). The police only react to what you do. Nothing
escalates at random, there are no heat levels, and nothing carries over from one race to the next. **Single-player
only:** in multiplayer, or if the game mode can't be read, the plugin does nothing.

## What it does

1. **Patrols that look like police.** About once every `PatrolSpacing` metres you drive (800 by default; the first
   patrol of each race comes early), a traffic car 300-700 m ahead of you becomes a patrol, up to `MaxPatrols` at a time
   (3). Cars in your lane within 100 m are never picked, and neither are crashed ones; longer cars are preferred.
   - **Police cars:** each patrol is drawn as one of the plugin's own police cars, modelled in Blender for this
     plugin (`Assets/models/police_models.py`, no game or third-party meshes): **Interceptor** (full-size sedan),
     **Pursuit** (long-bonnet coupe) and **Utility** (SUV), cycled. About 3.7-4k triangles each, with shared meshes and
     materials (a patrol costs two or three renderers), push bar, wrap-around lights, mirrors, spotlight and POLICE door
     decals. `CarModels = Boss` draws the game's boss cars (each boss's own car and body kit) instead, in a police
     `Livery`; `Traffic` keeps the traffic car's look. Only the look changes: **the traffic car's own model is never
     drawn** while it is a patrol (its renderers are switched off and re-checked, so nothing of it shows through), and
     its hit box, AI and physics stay exactly as they were. The look is scaled to the traffic car's length, its wheels
     turn with its speed.
   - **Lightbar:** a black bar with a red and a blue lens on the roof, dim while patrolling; while chasing it flashes
     red and blue at 2 Hz with a soft glow halo (visible from far away) and two real lights.
   - **3D marker:** a faceted gem floats above patrols further than ~35 m away and grows with distance, so you can spot
     them early: **blue** = patrol, **amber** = you're inside its notice zone, **red** = chasing you.

   A patrol is let go when it's more than 150 m behind you (unless it's chasing), when the game despawns or reuses the
   car, or when the race ends.
2. **Noticing: only a reckless pass.** Nothing happens while you drive near a patrol or behind it. At the moment you go
   past it, the pass is judged; it is **reckless** if:
   - you pass it more than `OverspeedKmh` (35 km/h) faster than it's going,
   - you cut by closer than `CloseLaneMetres` (3.5 m) across at 15+ km/h faster (lane splitting past it),
   - you crash, have a near miss (`Notice.NearMiss`) or drift (`Notice.Drift`) within `PassWindow` (1.5 s) of the pass.

   A clean pass at a sensible speed difference, in another lane, is ignored. After a chase, no patrol can notice you
   for `Cooldown` seconds (8). Inside `NoticeRange` (90 m) a patrol's marker turns amber: a pass there is being
   watched. In `Mode = Chill`, patrols drive around with their lightbars but never notice you.
3. **Chase.** The patrol that noticed you chases you, driven by the game's own traffic AI (it keeps to lanes and uses
   its normal avoidance), and **launches** to 85% of your speed instead of crawling up from traffic pace. **During a
   chase, a patrol you pass recklessly joins in as backup**, up to `MaxChasers` (3); each one costs `BackupPenalty` (5%)
   of your lead. Chasers **inherit your car's stats**:
   - top speed = `SpeedFactor` × your car's *current* top speed, upgrades and boosts included (1.0 = exactly yours),
   - quicker acceleration (`Acceleration`: the AI's speed smoothing time, 0.8 s, never slower than its own),
   - no rubber banding, and the game no longer despawns a chaser until it is 260 m behind you.

   All of these are put back exactly when the chase ends, when the car is despawned and when the plugin switches off.

   The **pursuit panel** (top-centre, under TrafficDensity's message) shows the units chasing you, the time left and a
   **BUSTED ↔ EVADE** meter that starts in the middle:

   | Towards EVADE | Towards BUSTED |
   |---|---|
   | the nearest chaser behind you more than 25 m back (up to +3%/s at 85 m, +1%/s more above 80% of your top speed) | a chaser within 25 m behind you or level with you: −0.75 to −1.5%/s while you keep above 60% of your top speed, up to −9.5%/s when you're slowed or stopped next to it (×1.25 per extra unit on you) |
   | each near miss (+4%) | each collision (−12%), each backup unit joining (−5%) |
   | | stuck behind a patrol that's still ahead of you, below 60% of your top speed (−2%/s) |

   A patrol still **ahead** of you (you haven't passed it yet) is neutral, and nobody is busted in the first 3 s of a
   chase. Being busted means being caught slow or boxed in, not being overtaken.

   - **ESCAPED:** the meter reaches EVADE, every chaser falls more than 200 m behind (single units that drop off are
     "UNIT LOST"), or the game despawns the last one.
   - **BUSTED:** the meter reaches BUSTED. `CaughtPenaltySeconds` (5) come off the race timer.
   - **Time up:** after `Duration` seconds (45) the meter decides: past the middle means you escaped.

   Banners pop in under the panel: POLICE PURSUIT, BACKUP JOINED, UNIT LOST, ESCAPED, BUSTED.

**F3** turns patrols off and on for the current session (a banner confirms it). The `.cfg` file isn't changed.

### Daredevils (rivals)

The game's red **daredevil** cars (the ones that come up behind you fast; the devil icon shows while one is within
100 m behind you) become rivals. Daredevils are independent of patrols: `Mode` and F3 don't affect them, and a
daredevil is never picked as a patrol. Design doc: "Daredevil Rival AI" (claude.ai artifact `YW9wyNs6gWWz1w9kte5GjT`).

- **Pace from your car, scaled by skill (0.5.0):** rivals drive your car's numbers so the fight is fair, and each one's
  skill makes it quicker or slower.
  - **Top speed:** your current top speed (smoothed over 3 s, so a boost isn't copied the instant you fire it) × skill.
  - **Cornering:** your measured cornering × skill² (× `DriftCornerFactor` for drift cars). It's learned live as the
    90th percentile of v² × curvature at your spot on the racing line, starting at 16 m/s².
  - **Braking and acceleration:** 10 and 5 m/s² × skill.
  - **Skill per boss:** each boss has a fixed skill, spread evenly from `SkillMin` (0.90) to `SkillMax` (1.10) in the
    order the game lists its bosses. Half are quicker than you, half slower, and the same boss is always the same rival.
- **Racecraft:**
  - **Slipstream:** lined up within 1.5 m, within 30 m behind a car: +6% top speed.
  - **Overtakes:** a pass in the run-up to a corner takes its inside.
  - **Defending:** with you 25-60 m behind and closing, and a corner within 150 m, it makes one move to cover the
    inside and holds it through the corner. If you close right up to it (alongside or about to be), the never-hit rule
    wins and it gives way on its side.
  - **Push and conserve:** more than 120 m behind you it pushes 3% harder; more than 250 m ahead it eases 2%.
  - **Precision:** weaker rivals wander off the line by up to 0.9 m at skill 0.90; from 1.05 they sit on it.
- **Staying in the race:** while a daredevil is a rival, the game's despawn distances are raised to at least 350 m
  behind and 1,400 m ahead of you (the game's own values, logged once when the first rival is picked up, are about
  130 m and 830 m), and they're given back when it's let go. While it races the line, the game's own obstruction
  braking (a gentle, long-range slow-down behind any car in its path) is held off. The rival's braking envelope (below)
  replaces it: it brakes later and harder, the way a racing driver does. When the traffic snapshot is full (very heavy
  traffic, so some cars could be missing from it), the game's braking stays on as a backup.

- **Boss cars:** each one is drawn as one of the game's boss cars, with that boss's own paint and body kit, cycled so
  two on the road at once wear different cars. As with patrols, the traffic car underneath is never drawn and its hit
  box, AI and physics stay the game's. The devil icon and honk still work: the game ties them to the car, not its look.
- **Racing the line:** with the RacingLine plugin installed, they drive its optimal racing line (RacingLine publishes
  it; without it, or while it is still being built, they keep the game's driving):
  - sideways: the line's offset at their spot on the road, moving at most 4.5 m/s sideways (5.5 m/s for drift cars);
  - round traffic and you: a car ahead that sits on the line is passed on the inside of the coming corner (on a
    straight, on the side nearer to them), blended in over a lead-in that grows with the closing speed; with no way
    past they queue behind it;
  - **they never try to hit you** (skilled drivers who don't want damage):
    - **A wider berth.** You get 1.6 m more than your car's width plus theirs. They read where you're heading from
      your sideways movement, 0.6 s ahead.
    - **Never across you.** While you're alongside or close, the side of you they're on is a hard limit: a racing
      line or a pass round traffic never takes them across you. If you move over into them, they get out of the way
      on their side, faster.
    - **No cutting in.** They only swing back in front of you once they're 12 m + 1.5 s × your extra speed ahead.
    - **Braking.** Whatever is in line with them ahead (you or traffic) caps their speed so they can always brake to
      its real speed along the road (yours counts your slides and drifts) before a safe gap: 8 m + 0.35 s behind you,
      5 m + 0.2 s behind traffic (at their speed). The cap allows for the game's speed smoothing on the closing speed,
      and inside that gap their speed is cut at once;
  - speed: the fastest its skill allows on the line, from its own speed profile (corner speed √(cornering × radius),
    braking before corners and accelerating out of them as above). The game's speed is no longer also cut on curves:
    the line's corner speeds do that.
- **Driving style by car:** grip cars (everything not in `DriftCars`) hold the line without sliding and point where
  they go. Drift cars (`DriftCars`, by default Rotary, Delivery, Centipede and Centaur) corner at `DriftCornerFactor`
  (93%) of a grip car's grip, a little slower, but slide: they are turned into the corner by up to `MaxSlipAngle` (30°)
  as the cornering load rises, and counter-steer. Front wheels steer with the corner. The slide is visual only, and the
  physics car is never turned.
- Up to 6 at once. A daredevil is let go when it despawns, the pool reuses it, it falls more than 340 m behind you, or
  the race ends. More than 1 km ahead of you it is left to the game's driving until it is back within 1 km. After a crash the game's physics has it, and it keeps its look. When one is let go it gets back its
  top speed, speed smoothing, despawn distances, its home lane (the game's own lane change takes it back into a lane)
  and its own model.
  `BossLooks`, `DriftCars` and `SpeedFactor` apply to daredevils picked up after a change.

### The caught penalty

The game's `TimerManager.AddTimerSeconds(-5)` can't take time away: in single-player it passes the negative value to
`RaceTimer.RemoveCountdownTime`, which clamps it to 0, so nothing happens. Police instead calls
`RaceTimer.RemoveCountdownTime(5)`, the game's own way of taking time off, directly. It only does this while a
countdown timer is running, and it always leaves at least 3 s on the clock: a police catch never ends your race by
itself. If no time can be taken, the banner just says `BUSTED` and the log says why.

## Settings (`BepInEx/config/rogue.police.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch |
| `General.Mode` | Normal | `Normal` = patrols notice and chase. `Chill` = patrols, but they never notice you. `Off` = no patrols |
| `Patrols.MaxPatrols` | 3 | most patrols at once (0-4) |
| `Patrols.PatrolSpacing` | 800 | on average one new patrol per this many metres driven (300-10000) |
| `Look.CarModels` | Police | `Police` (the plugin's own police cars), `Boss` (boss cars in a police livery), `Traffic` (the traffic car's own look with a lightbar) |
| `Look.Livery` | Classic | `Classic` (black / white), `Interceptor` (all black; boss cars with blue trim), `Boss` (the boss's own paint; police models: Classic) |
| `Look.Markers` | true | 3D marker above patrols further than ~35 m |
| `Notice.NoticeRange` | 90 | marker turns amber within this many metres (a pass there is watched) (10-200) |
| `Notice.OverspeedKmh` | 35 | a pass this much faster than the patrol is reckless (10-300) |
| `Notice.CloseLaneMetres` | 3.5 | a pass closer than this across, at 15+ km/h faster, is reckless (0 = off) |
| `Notice.PassWindow` | 1.5 | seconds around a pass in which a crash / near miss / drift makes it reckless (0.2-5) |
| `Notice.NearMiss` | true | a near miss during a pass makes it reckless |
| `Notice.Drift` | true | drifting past a patrol makes the pass reckless |
| `Chase.MaxChasers` | 3 | most police cars chasing at once (1-4) |
| `Chase.BackupPenalty` | 5 | lead (%) lost per backup unit joining (0-50) |
| `Chase.Duration` | 45 | longest chase in seconds; then the meter decides (10-300) |
| `Chase.CaughtPenaltySeconds` | 5 | seconds off the race timer when busted (0-30, 0 = none; always leaves 3 s) |
| `Chase.Cooldown` | 8 | seconds after a chase before any patrol can notice you again (0-300) |
| `Chase.SpeedFactor` | 1.0 | chasers' top speed as a fraction of your car's current top speed (0.5-1.1) |
| `Chase.Acceleration` | 0.8 | chasers' speed smoothing time in seconds (0.2-5, lower = quicker) |
| `Daredevils.Enabled` | true | daredevils become boss-car rivals racing the line (independent of patrols; single-player only) |
| `Daredevils.BossLooks` | true | draw daredevils as boss cars (off = the game's red traffic car) |
| `Daredevils.RaceLine` | true | drive the racing line (needs the RacingLine plugin; else the game's driving) |
| `Daredevils.DriftCars` | Rotary, Delivery, Centipede, Centaur | boss cars that drift through corners; every other car holds the line with grip |
| `Daredevils.SkillMin` | 0.90 | the slowest boss's skill (× your pace) (0.5-1.5) |
| `Daredevils.SkillMax` | 1.10 | the fastest boss's skill (0.5-1.5) |
| `Daredevils.DriftCornerFactor` | 0.93 | drift cars' cornering against grip cars' (0.6-1.2) |
| `Daredevils.SpeedFactor` | 1.0 | a multiplier on every rival's top speed on top of its skill (0.5-1.5) |
| `Daredevils.Defend` | true | rivals cover the inside before corners when you close in (never across you) |
| `Daredevils.Slipstream` | true | rivals tow behind other cars: +6% top speed |
| `Daredevils.MaxSlipAngle` | 30 | how far drift cars slide in a hard corner, degrees (0-50) |
| `Debug.LogEvents` | true | log patrols, notices, chase results and daredevils |
| `Debug.ConfigVersion` | (written) | settings migration marker, don't edit |

**Upgrading:** settings you never changed move to the new defaults once (from 0.0.x: `NoticeRange` 60 → 90,
`OverspeedKmh` 80 → 35, `Duration` 40 → 45, `Cooldown` 20 → 8, `SpeedFactor` 0.97 → 1.0, `MaxPatrols` 2 → 3; from
0.1.0: `BackupPenalty` 10 → 5); values you changed are kept. The log lists what moved. **Removed in 0.2.0:**
`Notice.SpeedingKmh` and `Notice.SpeedingSeconds` (police no longer react to speeding near them, only to a reckless
pass); they may stay in an old config file harmlessly. **0.3.0:** `Look.BossCars` became `Look.CarModels` (an old
`BossCars = false` becomes `Traffic`; otherwise you get the new police cars) and the old entry is removed. **0.5.0:**
`Daredevils.GripCornering` and `DriftCornering` are removed (rival cornering now follows your car × skill).

Edits to the `.cfg` file apply at the next game start. The plugin reads every value live, so an in-game config
manager can change them while you play.

## This is a gameplay change

- It changes how up to `MaxChasers` traffic cars drive during a chase: they follow you at your car's top speed. The
  game's own traffic code is used as-is. Police never touches traffic cars' colliders or collision values (CurbFeel and
  TrafficDensity tune those) and, apart from daredevils that race the line (below), their obstruction braking.
- Daredevils (up to 6) are driven by Police while they race the line.
  - **Written each frame:** their lateral offset and lane-change fields (`CurrentLaneOffset`, `previousLaneOffset`,
    `targetLaneOffset`, `initialLane`, `MyAngle`, `LaneTransitionSpeedMultiplier`), their curve slow-down
    (`curvatureFactor`), the obstruction braking's output (`pedalFactor`, `targetPedalFactor`, held at 1 unless the
    traffic snapshot is full), `MaxSpeed` and `speedSmoothness`.
  - **Speed cut:** their running `Speed` is cut at once when they get too close to a car ahead.
  - **Despawn distances:** `behindDistanceDespawn` and `aheadDistanceDespawn` are raised for as long as a daredevil
    is a rival.
  - **When one is let go:** it gets back its top speed, speed smoothing, rubber-banding flag, despawn distances and
    home lane, and the game's own lane change takes it from where it is into its lane. A car the pool has already
    reused only gets its speed smoothing and despawn distances back.
  - A crashed one is left to the game's physics. Nothing is written while the game is paused. Daredevils more than
    1 km ahead of you are left to the game's driving.
- Getting busted can take seconds off your race timer.
- Nothing Police draws has a collider: the police car look is plain meshes, the lightbar's colliders are removed the
  moment it is created, the marker never had one. The boss prefab is only read, never spawned, so none of the game's
  boss scripts or effects run. Nothing is attached to the car itself: the look, lightbar and marker follow the car
  every frame, so a car the game recycles never carries them along. The traffic car's own model is only switched off
  (its renderers) and switched back on when the patrol is let go. Everything Police creates is destroyed when the
  patrol is let go, when the scene changes, or when the plugin switches off.
- Single-player only. Nothing is ever sent to other players; Police reads `GameState.IsMultiplayerMode` and stays off
  in multiplayer.

## What to check in the log (`/game-log Police`)

- `Police x.y.z loaded` (the version you installed) and `[Police] game check OK: ..., boss car models`. A
  `game check: missing ...` line lists what a game update removed and which features are off (without the boss data,
  patrols keep their traffic look with a lightbar).
- `[Police] daredevils: active (racing the line)` (or `active (no racing line: game driving)` without RacingLine),
  `daredevils: racing line vN (x km, every n m, ±n m)`, `daredevils: 10 boss cars: Rotary (Skull, skill 0.93, drift),
  ...` (once), `daredevils: race over, your cornering N m/s^2, your top speed N km/h` per race. With `LogEvents`:
  `daredevil <car> (<boss>): +310 m from you, skill 1.04, top 179 km/h, cornering 21 m/s^2, grip style (N driving)`
  and `daredevil <car> let go: <why> (...); skill 1.04, 48 s within 100 m of you, passed you 2x, you passed it 1x,
  closest 3.1 m, its average 152 km/h vs your 149`.
- `[Police] 3 police car models loaded (Interceptor, Pursuit, Utility) from ...plugins\Police` (once) and `police car
  built: <model> (4 wheels, size, livery)`. With `CarModels = Boss`: `N boss cars for police looks: <boss>: <car> ...`
  and `police car built from <car> (<boss>): N parts, 4 wheels, size`. `no police car models folder` means the
  `plugins\Police\*.pcm` files weren't installed (boss cars are used instead).
- `[Police] lightbar materials: shader '...', halos on/off` (once).
- `[Police] active`, `idle: ...` (why it's off: config, F3, multiplayer) and `waiting for a race`.
- With `LogEvents`:
  - `patrol picked: N m ahead ..., <car> (<boss>)` and `patrol released: <why>`
  - `noticed: <passed it N km/h faster / cut past it N m away / crashed, near miss or drifted while passing it> ...`,
    with your top speed now and the chaser's top speed and acceleration before → after
  - `backup joined: N units, lead N%`
  - `chase over: ESCAPED / CAUGHT (<why>) after N s, lead N%, N unit(s), -5 s`
- `... switched off for this session after an error` means one feature stopped (its fallback stays: traffic look,
  flat markers, simple HUD). `switched off for this session after repeated errors` means the whole plugin stopped.
  Either way the cars are restored and Police's own objects removed.

## Known limits

- Chasers are traffic cars. They stay in lanes, brake for traffic ahead of them (the game's obstruction logic) and slow
  down in curves like all traffic. A wall of cars can stop them.
- A patrol you crash into is wrecked: the game stops crashed traffic for good, so it can't chase you.
- The boss-car look is scaled to the traffic car's hit box (0.7-1.15×), so a few look a little smaller than the
  boss's own car; the hit box is still the traffic car's.
- Daredevils are still traffic cars underneath: they glide along the road (no suspension, no real tyre slip), and
  their slide is a turn of the look. They don't follow the traffic-aware line RacingLine shows you, and instead pass
  traffic by themselves.
- No siren sound yet. No score category yet (PURSUIT is planned).
- The pursuit panel and banners sit at y = 112-262 px (at 1080p), which overlaps HotReload's toast (dev only) when
  both show at once.
