# Driver character (Driver plugin, `rogue.driver`)

Original, fully scripted character: no game meshes, no sample or third-party content. Everything here is rebuilt
by `build_driver.py`; the `.blend` is a git-ignored working file.

## Build

```
blender -b --factory-startup --python build_driver.py -- [--out DIR] [--no-fbx]
blender -b DIR/driver.blend --python preview.py -- <previewdir> [standing seated hands eye lod1 detail handortho]
blender -b DIR/driver.blend --python preview.py -- <previewdir> none --cockpit <cockpit_X.dcm> <X.cfg>
python drm_io.py driver.drm driver_anims.dra     # format check, ends with RESULT: OK
python anim_clips.py --ride                      # (re)writes ride_sportbike into driver_anims.dra, every other clip kept
blender -b driver.blend --python preview_ride.py -- <previewdir> [--csm <dir with the Bikes .csm models>]
blender -b driver.blend --python preview_ride.py -- <previewdir> --poses <poses.json>   # poses solved by the plugin's code
```

`--poses` renders poses that were solved somewhere else. Each one is a JSON entry `{name, key, lean, state, lp, lq}`,
with the local position and rotation per bone in `driver.drm` order, in Unity root space. That's how the Driver 0.4.0
ride-style behaviours were checked: the plugin's own `RideBody` and `Solver.FrameBike` run offline through each
scenario, then each pose is rendered on the stand-in bike with a ground plane and a chase view from behind. The ride
style is procedural and has no clips of its own, so `anim_clips.py` and `driver_anims.dra` are unchanged.

## Sport-bike riding pose (`ride_sportbike`, Driver 0.3.0)

`ride_sportbike` is a base pose like `seated_base`: one frame, every bone's local rotation and the pelvis position
(flags 4). It isn't keyframed in Unreal. `anim_clips.py` solves it in plain Python, in Unity space, from the A-pose
(`ride_clip`) on the Bikes plugin's S1000RR sockets (`BIKES`):

- **Torso (`RIDE_SPORTBIKE`):** the pelvis is tilted 26° forward and the spine is pitched 52° from vertical
  (spread 40/30/30). The neck stays 24° forward and the head looks 6° down, so the chin is over the tank. The
  shoulders reach 8° forward.
- **Fit:** the hip joints go 0.10 m above the seat. The balls of the feet go on the pegs with the heel up, and the
  knees are swung out to ±0.20 m. The hands go on the grips with the palm down and the elbows out.

`ride_fit` / `ride_frame` are the same steps and numbers as the plugin's `Solver.FitBike` / `FrameBike`
(`SolverBike.cs`), including hang-off. An offline run of the C# solver matches them to the millimetre.

`build_driver.py` writes the pose next to `seated_base` and `breathe_add`, and `fbx_to_dra.py` keeps it.
`preview_ride.py` renders the pose on each bike, upright and hanging off at 25° and 40°, on a stand-in built from
the sockets. With `--csm`, it also uses the real bike model (read-only), and it prints the shortfalls and the knee and
elbow positions.

## Animation clips

Twelve original clips, keyframed on our own 55-bone skeleton (no Mannequin, no Epic samples, no Fab / store content).
The motion is defined once, in `anim_clips.py`; Unreal Engine 5.8 authors them as AnimSequences and exports FBX, and
Blender converts the FBX into `driver_anims.dra`:

```
python anim_clips.py driver.drm                  # self-check of the definitions in Unity space, RESULT: OK
rem UE cannot load a -script path with spaces: copy ue_anims.py to a folder without spaces, e.g. <project>\Scripts
set DRIVER_MODEL_DIR=<this folder>
set DRIVER_ANIM_OUT=<project>\Export
UnrealEditor-Cmd.exe <project>.uproject -run=pythonscript -script=<project>\Scripts\ue_anims.py -unattended -nosplash -nullrhi
blender -b --factory-startup --python fbx_to_dra.py -- <project>\Export      # writes driver_anims.dra, RESULT: OK
blender -b driver.blend --python preview_clips.py -- <outdir>                 # contact sheet per clip + limb check
```

The Unreal project (any UE 5.8 project with the Python Editor Script Plugin and Editor Scripting Utilities) imports
`driver.fbx` into `/Game/Driver` once, builds `/Game/Driver/Anims/A_<clip>` and exports `A_<clip>.fbx` + `clips.json`.
`driver.fbx` comes into Unreal at 1/100 scale (1 Unreal unit = 1 m); `ue_anims.py` measures the scale, so the clips
are unaffected.

