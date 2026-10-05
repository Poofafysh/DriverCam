# Driver

BepInEx 6 IL2CPP plugin for **Driving Rogue**: a 3D racing driver sits in your car. The driver's hands hold
DriverCam's steering wheel and turn it with you, the right foot works the pedal, and the head follows HeadLook. In
DriverCam's driver view you look down at your own body, arms and gloves on the wheel. On one of the Bikes plugin's
motorcycles the driver rides it instead (0.3.0), and since 0.4.0 it moves the way riders do in the RIDE games.

Current version: **0.4.1** (0.4.1: fixes 0.4.0 failing to load: a Runner method with an `out` struct parameter broke IL2CPP injection).

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
- **Small motion.** The driver breathes and moves the head a little (the `idle_seated` clip), and turns with HeadLook
  and DriverCam's look-into-turn (see Animations).

## Animations (0.2.0)

The clips in `driver_anims.dra` (original keyframes on the driver's own skeleton, made in Unreal Engine 5.8: see
`Assets/model/README.md`) are layered on the fitted pose every frame, under the steering-wheel IK. The IK owns the
arm bones, so the hands stay on the rim whatever the clips do to the body.

| Clip | When | How |
|---|---|---|
| `idle_seated` | always | breathing and small head moves (4 s loop); replaces 0.1's `breathe_add` |
| `steer_left` / `steer_right` | steering | scrubbed by how far you steer: the shoulders turn and lean into the corner. Only the torso, neck and head are used: the clip's hand-over-hand arms leave the rim (they were made without the wheel), so the hands stay on the IK |
| `look_left` / `look_right` | HeadLook (`rogue.headlook`) | the yaw (plus DriverCam's look-into-turn) scrubs the clip: spine, neck and head turn together up to about 79°; past that the head turns on by itself. Pitch stays procedural |
| `brake_brace` | hard braking (brake over 60% above 3 m/s) | eased in over 0.15 s and out over 0.35 s |
| `crash_jolt` | the game's collision count goes up (`CollisionScoreProviderSO.TotalHits`) | 0.8 s one-shot; a hit within 0.25 s of the last doesn't restart it |
| `shift` | a gear change (`VehicleGearboxHandler.CurrentGearIndex`) | the right hand leaves the wheel for the cockpit's gear knob (`RL_ShiftKnob` / `ShifterKnob` in DriverCam's `cockpit_<Car>.dcm`) in 0.2 s, pulls it back on an upshift or pushes it forward on a downshift, stays 0.45 s after the last change and goes back to the rim in 0.25 s. A knob out of reach (most shipped cockpits put it 0.6-1.25 m from the eye) makes the driver lean toward it (up to 14°) and the hand reaches as far as it can; the log gives the gap per car. A cockpit without a knob (`Saber_auto`) keeps both hands on the wheel; a car without a DriverCam cockpit uses the clip's own arm. The torso and head follow the clip |
| `celebrate` | the level is completed (`GameState.LevelCompleted`) | 2.4 s fist pump with the right arm (blended off the wheel and back) |

The standing clips (`idle_standing`, `walk`, `wave`) are loaded but not used by the plugin. All the layering is plain
maths with no allocations (about 15-30 µs a frame with every layer on, measured offline). The game is read 15 times a
second while the driver shows, never while paused; a hidden driver, a new body, a car change or the level ending
stops every clip, and the gear, collision and level-completed baselines are taken again (so a restart never fires an
event by itself). `Anim.Enabled` off gives the 0.1 driver (breathing only).

Measured on the shipped cockpits, the hands sit on the rim on every car except Justice. Justice's wheel is 0.75 m from
the eye, so the hands stay about 4 cm short even with the arms stretched. The log says so.

## Riding a bike (0.3.0)

When your vehicle is one of the Bikes plugin's motorcycles (BMW S1000RR, Sport Bike), the driver becomes a rider.

- **How a bike is recognised.** Bikes builds each bike on a hidden donor car and puts the bike model under a node
  named `Bikes.Lean` (under the body node, in a body named `Bikes.<Key>_Body`). Driver looks for that node once per
  car body (and once more a second later). The model key comes from the body's name, or else from the frame mesh's
  name. Driver doesn't need Bikes' code and reads nothing from the game for this.
