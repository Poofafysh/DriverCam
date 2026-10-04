# PitStop

BepInEx 6 IL2CPP plugin for **Driving Rogue**: press **F2** to refill your car's health.

Current version: **0.3.0**

In **Rogue Hub** (0.2.0): a "Refill health" button (also in the quick menu by default), a status line (ready / this run is off the leaderboards), and each refill's result (or the reason it was refused) as a notification. The cooldown is limited to 0-120 s. Pressed in the hub while the race is paused, the refill is queued and happens when the race resumes (PitStop never changes the game while it's paused); F2 does nothing while paused.

## What it does

- **F2** (`RefillKey`) fills your car's health to 100% through the game's own heal, the same one its repair pickups
  use (`VehicleHealth.Heal(100%, forceFinalValue)`): the health bar updates, the heal sound plays and the game's own
  heal event fires. If a card scales heals down, PitStop tops it up to full anyway (`SetHealthFactor(1)`).
- Only in a race, and never on a car that's already wrecked (it doesn't revive). In multiplayer (0.3.0) it refills
  your own car only; the game sends your car's health to the other players itself (everyone should run the same build).
  The log says why when it does nothing (`[PitStop] health already full`, ...).
- `Cooldown` (0 = any time) limits how often it works.

## This is a gameplay change (a cheat), kept off the leaderboards

Refilling health makes runs easier, so **a run in which F2 refilled health is never uploaded to the game's Steam
leaderboards** (decided 2026-10-03). Every leaderboard upload goes through one game method,
`LeaderboardsManager.PublishEntry` (called by `LeaderboardSO.PublishScore`, which the game's run-end updaters use: total
score, full-run time, completed runs, completed races, cards used). PitStop puts a Harmony prefix on it that skips the
upload while the current run is marked:

- The first refill of a run marks it (`Leaderboard.RefilledThisRun = true`, saved in the config, so quitting and
  continuing the run keeps the mark).
- The mark clears when a new run starts (a new player car at stage 0, race 0). Runs without a refill upload as normal.
- If the patch can't be installed (e.g. after a game update), **F2 does nothing**: a refill can never reach the
  leaderboard.

PitStop sends nothing to other players itself: in multiplayer the game's own health sync carries your refilled health.

## Settings (`rogue.pitstop.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch |
| `General.RefillKey` | F2 | Input System key name |
| `General.Cooldown` | 0 | seconds before the key works again |
| `Leaderboard.RefilledThisRun` | false | written by the plugin: true from a run's first refill until the next run starts (that run's leaderboard uploads are skipped) |

## Log (`/game-log PitStop`)

- `[PitStop] leaderboard guard installed (LeaderboardsManager.PublishEntry)` at startup, then `PitStop 0.3.0 loaded. F2 refills your car's health (also your own car in multiplayer; a run with a refill isn't uploaded to the leaderboards).` (with ` The current run had a refill: its leaderboard upload stays blocked until a new run starts.` added if the saved flag is still set).
- `[PitStop] health refilled: 34% -> 100%; this run won't be uploaded to the leaderboards`, or why it didn't (`health already full`, `no player car`, `race already
  over`, `the car is already wrecked`); in multiplayer the refill line ends with `(multiplayer: your car)`.
- `[PitStop] leaderboard upload skipped: this run used a health refill` (once per leaderboard at the run's end) and
  `[PitStop] new run: leaderboard uploads allowed again`.
- `[PitStop] game check: missing ...` / `the refill key stays off (...)`: a game update removed something it needs.

No other plugin patches `LeaderboardsManager`.
