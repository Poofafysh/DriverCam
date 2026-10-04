# Driver

BepInEx 6 IL2CPP plugin for **Driving Rogue**: a 3D racing driver sits in your car. The driver's hands hold
DriverCam's steering wheel and turn it with you, the right foot works the pedal, and the head follows HeadLook. In
DriverCam's driver view you look down at your own body, arms and gloves on the wheel.

Current version: **0.1.0**.

## What it changes

Only how the car looks. Driver reads the camera mode, the car body and your steering, throttle and brake inputs, and
draws a model. It never writes to the game: no Harmony patches, nothing networked, no effect on scores or leaderboards.
It works in multiplayer too, but only for your own car.

- **The model** is original and fully scripted (`Assets/model/build_driver.py`): no game meshes and no sample or
  third-party content. It has a navy-violet suit with glowing pink and cyan stripes, a black helmet with a smoked visor,
  and gloves and boots. It uses a UE-named 55-bone skeleton, so the same model (`driver.fbx`) can be animated in
  Unreal Engine.
- **Driver view** (DriverCam): the head, neck and collar are left out, because the camera sits at the eyes. The helmet
  is not drawn, but it still casts its shadow.
- **Chase and hood views**: the driver is hidden by default. The game's cars have painted, opaque windows, so the
  driver can't be seen from outside, and hiding it costs nothing. `ShowInChaseView` draws it anyway.

## How the driver is seated

The seat, eye point and steering wheel come from the first of these sources that has them. All three are in the car's
body frame:

1. **DriverCam live**: DriverCam 0.11.0+ publishes AppDomain data `rogue.drivercam` while its driver view is on. This
   is exact and includes Edit-mode changes to the seat, eye and wheel. Driver re-fits within half a second of a change.
2. **DriverCam's files** (read-only): `plugins/DriverCam/cockpit_<Car>.dcm` (eye, steering wheel pivot, the
   `RL_SeatDriver_Cushion` and `RL_SeatDriver_Back` parts) and the car's settings (`config/DriverCam_cars/<Car>.cfg`,
   or `plugins/DriverCam/cars/<Car>.cfg`). The maths is the same as DriverCam's. Each car is read once, on a worker
   thread.
3. **Estimate** from the car body's size, used when DriverCam isn't installed or the car has no fitted cockpit.

The fit:

- **Scale.** The driver is scaled so the eyes are 0.74 m above the cushion, clamped to 0.85-1.3.
- **Torso.** The hips go onto the seat, and the spine bends so the eyes reach the camera point. The head stays level.
- **Reach.** When the wheel is far away, the grip moves lower on the rim (up to 30°), then the shoulders come forward
  (up to 5 cm), then the driver leans in (up to 10°).
- **Hands.** Both hands are placed on the rim by two-bone IK every frame, following the wheel's spin. The wheel's spin
  comes from DriverCam's published spin, or otherwise from DriverCam's own formula (−turn × SteerAngle). Past ±100° a
  hand slides along the rim instead of turning with it. A hand that would come off the rim slides back toward its
  straight-ahead spot. An arm that still can't reach is stretched by up to 12%.
- **Feet.** The right foot tips forward with the throttle and the brake. The left foot rests.
- **Small motion.** The driver breathes (an additive clip), and the neck and head turn with HeadLook and DriverCam's
  look-into-turn.

Measured on the shipped cockpits, the hands sit on the rim on every car except Justice. Justice's wheel is 0.75 m from
the eye, so the hands stay about 4 cm short even with the arms stretched. The log says so.

## In game

Driver has no hotkeys. Switch it on or off with `General.Enabled`, in the config file or in RogueHub. While the game
is paused, the driver holds its last pose.

## Tuning (rogue.driver.cfg)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | show the driver (off destroys everything Driver made) |
| `Look.ShowInDriverView` | true | DriverCam's driver view: body, arms and hands (no head) |
| `Look.ShowInChaseView` | false | chase and hood views; hidden inside the opaque body anyway |
| `Look.Outline` | false | the game's cartoon outline (off by default: in driver view an outline hull this close to the camera can fill the screen) (a copy of your car's outline material); applies the next time the driver is built |
| `Debug.LogEvents` | false | log camera-mode changes, re-fits, show / hide and object builds |
| `Debug.ForceCpuSkin` | false | skin on the CPU instead of the GPU (used automatically when the self-test fails) |

## Build

