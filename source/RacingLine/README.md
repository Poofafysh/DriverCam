# RacingLine

BepInEx 6 IL2CPP plugin for **Driving Rogue**: a new score category, **Racing Line**, that rewards driving corners well on grip: the right line, good speed, braking straight, lifting in and powering out.

Current version: **0.1.1** (v2 scoring; placeholder icons)

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
5. **Points.**
   - **Running points:** `PointsPerMetre` × q per metre, halved while drifting. They're paid as **combo ticks** every 0.5 s while q ≥ `TickMinQ`. Ticks keep the game's combo alive through grip corners; they don't pop up by default.
   - **At the zone exit**, one popup pays the rest. The corner total is running × (1 + 0.5 × exit) × coasting × clean × Grip line:
     - **exit** is early full throttle plus exit speed, times the corner type factor
     - **coasting** is −15% per second coasting *after* the apex (floor 0.6)
     - **clean** is ×1.15 with no collision
     - **Grip line** is ×2 with no drift anywhere in the zone
   - **Grade by mean q:** GOLD ≥ 0.8, SILVER ≥ 0.6, BRONZE ≥ 0.4.
   - **Streak:** +0.15× per corner with q ≥ 0.45, up to ×2. A collision drops it two steps, and a corner under 0.3 resets it.
   - **Pace:** every payout × (0.8 + 0.4 × your average speed ÷ reference).
   - **Traffic grace:** for 1.5 s after a near miss, the position term holds, so dodging isn't punished.
6. **Coins.** Corner grades add units (GOLD 1, SILVER 0.6, BRONZE 0.3, ×1.5 Grip line) toward a target of `CoinTargetPerCorner` × the race's corners. A steady SILVER run earns the full `CoinReward` (130), like maxing any other category.

A simulated 3 km test road with 8 long corners gave these totals:

| Driving | Points |
|---|---|
| Tidy grip | 2,194 |
| Coasting out of every apex | 1,521 |
| Drifting every corner (drift points come separately) | 796 |
| Wide and slow | 531 |

## Native category

**Single-player:** Racing Line is a real score category. It's an **inert copy of the game's Top Speed category**:
- its own logic can never start (`timeThreshold` 1e9, `targetSpeedFactor` 2, `scoreMultiplier` 0)
- it has id `rogue.racingline`, our name and icons, and `contributeToCombo` on
- it's appended once to the level's score list

Points go through the game's own `AddToScore`. The end-of-run **Victory screen** also gets a RACING LINE row (a clone of the Near Miss row, placed right after it) showing the run's Racing Line total. Each race's score is added once when its results screen opens. A run total higher than `Records.BestRunTotal` shows NEW RECORD. The total lives in this game session only: a run continued from a save only counts races played since launch. The combo event fires whatever the popup flag says, which was checked in `AddToScore` (0x1806EC990). A RACING LINE row is added to the results screen.

Accepted side effects:
- **Top Speed cards also affect Racing Line**, because the copy reports type Top Speed.
- Racing Line points are part of the single-player run total the game uploads to its Steam leaderboard.

**Display mode** (any multiplayer session, or if native setup fails): corners are scored and shown in the readout only, and nothing is added to the game.

## Safety

- **No Harmony patches.** Every game member is checked by name at startup (`GameApi.Check`), and each feature switches off alone if one is missing.
- **Engine calls.** All of them were checked against the Il2Cpp dump, because some Unity methods are stripped in this build (for example `GUI.DrawTexture`).
- **Failure switches.** Scoring, the native category and the results row each switch themselves off after an error. 5 errors in 10 s switches the whole plugin off; the game keeps running.
- **Results row.** It's only removed once the results screen has closed, so the screen's animation always finishes.
- **Respawns and jumps.** A jump in distance along the road (back more than 5 m or forward more than 50 m in one frame) drops the corner in progress. Distance is tracked while airborne or not in control too, so a long jump isn't mistaken for a teleport; only frames on the ground and in control score.
- **Old copies are never destroyed.** Game card effects can keep references to score categories for a whole run, so a destroyed copy would break the game's own code. If the score manager is rebuilt each level, that leaves one tiny unused object per level, which is harmless.
- **Saves stay compatible both ways.** The id is unique, and unknown ids are skipped on load.

## What to check in the log (`/game-log RacingLine`)

| Line | Tells you |
|---|---|
| `game check OK` | nothing the plugin reads is missing |
| `line built: ...; N corners (a onto straights, c linked)` | line, corners and types for this race |
| `Racing Line added as a score category (... Top Speed template ...)` | native mode is on |
| `corner 12A: SILVER grip q 0.68 exit 0.74 full-throttle 0.9 s coast 0.0 s -> 214 pts ...` | per corner, with `LogCorners` on (off by default) |
| `results row added: 01:12, 2310 pts, 130 coins` | the results screen got its row |
| `victory row added: 12,345 (new record)` | the end-of-run Victory screen got its row |

## Controls

| Key | Does |
|---|---|
| F5 | show / hide the line preview and the live readout (q, position, speed, pedals, streak, pace) |

## Settings (`rogue.racingline.cfg`)

| Section | Keys (defaults) |
|---|---|
| General | `Enabled` (true) |
| Preview | `ShowLine` (false), `DrawAhead` (150) |
| Line | `Margin` (1.5), `SampleStep` (2.5), `FrameBudgetMs` (1) |
| Scoring | `NativeCategory` (true), `CoinReward` (130), `CoinTargetPerCorner` (0.6), `TickPopups` (false), `CornerMinRadius` (300), `LogCorners` (false) |
| Quality | `LineFull` (2.5), `LineZero` (8), `LineFloor` (0.25), `WeightSpeed` (0.45), `WeightGrip` (0.25), `WeightPedals` (0.30), `PointsPerMetre` (0.22), `TickInterval` (0.5), `TickMinQ` (0.3), `DriftFactor` (0.5), `TrafficGrace` (1.5) |
| Bonuses | `ExitWeight` (0.5), `Clean` (1.15), `GripLine` (2), `CoastPerSecond` (0.15), `CoastFloor` (0.6), `Gold` / `Silver` / `Bronze` (0.8 / 0.6 / 0.4), `StreakStep` (0.15), `StreakMax` (2) |
| Car | `GripStart` (9 m/s²), `BrakeDecel` (10), `AccelRate` (5) |
| Icons | `HudIcon` / `StatIcon` (PNG names in `plugins/RacingLine/`; missing = built-in placeholder) |
| Records | `BestRunTotal` (0; written by the plugin: the best Racing Line run total, for the Victory screen's NEW RECORD) |

v1 keys (`Band`, `Core`, `Grace`, ...) are no longer used. They may stay in an old config file harmlessly.

## Files

| File | Job |
|---|---|
| `Plugin.cs` | config, startup check, starts the runner |
| `GameApi.cs` (+ `.Player`, `.Native`, `.Results`, `.Victory`) | the only files that touch game types (`.Victory`: the Victory screen row) |
| `Net.cs` | offline / host / client role from Mirror |
| `LineBuilder.cs` / `LineSolver.cs` | sampling and the two-level line solve |
| `Corners.cs` / `SpeedProfile.cs` / `LineScorer.cs` | corners and zones, reference speed, the v2 rules (plain .NET, tested outside the game) |
| `Icons.cs` | PNG icons or the built-in placeholder glyph |
| `Runner.cs` | path watch, build steps, input smoothing, scoring, payouts, results row, preview, failure switches |
