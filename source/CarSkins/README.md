# CarSkins

BepInEx 6 IL2CPP plugin for **Driving Rogue**: draws one of your cars as a different model. It's cosmetic only and
single-player only. Physics, the hit box, stats and scoring are the game's.

Current version: **0.1.0**

## What it does

- **Saber → BMW E46 (1998):** the first skin. The model is a free low-poly BMW E46 from Sketchfab (CC Attribution,
  see Credits), decimated in Blender to **4,920 triangles**: body 3,300, glass 300, wheels 4 × 330. Its base-colour
  texture is cut to 1024 px.
  - `Assets/build_e46.py` rebuilds it from the download:
    ```
    blender -b --factory-startup --python build_e46.py -- <BMW_E46.obj> <textures dir> <out dir> [preview.png]
    ```
- **Fitted to the game car:** the model is scaled so its wheelbase matches the Saber's (the game's four wheel spin
  pivots), then placed so its front axle sits on the game's front axle.
  - **The body** hangs off the node that holds the game's body mesh, so it follows suspension, body roll and the drift
    turn.
  - **The wheels** hang off the game's wheel spin pivots, so they spin and steer with the real wheels.
- **The game's own car is hidden, not removed.** Only the meshes under the skin's `Content/Body` and `Content/Wheels`
  are hidden, never effects, shields, slipstream, text or icons. They're hidden with `Renderer.forceRenderingOff`, so
  their `enabled` flag is untouched. DriverCam's car-body measurement and cockpit fit therefore see the same car as
  before.
  - It's re-checked twice a second, so a new body-kit part is hidden too.
- **Everything is given back:** the game's meshes are shown again and the skin removed when you drive another car,
  switch it off, play multiplayer, or after an error (3 errors switch CarSkins off for the session).
- Police and daredevil boss looks copy the game's own prefab and never get the skin.

## Settings (`rogue.carskins.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | draw the car as the replacement model |
| `General.Car` | Saber | which car gets the skin (the game's car name) |
| `General.Model` | BMW_E46 | a `.csm` file in `BepInEx/plugins/CarSkins` (no extension) |

## What to check in the log (`/game-log CarSkins`)

- `[CarSkins] game check OK: player car, car name, skin holder, game mode`, then `CarSkins 0.1.0 loaded: Saber drawn as BMW_E46 (single-player).`
- `[CarSkins] model BMW_E46 loaded: N triangles, 4 wheels, W x H x L m` (once).
- `[CarSkins] Saber drawn as BMW_E46: scale S (wheelbase G m vs M m), body under 'Body'` each time you drive the Saber.
- `[CarSkins] the game's car is back (<why>)` when it lets go.
- `couldn't find its wheels (N/4 spin pivots) or body node` or `odd wheelbase` mean the skin couldn't be fitted; the
  game's car stays.

## Known limits

- The game's black cartoon outline isn't drawn on the skin.
- The wheels sit on the game's own track (about ±1.0 m), a little wider than the E46's arches (about ±0.89 m), so
  they stick out by roughly 10 cm.
- DriverCam's `HideCarBody` hides the skin too (it follows the game body's on/off switch).
- DriverCam and CurbFeel measure every renderer under the car's body, ours included. The scaled E46 fits inside the
  Saber's measured box, so nothing changes today, but a bigger skin could shift the cockpit fit.
- In DriverCam's driver view, the skin's body surrounds the camera the way the Saber's did. The cockpit is fitted to
  the Saber, so check that it still lines up with the E46's windows.
- Only the base-colour texture is used (no normal or roughness maps), to keep it light.

## Credits

- **BMW E46 1998 low-poly car**, by **TODO: author name** on Sketchfab (**TODO: model link**), licensed
  [CC Attribution](https://creativecommons.org/licenses/by/4.0/). Decimated and re-textured at 1024 px for this plugin.