`fbx_to_dra.py` reads each FBX as world-space rotation deltas against the FBX rest pose (so the importer's bone axes do
not matter), maps them to Unity with a Kabsch fit of the rest joints onto `driver.drm` (reflection allowed), and checks
every clip against `anim_clips.py` evaluated directly in Unity space (it fails over 0.5 degrees; it measures ~0.001).
It keeps `seated_base` and `breathe_add` from the current `driver_anims.dra`.

| Clip | Base | Mode (.dra flags) | Length | Tracks | Plugin use (planned) |
|---|---|---|---|---|---|
| `idle_seated` | seated | additive, loop (3) | 4 s @ 15 | 6 | replaces `breathe_add` |
| `steer_left` / `steer_right` | seated | additive (2) | 1.2 s @ 30 | 10 | scrubbed by steering; arms left to the IK |
| `shift` | seated | pose (0) | 1 s @ 30 | 5 | right arm to the lever and back, on a gear change |
| `look_left` / `look_right` | seated | additive (2) | 0.6 s @ 30 | 4 | scrubbed by HeadLook yaw |
| `brake_brace` | seated | additive (2) | 0.5 s @ 30 | 7 | scrubbed by brake |
| `crash_jolt` | seated | additive (2) | 0.8 s @ 30 | 6 | one-shot on a collision |
| `celebrate` | seated | pose (0) | 2.4 s @ 30 | 5 | right-hand fist pump at level end |
| `idle_standing` | rest | full, loop (5) | 4 s @ 15 | 55 | outside character |
| `walk` | rest | full, loop (5) | 1 s @ 30 | 55 | outside character |
| `wave` | rest | full (4) | 2.4 s @ 30 | 55 | outside character |

Additive = bone-local deltas (`local = base * delta`), pose = full local rotations of the bones that move, full =
every bone's local rotation + pelvis position. Additive seated clips also carry arm tracks (hand over hand on the
wheel); the plugin's arm IK owns `upperarm`, `lowerarm`, `lowerarm_twist_01` and `hand` while the hands are on the rim
(`anim_clips.IK_ARM`), so in game only their torso / neck / head part shows.

`preview.py --cockpit` reads DriverCam's cockpit and car setup read-only. It seats the driver with the same fit rules
the plugin uses: eye, wheel and seat, then the reach order (grip angle, clavicle, lean).

## Files

| File | What it is |
|---|---|
| `build_driver.py` | the mesh, UVs, atlases, armature and weights, LOD0/LOD1, the seated solve and the export |
| `drm_io.py` | `.drm` / `.dra` writer, reader and checker (plain Python); the format is in its docstring |
| `preview.py` | renders and an overlap report for the seated pose |
| `driver.drm`, `driver_anims.dra` | runtime model and clips (`seated_base`, `breathe_add`, `ride_sportbike` + the 12 clips below) |
| `anim_clips.py` | the 12 clips as original keyframes (plain Python), shared by every tool below; the `ride_sportbike` pose and the bike-rider solve (`--ride`) |
| `preview_ride.py` | Blender: renders of `ride_sportbike` on a stand-in bike from the Bikes sockets (optionally the real `.csm`) |
| `ue_anims.py` | Unreal Engine 5.8 (headless): AnimSequences on the imported skeleton, FBX export |
| `fbx_to_dra.py` | Blender (headless): the FBX exports to `driver_anims.dra`, with a round-trip check |
| `preview_clips.py` | Blender: contact sheets of the clips as read from `driver_anims.dra`, limb check |
| `driver.fbx` | for Unreal Engine: one skeletal mesh (body + helmet + visor, 3 materials), actions `A_Pose`, `Seated_Drive`, `Seated_Breathe` |
| `driver_atlas.png`, `driver_emit.png`, `driver_helmet.png`, `driver_helmet_emit.png` | palette atlases (base colour and emission) |
| `driver_sockets.json` | sockets (they are not bones): `eye_c`, `hip_c`, `helmet`, `grip_l/r`, `heel_l/r`, `toe_l/r` |

## Conventions

- Blender: the character faces -Y, Z is up, units are metres and the character's left is +X. The armature is named
  `Armature` and its top bone is `root`, so UE adds no extra root.
- Skeleton: 55 bones with UE names. The A-pose is the rest pose.
- Unity: `C = [[-1,0,0],[0,0,1],[0,-1,0]]`. Positions use `p_u = C p`, bone matrices `C M Cᵀ`. Because det C = -1,
  the triangles are written in flipped order, as DriverCam's cockpit export does.
- Grip socket: +Z points from the pinky to the index finger (align it with the rim tangent, toward 12 o'clock).
  +Y is the palm normal: at the rim it points mostly at the hub, a little toward the dash.
- Driver-view variant: drop every triangle that has a vertex with `MASK` bit0 set (neck and collar). The torso has
  a neck plug, so no hole shows.