- **Where the rider goes.** Driver uses the bike's sockets in the bike frame (metres, origin on the ground midway
  between the axles, +z forward). If Bikes publishes AppDomain data `rogue.bikes.rider.<Key>`, Driver uses that
  (`float[11]`: seat xyz, right grip xyz, right peg xyz, hip height above the seat, knee half-width). Otherwise it
  uses its own table (the Bikes author's numbers):

  | Bike | Seat | Grips | Pegs |
  |---|---|---|---|
  | S1000RR | (0, 0.82, -0.18) | (±0.32, 0.86, 0.38) | (±0.17, 0.36, -0.30) |
  | Sport Bike | (0, 0.85, -0.20) | (±0.33, 0.90, 0.40) | (±0.18, 0.38, -0.32) |

  An unknown model uses the S1000RR's sockets, and the log says so.
- **The pose.** The base pose is the clip `ride_sportbike` in `driver_anims.dra`. It's solved in
  `Assets/model/anim_clips.py`, with the same steps and numbers as the plugin. The torso is pitched forward 52°
  (the pelvis 26°), the chin is over the tank, the eyes look up the road, and the shoulders reach forward. The rider
  is at scale 1 (1.76 m).
- **The fit**, once per bike:
  - The hip joints go 0.10 m above the seat point.
  - Each leg is placed by two-bone IK. The ball of the foot is on the peg with the heel up, and the knee is swung out
    to ±0.20 m (against the tank).
- **Every frame:**
  - Both hands go onto the grips by two-bone IK. The elbows are bent and out, the palm is down and the fingers point
    forward. An arm that is short is stretched by up to 12%.
  - The head turns with HeadLook. It also rolls 30% of the lean back toward the horizon.
  - The clip layers work as in a car: `idle_seated`, `look_*`, `brake_brace` and `crash_jolt`. On a bike,
    `celebrate` is a right-hand fist pump over the shoulder (by IK), and the left hand stays on its grip. There is no
    shift hand and no steering clip.
- **Leaning.** `Driver_Root` is parented under `Bikes.Lean` (the bike frame, real size), so the rider leans with the
  bike and moves with it exactly.
- **Hang-off** (`Bike.HangOff`, on by default). Driver reads the lean from `Bikes.Lean`'s roll.
  - The hips slide to the inside, up to 0.15 m, and are fully across at 30° of lean. The pelvis rolls 6° and the
    upper body 3°.
  - Past 30°, the upper body leans in up to 12° more (all of it by 45°).
  - The inside knee opens by 0.12 m.
- **Steering.** The bike model's bars don't turn: Bikes turns only the front wheel (`Bikes.SteerF`). So the hands stay
  on the fixed grips. If Bikes adds a `Bikes.Bars` node (pivot on the steering axis, local rotation = the bars' turn),
  Driver turns the grips with it, with no Driver change.
- **Views.** On a bike, the rider shows in every view, because a bike has no body to hide it.
  - In DriverCam's driver view the head is left out, as in a car (`Look.ShowInDriverView`).
  - DriverCam has no bike cockpit. It fits its cockpit and eye point to the hidden donor car (the Saber by default), so
    the camera is not at the rider's eyes. The Bikes README lists this as untested.
  - Driver ignores DriverCam's seat data on a bike.
- **Exit paths.** Driver_Root is a child of the bike, so it is destroyed with the bike body (death, restart, car change)
  and rebuilt when needed. Switching to a car, or `Bike.Enabled` off, unparents it and re-fits it to the car seat. With
  `Bike.Enabled` off, the driver sits in the donor car's seat, hidden in the chase view as in any car.
- **Measured** (offline: the plugin's solver against the shipped `driver_anims.dra`):
  - Both bikes: the hands are exactly on the grips (0 cm short) and the knees are 40 cm apart.
  - S1000RR: the eyes are at 1.40 m.
  - Full hang-off at 40°: the eyes move 0.27 m to the inside, and the hands still reach the grips.
- **Previews.** `Assets/model/preview_ride.py` (Blender) renders the pose on a stand-in built from the sockets, with
  the real bike model as an option.
- **Limit.** The knees sit about 0.53 m high, below the tank's knee pads. That's the geometry: the given pegs are only
  0.12 m behind the seat point, and the ball of the foot is on the peg.
- **Fingers** (0.4.0): the fingers wrap the grips (they were straight in 0.3.0), whatever the ride style.

## Ride style (0.4.0)

`Bike.RideStyle` (on by default) makes the rider behave the way players and reviewers describe the riders of Milestone's
RIDE 4 and RIDE 5, with real track technique filling the gaps. All the motion is our own: procedural layers on our own
skeleton (`RideStyle.cs` decides, `SolverBike.cs` poses). Nothing is taken from those games: no animations, models or
code. `Bike.RideStyle` off gives the 0.3.0 rider.

Every input is something Driver already reads: the speed (`VehicleMovement.CurrentSpeed`, 15 times a second), the
bike's lean (`Bikes.Lean`'s roll) and how fast it changes, the steering (the game's turn input), the throttle, the
brake and the gear. Every layer is eased, so nothing jumps.

| Behaviour | When | What the rider does |
|---|---|---|
| **Foot down** (`FootDown`, `FootDownSide`) | below 1.5 m/s (and still down while revving at a standstill) | in 0.4 s, the left foot (or the right) goes to the ground beside and ahead of the peg, with the knee nearly straight. The hips shift 3 cm and the pelvis rolls 5° toward it. The foot goes back on the peg in 0.3 s as you pull away |
| **Launch** | throttle over 50% from a standstill | in 0.2 s the torso goes 12° further over the tank, and it eases back by 15 m/s |
| **Tuck** (`Tuck`) | above 28 m/s (100 km/h), throttle over 60%, lean under 12°, no brake | in 0.7 s the rider goes flat behind the screen: the torso 26° lower, the hips 4 cm back, the elbows tucked down and in, and the head up to see over the screen. As in RIDE 4, the rider **sits up in 0.25 s** the moment the throttle drops under 20%, the brake comes on, the lean passes 15° or the speed falls under 24 m/s |
| **Hard braking** | brake over 50% above 15 m/s (off under 35%) | in 0.15 s the rider sits up 20° and slides 4 cm back, and the arms straighten to brace. It relaxes in 0.3 s |
| **Leg out** (`LegDangle`) | brake over 40% above 20 m/s, lean under 30% of Bikes' `MaxLean` | in 0.25 s the inside leg leaves its peg and hangs out, down and forward, swaying a little. The inside is the side you steer to, or else the side of the lean, or else the side of the last corner. The leg goes back on the peg in 0.2 s past 30% of `MaxLean` or under 20% brake |
| **Hang-off** (`HangOff`) | from the lean 0.35 s ahead (the lean plus its rate) | the hips slide up to 15 cm to the inside on a spring capped at 0.6 m/s. A side change takes about 0.6 s, and you can see it happen, as in RIDE 5. The upper body follows 0.1 s behind, and the inside knee opens |
| **Chicane** | the lean changes side | the hips lift up to 3 cm as they cross the seat, and the head turns to the new side first |
| **Knee down / elbow drop** (`KneeDown`) | past 80% of `MaxLean` | the inside knee opens 8 cm wider toward the tarmac, and the inside elbow drops (Balanced 70%, ShouldersOut fully, OldSchool not at all) |
| **Style** (`Style`) | in corners | how far the upper body leans in beyond the hips past 30° of lean. Balanced: up to 12°. ShouldersOut: up to 22°. OldSchool: 6°, the hips only 60% across, the knee half out and no elbow drop |
| **Look into the corner** (`LookIntoCorner`) | steering, or the lean 0.35 s ahead | before the body moves, the head turns up to 26° and rolls 7° toward the inside, easing in over about 0.3 s. The shoulders follow by 20%. HeadLook adds on top |
| **Head level** | always | the head's tilt in the world is half the bike's lean, however far the upper body leans in. In 0.3.0 the head rolled 30% of the lean back toward the horizon, on top of the torso's tilt |
| **Exit drive** | throttle over 50% while the lean falls | the torso goes 5° further forward and the elbows partly in, until the tuck takes over |
| **Gear** | a gear change | the left foot taps the shifter in 0.22 s: the toe goes up and 3 cm higher on an upshift, and down on a downshift. A downshift while braking also pulls the clutch with two fingers for 0.2 s |
| **Brake fingers** (`BrakeFingers`) | brake on | in 0.1 s, 2 (or 4) right fingers go from the grip to the lever, and they pull it in with the brake |
| **Throttle hand** | always | the right hand rolls back on the grip with the throttle, by up to 22° |
| **Bob** | always | the torso pitches up to 4° and slides about 1 cm as you speed up and slow down, plus a bob of a few millimetres at speed |

- **Left out.** There are no wheelie or stoppie reactions, because Bikes doesn't pitch the bike. The crash jolt is the
  existing `crash_jolt` clip.
- **With `Anim.Enabled` off**, the ride style still works, because it reads the speed and the gear for itself. The
  clips (idle, look, jolt, celebrate) don't play.
- **With the ride style on**, the bike rider doesn't use the `brake_brace` clip, because the rider's own sit-up
  replaces it.
- **MaxLean.** The knee-down and leg-out thresholds scale with Bikes' `[Look] MaxLean`. Driver reads it, read-only,
  from Bikes' config through the BepInEx chainloader, twice a second while you ride. If Bikes' setting isn't there, it
  uses 50°.
- **First person.** DriverCam's driver view shows the body without the head. The tuck, the lever fingers, the
  throttle hand and the arms show there. The head lead and the head tilt show only in the chase views, because Driver
  doesn't move the camera.
- **Cost.** The controller and the whole rider solve take about 30 µs a frame (measured offline), with no allocations.
  The lever fingers (18 bones) are written on top of the usual bones, but only on frames when they move.
- **Measured offline.** The plugin's own `RideBody` and `FrameBike` were driven through each scenario at 60 fps on the
  S1000RR:
  - The hands stay on the grips: 0.0 cm, or 0.6 cm at full ShouldersOut knee-down.
  - The tuck lowers the eyes 14 cm.
  - Braking sits the torso up from 49° to 37° from vertical.
  - The ball joint of the planted foot ends 5 cm above the ground, so the sole is on the ground. Bikes tipping the
    bike at a standstill would close that gap.
  - At a 45° lean, the inside knee is 8 cm off the ground.
  - In a chicane from +35° to −35°, the hips take about 0.7 s to go from side to side.
- **Previews.** `Assets/model/preview_ride.py --poses <json>` renders poses solved by the plugin's own code, with a
  ground plane and a chase view from behind.

## In game

Driver has no hotkeys. Switch it on or off with `General.Enabled`, in the config file or in RogueHub. While the game
is paused, the driver holds its last pose.

## Tuning (rogue.driver.cfg)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | show the driver (off destroys everything Driver made) |
| `Look.ShowInDriverView` | true | DriverCam's driver view: body, arms and hands (no head) |
| `Look.ShowInChaseView` | false | chase and hood views; hidden inside the opaque body anyway |
| `Anim.Enabled` | true | the animation clips above; off = breathing only (as 0.1) |
| `Anim.ShiftHand` | true | the right hand to the gear knob on a gear change |
| `Anim.Celebrate` | true | the fist pump when you complete a level |
| `Bike.Enabled` | true | on a Bikes motorcycle the driver rides it (shown in every view); off = seated in the hidden donor car |
| `Bike.HangOff` | true | hang off in corners (hips up to 15 cm inside, upper body up to 12° more past 30° of lean) |
| `Bike.RideStyle` | true | the RIDE-style rider (0.4.0, above); off = the 0.3.0 rider |
| `Bike.Style` | Balanced | Balanced, ShouldersOut or OldSchool: how far the upper body leans in, the hip slide, the knee and the elbow |
| `Bike.Tuck` | true | tuck behind the screen at speed on the throttle; sit up when you lift off, brake or lean |
| `Bike.LegDangle` | true | the inside leg off its peg under hard braking |
| `Bike.KneeDown` | true | the knee wider and the inside elbow down past 80% of Bikes' MaxLean |
| `Bike.LookIntoCorner` | true | the head leads into corners (adds to HeadLook) |
| `Bike.FootDown` | true | a foot on the ground below 1.5 m/s |
| `Bike.FootDownSide` | Left | which foot goes down |
| `Bike.BrakeFingers` | 2 | fingers on the brake lever (2 or 4) |
| `Look.Outline` | false | the game's cartoon outline (off by default: in driver view an outline hull this close to the camera can fill the screen) (a copy of your car's outline material); applies the next time the driver is built |
| `Debug.LogEvents` | false | log camera-mode changes, re-fits, show / hide, object builds and animation events |
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
  | `Solver.cs` | the fit, the IK and the clip layers, in plain maths (`Maths.cs`), about 15-100 µs a frame |
  | `SolverBike.cs` | the bike rider: `BikeSeat` (sockets per model), `FitBike` / `FrameBike` (legs, grips, hang-off, the ride-style layers, lever fingers) |
  | `RideStyle.cs` | the ride-style controller (`RideBody`): speed, lean, steering, pedals and gear into eased body layers (`RideOut`) |
  | `Anim.cs` | clip sampler (`ClipSampler`), the frame inputs (`AnimIn`) and the game events to clip times (`AnimEvents`) |
  | `SeatSource.cs` | the seat sources |
  | `DriverRig.cs` | the Unity objects |
  | `Runner.cs` | the lifecycle |

- **Objects.** `Driver_Root` is unparented and placed right before each frame is drawn
  (`Application.onBeforeRender`). It sits at the shaken body frame (the body mesh's pose without its rest offset, as
  DriverCam's `DriverView.BodyFrame` does), so the driver shakes with the car and the cockpit. Each frame writes about
  20 bone transforms and nothing else.
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

- **Not in 0.2.0:**
  - the chase-view silhouette (a ZTest-Greater outline through the body)
  - per-camera helmet toggling for the mirrors (DriverCam's mirrors see the head-less body)
  - the g-force lean
  - hand-over-hand steering (the clip's arms don't keep the hands on the rim; the IK slides them instead)
  - the standing clips (a driver outside the car)
  - LOD1
  - per-car overrides for the foot position

## Log (`/game-log Driver`)

- At startup:
  - `Driver x.y.z loaded: a driver in your car (driver view <on|off>, chase view <on|off>, animations <on|off>, bike rider <on with hang-off|on|off>, ride style <Balanced|ShouldersOut|OldSchool|off>).`
  - `[Driver] game check OK: camera controller, car body<, pedals>`
  - `[Driver] animation triggers: gear <yes|no>, speed <yes|no>, collisions <yes|no>, level completed <yes|no>` (a warning listing what is
    missing when one is not found; only that animation is off)
- On the first car:
  - `[Driver] model loaded: 55 bones, LOD0 3854 vertices, clips seated_base, breathe_add, idle_seated, steer_left, steer_right, shift, look_left, look_right, brake_brace, crash_jolt, celebrate, idle_standing, walk, wave (build_driver.py <sha>)`
  - `[Driver] animations: <idle, steer, look, brake brace, crash jolt, shift, celebrate> (<on|off: [Anim] Enabled>; shift hand <on|off>, celebrate <on|off>)`
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
  - The shift hand: `[Driver] <Car>: shift hand to the gear knob (in reach | in reach with a ... deg lean | ... cm out of reach after a ... deg lean: the hand reaches toward it)`,
    `[Driver] <Car>: no gear knob in the cockpit: no shift hand` or `[Driver] <Car>: no DriverCam cockpit: the shift clip's own arm on a gear change`.
- On a Bikes motorcycle (once per bike and socket source):
  - `[Driver] car changed to <Car> (a Bikes motorcycle: <Key>)` (or `[Driver] car <Car> is a Bikes motorcycle (<Key>)` when the
    bike node showed up a second after the body)
  - `[Driver] riding <Key>: rider on the bike (sockets from table|bikes|table (unknown model: S1000RR sockets)), hang-off <on|off>, grips <fixed (the bike's bars don't turn)|turn with Bikes.Bars>`
  - `[Driver] <Key>: ride style <Balanced|ShouldersOut|OldSchool> (tuck <on|off>, leg out <on|off>, knee down <on|off>, look into corners <on|off>, foot down <left|right|off>, brake fingers <2|4>; Bikes MaxLean <n> deg from <rogue.bikes.cfg|default (Bikes' MaxLean not found)>)`.
    When the game's speed can't be read, the line ends `; no speed: no tuck, leg out or foot down`. With the ride
    style off: `[Driver] <Key>: ride style off ([Bike] RideStyle): the 0.3.0 rider`.
  - `Driver: <Key> seat from bike-<source>, scale 1.00, reach short 0.0 cm (knees 40 cm apart)`
  - `[Driver] <Key>: rider off ([Bike] Enabled): the driver sits in the donor car's seat`, and (a warning)
    `[Driver] <Key>: driver_anims.dra has no ride_sportbike pose: no rider (the driver sits in the donor car's seat)`
- When a level is completed: `[Driver] celebrate (level completed)`.
- On car changes and teardown:
  - `[Driver] car changed to <Car>`
  - `[Driver] driver removed (switched off | error | plugin unloaded)`
  - Errors: `[Driver] error (n/3): ...`. After 3 errors: `[Driver] switched off for this session after repeated errors (driver removed).`
- With LogEvents on (Debug section):
  - `[Driver] camera mode '<id>' -> Driver|Hood|Chase|Other`
  - `[Driver] bike rider mode: <on|off> (<Car>)`
  - `[Driver] shown|hidden (<view> view)`
  - `[Driver] driver objects built (<GPU|CPU> skinning, layer <n>)`
  - `[Driver] <Car>: re-fit (<source> changed), ...`
  - `[Driver] no car: driver removed`
  - `[Driver] new body for <Car>`
  - `[Driver] shift <n> -> <m> (hand to the knob | shift clip arm)`
  - `[Driver] crash jolt (hits <n>)`
  - On a bike, each time the rider's state changes: `[Driver] ride: <cruise|standstill (foot down)|launch|tuck|hard braking|braking, leg out|knee down|hang-off> (<speed> m/s, lean <n> deg, throttle <x>, brake <x>)`
