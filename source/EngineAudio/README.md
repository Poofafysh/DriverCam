# EngineAudio

BepInEx 6 IL2CPP plugin for **Driving Rogue**: a realistic engine sound made from the game's own engine recordings,
played from a simulated RPM that follows the game's gearbox and your throttle.

Current version: **0.2.0**

Design doc (claude.ai): "Engine Audio - Realistic Engine Sound" (`391e5ae4-7e88-410a-a632-30b562d13148`).

## Why

The game plays each car's recorded rev sweeps (4 rev-up, 3 rev-down, 1 idle loop per engine: muscle, V8, inline 4,
inline 6, V12, RX7) back to back on a timer, at a fixed pitch. The sound has no link to your speed, gear or how hard you
press the throttle, the last sweep restarts from low revs every 10-15 s at top speed, and every change fades through
near-silence.

## What it does

- **A simulated engine:** RPM follows the game's own 5-gear gearbox (`VehicleGearboxHandler`): it climbs through each
  gear, drops to 58% of redline on every upshift, blips on downshifts, revs when you press the throttle standing still,
  and bounces off a rev limiter if you hold the top of a lower gear. Idle 900, redline 7,500 by default.
- **Top speed, where a race is mostly spent:** flat out in the last gear at the car's top speed, the engine holds a
  steady high note at `TopSpeedRpm` (93% of redline) instead of banging off the limiter: the car is at its speed limit,
  not the engine at its rev limit. The note wanders by about ±1% (a new random drift every 0.4-1.1 s) so it lives.
  While the RPM is steady, the recording is played as longer grains (each starts at a random point up to 0.32 s before
  the RPM's spot and plays forward, crossfading over 0.16 s; less on short clips) instead of re-grabbing the same 0.11 s
  ten times a second, so a held note keeps its texture instead of buzzing. Steady mode starts when the RPM's spot moves
  slower than 0.25 s of recording per second and only ends above 0.55, so the top-speed wander practically never flips
  it back (and the pitch eases if it does).
- **Drift flare:** drifting on the throttle flares the revs by up to 6% of redline (the driven wheels spin up).
- **Tyre squeal:** drifting or cornering hard squeals, layered over the game's own drift sound (a soft rubber hiss,
  which stays). The squeal is synthesized in the background when the plugin loads (about 50 ms of work, off the main
  thread). It is not a recording, and nothing from other games is used. Each clip is a bright tonal whine with harmonics, a stick-slip roughness and a little hiss: two 2 s loops
  (960 Hz and 1240 Hz) and three chirps.
  - What drives it is your **slip angle**: where the visible body points against where it is really going. The
    game's drift state also counts, and hard cornering without a drift squeals a little.
  - Small angles play the lower squeal and big angles blend into the higher one. The pitch rises with the angle and
    your speed.
  - A chirp plays when a drift starts, when the car flicks the other way, and when the tyres let go.
  - It is silent in the air and below about 20 km/h. It plays on the game's drift-sound mixer group, so the
    sound-effects volume applies.
- **The recordings follow the RPM:** the gear's rev-up sweep (on throttle) and rev-down sweep (off throttle) are played
  at the position that matches the RPM, held there as short overlapping grains when the RPM is steady, with a small
  pitch correction between grains. Throttle (eased over about 0.1 s, so a keyboard's on/off becomes a short blend)
  mixes on-load and off-load with an equal-power crossfade, the idle loop
  takes over near a standstill (its pitch follows the RPM), and the RX7's blow-off plays on a hard upshift. The game's
  boost pitch carries over.
- **Traffic:** each traffic car's engine loop now changes pitch with its own speed through four simple gears, with a
  small fixed difference per car, and starts its loop at a random point.

**F1** switches between EngineAudio and the game's own engine sound for the current session, to compare.

## How it works (and how it's undone)

- The game's engine code keeps running untouched. EngineAudio only sets `AudioSource.mute` on the car's 12 engine
  sources (no game code ever writes `mute`) and plays the same clips on 7 AudioSources of its own, on a child object of
  the car's engine-sound object, routed to the same mixer group (Engine, so the game's engine volume setting applies).
- Switching off (F1, config, an error, a new car, plugin unload) unmutes every source it muted and destroys its own
  (the squeal's three sources live on the same object). 3 errors switch it off for the session; a squeal that can't be
  built switches only the squeal off. F1 switches the squeal off with the engine (EngineAudio vs the game's own sound).
- It follows the game's own engine on / off: while the game is paused (time scale 0) or has stopped its engine sound
  (tutorial steps, setup), EngineAudio is silent too, and it comes back when the game's engine does.
- Traffic: a Harmony postfix on `AIVehicleSoundHandler.HandleSFXs` (the game's per-frame traffic sound update) sets
  pitch and volume; the game rewrites pitch every frame itself, and the volume each source had before is written back
  when it's turned off. A postfix on `SetupEngineSound` picks a random start point.
- Sound only: nothing about the car's physics, gearbox, inputs or the network is written. It works the same in
  multiplayer (your own car's sound, locally).

## Settings (`rogue.engineaudio.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch |
| `General.ToggleKey` | F1 | EngineAudio / the game's own engine sound, this session |
| `Engine.IdleRpm` | 900 | simulated idle (500-2000) |
| `Engine.RedlineRpm` | 7500 | simulated redline (idle + 2000 to 12000); upshifts drop to 58% of it |
| `Engine.Limiter` | true | rev limiter at the top of a lower gear at full throttle (never in the last gear) |
| `Engine.TopSpeedRpm` | 0.93 | where the engine sits at top speed in the last gear, share of redline (0.7-0.99) |
| `Engine.DriftFlare` | true | revs flare up when you drift on the throttle |
| `Tires.Enabled` | true | tyre squeal when drifting / cornering hard (read live) |
| `Tires.Volume` | 0.5 | tyre squeal volume (0-2); the game's sound-effects volume applies on top |
| `Engine.Volume` | 1 | relative to the car's own engine volume (0-2) |
| `Traffic.Enabled` | true | speed-following traffic engine pitch |
| `Debug.Overlay` | false | one-line readout: RPM, gear, span, throttle, active recordings, slip angle, squeal level |

## Log (`/game-log EngineAudio`)

- `[EngineAudio] game check OK: player engine, gearbox, tyres, traffic engines` and `traffic engines patched`.
- `[EngineAudio] engine voices ready: rev-up <clips>; rev-down <clips>; blow-offs N; the game's gearbox, mixer group
  'Engine', clip load type ...; tyre squeal on (mixer group '<the drift sound's group>')` once per car (or `tyre squeal
  off (<why>)`, or `clips still being made` followed a moment later by `[EngineAudio] tyre squeal on (...)`).
- `[EngineAudio] engine sound handed back to the game (<why>)` when it lets go.

## Known limits

- Built from the game's own recordings only (no sounds from other games can be added to this repo). A later phase can
  load engine loops you supply yourself.
- AI racers and multiplayer decoys keep the game's engine logic.
- How smooth the grains sound depends on how the clips are loaded (logged as "clip load type"); streaming clips seek
  less cleanly than decompressed ones.
