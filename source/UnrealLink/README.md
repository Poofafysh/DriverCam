# UnrealLink (prototype)

BepInEx 6 IL2CPP plugin for **Driving Rogue**, plus an Unreal Engine 5.8 plugin (**RogueLink**). Unreal runs next to the
game in a small window, renders your avatar from the game's own camera with a transparent background, and the game
draws that picture over its view. This is the base for things Unity's Driver plugin can't do well: Unreal animation
(AnimBP state machines, Control Rig, IK), and later getting out of the car, walking, adjusting mirrors.

Current version: **0.1.0**. **Off by default** (`General.Enabled`). Not tested in the game yet; see "What needs an
in-game test".

## How it works

```
Driving Rogue (D3D11)                                      Unreal 5.8 -game -dx11 (320 x 180 window)
UnrealLink plugin                                          RogueLink plugin
 onBeforeRender: camera, car body, view, inputs,   --->     OnBeginFrame: wait for the frame event (25 ms max),
 light, DriverCam seat / HeadLook / EngineAudio /  shared   read the frame (seqlock) -> FRogueLinkState (Blueprint)
 Bikes data -> Local\RogueUnrealLink.State          memory  ARogueLinkRig: avatar on the seat, hands on the wheel (IK),
 SetEvent(Local\RogueUnrealLink.Frame)                      SceneCapture2D at the game camera, only the avatar
                                                            compute pass: alpha fixed -> RGBA8
 GL.IssuePluginEvent -> UnrealLinkNative.dll:     <---      copy into a ring of 3 shared D3D11 textures
 keyed mutex, CopyResource into a RenderTexture    shared   (keyed mutex, key 0 as a plain lock), newest slot +
 RawImage on an overlay canvas (sorting -100)      texture  its game frame published in the shared memory
```

