# RacingLine

BepInEx 6 IL2CPP plugin for **Driving Rogue**: a new score category, **Racing Line**, that rewards driving corners well on grip: the right line, good speed, braking straight, lifting in and powering out.

Current version: **0.7.0** (v2 scoring counted live, traffic-aware line drawn on the road; placeholder icons)

Design docs (claude.ai artifacts):
- "Racing Line Mechanic - Design & Build Plan" (`799a19cf-48f1-4019-9d37-925b9838d47f`): the category itself and the Safety rules that apply to every change here.
- "Racing Line v2 - Scoring Algorithm" (`659a25d6-d687-40cb-a9ba-d242facc6614`): the scoring rules below and the research behind them.
- "Grip vs Drift - Corner Scoring Balance" (`1baa35b8-5ec4-4b36-8158-d27cad8f7b12`): why Racing Line feeds the combo.

## How it scores (v2)

1. **The line.** When a run starts, it computes the minimum-curvature line from the run's centre-line path: the straightest line the road allows, staying `Margin` inside each edge. The build is spread over frames (`FrameBudgetMs`).
2. **Corner zones.** Bends tighter than `CornerMinRadius` are corners. Each zone runs from 40 m before the corner (the braking zone) to 60 m after it (the exit). Corner types:
   - **A**: onto 200 m or more of straight. The exit counts ×1.25.
   - **B**: normal.
   - **C**: links straight into the next corner. The exit counts ×0.8.
3. **Reference speed.** Built for every point of the line: the cornering limit, then a backward pass for braking and a forward pass for acceleration. It's scaled to your car's own top speed, and the grip limit learns your car's 95th-percentile cornering load. When the player car changes (a new car object, which also happens at each new level), the grip estimate starts again from `GripStart` and the profile is rebuilt.
4. **Quality every frame** in a zone: **q = Pos × (0.45·Speed + 0.25·Grip + 0.30·Pedals)**.
   - **Pos:** 1 within `LineFull` (2.5 m) of the line, easing down to `LineFloor` (0.25) at `LineZero` (8 m). Or full credit if your own path is as straight as the line.
   - **Speed:** your speed ÷ the reference speed.
   - **Grip:** how hard the car is cornering.
   - **Pedals:** before the apex anything goes, because **braking with the wheel turned starts a drift in this game, so there's no trail braking**. Brake in a straight line, then lift or use light throttle into the apex. After the apex it's 0.3 + 0.7 × throttle.
5. **Points, counted live** like the game's own categories: each corner zone is a live action, and its points count up on the HUD while you drive it (nothing is paid at the end of the corner).
   - **Every frame** with q ≥ `TickMinQ` earns `PointsPerMetre` × 1.25 × q^1.5 per metre (halved while drifting), multiplied by everything that is true *right now*:
     - **Grip line** ×2 while you haven't drifted in this corner (lost from the moment you drift)
     - **clean** ×1.15 while you haven't hit anything in this corner
     - **exit** after the apex: × (1 + 0.5 × throttle × speed ÷ reference × the corner type factor)
     - **coasting:** −15% per second coasting *after* the apex (floor 0.6)
     - **streak** and **pace** (below)
   - The action goes into the game's combo when the zone ends, or after `LiveGrace` (0.6 s) below `TickMinQ`; flowing points keep the combo alive through grip corners. **A collision cancels the live action** (that corner's points so far are lost), like a crash does to a drift.
   - **Grade by mean q:** GOLD ≥ 0.8, SILVER ≥ 0.6, BRONZE ≥ 0.4 (for coins and the streak; shown on the HUD card).
   - **Streak:** +0.15× per corner with q ≥ 0.45, up to ×2. A collision drops it two steps, and a corner under 0.3 resets it.
   - **Pace:** every payout × (0.8 + 0.4 × your average speed ÷ reference).
   - **Traffic:** "the line" is the traffic-aware line (see below), so a car sitting on the racing line moves the line around it. As a fallback, for 1.5 s after a near miss the position term holds, so dodging isn't punished.
6. **Coins.** Corner grades add units (GOLD 1, SILVER 0.6, BRONZE 0.3, ×1.5 Grip line) toward a target of `CoinTargetPerCorner` × the race's corners. A steady SILVER run earns the full `CoinReward` (130), like maxing any other category.

A simulated 3 km test road with 8 long corners gave these totals:

