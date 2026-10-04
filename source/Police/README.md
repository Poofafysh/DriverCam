# Police

BepInEx 6 IL2CPP plugin for **Driving Rogue**: police patrols that engage **only when you pass them recklessly**: much
faster than they're going, cutting close, or crashing, near-missing or drifting as you go by. Pass one recklessly while
you're already being chased and it joins in as backup. Get away and you've escaped. Get busted and you lose a few
seconds off the race timer. Chases score in their own category, **PURSUIT**.

Current version: **0.7.0** (preview)

Design doc (claude.ai): "Police Pursuit - v1 Concept" (revised). The police only react to what you do. Nothing
escalates at random, there are no heat levels, and nothing carries over from one race to the next. **Single-player
only:** in multiplayer, or if the game mode can't be read, the plugin does nothing.

## What it does

1. **Patrols that look like police.** About once every `PatrolSpacing` metres you drive (800 by default; the first
   patrol of each race comes early), a traffic car 300-700 m ahead of you becomes a patrol, up to `MaxPatrols` at a time
   (3). Cars in your lane within 100 m are never picked, and neither are crashed ones; longer cars are preferred.
   - **Police cars:** each patrol is drawn as one of the plugin's own police cars, modelled in Blender for this
     plugin (`Assets/models/police_models.py`, no game or third-party meshes): **Interceptor** (full-size sedan),
     **Pursuit** (long-bonnet coupe) and **Utility** (SUV), cycled. About 2.7k triangles each, with shared meshes and
     one shared palette material (a patrol costs 6 draws, see [Performance](#performance)), push bar, wrap-around
     lights, mirrors, spotlight and POLICE door decals. `CarModels = Boss` draws the game's boss cars (each boss's own car and body kit) instead, in a police
     `Livery`; `Traffic` keeps the traffic car's look. Only the look changes: **the traffic car's own model is never
     drawn** while it is a patrol (its renderers are switched off and re-checked, so nothing of it shows through), and
     its hit box, AI and physics stay exactly as they were. The look is scaled to the traffic car's length, its wheels
     turn with its speed.
   - **Lightbar:** a black bar with a red and a blue lens on the roof, dim while patrolling; while chasing it flashes
     red and blue at 2 Hz with a soft glow halo (visible from far away) and a real light on the lit side. Only the 2
     chasers nearest the camera, within 70 m (off again past 80 m), light the scene; further out the halo carries it.
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
3. **Chase.** The patrol that noticed you chases you and **launches** to 85% of your speed instead of crawling up from
   traffic pace. **Chase driving (0.7.0, `Chase.Drive`):** each chaser is driven like a daredevil, by the same gap
   planner. It heads for your lane, passes traffic through the gaps (across lanes too), takes corners at full speed
   (the game's curve slow-down is held off) and only brakes for a car it can't get round. It **never steers into
   you**: the daredevils' never-hit rules apply. When the chase ends it gets its home lane back. With `Drive = false`
   (or if the lane fields are missing from the game) chasers keep the game's traffic driving and queue behind traffic
   in their lane. A patrol you crash into is wrecked and can't chase; a unit that crashes during a chase leaves it at
   once (`UNIT WRECKED`). **During a
   chase, a patrol you pass recklessly joins in as backup**, up to `MaxChasers` (3); each one costs `BackupPenalty` (5%)
   of your lead. Chasers **inherit your car's stats**:
   - top speed = `SpeedFactor` × your car's *current* top speed, upgrades and boosts included (1.0 = exactly yours),
   - quicker acceleration (`Acceleration`: the AI's speed smoothing time, 0.8 s, never slower than its own),
   - no rubber banding, and the game no longer despawns a chaser until it is 260 m behind you,
   - **catch-up**: a chaser more than 60 m behind you gets up to `CatchUp` (15%) more top speed, in full at 200 m, and
     is back to normal within 60 m. The game's traffic AI loses ground fast, so without it distance decided almost
     every chase.

   All of these are put back exactly when the chase ends, when the car is despawned and when the plugin switches off.

   The **pursuit panel** (top-centre, under TrafficDensity's message) shows the units chasing you, the time left and a
   **BUSTED ↔ EVADE** meter that starts in the middle:

   | Towards EVADE | Towards BUSTED |
   |---|---|
   | the nearest chaser behind you more than 25 m back (up to +3%/s at 85 m, +1%/s more above 80% of your top speed) | a chaser within 25 m behind you or level with you: nothing at 80%+ of your top speed, up to −0.75 to −1.5%/s at 60%, up to −9.5%/s when you're slowed or stopped next to it (×1.25 per extra unit on you) |
   | each near miss (+4%) | each collision (−12%) **with a unit near enough to see it** (80 m behind to 40 m ahead of you), each backup unit joining (−5%) |
   | | stuck behind a patrol that's still ahead of you, below 60% of your top speed (−2%/s) |

   A patrol still **ahead** of you (you haven't passed it yet) is neutral, and nobody is busted in the first 3 s of a
   chase. Being busted means being caught slow or boxed in, not being overtaken: **the bar only empties with a unit on
   you** (without one it stops at 1%), and a wrecked unit never counts.

   - **ESCAPED:** the meter reaches EVADE, or every chaser stays more than 200 m behind **for 3 s with the meter at or
     past the middle** (not in the first 12 s of a chase), or the last unit is wrecked (`they crashed`; in the first
     3 s the chase is just dropped, no points). Below the middle a distance doesn't end the chase: the chasers keep
     catching up. A unit the game despawns, or one more than 260 m behind, is "UNIT LOST" (with the meter at or past
     the middle, also one more than 200 m behind while others remain); when the last one is lost that way you escaped
     too (log: `lost them: ...` when the meter was below the middle).
   - **BUSTED:** the meter reaches BUSTED. `CaughtPenaltySeconds` (5) come off the race timer.
   - **Time up:** after `Duration` seconds (45) the meter decides: at or past the middle means you escaped.
   - **The race ends mid-chase:** at or past the middle counts as escaped at the finish (the time-up bonus), below it
     the live points are banked.

   Banners pop in under the panel: POLICE PURSUIT, BACKUP JOINED, UNIT LOST, ESCAPED (with the chase's PURSUIT points,
   `ESCAPED +1,240`), BUSTED.
4. **PURSUIT score category.** In single-player, chases score in a real score category of their own, made like
   RacingLine's Racing Line category: an inert copy of the game's Top Speed category with the id `rogue.police`, the
   name PURSUIT and a siren icon drawn in code, appended once to the level's score list (an existing copy is reused,
   old ones are never destroyed). It counts toward TOTAL, the grade, XP and coins like the game's own categories, and
   its points go into the game's combo.

   | When | PURSUIT points (× `PointsScale`, before the game's card and combo multipliers) |
   |---|---|
   | during the chase (counted up live on the HUD) | 8 per 1% the lead bar rises, plus 10 per second at 80%+ of your top speed |
   | **ESCAPED** | the live points plus an escape bonus: 250 + 25 per second chased (up to `Duration`) + 150 per extra unit + up to 300 for a close call (lowest lead 50% → 0, 0% → 300); one coin unit |
   | escaped at **time up** or **at the finish** (meter at or past the middle) | the live points + 100; one coin unit |
   | **BUSTED** | that chase's live points are lost (and the 5 s penalty) |
   | the race ends mid-chase below the middle | the live points are banked |
   | anything else ends it (Police, `Pursuit.Enabled` or F3 off, new race, quit, an error) | the live points are dropped |

   A crash that fails the game's combo also clears the chase's live points so far (as for any category); the next
   points start a new live action. Coins: `CoinReward` (100) for 2 escapes in a race. Example: a 20 s escape from one
   unit, at speed all the way, with the lead dipping to 40% and then climbing to 100%: 480 (60% gained) + 200 (20 s
   at speed) live, + 810 escape bonus (250 + 500 + 0 + 60) = 1,490.
   - **Results screen** (after each race): a PURSUIT row (a copy of the Top Speed row, after Near Miss and after
     RacingLine's RACING LINE row) with escapes / chases, the race's PURSUIT points and coins.
   - **Victory screen** (end of the run): a PURSUIT row (a copy of the Near Miss row, after RACING LINE) with the run's
     PURSUIT total, and NEW RECORD when it beats `Records.BestRunTotal`. Each race is counted once; the total resets at
     the first race of a new run (the run position is read again until a read succeeds for that race, so a missed read
     can't carry the last run's total into a new one) and lives in this game session only.
   - The rows follow whether PURSUIT is in the level, not the settings: points banked before you switch Pursuit (or
     the plugin) off mid-race still get their row and count toward the run total.
   - The rows are display only (TOTAL already has the points) and are only removed once their screen has closed
     (unloading the plugin removes them at once). A Victory screen that is already open when the plugin starts
     watching it gets no row (log: `victory screen already open when first watched`), so it never counts a race early. The
     row code is shared with RacingLine (`source/Shared/ScoreRows.cs`).

**F3** turns patrols off and on for the current session (a banner confirms it; ignored while the game is paused). The `.cfg` file isn't changed.

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
  - **Pacing (kept in the fight, 0.7.0):** ahead of you its pace is also capped by **your real road speed**, so you
    catch it up even when traffic holds you below your top speed: more than 250 m ahead at most 85% pace and 90% of
    your speed, 100-250 m ahead at most 98% and 95% of your speed. From 120 m behind to 100 m ahead it drives its
    honest pace. From 120 m to 150 m behind it pushes 3% harder, and from 150 m to 300 m behind from 3% up to 10%.
    The pace eases between these (about 0.5/s).
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
  - round traffic and you: a gap planner scores every position across the road (every 0.5 m) by the speed it can
    carry there, given each car whose band covers it, and picks the fastest gap. Ties go to the one nearer the
    racing line, then to the one needing the least move, with a little hysteresis so it doesn't flip between equal
    gaps. A gap that means cutting across a car before getting past it is ruled out. They only brake for a car they
    won't be clear of in time: a car they're already swerving round no longer slows them. With no gap, they queue
    behind the traffic;
  - **they never try to hit you** (skilled drivers who don't want damage):
    - **A wider berth.** You get 1.6 m more than your car's width plus theirs. They read where you're heading from
      your sideways movement, 0.6 s ahead.
    - **Never across you.** While you're alongside or close, the side of you they're on is a hard limit: a racing
      line or a pass round traffic never takes them across you. If you move over into them, they get out of the way
      on their side, faster.
    - **No cutting in.** They only swing back in front of you once they're 12 m + 1.5 s × your extra speed ahead.
    - **Room to traffic (0.7.0).** The side margin to a traffic car grows with the closing speed (0.9 m, +0.02 m per
      m/s faster, at most +1 m), and a car coming up from behind within 1.5 s (40 m) is never moved in front of.
    - **Braking.** Whatever is in line with them ahead (you or traffic) caps their speed so they can always brake to
      its real speed along the road (yours counts your slides and drifts) before a safe gap: 8 m + 0.35 s behind you,
      5 m + 0.2 s behind traffic (at their speed). The cap allows for the game's speed smoothing on the closing speed,
      and inside that gap their speed is cut at once. Behind you this always applies. A traffic car they will swerve
      clear of before reaching its safe gap doesn't slow them, and they never move into a car alongside;
  - speed: the fastest its skill allows on the line, from its own speed profile (corner speed √(cornering × radius),
    braking before corners and accelerating out of them as above). The game's speed is no longer also cut on curves:
    the line's corner speeds do that.
- **Driving style by car:** grip cars (everything not in `DriftCars`) hold the line without sliding and point where
  they go. Drift cars (`DriftCars`, by default Rotary, Delivery, Centipede and Centaur) corner at `DriftCornerFactor`
  (93%) of a grip car's grip, a little slower, but slide: they are turned into the corner by up to `MaxSlipAngle` (30°)
  as the cornering load rises, and counter-steer. Front wheels steer with the corner. The slide is visual only, and the
  physics car is never turned.
- Up to 6 at once. One more than 300 m ahead of you is only picked up while no rival is within 300 m of you and fewer
  than 2 race (0.7.0: most used to be picked up far ahead and never raced you). A car Police takes over starts from
  the speed it really drives (the game's slow-down factors are held at 1 while it races; its running speed used to
  jump). A daredevil is let go when it despawns, the pool reuses it, it falls more than 340 m behind you, or
  the race ends. More than 1 km ahead of you it is left to the game's driving until it is back within 1 km. After a crash the game's physics has it, and it keeps its look. When one is let go it gets back its
  top speed, speed smoothing, despawn distances, its home lane (the game's own lane change takes it back into a lane)
  and its own model.
- **Crashes:** each crash is logged once, with what the rival saw at the last traffic snapshot before it (its place
  against you, speed, lane offset, whether it was racing the line, and the nearest car ahead near its path). A crashed
  rival the game clears away (TrafficDensity's collision despawn removes wrecks) is let go as "crashed, wreck cleared",
  not "gone (despawned)". Each let-go line carries the race's crash count, and a summary is logged when the race ends
  with rivals still driving (when the last one is let go at race over).
- **Traffic in the snapshot:** wrecks stay in the rivals' traffic snapshot as stopped obstacles. Physics moves them, so
  the game's road position for them may not be where they are: each wreck's band is widened by 2 m on both sides and
  its length by 2 m at each end. Each traffic car's sideways speed is tracked between snapshots: a car changing lanes
  is avoided over its lane now and where it is heading in 0.6 s, like you.
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
| `Chase.CatchUp` | 0.15 | extra top speed for a chaser more than 60 m behind you, in full at 200 m (0-0.5, 0 = off) |
| `Chase.Drive` | true | chasers drive like the daredevils: pass traffic round the gaps, full speed in corners, never into you (off = the game's traffic driving) |
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
| `Pursuit.Enabled` | true | PURSUIT score category (single-player); off = chases score nothing (at once: a running chase's live points are dropped) |
| `Pursuit.PointsScale` | 1.0 | multiplies every PURSUIT point, live and escape bonus (0-3) |
| `Pursuit.CoinReward` | 100 | coins at full target (2 escapes in a race) (0-1000) |
| `Records.BestRunTotal` | (written) | best PURSUIT run total so far, for the Victory screen's NEW RECORD |
| `Debug.LogEvents` | true | log patrols, notices, chase results and daredevils |
| `Debug.ConfigVersion` | (written) | settings migration marker, don't edit |

**Upgrading:** settings you never changed move to the new defaults once (from 0.0.x: `NoticeRange` 60 → 90,
`OverspeedKmh` 80 → 35, `Duration` 40 → 45, `Cooldown` 20 → 8, `SpeedFactor` 0.97 → 1.0, `MaxPatrols` 2 → 3; from
0.1.0: `BackupPenalty` 10 → 5); values you changed are kept. The log lists what moved. **Removed in 0.2.0:**
`Notice.SpeedingKmh` and `Notice.SpeedingSeconds` (police no longer react to speeding near them, only to a reckless
pass); they may stay in an old config file harmlessly. **0.3.0:** `Look.BossCars` became `Look.CarModels` (an old
`BossCars = false` becomes `Traffic`; otherwise you get the new police cars) and the old entry is removed. **0.5.0:**
`Daredevils.GripCornering` and `DriftCornering` are removed (rival cornering now follows your car × skill).
**0.6.0:** new `[Pursuit]` and `[Records]` sections and `Chase.CatchUp` (no migration needed). **0.7.0:** new
`Chase.Drive` (no migration needed).

Edits to the `.cfg` file apply at the next game start. The plugin reads every value live, so an in-game config
manager can change them while you play.

## This is a gameplay change

- It changes how up to `MaxChasers` traffic cars drive during a chase: they follow you at your car's top speed and,
  with `Chase.Drive`, are steered like daredevils (the same fields are written each frame: lane offset and lane-change
  fields, `curvatureFactor`, `pedalFactor` unless the snapshot is full, `MaxSpeed`; their running `Speed` is cut when
  too close to a car). Given back with their home lane when the chase ends. Police never touches traffic cars' colliders or collision values (CurbFeel and
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
- **PURSUIT points count.** They are part of TOTAL and of the single-player run total the game uploads to its Steam
  leaderboard (set `Pursuit.Enabled = false` to keep chases out of the score). The category is a copy of Top Speed, so
  Top Speed cards and multipliers also affect it, and a chase's live action keeps the game's combo going while it
  runs. Saves stay compatible both ways: the id is unique, and the game skips ids it doesn't know.
- Nothing Police draws has a collider: the police car look is plain meshes, the lightbar's colliders are removed the
  moment it is created, the marker never had one. The boss prefab is only read, never spawned, so none of the game's
  boss scripts or effects run. Nothing is attached to the car itself: the look, lightbar and marker follow the car
  every frame, so a car the game recycles never carries them along. The traffic car's own model is only switched off
  (its renderers) and switched back on when the patrol is let go. Everything Police creates is destroyed when the
  patrol is let go, when the scene changes, or when the plugin switches off.
- Single-player only. Nothing is ever sent to other players; Police reads `GameState.IsMultiplayerMode` and stays off
  in multiplayer.

## What to check in the log (`/game-log Police`)

- `Police x.y.z loaded` (the version you installed) and `[Police] game check OK: ..., boss car models, daredevils, pursuit score, results/victory rows`. A
  `game check: missing ...` line lists what a game update removed and which features are off (without the boss data,
  patrols keep their traffic look with a lightbar).
- `[Police] daredevils: active (racing the line)` (or `active (no racing line: game driving)` without RacingLine),
  `daredevils: racing line vN (x km, every n m, ±n m)`, `daredevils: 10 boss cars: Rotary (Skull, skill 0.93, drift),
  ...` (once), `daredevils: race over, your cornering N m/s^2, your top speed N km/h` per race. With `LogEvents`:
  `daredevil <car> (<boss>): +310 m from you, skill 1.04, top 179 km/h, cornering 21 m/s^2, grip style (N driving)`
  and `daredevil <car> let go: <why> (...); skill 1.04, 48 s within 100 m of you, passed you 2x, you passed it 1x,
  closest 3.1 m, its average 152 km/h vs your 149; crashes this race N` (why: `gone (despawned)`, `crashed, wreck
  cleared`, `crashed, wreck cleared (reused by the pool)`, `reused by the pool`, `left behind` / `crashed, left behind`, `race over`, ...). Also with `LogEvents`:
  `daredevil <car> crashed: +412 m from you, 140 km/h, offset 1.8 m, steering yes, nearest car 6.2 m ahead lane 1.5
  92 km/h same way; closest car any side 0.3 m (behind 7 m, side clearance 0.3 m, closing 25 km/h); picked up 4.2 s
  ago, steering 4.1 s, instant cut 30 km/h 0.4 s before, capped by a car; edge 3.1 m (line ±8.5), corner radius 180 m`
  (0.7.0 forensics after the `;`: the nearest car on any side at the last snapshot, timing, the planner's state, how
  far out the car's side is against the line's limit, and the corner) (once per crash; `oncoming` for a car on the other side, `nearest car 6.2 m ahead lane 1.5 a wreck` for a crashed
  car, `no car ahead near its path` when none, `no traffic snapshot` when it crashed before its first snapshot),
  `daredevil <car>: contact with you? (you collided while it was N m away)` (your collision count went up while a
  rival was within 1 m: the never-hit rule should keep this at zero), each let-go report has `contacts N`, and
  (always) at the end of every race with a rival `daredevils race summary: rivals N, crashed C, passed you P, you
  passed them Q, closest X m, contacts with you K`.
- `[Police] daredevils: the game despawns traffic N m behind / N m ahead of you; rivals use at least N / N m` (once: the game's own despawn distances, and the larger distances rivals are kept for), and `daredevils: racing line gone` when RacingLine's line is dropped (menu or loading).
- `[Police] settings updated: ...` (or `settings updated to the current defaults (values you had changed are kept): ...`) once after an update that moved or removed config values.
- `[Police] 3 police car models loaded (Interceptor 2650 tris / 2603 verts, Pursuit ..., Utility ...) in N ms from
  ...plugins\Police` (once) and `police car built: <model> (6 draws, 4 wheels, size, livery)`. With `CarModels = Boss`:
  `N boss cars for police looks: <boss>: <car> ...` and `police car built from <car> (<boss>): N parts (N draws, N
  vertices), 4 wheels, size` (also for each daredevil's boss car). `no police car models folder` means the
  `plugins\Police\*.pcm` files weren't installed (boss cars are used instead).
- `[Police] lightbar materials: shader '...', halos on/off` (once).
- `[Police] active`, `idle: ...` (why it's off: config, F3, multiplayer) and `waiting for a race`.
- With `LogEvents`:
  - `patrol picked: N m ahead ..., <car> (<boss>)` and `patrol released: <why>`
  - `noticed: <passed it N km/h faster / cut past it N m away / crashed, near miss or drifted while passing it> ...`,
    with your top speed now, the chaser's top speed and acceleration before → after and `driving: own (passes
    traffic)` or `driving: game traffic`
  - `patrol released: wrecked (+N m from you)` (a unit that crashed mid-chase; banner UNIT WRECKED)
  - `backup joined: N units, lead N%`
  - `chase over: ESCAPED / CAUGHT / cancelled (<why>) after N s, lead N%, N unit(s), -5 s, max gap N m, catch-up used N s;
    drain: unit on you N s (slow N s), collisions N (-N%), unseen N, backup -N%`
    (max gap = the farthest any unit fell behind you; catch-up used = seconds a unit drove with extra top speed; drain
    = what pulled the bar down: seconds a unit was on you, of them below 60% of your top speed, collisions a unit saw
    and their cost, collisions no unit saw (free), backup penalties).
    `<why>` is `lead bar full`, `lead bar empty`, `time up at N%`, `ahead at the finish at N%`, `left it N m behind`
    (only with lead 50%+, 3 s out of sight, after 12 s), `they crashed: the last unit was wrecked`, `the unit crashed at
    the start` (cancelled), or,
    when every unit was lost, `the last unit fell N m behind`, `the last unit was despawned (...)` or
    `every unit was despawned`, with `lost them: ` in front when the lead was below 50%.
    A cancelled chase gives the reason it was dropped: `race over` (its pending PURSUIT points are banked),
    `new race`, `off (F3)`, `no player car`, `no traffic spawner`, `patrols off`, `plugin switched off`, ...
    Expect most escapes by lead bar or time up and typical chases of 15-40 s.
- PURSUIT:
  - `[Police] Pursuit added as a score category (id rogue.police, Top Speed template, N categories in this level)`
    (or `... already in this level's score list; reusing it`)
  - `[Police] pursuit: +N pts (escaped after N s, N unit(s), lowest lead N%: N live + N escape bonus; before card
    multipliers)` on each chase end (`caught: N pending pts lost`, `race over: N pending pts banked`,
    `cancelled (<why>): ...`; `pursuit: cancelled (Pursuit switched off): N pending pts dropped` if you turn Pursuit off mid-chase)
  - `[Police] results row added: 2/3, 1840 pts, 100 coins` (escapes / chases)
  - `[Police] victory screen found: score controller on '...' (active ...), results window active ...` (once),
    `[Police] victory row added: 12,345 (new record)`, or `victory row skipped: <why>`
  - `[Police] new run: pursuit run total reset (previous run N)`, or
    `[Police] run position never read for the last race: pursuit run total kept` (the run position couldn't be read
    for a whole race, so the run total was carried over instead of reset)
- `... switched off for this session after an error` means one feature stopped (its fallback stays: traffic look,
  flat markers, simple HUD). `switched off for this session after repeated errors` means the whole plugin stopped.
  Either way the cars are restored and Police's own objects removed.

## Performance

0.6.0 perf pass (Police is still 0.6.0: this was done before its release). Nothing about what patrols, chases or
daredevils do changed: the daredevil code only had pure performance refactors with identical results. The visual
changes are listed below the table.

| Per patrol (own police models) | Before | After |
|---|---|---|
| Triangles (body + 4 wheels) | 3,710-4,030 (3,118-3,438 + 4 x 148) | 2,650-2,770 (2,138-2,258 + 4 x 128) |
| Vertices sent to the GPU | 11,130-12,090 (none shared) | 2,603-2,737 (welded) |
| Draw calls, camera pass | 22 car + 3 lightbar + 0-1 halo + 0-1 marker | 6 car + 3 lightbar + 0-1 halo + 0-1 marker |
| Draw calls, each shadow cascade | 22 | 1 (the body) |
| Materials / shader variants | 12 per model, 3 variants | 1 palette + 1 decal for all models, 2 variants (both compiled into the game's URP Lit) |
| `.pcm` files | 1.78 MB text, decal png loaded 3x | 0.56 MB text (indexed), decal png loaded once |
| Real lights while chasing | 1 per chaser at any distance (up to `MaxChasers`) | 1 per chaser for the 2 nearest within 70 m of the camera, else 0 |

| CPU | Before | After |
|---|---|---|
| Traffic snapshot (daredevils, every 0.1 s) | ~12 native calls + 3 wrappers per car in range | ~10 calls + 1 wrapper (path follower and box kept per car) |
| Daredevil search (every 0.25 s, whole race) | ~11 calls + 3 wrappers for every car | 6 calls + 1 wrapper for cars out of range (most), cached parts for the rest |
| Player read in `Daredevils.Drive` (every frame) | ~20 calls incl. the score counts | ~14 (no collision / near-miss counts: daredevils never used them) |
| Pursuit HUD (every frame) | 2 alphas, meter, needle and fill colour written; both siren images rebuilt every frame in a chase | only what changed is written; the siren glow pulses through the CanvasRenderer colour (no image rebuild) |
| `OnGUI` | `Event.current` wrapper + layout pass on every UI event while patrols exist | returns before touching IMGUI unless the flat fallback markers / HUD are in use; no layout pass |
| Visuals (every frame) | camera transform wrapper, marker `enabled` read, idle halos turned to face the camera | none of these |
| Wrapper allocations (steady state, 60 cars, rivals out) | 2-3 a frame (player, camera, UI event) + the scans' ~1,100-1,800/s | 1 a frame (`VehicleManager.Instance`) + the scans' ~850/s (one per car per scan: `activeAiCars[i]`) |

Honest frame-time estimate (not measured in game: the `RogueShared.Perf` timers Police.Update / LateUpdate /
Daredevils / Daredevils.Drive show it in TrafficDensity's [Perf] overlay): with 3 patrols in view, 2 shadow cascades
and a depth pre-pass, about 190 fewer draws, roughly 0.3-1 ms of render-thread time; 0.1-0.5 ms GPU while chasing
from the lights, depending on how the game's renderer handles extra lights; 0.5-1.5 ms less main-thread time per
second of play from the scans (a few hundredths of a ms a frame) and less GC pressure; 0.05-0.15 ms a frame while
chasing from the HUD. On a typical frame that is a few percent at most; the boss-car looks (`CarModels = Boss`,
daredevils) were not changed apart from their wheels' shadows and are now the larger cost (their draws and vertices
are in the log).

**Visual trade-offs:** the cars lost loft stations that changed nothing at 20-300 m (each within 5 mm of the old
surface; the paint, glass and light boundaries are kept), mirror pods are chamfered instead of rounded, the bumpers
and push bar have fewer segments (rendered before / after comparisons differ by under 0.3/255 on average). Police
car and boss-car wheels and the door decals cast no shadow (they are under / on the body, whose shadow is kept). A
chaser further than 70 m from the camera, or a third chaser, no longer lights the road (its lightbar and halo still
flash). The decal is now drawn with a shader variant the game is known to have compiled (`_ALPHATEST_ON` alone was
not in the game's URP Lit forward pass).

## Known limits

- Chasers are traffic cars underneath. With `Chase.Drive` they pass traffic like daredevils, but a road full from side
  to side still stops them, and they never box you in on purpose (they keep the never-hit margins). Without it they
  stay in lanes, brake for traffic ahead of them and slow down in curves like all traffic.
- A patrol you crash into is wrecked: the game stops crashed traffic for good, so it can't chase you.
- The boss-car look is scaled to the traffic car's hit box (0.7-1.15×), so a few look a little smaller than the
  boss's own car; the hit box is still the traffic car's.
- Daredevils are still traffic cars underneath: they glide along the road (no suspension, no real tyre slip), and
  their slide is a turn of the look. They don't follow the traffic-aware line RacingLine shows you, and instead pass
  traffic by themselves.
- No siren sound yet. Speed traps (more PURSUIT points) aren't in yet.
- The PURSUIT run total for the Victory screen lives in this game session only: a run continued from a save after a
  restart shows only the races played since launch.
- The pursuit panel and banners sit at y = 112-262 px (at 1080p), which overlaps HotReload's toast (dev only) when
  both show at once.
