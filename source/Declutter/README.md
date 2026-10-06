# Declutter

BepInEx 6 IL2CPP plugin for **Driving Rogue**: hides the scenery you can't really see, to cut clutter and draw calls.

Current version: **0.2.0**

## What it does

- **Every race.** Works in single-player and multiplayer, because it's purely cosmetic and local: nothing that
  collides changes, and only what your own screen draws. It stands aside in Sandbox races, since Sandbox strips those
  maps itself.
- **Per road tile, once it has loaded.** Each renderer under the tile's `Biomes` group is hidden when:
  - it's small (largest side under `SmallSize`, 2 m) and more than `SmallDistance` (40 m) from the road; or
  - it's more than `FarDistance` (120 m) from the road.
- **"From the road"** is the gap between the renderer's bounds and the tile's road path (its `PathWaypoints`, sampled
  every 5 m or less).
- **Never hidden:** anything on the Street, Guardrail or weather layers, and road, sidewalk, curb, barrier, wall,
  tunnel, bridge and ramp parts (by their own or a parent's name).
- **How it hides:** `Renderer.forceRenderingOff`, so `enabled` stays as the game set it (CurbFeel, DriverCam and
  LODGroups see the same renderer). Switched off, after an error, or in a Sandbox race, everything it hid is shown
  again.
- **No damage ghost (0.2.0):** the see-through copy of your car the game flashes when you're hit (its GlitchFX "ghost
  phasing" effect) is hidden with `forceRenderingOff`, in every race including Sandbox. `[Effects] HideDamageGhost`
  (default on) shows it again at once.
- **Cost:** a newly loaded tile is sorted a slice per frame, within `BudgetMs` (1.5 ms). After that it costs nothing
  per frame. Nothing runs while paused.

## Settings (`rogue.declutter.cfg`)

| Setting | Default | Meaning |
|---|---|---|
| `General.Enabled` | true | On / off (applies at once) |
| `Rules.SmallSize` | 2 | Props whose largest side is under this many metres count as small (0.5-6) |
| `Rules.SmallDistance` | 40 | Small props further than this from the road are hidden, m (10-150) |
| `Rules.FarDistance` | 120 | Anything further than this from the road is hidden, m (40-400) |
| `Effects.HideDamageGhost` | true | Hide the see-through copy of your car the game flashes when you're hit (applies at once) |
| `Tuning.BudgetMs` | 1.5 | Time per frame for sorting a new tile, ms (0.5-10) |

The rules apply to tiles loaded after a change (the next race). Rogue Hub shows them all, with a live status line.

## Log (`/game-log Declutter`)

- `Declutter 0.1.0 loaded: hiding props under 2 m beyond 40 m and everything beyond 120 m from the road.`
- Per tile: `[Declutter] tile <scene>: N renderers hidden (F far from the road, S small props)`.
- `[Declutter] N renderers shown again in T tiles (switched off | Sandbox race | errors | plugin unloaded)`.
- `[Declutter] damage ghost hidden: N renderer(s) of the car's glitch effect` (once per car), and
  `[Declutter] damage ghost shown again (N renderer(s); switched off | errors | plugin unloaded)`.
- Trouble: `[Declutter] tile <scene> left as it is after an error: ...`, `[Declutter] error (n/3): ...`, then
  `[Declutter] switched off for this session after repeated errors (everything is shown again)`.

## Limits

- **Not tested in game yet.** The 40 m / 120 m rules may hide a tall landmark you'd see from far away; raise
  `FarDistance` if the skyline looks empty.
- Tiles without a road path are left alone.
- Distance is measured to the tile's own road path only, so at a corner a prop near the next tile's road can be hidden.
- A tile is sorted only once the game has placed it and it has stood still for about 2 s. A tile that moves later
  shows everything again and is sorted again (log: `tile <scene> moved after sorting started ...`).
- It steps aside for a whole Sandbox run, from the menu on (reads `Sandbox.Plugin.Active` and `InSandboxRun`).