- **One Unreal frame per game frame.** The game sets a named event after writing its frame; Unreal waits for it at the
  start of each frame (25 ms timeout while the game is connected, 100 ms while it isn't, so it idles at about 10 fps).
- **Everything is in the car body's frame** (`VehicleProvider.BodyTransform`). The car sits at Unreal's world origin, so
  the avatar, the seat and the camera all move together and the one-frame delay mostly cancels in the driver view.
- **Coordinates:** Unity (x right, y up, z forward, m) to Unreal (x forward, y right, z up, cm): `(z, x, y) * 100`;
  quaternions `(qz, qx, qy, qw)` (a cyclic axis swap, both left-handed). The test pictures below confirm it.
- **Alpha:** Unreal keeps scene-capture alpha inverted on purpose (1 - opacity). A small compute shader
  (`Unreal/RogueLink/Shaders/Private/RogueLinkAlpha.usf`) writes (rgb, opacity) into an RGBA8 texture, which is what
  the ring holds, so the game uses plain alpha blending (the stock `UI/Default` shader).
- **Protocol:** `LinkProtocol.cs` and `Unreal/RogueLink/Source/RogueLink/Public/RogueLinkProtocol.h` (same offsets,
  `static_assert`ed). Version 1.

## Files

| Path | What |
|---|---|
| `Plugin.cs`, `Runner.cs` | BepInEx entry, settings; the per-frame writer, attach / retry, show / hide, `[Perf]` line |
| `GameState.cs` | the only file that reads game types (camera, view, car body, pedals, speed, sun) |
| `LinkProtocol.cs` | the shared-memory layout, mapping and event through kernel32 (also used by the test tool) |
| `SharedTexture.cs`, `Native/UnrealLinkNative.cpp`, `Native/build.cmd` | the D3D11 helper: device from a RenderTexture, open the ring, copy on Unity's render thread |
| `Compositor.cs` | overlay canvas + RawImage |
| `UnrealProcess.cs` | optional auto-start of Unreal |
| `Unreal/RogueLink/` | the Unreal plugin (subsystem, rig, link, game mode, alpha shader) |
| `Unreal/Project/` | code-project stub and `DefaultEngine.ini` settings for the Unreal project |
| `Test/LinkTest/` | console tool that plays the game's side and saves Unreal's picture as a PNG |
| `../../tools/unreallink-start.ps1` | sets up, builds and starts Unreal in link mode |

The repo has source only. `Native/bin/` and every `bin/`/`obj/` are git-ignored; the Unreal plugin is built inside the
Unreal project, never in the repo.

## Setup

1. **Unreal side (once, then after every RogueLink change).** UE 5.8 and VS 2022 with the C++ workload. The Unreal
   project is `RogueDriverAnim` (the Driver animation project, which already has the Driver character
   `/Game/Driver/driver` and the clips `/Game/Driver/Anims/A_*`). Keep it at a short path (UE builds fail past 260
   characters).
   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File tools/unreallink-start.ps1 -Setup -Build -NoLaunch
   ```
   `-Setup` copies `Unreal/RogueLink` into `<project>\Plugins\RogueLink`, adds the code stub (`Source\*.Target.cs`,
   `Source\RogueDriverAnim\`) so the installed engine can build plugin C++, merges
   `Unreal/Project/Config/DefaultEngine.RogueLink.ini` into `Config\DefaultEngine.ini` between `>>> RogueLink` /
   `<<< RogueLink` markers (D3D11, the empty `/Engine/Maps/Entry` map, `RogueLinkGameMode`, alpha propagation, no
   Lumen / VSM / AA / auto exposure, no frame-rate smoothing) and adds the module and the plugin to the `.uproject`.
   `-Project` / `-Engine` (or `UNREALLINK_PROJECT` / `UNREALLINK_ENGINE`) for other paths.
2. **Game side.** Build as any plugin (`/build UnrealLink`). The build runs `Native\build.cmd` (VS 2022 x64 compiler,
   found with vswhere) and deploys `UnrealLink.dll` plus `plugins\UnrealLink\UnrealLinkNative.dll`. Without the C++
   tools the plugin still builds and says in the log that the helper is missing.
3. **Play.** Start Unreal (`tools/unreallink-start.ps1`; the first start compiles shaders, later starts take about
   20-40 s), start the game, switch `General.Enabled` on (Rogue Hub > UnrealLink). Enter DriverCam's driver view once
   per car: the avatar needs DriverCam's seat and wheel. `-Stop` ends Unreal.

## Test without the game

```
dotnet build -c Release source/UnrealLink/Test/LinkTest
dotnet source/UnrealLink/Test/LinkTest/bin/Release/net8.0/LinkTest.dll -frames 1200 [-driver] [-w 960 -h 540] [-png out.png]
```

LinkTest writes the game block at 120 Hz (a seat, a wheel that steers, a 3/4 camera or `-driver` at the eye),
opens Unreal's ring through `UnrealLinkNative.dll` (its own D3D11 device, the same open / keyed-mutex / copy code as
the game), saves the newest picture (`out.png` with alpha and `out_rgb.png` colour only), checks the alpha, and times
the game-side copy with GPU timestamp queries. With no game connected, Unreal shows a test pose (3/4 view, steering
and looking around) so the window is never empty; those pictures are stamped game frame 0, which the game never
shows. The game also shows only pictures rendered after the layer last became wanted (race start, Ctrl+Insert, back
to the driver view) or after the textures were reopened, so an older picture never flashes up.

## Measured (2026-10-04, GTX 1080 Ti, Driving Rogue running at 4K / 120 Hz on the same GPU the whole time)

| | Value |
|---|---|
| Builds | RogueLink (UE 5.8.3, MSVC 14.44): first build about 80 s, then 13-38 s. UnrealLink + helper: OK, `il2cpp-check` OK (84 engine methods checked) |
| Pacing | 1190-1194 Unreal frames per 1200 game frames: one Unreal frame per game frame |
| Unreal frame | 10.2-10.6 ms, so 53-54% of the game's 120 frames get a new picture (the rest repeat the last one) |
| Unreal GPU (its own counter) | 6.5-7.7 ms; pose to finished texture 7.0-8.1 ms |
| Render size | 480 x 270, 960 x 540 and 1920 x 1080 all cost the same: the time is fixed overhead, not pixels |
| Game-side copy (GPU) | 0.07-0.18 ms at 960 x 540; 0.75 ms while Unreal was drawing at the same moment (waits on the shared GPU) |
| Alpha | 3/4 view: 92.5% transparent, 7.5% avatar, no partly transparent pixels (no AA) |
| Latency (LinkTest) | the picture for frame N is ready before frame N+1 is written; in the game the copy is queued in the same `onBeforeRender` that writes frame N, so expect 1 frame (8.3 ms) |
| Hands | wrists up to 15 cm off their rim targets on LinkTest's made-up seat (no reach strategy yet, see Limits) |

All Unreal numbers were taken while the game was using the GPU, so they include waiting for it. Unreal alone was not
measured. The fixed overhead is most likely the editor binaries in `-game` mode plus the preview window; a packaged
Development build and `-RenderOffscreen` are the first things to try.

## Settings (`rogue.unreallink.cfg`)

| Setting | Default | Meaning |
|---|---|---|
| `General.Enabled` | false | Draw the Unreal layer (needs Unreal running) |
| `General.ShowInChase` | true | Also in the chase / hood views (no occlusion yet: the driver shows through the car body) |
| `General.ToggleKey` | Insert | Ctrl + this key shows / hides the layer for the session (all of F1-F11 are taken; None = no key) |
| `Render.ResolutionScale` | 0.5 | Unreal renders at this fraction of the game's resolution (0.25-1) |
| `Render.FlipY` | false | Flip the picture if it shows upside down |
| `Debug.LogTimings` | true | The `[Perf]` line every 10 s while connected |
| `Launch.AutoLaunch` | false | Start Unreal when it doesn't answer (needs `Launch.Command`). The Unreal that then reports its pid is closed when the game quits only if its parent process (up to 3 levels up) is a launcher this plugin started and it started while that launcher ran; any other Unreal or editor (started by hand, PIE, opened after a launch attempt) is never claimed or closed |
| `Launch.Command` | empty | e.g. `powershell.exe` |
| `Launch.Arguments` | empty | e.g. `-NoProfile -ExecutionPolicy Bypass -File <repo>\tools\unreallink-start.ps1` |

## Shared data

- Reads (by name, never an assembly reference): `rogue.drivercam` (+ `.car`), `rogue.headlook`, `rogue.engineaudio`,
  `rogue.bikes.rider.<Key>`, copied as-is into the shared memory.
- Publishes **`rogue.unreallink.live`**: float[2], `[0]` 1 = the Unreal layer is on screen over the player's car this
  frame, `[1]` `Time.unscaledTime` of the last write. Driver can hide its own body while `[0]` is 1 and `[1]` is fresh
  (not done yet: Driver is not changed by this plugin; until then switch Driver off to avoid two drivers).

## Log (`/game-log UnrealLink`)

- `UnrealLink 0.1.0 loaded (off; render scale 0.5, Ctrl+Insert shows / hides the layer).`
- When switched on: `[UnrealLink] game check OK: camera controller, car body<, pedals, speed>`,
  `[UnrealLink] native helper v1 loaded`,
  `[UnrealLink] link open (<map name>, <linear> colour space); waiting for Unreal`.
- No Unreal: `[UnrealLink] Unreal is not running: nothing drawn (start it with tools/unreallink-start.ps1; still looking every 3 s)`.
- `[UnrealLink] Unreal connected (pid N)`, then
  `[UnrealLink] opened Unreal's shared textures: 3 slots, W x H, DXGI format 28 -> ARGB32 (sRGB view, linear project), generation N`.
- `[UnrealLink] camera mode '<id>' -> Driver`, `[UnrealLink] car '<name>'; the seat comes from DriverCam once its driver view has run for this car`.
- Every 10 s: `[UnrealLink] [Perf] N frames: N new pictures (N%), latency avg N frames (max N), layer shown N frames; game side N ms CPU a frame, copies N/N (busy N, failed N), render thread N us avg / N max; Unreal frame N ms, GPU N ms, pose->texture N ms`.
- `[UnrealLink] layer <hidden | shown> (Ctrl+<key>)`, `[UnrealLink] Unreal stopped answering: layer hidden, still looking every 3 s`,
  `[UnrealLink] link closed (<why>): layer hidden`, `[UnrealLink] started '<command>' (pid N) to bring Unreal up`,
  `[UnrealLink] Unreal pid N was started by AutoLaunch (launcher pid N): it is closed when the game quits`.
- Unreal restarted (crash or close, then started again): `[UnrealLink] Unreal process changed (pid N -> N): its shared textures are reopened`.
  When Unreal stops answering (heartbeat older than 0.25 s) or its pid changes, the game releases its textures and
  hides the layer; the next ring Unreal publishes is opened fresh (Unreal seeds its handle generation from its pid).
- Trouble: `[UnrealLink] can't start: ...` (not D3D11, helper missing, game types missing), `[UnrealLink] open shared textures: ...`,
  `[UnrealLink] error (n/3): ...`, then `[UnrealLink] switched off for this session after repeated errors`.

## Unreal's log

`<project>\Saved\Logs\RogueDriverAnim.log`, category `LogRogueLink`: `RogueLink: game connected`,
`shared texture ring: W x H, DXGI format 28, 3 slots`, `RogueLink [Perf] N frames in 5.0 s: ...` (paced frames, frame
and GPU time, wait), `RogueLink rig: N captures in 10 s ..., hands off the rim by at most N cm`,
`RogueLink rig: avatar hidden (no DriverCam seat for this car yet)`.

Unreal captures only while the game shows the layer (driving, `Enabled`, not hidden with Ctrl+Insert, a view that
shows it); with no avatar to draw (bike, no seat yet) it publishes one empty picture and then stops capturing. Its
own preview window still draws every frame (`-Offscreen` removes it).

## What needs an in-game test

1. The picture shows at all: the helper gets Unity's device from a RenderTexture, opens the handles and copies on the
   render thread (never run inside the game yet). If it is upside down, `Render.FlipY`.
2. Colours / gamma: the RenderTexture is an sRGB view in a linear project; compare against the window.
3. The avatar lines up with DriverCam's cockpit: camera pose, FOV (vertical to horizontal with the screen aspect), the
   seat fit (Driver's rules: scale from eye height, hips onto the seat) and the wheel grips.
4. Sorting: the layer must be under the HUD (sorting -100) and over the 3D view.
5. Costs inside the game: the `[Perf]` line (game-side CPU, render-thread time, how many frames get a new picture).
6. The one-frame delay in the driver view (HeadLook turns and DriverCam's shake are not in the body frame).

## Limits (v1)

- **No occlusion:** the avatar draws over the dash, the A-pillars and the wheel rim, and through the car body in chase
  views.
- **Cars only.** On a Bikes motorcycle the avatar is hidden (its rider data is sent, not used yet).
- **Needs DriverCam's seat** (published while its driver view runs; valid for the car until the next car). No seat =
  no avatar.
- **Body frame, not DriverCam's shaken frame:** DriverCam's seat data is in its shaken body frame; small offsets
  while the car shakes.
- **Hands:** two-bone IK to the rim (Driver's grip angles, 165 and 15 degrees on the spun wheel), the hand keeps the
  clip's rotation; no reach strategy (grip drop, shoulder, lean) yet, so a far wheel leaves the hands short.
- **Look:** the Driver character's materials have no textures in the Unreal project (white suit); flat two-light setup
  from the game's sun and ambient.
- **Two processes on one GPU:** Unreal can't hold 120 fps next to the game on this machine yet (about 95 fps).
- **Editor binaries** (`UnrealEditor.exe -game`): about 2 GB of RAM and 20-40 s to start. A packaged build is the plan.
- Unreal's D3D11 renderer must stay in the engine (`-dx11`); a D3D12 path (shared fence) would be needed otherwise.

## Roadmap

1. **Depth occlusion:** Unreal writes linear depth into a second shared texture; the helper composites with a small
   pixel shader against Unity's depth, so the dash and pillars hide the arms.
2. **Speed:** packaged Development build, `-RenderOffscreen`, then a lighter renderer setup; aim for every frame at
   half resolution.
3. **AnimBP / Control Rig:** replace the C++ IK in `ARogueLinkRig` with an Anim Blueprint fed by `FRogueLinkState`
   (seated state machine, Control Rig hands on the rim, shift hand, head look).
4. **Getting out and walking:** the camera leaves the body frame; send the world pose predicted one frame ahead
   (`pos + v * dt`, rotation pushed by the angular velocity), a ground / collision proxy built from the game's road
   (no game meshes shipped), and a walk state in the AnimBP.
5. **Mirror adjust:** DriverCam's mirror poses into the shared memory, hand IK onto the mirror.
6. **Multiplayer:** each player runs their own Unreal and draws only their own avatar; showing other players' avatars
   needs their pose over the network (a Police / Sandbox style Steam channel), and one Unreal capture per visible player
   or one capture with all of them in world space.
7. Hide the Unity Driver body when `rogue.unreallink.live` is set (a small Driver change, with its owner's OK).