`dotnet build -c Release` in `source/Driver` (needs `source/local.props`), with the game closed. The build copies
`Driver.dll` into `BepInEx/plugins` and the model files (`driver.drm`, `driver_anims.dra` and the PNG textures) into
`BepInEx/plugins/Driver/`. Use `-p:SkipDeploy=true` for a build that only checks the code. To rebuild the model, see
`Assets/model/README.md` (Blender, headless).

## Notes

- **Files:**

  | File | What it does |
  |---|---|
  | `Plugin.cs` | config |
  | `GameApi.cs` | the only file that touches game types; checked by name at load |
  | `RigFile.cs` | `.drm` / `.dra` reader, plain C#; every index is checked |
  | `Solver.cs` | the fit and the IK, in plain maths (`Maths.cs`), about 15-100 µs a frame |
  | `SeatSource.cs` | the seat sources |
  | `DriverRig.cs` | the Unity objects |
  | `Runner.cs` | the lifecycle |

- **Objects.** `Driver_Root` is unparented and placed right before each frame is drawn
  (`Application.onBeforeRender`). It sits at the shaken body frame (the body mesh's pose without its rest offset, as
  DriverCam's `DriverView.BodyFrame` does), so the driver shakes with the car and the cockpit. Each frame writes about
  15 bone transforms and nothing else.
- **Skinning.** The body is one `SkinnedMeshRenderer` (Bone4, no motion vectors). The first build runs a self-test: it
  bends the left forearm, bakes the mesh and compares a wrist vertex with the managed maths. If the test fails, a
  CPU-skinned `MeshRenderer` is used instead.
- **Exit paths:**

  | Exit | What happens |
  |---|---|
  | No camera or no car (level end, quit to menu) | hidden at once; the objects are destroyed after 2 s |
  | Another body (death, restart, car change) | re-fit |
  | `Enabled` off | objects, meshes, materials and textures destroyed |
  | 3 errors | everything destroyed and the `onBeforeRender` delegate removed for the session |
  | Plugin unload | the same as 3 errors |
  | Leaving DriverCam's view | the full mesh comes back and the helmet casts its shadow normally |

- **Not in 0.1.0:**
  - the chase-view silhouette (a ZTest-Greater outline through the body)
  - per-camera helmet toggling for the mirrors (DriverCam's mirrors see the head-less body)
  - the g-force lean
  - hand-over-hand steering
  - LOD1
  - per-car overrides for the foot position

## Log (`/game-log Driver`)

- At startup:
  - `Driver x.y.z loaded: a driver in your car (driver view <on|off>, chase view <on|off>).`
  - `[Driver] game check OK: camera controller, car body<, pedals>`
- On the first car:
  - `[Driver] model loaded: 55 bones, LOD0 3854 vertices, clips seated_base, breathe_add (build_driver.py <sha>)`
  - `[Driver] car <Car>`
- When the driver is first shown:
  - `[Driver] model ready: 3854 vertices, 5184 triangles (... without the head), 55 bones, helmet ... vertices, outline on`
  - `[Driver] skinning self-test OK: wrist vertex moved ... cm, GPU vs managed ... mm`. FAILED is followed by
    `[Driver] skinning self-test failed: using CPU skinning (about 0.3 ms a frame while the driver shows)`.
- Once per car and seat source:
  - `Driver: <Car> seat from drivercam-live|drivercam-files|estimate, scale 1.00, reach short 0.0 cm (grip drop ... deg, shoulders ... cm, lean ... deg, eye ... cm off)`.
    When the reach is short, the line adds `(... cm with the arms stretched up to 12%)`.
  - When both DriverCam sources exist: `[Driver] <Car>: DriverCam live vs files: eye ... mm, wheel ... mm, seat ... mm`.
  - Without a fitted DriverCam cockpit: `[Driver] <Car>: DriverCam files not used (<reason>)`.
- On car changes and teardown:
  - `[Driver] car changed to <Car>`
  - `[Driver] driver removed (switched off | error | plugin unloaded)`
  - Errors: `[Driver] error (n/3): ...`. After 3 errors: `[Driver] switched off for this session after repeated errors (driver removed).`
- With LogEvents on (Debug section):
  - `[Driver] camera mode '<id>' -> Driver|Hood|Chase|Other`
  - `[Driver] shown|hidden (<view> view)`
  - `[Driver] driver objects built (<GPU|CPU> skinning, layer <n>)`
  - `[Driver] <Car>: re-fit (<source> changed), ...`
  - `[Driver] no car: driver removed`
  - `[Driver] new body for <Car>`