| Driving | Points |
|---|---|
| Tidy grip | 2,067 |
| Coasting out of every apex | 1,603 |
| Drifting every corner (drift points come separately) | 801 |
| Wide and slow | 626 |

These are simulated totals before the game's multipliers. In-game totals are much higher (21k-172k in the live log) because the game's card multipliers apply on top.

## Traffic-aware line

If an NPC car is sitting where the racing line goes, that can't be the perfect line. Every 0.1 s the plugin reads the traffic cars from 15 m behind you to `LookAhead` (150 m) ahead: where each one is along the road, which lane it's in, its size and its speed. Oncoming cars (reverse-traffic runs) are included, and they close at your speed plus theirs. Cars that have crashed are skipped, because physics now moves them and their road position is no longer known.

- **In the way:** a car is in the way when the line passes closer than its half-width + `PlayerHalfWidth` + `Margin` (1 + 1 + 0.5 = 2.5 m for a normal car), and only while you're catching it.
- **Passing side:** the line passes the car on whichever side is closer to the racing line. That side has to be inside the line's edge limit and not taken by another car alongside. Cars alongside each other share one way round.
- **Shape:** the line moves over on a smooth ramp. It starts `LeadInSeconds` × closing speed before the car (between `MinLeadIn` 15 m and `MaxLeadIn` 60 m), holds beside the car, and comes back over `LeadOut` (15 m). Detours one after another blend smoothly; the line never jumps sides.
- **No way past** (for example both lanes of a narrow road taken side by side): that stretch counts as perfect position wherever you drive, so you're never punished for a line that can't be driven.
- **Scoring:** position is measured from this line, not from the plain one. Inside a detour, the space the car takes up never counts as "on the line". Driving the plain line straight through a car scores like being far off it, and the "path as straight as the line" credit doesn't apply there either.
- **Cost:** at most 48 cars at 10 Hz, with no per-frame work beyond the scoring itself.
- **Multiplayer clients** use the plain line. The host drives the traffic, and the clients' copies of the cars haven't been checked.
- **Turning it off:** `Enabled` = false goes back to the plain line everywhere. Missing game members switch only this feature off; the startup check names them.

With F5 the line on the road is this line: orange where it goes around a car, faint where there's no way past.

Simulated on the same 3 km test road with a car parked on the line's apex in every corner:

| Driving | Points |
|---|---|
| No traffic, plain line | 2,266 |
| Traffic, follows the traffic-aware line | 2,229 (the 1.6% is the grip term reacting to the swerve; the position terms are identical) |
| Traffic, drives the plain line straight through the cars | 2,073 (no collision simulated; in the game the crash also costs Clean and the streak) |
| Two-lane road, both lanes blocked side by side, plain line | the same as no traffic (no way past: counts as perfect position) |

**Known limits:**
- The line passes each car or group on one side. Passing a car cleanly on the other, farther side still counts as off the line there.
- A car that isn't in the way of the plain line isn't checked against a detour's ramp near it.

## Native category

**Single-player and multiplayer** (multiplayer with `Scoring.InMultiplayer`, on by default; your own score): Racing Line is a real score category. It's an **inert copy of the game's Top Speed category**:
- its own logic can never start (`timeThreshold` 1e9, `targetSpeedFactor` 2, `scoreMultiplier` 0)
- it has id `rogue.racingline`, our name and icons, and `contributeToCombo` on
- it's appended once to the level's score list

Points go through the game's own live-action path, the same one Drift uses: `OnScoreBegin` + `OnScoreActivated` when a corner's points start flowing, `AddToTemporaryScore` every frame (the HUD counts up), `TransferTempToComboScore` + `OnScoreEnd` when the zone ends, `CancelTemporaryScore` on a collision. The game's own `FinishLevel` banks anything still in flight, and a failed combo clears it.

### Rows on the score screens

