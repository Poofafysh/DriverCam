# Police

BepInEx 6 IL2CPP plugin for **Driving Rogue**: police patrols that engage **only when you pass them recklessly**: much
faster than they're going, cutting close, or crashing, near-missing or drifting as you go by. Pass one recklessly while
you're already being chased and it joins in as backup. Get away and you've escaped. Get busted and you lose a few
seconds off the race timer. Chases score in their own category, **PURSUIT**.

Current version: **0.9.0** (preview)

Design doc (claude.ai): "Police Pursuit - v1 Concept" (revised). The police only react to what you do. Nothing
escalates at random, there are no heat levels, and nothing carries over from one race to the next. **Multiplayer
(0.8.0):** the host drives every patrol, chase and daredevil for every player, and each guest's Police draws them
and scores its own chases (see [Multiplayer](#multiplayer)); everyone in the session should run the same build. If
the game mode can't be read, the plugin does nothing.

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
3. **Chase.** The patrol that noticed you chases you and **launches** instead of crawling up from traffic pace (0.9.0,
   driven chasers: a hard but smooth acceleration ramp, 15 m/s² up to 85% of your speed, then 12 m/s²; it used to jump
   to 85% of your speed in one frame; with the game's driving it still does). **Chase driving (0.7.0, `Chase.Drive`):** each chaser is driven like a daredevil, by the same gap
   planner. It heads for your lane, passes traffic through the gaps (across lanes too), takes corners at full speed
   (the game's curve slow-down is held off) and only brakes for a car it can't get round. It **never steers into
   you**: the daredevils' never-hit rules apply. It drives smoothly (0.9.0, see
   [Smooth driving](#smooth-driving-090-chasers-and-rivals)). When the chase ends it gets its home lane back. Chasers use every lane of the live road (0.8.2: the road's width is read once a second, so Sandbox's 30 m / 6-lane road works too; 7.5 m either side on the game's 20 m roads). With `Drive = false`
   (or if the lane fields are missing from the game) chasers keep the game's traffic driving and queue behind traffic
   in their lane. A patrol you crash into is wrecked and can't chase; a unit that crashes during a chase leaves it at
   once (`UNIT WRECKED`). **During a
   chase, a patrol you pass recklessly joins in as backup**, up to `MaxChasers` (3); each one costs `BackupPenalty` (5%)
   of your lead. Chasers **inherit your car's stats**:
   - top speed = `SpeedFactor` × your car's *current* top speed, upgrades and boosts included (1.0 = exactly yours),
   - quicker acceleration (`Acceleration`: the AI's speed smoothing time, 0.8 s, never slower than its own; a chaser
     we drive uses its own speed ramp instead, see [Smooth driving](#smooth-driving-090-chasers-and-rivals)),
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
4. **PURSUIT score category.** Chases score in a real score category of their own (in multiplayer each player's
   own, with `Multiplayer.Enabled`), made like
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
   - In multiplayer a new car alone starts a new race for the counters (0.7.1: a stale `0/1` row used to carry
     over from the last single-player race).
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
  - sideways: the line's offset a little ahead of their spot (speed × 0.33 s, where the lateral spring will have got
    to it), at most 4.5 m/s sideways (5.5 m/s for drift cars), through the smooth lateral controller (0.9.0, see
    [Smooth driving](#smooth-driving-090-chasers-and-rivals));
  - round traffic and you: a gap planner scores every position across the road (every 0.5 m) by the speed it can
    carry there, given each car whose band covers it, and picks the fastest gap. Ties go to the one nearer the
    racing line, then to the one needing the least move. 0.9.0: a chosen gap is kept for at least 0.8 s unless it
    becomes unsafe or another is more than 3 m/s faster, and any move of more than 0.5 m costs extra, so it doesn't
    flip between equal gaps. A gap that means cutting across a car before getting past it is ruled out. They only brake for a car they
    won't be clear of in time: a car they're already swerving round no longer slows them. With no gap, they queue
    behind the traffic;
  - **they never try to hit you** (skilled drivers who don't want damage):
    - **A wider berth.** You get 1.6 m more than your car's width plus theirs. They read where you're heading from
      your sideways movement, 0.7 s ahead (0.9.0; it was 0.6 s).
    - **Never across you.** While you're alongside or close, the side of you they're on is a hard limit: a racing
      line or a pass round traffic never takes them across you. If you move over into them, they get out of the way
      on their side, faster.
    - **No cutting in.** They only swing back in front of you once they're 12 m + 1.5 s × your extra speed ahead.
    - **Room to traffic (0.7.0).** The side margin to a traffic car grows with the closing speed (0.9 m, +0.02 m per
      m/s faster, at most +1 m), and a car coming up from behind within 1.5 s (40 m) is never moved in front of.
      Already inside the band of a car beside them (it moved over, or they were squeezed), they only move out of
      it, never deeper toward its middle (0.7.1: the crash forensics showed rivals and chasers swinging into the car
      beside them). Chasers use the same planner.
    - **Braking.** Whatever is in line with them ahead (you or traffic) caps their speed so they can always brake to
      its real speed along the road (yours counts your slides and drifts) before a safe gap: 8 m + 0.35 s behind you,
      5 m + 0.2 s behind traffic (at their speed). The cap allows for the game's speed smoothing on the closing speed.
      0.9.0: inside that gap they brake smoothly but hard (up to 13.5 m/s², see
      [Smooth driving](#smooth-driving-090-chasers-and-rivals)); their speed is only cut at once when even that can't
      stop them closing before contact (an emergency cut, logged). Behind you this always applies. A traffic car they will swerve
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
  is avoided over its lane now and where it is heading in 0.6 s.
  `BossLooks`, `DriftCars` and `SpeedFactor` apply to daredevils picked up after a change.

### Smooth driving (0.9.0): chasers and rivals

From the 2026-10-04 feedback ("the cops and daredevils are still super unstable ... they need to be smooth like we
are"). Chasers (`Chase.Drive`) and rivals racing the line now move like a driver, not like a cursor:

- **Why they twitched (game code read in GameAssembly.dll):** the game moves a traffic car in `FixedUpdate` (50 Hz):
  distance += `Speed` × time, and the (kinematic, interpolated) body is smooth-damped in 0.1 s towards the path point
  plus `CurrentLaneOffset`. In `Update` it smooth-damps `Speed` towards `MaxSpeed`. Up to 0.8.2 Police wrote a
  `MaxSpeed` that jumped whenever the planner's speed cap changed (the game's smooth-damp turns a jump into an
  acceleration spike of hundreds of m/s²), cut `Speed` at once when close to a car (14-23 times a minute in the offline
  test), moved the lane offset at a constant sideways speed (0 → 4.5 m/s in one frame) and turned the car's look by
  that sideways speed, about 0.1 s before the body really moved.
- **Sideways:** the planner's target goes through a stiff, critically damped spring with limits: sideways speed at most
  4.5 m/s (5.5 drift cars) and about 0.3 × its speed, sideways acceleration speed × 0.5 /s (3-11 m/s², so the yaw rate
  stays under about 30°/s), built up at most 45 m/s³. It slows down in time to stop at its target (no overshoot into a
  car beside it). Evading you (you moved over into it): much quicker (up to 18 m/s², 400 m/s³), never slower than
  before at getting out of your way.
- **The look:** a grip car's yaw follows the body's real sideways motion (the game's own 0.1 s smoothing modelled),
  eased in 1/20 s, so an emergency cut can't snap it either. Drift slide and front-wheel steer as before.
- **Choosing a gap:** kept for at least 0.8 s unless it becomes unsafe (a hard limit rules it out) or another is more
  than 3 m/s faster. The planner's sideways timing now uses the car's real sideways acceleration, how fast that builds
  up, its current sideways speed and the game's 0.1 s smoothing (it used to assume an instant 4.5 m/s), so it doesn't
  plan swerves it can't make.
- **Speed:** our own commanded speed with bounded acceleration (rivals 10 × skill m/s², at least 6; chasers 12, 15
  while launching), braking 10 m/s² and jerk 25 m/s³ normally; a car that needs more than 6 m/s² of braking gets up
  to 13.5 m/s² at 60 m/s³. It is written as both `MaxSpeed` and `Speed` every frame (with `speedDampVelocity` = its
  acceleration), so the game's smoothing has nothing left to do and a car handed back carries on from the same speed.
- **Emergency cut (rare, logged):** only when the car is closing on a car in its way faster than 1 m/s and even
  13.5 m/s² ramped in at 60 m/s³ can't stop it before contact (1.5 m behind traffic, 3 m behind a player). Never for a
  car alongside (braking can't prevent a touch from the side: it brakes at 8 m/s² to drop back instead) or for a
  traffic car it is already swerving clear of in time (it brakes at most 9 m/s² meanwhile).
- **Toward a player, safety beats smoothness** (never hitting a player matters more than looking smooth):
  - within 2 m of a player's band (alongside or close) it uses the full sideways evade limits at once (braking near a player comes from the never-hit envelope, the emergency cut and the squeeze rule below);
  - a player alongside keeps the emergency cut (traffic alongside doesn't);
  - your predicted lane looks 0.7 s ahead (0.6 s + the game's 0.1 s position smoothing), and while you close in from
    behind it also covers where your sideways speed takes you by the time you get there (0.7-1.5 s);
  - **squeezed:** inside a player's band widened by 2.5 m with no way out sideways (the road edge), behind or alongside
    that player, it drops back early to their speed − 8 m/s (10 m/s²), before the squeeze rather than once pinned;
  - **held up ahead of a fast player:** it may cross to the other side of that player's lane while the player is
    still at least 8 m behind and won't arrive before it is across with 1.5 × its sideways time + 0.5 s to spare (it
    used to be chased out to the road edge, into the lane the player then took);
  - with a player close on its tail, it matches the speed of the car it follows instead of dropping 1 m/s below it.
- **Chasers aim ahead:** at your lane where you'll be in 0.4 s (your sideways speed), a remote player's likewise.
  Remote players' predicted lanes also look 0.7 s ahead (plus their update's age).
- Nothing changes while paused (nothing is written). Never-hit margins, remote players' latency margins (multiplayer),
  the host-only rule and every exit path are as before.
- **Offline test** (a scratch harness, not in the repo: the planner and controllers on synthetic 4-lane traffic with
  uneven frame times and the game's FixedUpdate / Update pipeline modelled; 120 runs × 120 s each):

  | | rivals 0.8.2 | rivals 0.9.0 | chasers 0.8.2 | chasers 0.9.0 |
  |---|---|---|---|---|
  | yaw rate, per-second max (avg / p95) | 1000 / 2807 °/s | 21 / 41 °/s | 831 / 2603 °/s | 22 / 42 °/s |
  | look yaw vs real motion (avg error) | 1.7° | 0.3° | 1.3° | 0.3° |
  | commanded sideways accel (p99 / max) | 540 / 1404 m/s² | 16 / 25 m/s² | 540 / 1404 m/s² | 16 / 25 m/s² |
  | planner target flips | 41 /min | 30 /min | 37 /min | 27 /min |
  | instant / emergency cuts | 22.6 /min | 2.6 /min | 14.0 /min | 2.4 /min |
  | average speed | 106 km/h | 98 km/h | 106 km/h | 96 km/h |
  | contacts with the (blind, test) player | 10 | 2 | 5 | 3 |
  | ... on a second set of 120 runs | 20 | 2 | 11 | 2 |

  The test player changes lanes without looking. Every contact left in 0.9.0 is that player moving sideways into the
  car or running into it from behind, with the car pinned at the road edge.

### The caught penalty

The game's `TimerManager.AddTimerSeconds(-5)` can't take time away: in single-player it passes the negative value to
`RaceTimer.RemoveCountdownTime`, which clamps it to 0, so nothing happens. Police instead calls
`RaceTimer.RemoveCountdownTime(5)`, the game's own way of taking time off, directly. It only does this while a
countdown timer is running, only in single-player (in multiplayer the countdown is game-networked: no penalty), and
it always leaves at least 3 s on the clock: a police catch never ends your race by
itself. If no time can be taken, the banner just says `BUSTED` and the log says why.

## Multiplayer

0.8.0, `Multiplayer.Enabled` (on by default). Everyone in the session should run the same Police build. **The host is
the authority for everything traffic-based**: police, chases and daredevils.

- **Host:** drives every patrol, chase and daredevil, as in single-player, for **every player**. Each player has their
  own chase (lead bar, units, time, cooldown, outcome) under the same rules: police react only to what that player
  does, busted = caught slow or boxed in, no heat levels. The host reads every player's road distance, lane and speed
  from the values the game itself syncs (`NetworkPlayer`), and their collisions, near misses, drifting and top speed
  from the report their own Police sends. New patrols follow the leading player and are picked ahead of each player in
  turn; a patrol is let go only when it is 150 m behind every player. Chasers and daredevils treat **every player** as
  a never-hit obstacle (the same rules as for you, with latency margins below). Daredevils' pace, defending and
  pick-up stay relative to the host's own car.
- **Guest:** never drives or picks anything. It draws the host's patrols (police car, lightbar, marker; by its own
  `Look` settings) and daredevils (the same boss car) on its own copies of those cars, shows its own pursuit panel and
  banners and scores its own PURSUIT from the host's chase events (its own `Pursuit` settings). Its own race end
  escapes / banks its chase at once.
- **No time penalty in multiplayer:** the race countdown is shared by the game's networking
  (`NetworkGameManager.AddTimerSeconds` goes through a Command / RPC, the elapsed time is synced), so a local cut could
  shorten everyone's race or end one player's race early. A catch in multiplayer is BUSTED (that chase's PURSUIT points
  lost) with no time taken; the log says `no time penalty (multiplayer: shared race timer)`.
- **Whose settings:** the host's `Patrols`, `Notice`, `Chase` and `Daredevils` driving settings apply to everyone, and
  the host's F3 / `Mode` turn police off for everyone. A guest's `Mode = Off`, F3 or `Enabled = false` turns police
  off for that guest only (the host stops chasing them, no looks drawn: its own switch is respected); a guest's
  `Mode = Chill` means it is never noticed.
- **Who has it off, and why (0.9.0):** the Police link runs whenever the game is a host or a guest, even with that
  player's `General.Enabled` or `Multiplayer.Enabled` off. Then it only says hello, with "police off / daredevils off
  and why" (no cars, no chases, nothing drawn), so the other side's log says why nothing shows: a host with
  `General.Enabled = false` (the 2026-10-04 report: no police for anyone) is logged on every guest, and a guest that
  has Police off is logged on the host. A guest's `Look`, `Pursuit` and `Daredevils.BossLooks` / `Enabled` (drawing) are its own. `CaughtPenaltySeconds`
  does nothing in multiplayer (see above).
- **Latency, honestly:** on the host a guest's synced position is about half a round trip old, and the guest sees the
  host's cars about half a round trip plus the interpolation buffer late. The round trip is measured on the Police
  channel (hellos); the buffer is assumed at 0.15 s (the game's traffic interpolation isn't known). For a remote player
  the planner moves their position on by their speed for that time, places them further ahead by the car's own speed
  times the view delay, widens their band by 0.4-1.5 m (more when they're changing lanes) and lengthens every gap to
  them by 2 m. A player with no Police link is still a never-hit obstacle, but never chased.
- **Exit paths:** a player leaving (their chase cancelled, chasers given back), the host leaving or the link going quiet
  for 3 s (guest: chase cancelled, looks removed), race end, new race, quit, role change, `Multiplayer.Enabled` /
  `Enabled` / F3 off, the breakers and unloading all end chases, remove looks and close the Steam session.

**The Steam channel.** Steamworks `SteamNetworkingMessages` on private channel **7741**, called through the flat C
exports of the game's own `steam_api64.dll` (nothing in the game's own networking is used or changed). A guest finds
the host's SteamID from the address it connected to (FizzySteamworks: the host's SteamID) or the lobby owner, and says
hello every second; the host invites every remote player (the SteamID of its connection) and every lobby member with a
hello every 2 s until they link (Steam only delivers a peer's messages once we have sent to it); the host accepts a hello only from a remote player in the session (its connection must give a SteamID
and it must be the sender's; a player without one is never linked, so never chased, but stays a never-hit obstacle). Messages: magic `RPOL`, protocol 1, type, then:

| Type | Direction | Body |
|---|---|---|
| Hello (1, reliable, 1 s) | both | Police version, sender's player netId, time stamp, echo of the other's last stamp + hold time (round trip); 0.9.0 adds a status at the end: flags (1 police on, 2 daredevils on / drawn, 4 Chill), why police is off, why daredevils are off (an older Police ignores it; a 0.9.0 reader treats a hello without it as "status not sent") |
| State (2, unreliable, every 0.15 s) | host to guest | the guest's netId; patrols (netId, look cursor, chasing / chasing you); rivals (netId, boss rank); the guest's chase: on, chase id, lead %, units, seconds left |
| Event (3, reliable) | host to guest | the guest's netId, chase id, kind: Start (units), Step (lead before / after, dt, at speed, units), End (PURSUIT end kind, outcome, seconds, duration, lead, units, reason), Toast (text, colour) |
| Report (4, unreliable, every 0.15 s) | guest to host | the guest's netId, its collision and near-miss counts, drifting (now / since the last report), race over, wants police, notices (Mode Normal), top speed, current top speed |
| Bye (5, reliable) | both | why the link stopped |

Cars are matched by their Mirror netId (`NetworkAIVehicle`). Every count and value read from a message is bounded;
anything malformed or from another SteamID is dropped.

**What to check in the log (multiplayer):**
- `[Police] multiplayer host (was single-player): police and daredevils for every player, shared over the Steam
  channel` (or `multiplayer guest (...)`), and `multiplayer: Steam link ready (private channel 7741, ...)` (or `Steam
  link unavailable (<why>); retrying every 10 s`).
- Host: `multiplayer: player N in the session (...)`, `multiplayer: inviting N player(s) to the Police link (channel 7741)`
  (once), `multiplayer: linked to player N (Police x.y.z); police on on their side; police and daredevil looks shared
  with them` (`NOT this build` when the versions differ; 0.9.0 status: `police off on their side (General.Enabled
  false): they see no police or rivals`, `their game doesn't draw rivals (Daredevils.Enabled false)`, or `their status
  isn't sent by that version`), then `multiplayer: player N's Police: <status>` whenever it changes, and once per player
  `multiplayer: version mismatch: player N runs Police x.y.z, this host y.y.y: everyone should run the same build`,
  `police for player N: on (top speed N km/h, notices)` / `off (<why>)` /
  `removed (<why>)`, `multiplayer: link to player N lost (nothing for 3 s)`, `multiplayer: player N stopped its Police
  link (<why>)`, `multiplayer: player N left the session`, `multiplayer: host link closed (<why>)`, and with
  `LogEvents` the usual lines with ` (player N)` after them: `noticed (player N): ...`, `backup joined (player N)`,
  `chase over (player N): CAUGHT ..., no time penalty (multiplayer: shared race timer)`, `race over (player N): no more police for them this race`;
  `patrol picked: ... netId N`, `daredevil ...: ..., netId N`; `hello from ... ignored (...)` for a stranger;
  `daredevils: active (racing the line), multiplayer host: every player is kept clear of`; `daredevils: remote players can't be read: rivals get the game's driving until they can (never-hit rule)` and `daredevils: every player readable again: rivals race the line`.
- Guest:
  - `multiplayer guest: host found (connect address)` (or `lobby owner`), then (0.9.0) `multiplayer guest: linked; host
    Police x.y.z, police on, daredevils on` (`off (<why> on the host)`; an older host: `that version doesn't send whether
    its police / daredevils are on`), and `multiplayer guest: version mismatch: the host runs Police x.y.z, this game
    y.y.y: run the same build` when they differ
  - the host's status, at linking and whenever it changes (0.9.0): `multiplayer guest: the host's Police is off
    (General.Enabled false on the host): no police this session` (or `Multiplayer.Enabled false`, `Mode = Off`,
    `patrols off with F3`, `switched off after an error`, `game check failed`), `the host's Police is on again: police
    this session`, `the host's daredevils are off (<why> on the host): no rivals this session`, `the host's daredevils
    are on again`
  - state lines (on every change; 0.9.0: the waiting ones again every 10 s until linked): `multiplayer guest: waiting
    for the Steam link (<why>)`, `waiting for the host's Police (looking for the host's SteamID)`, `waiting for the
    host's Police (no hello yet: is Police 0.9.0 installed on the host? channel 7741)`, `linked (police and daredevils
    from the host)` (`linked (police from the host; the host's rivals aren't drawn: Daredevils.Enabled false here)`),
    and, with its own Police off, `multiplayer guest: Police is off in this game (General.Enabled false): the host's
    police and daredevils aren't drawn here (the host is told)` (or `Multiplayer.Enabled false`, `Mode = Off`, `off
    (F3)`)
  - `multiplayer guest: first patrol from the host drawn (<look>)` and `first daredevil ...`, once each (0.9.0: both)
  - `multiplayer guest: chase on (from the host, N unit(s))`
  - `multiplayer guest: chase over: ESCAPED / CAUGHT / cancelled (<why>) after N s, lead N%, N unit(s)`, CAUGHT with `, no time penalty (multiplayer: shared race timer)`
  - `multiplayer guest: chase over: ESCAPED (ahead at the finish at N%)` or `multiplayer guest: chase over: banked (race over)` (its own race end)
  - `multiplayer guest: link to the host lost (nothing for 3 s)`, `multiplayer guest: the host stopped its Police link (<why>)`, `multiplayer guest: link closed (<why>)`
  - `daredevils: idle: multiplayer guest: ...`, and the usual `pursuit: +N pts (...)` lines from its own PURSUIT
- `multiplayer link switched off for this session after an error` (everything multiplayer stops, patrols too in
  multiplayer; single-player is unaffected).

## Settings (`BepInEx/config/rogue.police.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch (in multiplayer the Steam link still runs with it off: hellos only, no cars, chases or drawing, so the other players' logs say why) |
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
| `Chase.CaughtPenaltySeconds` | 5 | seconds off the race timer when busted (0-30, 0 = none; always leaves 3 s; single-player only: none in multiplayer) |
| `Chase.Cooldown` | 8 | seconds after a chase before any patrol can notice you again (0-300) |
| `Chase.SpeedFactor` | 1.0 | chasers' top speed as a fraction of your car's current top speed (0.5-1.1) |
| `Chase.Acceleration` | 0.8 | chasers' speed smoothing time in seconds (0.2-5, lower = quicker) |
| `Chase.CatchUp` | 0.15 | extra top speed for a chaser more than 60 m behind you, in full at 200 m (0-0.5, 0 = off) |
| `Chase.Drive` | true | chasers drive like the daredevils: pass traffic round the gaps, full speed in corners, never into you (off = the game's traffic driving) |
| `Daredevils.Enabled` | true | daredevils become boss-car rivals racing the line (independent of patrols; in multiplayer the host drives them, a guest with it off doesn't draw their boss cars) |
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
| `Pursuit.Enabled` | true | PURSUIT score category (in multiplayer each player's own); off = chases score nothing (at once: a running chase's live points are dropped) |
| `Pursuit.PointsScale` | 1.0 | multiplies every PURSUIT point, live and escape bonus (0-3) |
| `Pursuit.CoinReward` | 100 | coins at full target (2 escapes in a race) (0-1000) |
| `Records.BestRunTotal` | (written) | best PURSUIT run total so far, for the Victory screen's NEW RECORD |
| `Multiplayer.Enabled` | true | police and daredevils in multiplayer: the host drives them for every player, guests draw them and score / penalise themselves (off = no police or daredevils in multiplayer; 0.9.0: Police still tells the other players' Police that it is off, so their logs say why; see [Multiplayer](#multiplayer)) |
| `Debug.LogEvents` | true | log patrols, notices, chase results and daredevils |
| `Debug.Smoothness` | false | once a second per driven chaser / rival: largest yaw rate, lateral and speed jerk, planner flips, emergency cuts, speed reversals (the per-race `smoothness:` summary is logged either way) |
| `Debug.ConfigVersion` | (written) | settings migration marker, don't edit |

**Upgrading:** settings you never changed move to the new defaults once (from 0.0.x: `NoticeRange` 60 → 90,
`OverspeedKmh` 80 → 35, `Duration` 40 → 45, `Cooldown` 20 → 8, `SpeedFactor` 0.97 → 1.0, `MaxPatrols` 2 → 3; from
0.1.0: `BackupPenalty` 10 → 5); values you changed are kept. The log lists what moved. **Removed in 0.2.0:**
`Notice.SpeedingKmh` and `Notice.SpeedingSeconds` (police no longer react to speeding near them, only to a reckless
pass); they may stay in an old config file harmlessly. **0.3.0:** `Look.BossCars` became `Look.CarModels` (an old
`BossCars = false` becomes `Traffic`; otherwise you get the new police cars) and the old entry is removed. **0.5.0:**
`Daredevils.GripCornering` and `DriftCornering` are removed (rival cornering now follows your car × skill).
**0.6.0:** new `[Pursuit]` and `[Records]` sections and `Chase.CatchUp` (no migration needed). **0.7.0:** new
`Chase.Drive` (no migration needed). **0.8.0:** new `[Multiplayer]` section (no migration needed). **0.9.0:** new
`Debug.Smoothness` (no migration needed).

Edits to the `.cfg` file apply at the next game start. The plugin reads every value live, so an in-game config
manager can change them while you play.

## This is a gameplay change

- It changes how up to `MaxChasers` traffic cars drive during a chase: they follow you at your car's top speed and,
  with `Chase.Drive`, are steered like daredevils (the same fields are written each frame: lane offset and lane-change
  fields, `curvatureFactor`, `pedalFactor` unless the snapshot is full, and 0.9.0: `MaxSpeed`, `Speed` and
  `speedDampVelocity` from Police's own smooth speed ramp; an emergency cut of `Speed` only when nothing else avoids
  contact). Given back with their home lane when the chase ends. Police never touches traffic cars' colliders or collision values (CurbFeel and
  TrafficDensity tune those) and, apart from daredevils that race the line (below), their obstruction braking.
- Daredevils (up to 6) are driven by Police while they race the line.
  - **Written each frame:** their lateral offset and lane-change fields (`CurrentLaneOffset`, `previousLaneOffset`,
    `targetLaneOffset`, `initialLane`, `MyAngle`, `LaneTransitionSpeedMultiplier`), their curve slow-down
    (`curvatureFactor`), the obstruction braking's output (`pedalFactor`, `targetPedalFactor`, held at 1 unless the
    traffic snapshot is full), `MaxSpeed`, `Speed`, `speedDampVelocity` (0.9.0: Police's smooth speed ramp; `Speed`
    and `speedDampVelocity` are running state the game rewrites itself) and `speedSmoothness`.
  - **Emergency cut:** their running `Speed` is cut at once only when even hard braking can't stop them closing on a
    car before contact (logged).
  - **Despawn distances:** `behindDistanceDespawn` and `aheadDistanceDespawn` are raised for as long as a daredevil
    is a rival.
  - **When one is let go:** it gets back its top speed, speed smoothing, rubber-banding flag, despawn distances and
    home lane, and the game's own lane change takes it from where it is into its lane. A car the pool has already
    reused only gets its speed smoothing and despawn distances back.
  - A crashed one is left to the game's physics. Nothing is written while the game is paused. Daredevils more than
    1 km ahead of you are left to the game's driving.
- Getting busted can take seconds off your race timer (single-player only; never in multiplayer).
- **PURSUIT points count** (in multiplayer too, each player's own, with `Multiplayer.Enabled`). They are part of TOTAL and of the single-player run total the game uploads to its Steam
  leaderboard (set `Pursuit.Enabled = false` to keep chases out of the score). The category is a copy of Top Speed, so
  Top Speed cards and multipliers also affect it, and a chase's live action keeps the game's combo going while it
  runs. Saves stay compatible both ways: the id is unique, and the game skips ids it doesn't know.
- Nothing Police draws has a collider: the police car look is plain meshes, the lightbar's colliders are removed the
  moment it is created, the marker never had one. The boss prefab is only read, never spawned, so none of the game's
  boss scripts or effects run. Nothing is attached to the car itself: the look, lightbar and marker follow the car
  every frame, so a car the game recycles never carries them along. The traffic car's own model is only switched off
  (its renderers) and switched back on when the patrol is let go. Everything Police creates is destroyed when the
  patrol is let go, when the scene changes, or when the plugin switches off.
- Multiplayer (0.8.0): only the host writes to traffic cars (it owns them; the game itself syncs their positions to
  everyone). Police never writes a Mirror SyncVar or calls the game's Commands / RPCs. Mod state goes over a private
  Steam channel between the players' Police instances (see [Multiplayer](#multiplayer)). A guest only changes its own
  game: the looks on its local copies of the cars (renderers of its copy switched off and back on, never synced) and
  its own PURSUIT score. Nobody's race timer is touched in multiplayer (no caught penalty: the countdown is shared).

## What to check in the log (`/game-log Police`)

- `Police x.y.z loaded` (the version you installed) and `[Police] game check OK: traffic, player, collision/near-miss counts, race timer, game mode, boss car models, daredevils, pursuit score, results/victory rows, multiplayer host + guest`. A
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
  ago, steering 4.1 s, emergency cut 30 km/h 0.4 s before, capped by a car; edge 3.1 m (line ±8.5), corner radius 180 m`
  (0.9.0: `emergency cut`; it was `instant cut` up to 0.8.2)
  (0.7.0 forensics after the `;`: the nearest car on any side at the last snapshot, timing, the planner's state, how
  far out the car's side is against the line's limit, and the corner) (once per crash; `oncoming` for a car on the other side, `nearest car 6.2 m ahead lane 1.5 a wreck` for a crashed
  car, `no car ahead near its path` when none, `no traffic snapshot` when it crashed before its first snapshot),
  `daredevil <car>: contact with you? (you collided while it was N m away)` (your collision count went up while a
  rival was within 1 m: the never-hit rule should keep this at zero), each let-go report has `contacts N`, and
  (always) at the end of every race with a rival `daredevils race summary: rivals N, crashed C, passed you P, you
  passed them Q, closest X m, contacts with you K`.
- 0.9.0 smooth driving:
  - (always) at the end of every race with a driven chaser or rival: `[Police] smoothness: rivals N (avg max yaw X
    deg/s), chasers M (avg max yaw Y deg/s), typical yaw rate Z deg/s, planner flips F/min, emergency cuts C, speed
    reversals R/min, lateral jerk max J m/s^3, driven T s` (avg max yaw = each car's largest yaw rate, averaged over
    the cars; typical = the average of the per-second maxima). Compare builds with it: expect typical yaw rates of
    20-40 deg/s, emergency cuts a handful per race, lateral jerk max 45 (400 when one had to get out of your way).
  - With `LogEvents`: `daredevil <car>: emergency cut 140 -> 92 km/h (room 2.0 m, closing 48 km/h, needed 25 m/s^2)`
    and `chaser: emergency cut ...` (`chaser (player N): ...` for a remote player's chaser).
  - With `Debug.Smoothness`, once a second per car: `smoothness daredevil <car>: yaw rate max N deg/s, lateral jerk
    max N m/s^3, planner flips N, emergency cuts N, speed reversals N, speed jerk max N m/s^3` and `smoothness chaser
    <model>: ...`.
- `[Police] daredevils: the game despawns traffic N m behind / N m ahead of you; rivals use at least N / N m` (once: the game's own despawn distances, and the larger distances rivals are kept for), and `daredevils: racing line gone` when RacingLine's line is dropped (menu or loading).
- `[Police] settings updated: ...` (or `settings updated to the current defaults (values you had changed are kept): ...`) once after an update that moved or removed config values.
- `[Police] 3 police car models loaded (Interceptor 2650 tris / 2603 verts, Pursuit ..., Utility ...) in N ms from
  ...plugins\Police` (once) and `police car built: <model> (6 draws, 4 wheels, size, livery)`. With `CarModels = Boss`:
  `N boss cars for police looks: <boss>: <car> ...` and `police car built from <car> (<boss>): N parts (N draws, N
  vertices), 4 wheels, size` (also for each daredevil's boss car). `no police car models folder` means the
  `plugins\Police\*.pcm` files weren't installed (boss cars are used instead).
- `[Police] lightbar materials: shader '...', halos on/off` (once).
- `[Police] active`, `idle: ...` (why it's off: config, F3, `multiplayer (Multiplayer.Enabled = false)`, `multiplayer guest: the
  host runs the police`, `multiplayer: role unknown`) and `waiting for a race`. Multiplayer: see the list in
  [Multiplayer](#multiplayer).
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
- 0.9.0 smooth driving (offline test, not yet in game): in dense traffic chasers and rivals average about 8-10% less
  speed than with 0.8.2's instant sideways moves and speed cuts (a smooth car can't teleport round a car, and it gives
  players more room), and a car pinned between the road edge, traffic and a player moving into the outer lane can
  still be touched by that player (far less often than before). The commanded speed is written as `Speed` each frame, so the game's `Chase.Acceleration` smoothing no
  longer applies to a driven chaser.
- Daredevils are still traffic cars underneath: they glide along the road (no suspension, no real tyre slip), and
  their slide is a turn of the look. They don't follow the traffic-aware line RacingLine shows you, and instead pass
  traffic by themselves.
- No siren sound yet. Speed traps (more PURSUIT points) aren't in yet.
- Multiplayer: no caught penalty (the race countdown is shared). Daredevils get the game's driving while any remote
  player can't be read (log: `daredevils: remote players can't be read: ...`). A player whose connection gives no
  SteamID is never linked or chased (still kept clear of). Steam transport only (on LAN / KCP a guest can't find the host's SteamID, so only the host gets police);
  at most 3 remote players are chased; AI racers are ignored. On a guest the daredevils' drift slide and front-wheel
  steer aren't shown (only the boss car), and the patrol look's pointing into a lane change isn't sent either. The
  latency margins use an assumed 0.15 s interpolation buffer. Whether the game's multiplayer score sync carries a
  guest's PURSUIT points into the shared totals (the copy reports type Top Speed, as RacingLine's does) is not verified.
- The PURSUIT run total for the Victory screen lives in this game session only: a run continued from a save after a
  restart shows only the races played since launch.
- The pursuit panel and banners sit at y = 112-262 px (at 1080p), which overlaps HotReload's toast (dev only) when
  both show at once.
