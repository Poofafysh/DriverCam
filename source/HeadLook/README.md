# HeadLook

BepInEx 6 IL2CPP plugin for **Driving Rogue**: look around like turning your head in real life, up, down, left and
right, and spring back to centre when you let go.

Current version: **0.1.1**

## Controls

| Input | Does |
|---|---|
| Right stick (controller) | absolute: full stick = full head turn (100° left / right, 35° up, 25° down). The stick press stays the game's (ability / honk) |
| Hold right mouse + move (keyboard and mouse) | look around while held; released = back to centre |

## What it does in each view

- **Driver view (DriverCam):** the driver's head turns at the eye. DriverCam 0.9.3+ reads HeadLook's head angle and adds
  it to its own eye direction, so the cockpit, mirrors and car stay exactly where they are.
- **Hood view:** the camera turns in place.
- **Chase views:** the camera orbits around the car for left / right, so you can look at the sides and behind, and
  looks up / down in place (the orbit never dips under the road). `ChaseOrbit` off = turn in place like the hood view.

While driving, the game doesn't use the right stick's axes (only its press, for the ability / honk); the game's menus
use it to scroll and Photo Mode to move its camera. While the game is paused (time scale 0: pause and card menus)
HeadLook ignores input and the head returns to centre. In DriverCam's Edit mode (L3 + R3) the right stick edits the
seat and parts, so the driver's head doesn't turn there.

## How it works (and how it's undone)

- The head turn is applied right before each frame is drawn (`Application.onBeforeRender`) and the camera is put back
  before the game's own camera code runs again (Harmony prefixes on `CameraControllerInGame.Update`, `LateUpdate` and
  `FixedUpdate`). The game's camera smoothing never sees the offset, so letting go always returns to the game's exact
  view.
- The head angle is published for DriverCam as AppDomain data `rogue.headlook` (`float[3]`: yaw, pitch, running). There
  is no assembly reference either way: either plugin works without the other.
- 0.1.1: the per-frame maths uses plain C# (`Shared/FastMath.cs`) instead of Unity's `Mathf`, which is a slow interop
  call in this IL2CPP game. Same results.
- Camera only: nothing about the car or the game state is changed. 3 errors switch HeadLook off for the session and give
  the camera back.

## Settings (`rogue.headlook.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch |
| `Input.RightStick` | true | the right stick turns your head |
| `Input.RightMouse` | true | hold the right mouse button and move to look |
| `Input.MouseSensitivity` | 0.15 | degrees per pixel (0.02-1) |
| `Input.Deadzone` | 0.15 | right-stick deadzone (0-0.5) |
| `Input.InvertY` | false | invert up / down |
| `Look.MaxYaw` | 100 | furthest turn left / right, degrees (10-170) |
| `Look.MaxUp` / `MaxDown` | 35 / 25 | furthest look up / down, degrees (0-80) |
| `Look.Speed` | 12 | how quickly the head follows and springs back (2-40) |
| `Look.ChaseOrbit` | true | chase views orbit the car (off = turn in place) |

## Log (`/game-log HeadLook`)

- `HeadLook 0.1.1 loaded. Right stick, or hold the right mouse button, to look around.`, `[HeadLook] game check OK: camera controller`, `camera hooks installed`.
- `[HeadLook] camera mode '<id>' -> Hood / Chase / Driver / Other` each time the camera mode changes.