The game's score screens have one fixed row per built-in category, so RacingLine adds its own RACING LINE rows (shared code `../Shared/ScoreRows.cs`, also used by the Police plugin's PURSUIT rows). The rows are display only: the game's TOTAL already includes Racing Line's points.
- **Results screen** (after each race): a copy of the Top Speed row, after Near Miss, with the time on the line, the race's Racing Line points and coins. It is added to the screen's row list after the game's own rows (and before Police's PURSUIT row, if that was added first), so the screen's own count-up animates it in screen order, and it's only removed once the screen has closed. In display mode it shows the time only.
- **Victory screen** (end of the run): a copy of the Near Miss row, after Near Miss, with the Racing Line icon and the run's Racing Line total. It appears when the screen's results window opens (after Continue on the popup) and goes when it closes. A run total higher than `Records.BestRunTotal` shows NEW RECORD. Each race's score is added to the run total once, when its results screen (or the Victory screen) opens; the total resets at the first race of a new run (the run position is read again on later frames until a read succeeds for that race, so a missed read can't carry the last run's total into a new one and show a false NEW RECORD). It lives in this game session only: a run continued from a save only counts races played since launch.
- **Order:** RACING LINE comes right after Near Miss and before Police's PURSUIT row on both screens, whichever plugin adds its row first.

Accepted side effects:
- **Top Speed cards also affect Racing Line**, because the copy reports type Top Speed.
- Racing Line points are part of the run total the game uploads to its Steam leaderboard.

