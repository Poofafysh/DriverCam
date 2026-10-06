# Driver avatar: UE test world + animation (M0/M1 work in progress)

This is the Unreal side of the driver-avatar animation work (design: `scratchpad/ue_anim_design.md`). It is separate
from the UnrealLink **renderer** (`RogueLink` plugin, which composites the avatar into the game); nothing here is linked
to the game yet. Everything lives under `source/UnrealLink/Unreal/` and is copied into the UE project by
`tools/unreallink-start.ps1 -Setup`.

## What is here

- `Project/Source/RogueDriverAnim/`
  - `RogueDriverAnimInstance.{h,cpp}` — a C++ `UAnimInstance` holding the locomotion **logic** (ground speed → idle↔walk
    blend, `bShouldMove`, travel `Direction`, `bSeated`). The AnimGraph that consumes these values (a BlendSpace /
    state machine) is authored on an Animation Blueprint whose parent class is this one — the only editor-only step.
  - `RogueDriverTestPawn.{h,cpp}` — a CMC `ACharacter` that walks the avatar with WASD + mouse (legacy axis bindings
    `MoveForward/MoveRight/Turn/LookUp`; `Config/DefaultInput.ini` supplies the mappings). `MaxWalkSpeed` matches the
    AnimInstance's `WalkSpeed`.
  - `RogueDriverShotDirector.{h,cpp}` — renders the proof stills. A `-run=pythonscript` commandlet world has **no scene
    proxies**, so `SceneCapture2D`/`HighResShot` produce empty (transparent) frames there. This actor instead runs in a
    real `-game` session: it poses the avatar from our clips one shot at a time, frames a camera on the posed bounds,
    writes a PNG with `FScreenshotRequest`, then quits.
- `Project/Scripts/build_anim_test.py` — builds and saves `/Game/Driver/Test/L_AnimTest` (grid floor + sun + sky +
  avatar + camera + the shot director + a plain GameMode override). Copied to the project `Scripts/` by `-Setup`.

## Build + render the proofs

```
# 1. copy the source into the project and build the editor module (first build 2-5 min, then under a minute)
powershell -NoProfile -ExecutionPolicy Bypass -File tools/unreallink-start.ps1 -Setup -Build -NoLaunch

# 2. build/save the test level (editor commandlet)
UnrealEditor-Cmd.exe <proj>.uproject -run=pythonscript -script=<proj>/Scripts/build_anim_test.py -unattended -nosplash -stdout

# 3. render the stills in a real -game session (the director writes them to %RL_RENDER_OUT%)
set RL_RENDER_OUT=<out folder>
UnrealEditor.exe <proj>.uproject /Game/Driver/Test/L_AnimTest -game -RenderOffscreen -ResX=1000 -ResY=1000 -stdout
```

## Project-only assets (not in the repo)

The repo is source-only. These are generated into the UE project and are **not** committed:

- `/Game/Driver/driver`, `driver_Skeleton`, `/Game/Driver/Anims/A_*` — imported/baked by `ue_anims.py` from
  `source/Driver/Assets/model/driver.fbx` (reproducible).
- `/Game/Driver/Test/L_AnimTest` (`.umap`) — regenerate with `build_anim_test.py`.
- `ABP_Driver` (Animation Blueprint), `CR_DriverRuntime` (Control Rig), BlendSpaces — **editor-authored**, see below.

## Editor-only remainder (cannot be scripted headless)

| Step | Est. |
|---|---|
| `ABP_Driver` AnimBP: parent class `URogueDriverAnimInstance`; AnimGraph = state machine (Idle ↔ Walk by `bShouldMove`, BlendSpace by `MoveBlend`) + a Seated branch (`A_idle_seated` + additive steer/look). Assign it to the pawn's mesh. | 2-4 h |
| `CR_DriverRuntime` Control Rig: two-bone IK for the hands onto the wheel/wheel-proxy socket, evaluated in the AnimBP post-process. (The procedural C++ version already exists in `RogueLink`'s `ARogueLinkRig::ArmIK`.) | 3-5 h |
| `Config/DefaultInput.ini`: axis mappings W/S→MoveForward, D/A→MoveRight, Mouse X/Y→Turn/LookUp (or an Enhanced Input set). Then Play-in-editor to walk the avatar with WASD. | 30 min |