**Clean-exit boost (0.7.0, `[ExitBoost]`).** A corner taken on grip pays off on the exit: when a corner zone ends with
a grade (BRONZE or better), no drift and no collision, your car gets a short boost: up to +5% top speed and +25%
acceleration for 1.5 s at GOLD (SILVER 0.6x, BRONZE 0.3x). It uses the game's own temporary speed-boost mechanism
(`VehicleManager.SpeedModifierHandler.RegisterTemporaryModifier`, the same one as the game's drift-end boost), so it fades
in and out, removes itself and stacks with cards like any other boost. A new boost replaces the old one; switching it off,
the race ending or the plugin unloading takes it away. Your own car only, also in multiplayer.
`ExitBoost.KeepOffLeaderboards` (on by default, because the boost is a speed advantage) keeps a run in which a boost fired off the Steam leaderboards; the mark is saved in the config (`ExitBoost.UsedThisRun`), so a quit and continue keeps it.

**Display mode** (multiplayer with `Scoring.InMultiplayer` off, or if native setup fails; 0.6.0: multiplayer counts by default): corners are scored and shown on the HUD card only, and nothing is added to the game.

## The line on the road (F5)

F5 lays the racing line **on the road surface ahead of you**, like the driving line in Forza or The Crew: a ribbon of chevrons (`LineWidth`, 1 m) up to `DrawAhead` (150 m) ahead, following the road's height. Its colour tells you what to do at each point at your current speed:

| Colour | Means |
|---|---|
| green | on pace: you can carry your speed there |
| amber | lift: you'll need some braking to make that point's reference speed |
| red | brake: you need hard braking (or more than the car has) to make it |
| orange | the line goes around a traffic car there |
| faint | no way past traffic there (counts as perfect position) |

The colour is physics, not a guess: from your speed v, the reference speed there v_ref and the distance d, the braking needed is (v² − v_ref²) / 2d, compared with `BrakeDecel`. Chevrons are fixed to the road (they don't slide with the car); the ribbon fades in just ahead of the car and out at the far end.

A **HUD card** (bottom-left, shown with the line) gives the live corner points (green, counting up) or the race total, the last corner's grade (GOLD / SILVER / BRONZE pill, GRIP LINE tag), the streak, and a meter of where you are against the line (green band = full credit, dot = you). The old text readout is still there with `DebugText`.

Per frame the ribbon's vertices, colours and UVs are computed with plain C# on struct fields (`Shared/FastMath.cs`):
Unity's `Mathf`, `Color.Lerp` and even `new Vector3(...)` are slow interop calls in this IL2CPP game, and this ran about
12 of them per sample. The IMGUI fallback skips Unity's Layout pass (`useGUILayout = false`).

The ribbon is one mesh of ours drawn with the game's own URP Particles/Unlit shader (alpha-blended, vertex-coloured); if that shader isn't loaded, the old dot preview comes back. The card is uGUI with TextMeshPro in the game's HUD font.

## Sharing the line with other plugins

When a line is built, RacingLine publishes it as AppDomain data `rogue.racingline` (`LineShare.cs`), the same way
HeadLook shares its head angle with DriverCam. It is an `object[]`:
`{ int version, long pathPtr, float step, int n, float limit, float[] e, float[] curvature }`. `e` is the line's offset
from the centre per sample, in metres, + = right. That is the same frame as a traffic car's lane offset, and sample i
is i × step metres along the run's path. `curvature` is the racing line's own curvature (1/m, + = turning right). The
value is `null` while there is no line. The Police plugin's daredevils race this line. Nothing is published to other
players.

## Safety

- **No Harmony patches.** Every game member is checked by name at startup (`GameApi.Check`), and each feature switches off alone if one is missing.
- **Engine calls.** All of them were checked against the Il2Cpp dump, because some Unity methods are stripped in this build (for example `GUI.DrawTexture`).
- **Failure switches.** Scoring, the traffic-aware line, the native category, the results row and the Victory row each switch themselves off after an error. 5 errors in 10 s switches the whole plugin off; the game keeps running.
- **Rows.** A row is only removed once its screen has closed, so the results screen's animation always finishes (also while the plugin is off or switched off). Unloading the plugin removes both rows at once (accepted: an unload during the results screen's animation can leave it without a Continue button; at game quit that doesn't matter).
- **Respawns and jumps.** A jump in distance along the road (back more than 5 m or forward more than 50 m in one frame) drops the corner in progress. Distance is tracked while airborne or not in control too, so a long jump isn't mistaken for a teleport; only frames on the ground and in control score.
- **Old copies are never destroyed.** Game card effects can keep references to score categories for a whole run, so a destroyed copy would break the game's own code. If the score manager is rebuilt each level, that leaves one tiny unused object per level, which is harmless.
- **Saves stay compatible both ways.** The id is unique, and unknown ids are skipped on load.

## What to check in the log (`/game-log RacingLine`)

| Line | Tells you |
|---|---|
| `game check OK` | nothing the plugin reads is missing |
| `line built: ...; N corners (a onto straights, c linked)` | line, corners and types for this race |
| `ground line ready (transparent material, shader '...')` | the line drawn on the road is set up (`opaque fallback` if no transparent material could be made) |
| `Racing Line added as a score category (... Top Speed template ...)` | native mode is on |
| `corner 12A: SILVER grip q 0.68 exit 0.74 full-throttle 0.9 s coast 0.0 s -> 214 pts ...` | per corner, with `LogCorners` on (off by default); `traffic` after the grip/hit flags = traffic moved or blocked the line in that corner |
| `road width not known yet, waiting` | the road's width hasn't been read yet, so the line isn't built (logged once) |
| `path gone (menu or loading): line dropped` | the road path disappeared (back to a menu, or a load), so the line was dropped |
| `path grew in place: N m -> N m (same object), rebuilding; the current line stays up until then` | the game extended the road path while you drove; the line is rebuilt longer |
| `traffic: 9 cars near the player; nearest +42 m along the road, lane -1.7 m (player lane 1.6 m, + = right), 2.1 x 4.6 m, 18 m/s; ...` | once per race, the first traffic snapshot. Check it against what you see: a car ahead in the lane to your left should show a positive distance and a lane below yours |
| `traffic: line shifted in 3 of 14 corners, 5 clean passes` | per race, when its results screen opens (traffic-aware line on) |
| `traffic line switched off for this session after an error: ...` | the traffic-aware line hit an error; scoring carries on with the plain line |
| `results row added: 01:12, 2310 pts, 130 coins` | the results screen got its row |
| `live scoring: 18 live actions, 2140 pts counted live (game total 2310, includes card multipliers); 24 corners: gold 9, ...` | per race: confirms points were counted live through the game's action path |
| `victory screen found: score controller on '.../Victory Screen Panel/[CONTROLLERS]' (active True), results window active False` | once per session: the Victory screen was found (where its score controller sits) |
| `victory row added: 12,345 (new record)` | the end-of-run Victory screen got its row (`victory row refreshed: ...` if it was still there) |
| `exit boost: the game's drift-end boost is ...` | once per session: the game's own drift-end boost values (our boost copies its neutral fields) |
| `exit boost: corner 3L GOLD grip -> top speed +5%, acceleration +25% for 1.5 s` | each boost, with `LogCorners` |
| `leaderboard upload skipped: this run used the clean-exit boost` | with `ExitBoost.KeepOffLeaderboards` |
| `exit boost stays off: the leaderboard guard isn't installed and KeepOffLeaderboards is on` | once: the guard couldn't be installed, so no boost while `KeepOffLeaderboards` is on |
| `victory row skipped: <why>` | once per Victory screen with no row: display mode, run total 0, or the Near Miss row couldn't be copied |
| `victory screen already open when first watched: no row this time` | the Victory screen was already up when the plugin found it (or started ticking again): no row and nothing counted, so a stale screen never counts the current race |
| `new run: run total reset (previous run 12345)` | the first race of a new run: the Victory screen's run total starts again from 0 |
| `run position never read for the last race: run total kept` | the run position couldn't be read for a whole race, so the run total was carried over instead of reset |

## Controls

| Key | Does |
|---|---|
| F5 | show / hide the line on the road (coloured green / amber / red by pace, orange around traffic) and the HUD card |

## Settings (`rogue.racingline.cfg`)

| Section | Keys (defaults) |
|---|---|
| General | `Enabled` (true) |
| Preview | `ShowLine` (false), `DrawAhead` (150), `LineWidth` (1), `DebugText` (false) |
| Line | `Margin` (1.5), `SampleStep` (2.5), `FrameBudgetMs` (1) |
| Scoring | `NativeCategory` (true), `InMultiplayer` (true: points count in multiplayer too, your own score), `CoinReward` (130), `CoinTargetPerCorner` (0.6), `CornerMinRadius` (300), `LogCorners` (false) |
| ExitBoost | `Enabled` (true), `TopSpeed` (0.05), `Acceleration` (0.25), `Seconds` (1.5), `KeepOffLeaderboards` (true), `UsedThisRun` (written by the plugin) |
| Quality | `LineFull` (2.5), `LineZero` (8), `LineFloor` (0.25), `WeightSpeed` (0.45), `WeightGrip` (0.25), `WeightPedals` (0.30), `PointsPerMetre` (0.22), `TickMinQ` (0.3), `LiveGrace` (0.6), `DriftFactor` (0.5), `TrafficGrace` (1.5) |
| Bonuses | `ExitWeight` (0.5), `Clean` (1.15), `GripLine` (2), `CoastPerSecond` (0.15), `CoastFloor` (0.6), `Gold` / `Silver` / `Bronze` (0.8 / 0.6 / 0.4), `StreakStep` (0.15), `StreakMax` (2) |
| Car | `GripStart` (9 m/s²), `BrakeDecel` (10), `AccelRate` (5) |
| Icons | `HudIcon` / `StatIcon` (PNG names in `plugins/RacingLine/`; missing = built-in placeholder) |
| Records | `BestRunTotal` (0; written by the plugin: the best Racing Line run total, for the Victory screen's NEW RECORD) |
| Traffic | `Enabled` (true), `Margin` (0.5), `PlayerHalfWidth` (1.0), `LookAhead` (150), `MinLeadIn` (15), `MaxLeadIn` (60), `LeadInSeconds` (1.2), `LeadOut` (15) |

v1 keys (`Band`, `Core`, `Grace`, ...) and 0.2 keys (`TickPopups`, `TickInterval`) are no longer used. They may stay in an old config file harmlessly.

## Files

| File | Job |
|---|---|
| `Plugin.cs` | config, startup check, starts the runner |
| `GameApi.cs` (+ `.Player`, `.Native`, `.Traffic`) | the only plugin files that touch game types (`.Traffic`: the NPC car snapshot, with the sign/frame evidence) |
| `../Shared/ScoreRows.cs` | shared with Police: the rows on the results and Victory screens (RACING LINE rank 1, PURSUIT rank 2) |
| `TrafficLine.cs` | the traffic-aware line: detours around traffic, blocked stretches, clean passes (plain .NET, tested outside the game) |
| `Net.cs` | offline / host / client role from Mirror |
| `LineBuilder.cs` / `LineSolver.cs` | sampling and the two-level line solve |
| `Corners.cs` / `SpeedProfile.cs` / `LineScorer.cs` | corners and zones, reference speed, the v2 rules (plain .NET, tested outside the game) |
| `Icons.cs` | PNG icons or the built-in placeholder glyph |
| `GroundLine.cs` | the line drawn on the road (one dynamic mesh, coloured per point) |
| `LineHud.cs` | the HUD card (uGUI) |
| `../Shared/Fx.cs`, `../Shared/UiKit.cs` | shared with Police: the transparent material and procedural textures; the uGUI toolkit (panels, shadows, game font) |
| `LineShare.cs` | publishes the finished line as AppDomain data `rogue.racingline` (the Police plugin's daredevils race it) |
| `Runner.cs` | path watch, build steps, input smoothing, scoring, the live action, run total and row data, line and card, failure switches |
