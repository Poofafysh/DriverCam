# DriverCam source

| Path | What |
|---|---|
| `DriverCam/*.cs`, `DriverCam.csproj` | the BepInEx 6 IL2CPP plugin (net6.0) |
| `DriverCam/Assets/cockpit.dcm` | generic cockpit, used by cars without a fitted one |
| `DriverCam/Assets/cockpits/cockpit_<Car>.dcm` | cockpits fitted to each player car (car body coordinates) |
| `DriverCam/Assets/autofit.py` | Blender script that fits a cockpit to a car from the game's dumped prefabs |
| `DriverCam/Assets/cockpits/<Car>_gauges.png`, `<Car>_gauges_mph.png` | gauge faces for each car in km/h and mph (drawn by `interiors/gauges.py` from `specs.DIALS`) |
| `DriverCam/Assets/interiors/` | Blender scripts that model each car's interior after the real car it is based on (`build_interior.py`, per-car layout and dial ranges in `specs.py`, gauge faces `gauges.py`, needle check renders `gauge_preview.py`) |
| `DriverCam/SavedSettings/` | example config / part-layout files |
| `Directory.Build.props`, `local.props.example` | shared build settings; your game path goes in `local.props` (git-ignored) |
| `CurbFeel/` | the CurbFeel plugin (curbs, sidewalks, lane splitting): source, `README.md` (features, tuning, build) and `RESEARCH.md` (how the game's walls, curbs and traffic collisions work) |

## Build

Needs the .NET 6+ SDK and the game with BepInEx installed and launched once, so that `BepInEx/interop` exists.

Set your game folder once: copy `local.props.example` to `local.props` (git-ignored) and edit `GameDir`. `Directory.Build.props` loads it for every plugin. You can also pass `-p:GameDir="..."` on the command line.

```
cd DriverCam
dotnet build -c Release
```

The build copies `DriverCam.dll` and the cockpit files into the game's `BepInEx/plugins`. Close the game first, because it locks the DLL while running.

## How it fits together

- `Plugin.cs` holds the config entries, registers the behaviour and installs the Harmony patches.
- `DriverMode.cs` creates the "Driver" `CameraModeSO`, cloned from the game's Hood view.
- `Patches.cs` and `DriverCamBehaviour.cs` add Driver to Change Camera (C / Y) cycling, handle the F6 / View button toggle and the IMGUI settings panel, and strip the car outline.
- `DriverView.cs` places the camera, cockpit and mirrors in `Application.onBeforeRender`, from the rendered (shaken) body pose.
- `HeadLookLink.cs` (0.9.3): when the separate HeadLook plugin is installed, the driver's eye direction also turns by its head angle (right stick / right mouse), read from AppDomain data `rogue.headlook`. Not while Edit mode is active (the right stick edits there). Without HeadLook it adds nothing.
- `Cockpit.cs` and `CockpitModel.cs` load the `.dcm` cockpit format and build movable part groups. A model can carry its own
  colours with `mat <tag> r g b smoothness metallic glow` lines (used instead of the built-in neon palette for those tags;
  `glow` scales `InteriorBrightness`), and textured tags with `tex <tag> <png>` (the gauges).
- `Gauges.cs` (0.10.0, working gauges): turns the speedometer and tachometer needles and drives the W8's digital panel. See
  "Working gauges" below. `GaugeGame.cs` is its only file that reads game types, `GaugeRpm.cs` its own engine model and
  `EngineLinkReader.cs` the optional link to EngineAudio's RPM.
- `PartLayout.cs` stores per-car part tweaks. `CarPresets.cs` stores per-car view and mirror settings.
- `EditMode.cs` is the controller Edit mode (L3+R3).
- `MirrorView.cs` runs the rear and side mirrors, each with its own camera and render texture (0.11.0: cheap camera setup,
  see "Performance" below).
- `HudLayout.cs` moves and scales the health bar, speedometer and ability bar (0.11.0: writes nothing while the layout
  is off and restored, and only values that changed).
- `BikeLink.cs` (0.11.2) recognises a Bikes motorcycle and places the eye at the rider's head. See "On a Bikes
  motorcycle" below.
- `CarExporter.cs`, `GpuMeshReader.cs` and `TextureSaver.cs` export the driven car's mesh and textures in-game, as a fitting reference.

IL2CPP note: wrap Unity objects the mod creates with `Keep.Hold`, or the GC collects their managed wrappers.

## On a Bikes motorcycle (0.11.2)

The Bikes plugin builds each motorcycle (S1000RR, Sport Bike) on a hidden copy of a donor car (the Saber by default).
Before 0.11.2 the driver view treated it as that car: the Saber's cockpit, steering wheel, gauges, pillars, roof and
mirrors were drawn around the bike, with the eye at the car's seat. Now, with `Bike.DriverView` on (default):

- **What shows:** only the bike (tank, bars, clocks, screen: Bikes' own model) and, with the Driver plugin, the
  rider's body, arms and hands on the grips (Driver leaves the head out in the driver view).
- **What is skipped:** the cockpit isn't built or shown (one built before a late recheck recognised the bike is hidden), so there's no steering wheel, gauges or mirror cameras (the
  mirrors cost nothing on a bike). `View.HideCarBody` is ignored (it would hide the bike and the rider), and the car's
  outline isn't stripped (Bikes already hides the car's meshes). DriverCam doesn't publish seat data for Driver
  (`rogue.drivercam`) on a bike, because Driver uses the bike's own sockets there.
- **How a bike is recognised:** a `Bikes.Lean` node in the driven car's body (Bikes' stable names; the key comes from
  the `Bikes.<Key>_Body` copy). It's looked up once per body, and once more a second later.
- **The M2 G87** (Bikes' car model, with its own cabin) is a `Bikes.Car` node instead, found in the same search. Before,
  the Saber's cockpit (dash, wheel, pillars, mirrors) was drawn inside the M2's own cabin with the eye at the Saber's
  seat. Now it gets the same treatment as a bike: no donor cockpit, gauges or mirror cameras, `View.HideCarBody` and the
  outline strip skipped (they would hide the M2), so only the M2's own dash, wheel and seats show. The eye is Bikes'
  `Bikes.Eye` node under `Bikes.Car` (the driver's eye in that cabin, left-hand drive: 0.37 m left of centre, 1.12 m up,
  0.40 m behind the axle midpoint, model metres, scaled with the model), else that same point estimated by DriverCam.
  No horizon roll; `Bike.EyeUp` / `Bike.EyeForward` shift it. The camera's frame is `Bikes.Car`'s own rotation and
  the eye `Bikes.Eye`, both read in `Application.onBeforeRender` (0.11.3), so the camera is rigid in the cabin even
  though the game rolls, pitches and smooths the visual body after the rigidbody (with `Driver.HeadFollowsShake` 1, the
  default).
- **The M2's cabin parts (0.11.3, `CarCabin.cs`):** built once per M2 body from Bikes' empty sockets under `Bikes.Car`
  (Bikes 0.2.2): a flat mirror glass on `Bikes.MirrorC` / `Bikes.MirrorL` / `Bikes.MirrorR` drawn by the same
  mirror cameras, settings and cost rules as a cockpit's (`MirrorView`); on `Bikes.Cluster` the Vector W8's digital
  readout (`cockpit_Vektor.dcm` part `RL_Digital`: 3 speed digits in the HUD's unit and 18 rpm bars, its own km/h /
  mph face atlas, self-lit) registered with `Gauges`, so it shows the real speed and EngineAudio's RPM (else the
  built-in engine model); and the M2's seat (cushion top 0.42 m), eye and steering wheel (rim centre -0.375, 0.85, 0.19,
  radius 0.175 m, column tilted 20 degrees; model metres, scaled with the model) published for the Driver plugin
  (`DriverLink.PublishCar`). 0.11.4: with Bikes 0.2.3 the M2's wheel is its own node `Bikes.SteeringWheel` (rim centre
  -0.373, 0.859, 0.194, column about 25 degrees down); the driver view turns it about the column exactly like a cockpit
  wheel (`-turn x View.SteerAngle`, the same smoothed steering) and publishes that spin and lock, so the Driver plugin's
  hands follow the rim; it is put straight again outside the driver view and on leaving. Without the node (Bikes 0.2.2)
  the wheel is part of the body mesh and the published spin is 0. Leaving the M2 (car
  change, view off, `Bike.DriverView` off) removes them and takes the seat data back (`rogue.drivercam` [1] and [2] =
  0) so it never stands for the donor car. Without the sockets (Bikes 0.2.1) there are no mirrors or readout.
- **Motion blur (0.11.3, `MotionBlurGuard.cs`):** on a bike or in the M2 the driver view switches off every loaded
  VolumeProfile's active MotionBlur override (one scan on entering, never per frame) and switches them back on when the
  view leaves the Bikes vehicle, F6 goes off or DriverCam unloads: URP's blur reprojects with the camera's motion as if
  the world stood still. That was a wrong guess for the M2's smear (it kept smearing): the cause was the game's own
  velocity blur (its URP renderer feature `MotionBlurVelocityFeature`, the in-game Motion Blur setting), which blurs
  every pixel along the car's speed, fully for near pixels, except a mask drawn from the vehicle layer(s). DriverCam's
  cockpit and the Driver plugin use the car body's layer, so a normal car's cabin never smeared; Bikes' M2 model sat on
  the Default layer. Fixed in Bikes 0.2.3 (the model goes on the car's layer); DriverCam's camera is unchanged.
- **The eye:** when Driver rides the bike, the eye is Driver's `eye_c` socket on the rider's head bone (found through
  its `Driver_Helmet` node under `Bikes.Lean`, looked up at most once a second while missing). So the camera follows the
  tuck, the hang-off and the head. Without Driver's rider, it's an estimate in the bike frame: the seat point from Bikes'
  AppDomain data `rogue.bikes.rider.<Key>` (else the S1000RR's 0.82 m seat), plus 0.58 m up and 0.45 m forward for a
  sport-bike tuck. `Bike.EyeUp` and `Bike.EyeForward` (metres, default 0) shift it. 0.11.5: whichever source, the eye
  is then kept at least 0.55 m behind and 0.30 m above Bikes' `Bikes.Bars` along the bike's own axes (a tucked
  rider's helmet sits almost over the bars, which hid them below the view), and a motorcycle looks 8 degrees further
  down than `Driver.Pitch`, so the bars, tank and clocks are in view.
- **The horizon:** `Bike.CameraLean` (0-1, default 0) is how much the view rolls with the bike's lean, like RIDE's
  level-horizon option: 0 keeps the horizon level, 0.5 rolls by half the lean, 1 locks the view to the bike. Pitch,
  look-into-turn, HeadFollowsShake and HeadLook work as in a car; the car's seat offsets and Edit mode don't apply.
  0.11.5: while on a Bikes vehicle (bike or M2) the car-setup file is never written (`CarPresets.Save`): the car being
  "driven" is the hidden donor (the Saber), so seat / eye / part edits made there went into the Saber's own setup and
  broke its cockpit view.
- **Leaving the bike** (car change, death, restart, level end, F6 off, `Bike.DriverView` off): the next car body
  goes through the normal car path, which builds its cockpit as before. A car's driver view is unchanged.
- **Cost:** on a bike, less than in a car (no cockpit, gauges or mirror cameras). Per frame: the head bone's pose and
  plain maths. In a car: one pointer compare per call, plus one search of the car's hierarchy per new body (and one
  recheck).
- **Log:** `Bikes motorcycle <Key>: driver view without the car's cockpit, mirrors, gauges or HideCarBody; eye at the
  rider's head (...)` once per bike, and `Bikes motorcycle <Key>: eye at the Driver rider's head.` (or `... an estimate
  from Bikes' seat socket.`) when the eye source changes. In the M2: `Bikes car M2G87: driver view in its own cabin,
  without the donor car's cockpit or HideCarBody (mirrors and cluster readout on Bikes' sockets, CarCabin); eye at Bikes' eye socket.` (or `... an estimate of
  the M2's seat.`). In the M2 (0.11.3) once per body: `Bikes car cabin: 3 of 3 mirrors on Bikes' mirror sockets;
  cluster: the W8's digital readout (RL_Digital, 17 cm wide in model units); steering wheel turns with the steering
  (Bikes.SteeringWheel); driver seat, eye and wheel published for the Driver plugin.` (0.11.4; `steering wheel fixed in
  the body (Bikes 0.2.3 or newer turns it)` with an older Bikes) and once per entry: `Driver view on a Bikes vehicle: switched off N motion blur override(s) (back on
  when the view leaves it).` (or `no motion blur in the scene's profiles`).

| Setting | Default | What |
|---|---|---|
| `Bike.DriverView` | on | the bike (and M2 G87) driver view above; off = the donor car's cockpit and seat, as before |
| `Bike.CameraLean` | 0 | how much the view rolls with the bike's lean (0 level horizon, 1 locked to the bike) |
| `Bike.EyeUp` | 0 m | extra eye height on a bike or in the M2 (-0.3 to 0.3) |
| `Bike.EyeForward` | 0 m | extra eye shift forward / back on a bike or in the M2 (-0.3 to 0.3) |

## Not included

The Blender working file (`cockpit.blend`) and the reference car model are not in this repo. The .blend contains the game's own car mesh, and the reference model is third-party. Game assets (the AssetRipper/Il2Cpp dumps) are not included either.

## Interiors (modelled after the real cars)

The ten fitted cockpits have a modelled cabin, each after the real car its game car is based on: Rotary = Mazda RX-7 (FD),
Shadow = Lamborghini Countach, Bond = Aston Martin DB5, Phoenix = Pontiac Firebird (3rd gen), Centaur = '69 Mustang
fastback, Delivery = DeLorean DMC-12, Saber = Nissan Skyline GT-R (R32), Centipede = Porsche 911 Turbo (930),
Justice = Chevrolet Corvette (C3), Vektor = Vector W8 (identified from the body shapes; correct them in `specs.py`).
Each has the real car's dash shape, instrument binnacle and gauge layout (textured dials), centre stack, console and
shifter, seats (driver, passenger, rear bench on 2+2s), door cards and steering wheel style, in that car's colours.

`build_interior.py` keeps the parts of the old cockpit that were fitted to the body (shell, doors, side mirrors, the
rear-view mirror glass) and replaces the `Interior` and `SteeringWheel` groups. It builds the cabin around the car's
**live** setup (`live` as the last argument: `<GameDir>/BepInEx/config/DriverCam_cars/<Car>.cfg`, else the shared copy
in `plugins/DriverCam/cars/`; GameDir from `source/local.props`; both only read): seat offset, part offsets, FOV,
pitch. The cabin sits where the tuned old dash was, so `Part.Interior` is `0 0 0 0 0 0 1` in those files (a migrated
setup gets the old offset from `specs.OLD_INTERIOR` for the build); the steering wheel keeps its tuned offset (same
pivot). The dials are aimed so they are seen through the upper opening of the wheel from the tuned eye.
These cockpit files carry a `layout 2` line. Players' own setups (`BepInEx/config/DriverCam_cars/<Car>.cfg`) may still
hold the old shipped `Part.Interior` offset, which would now shift the cabin twice: the first time a layout-2 cockpit is
loaded for that car, DriverCam resets that one value to `0 0 0 0 0 0 1` if it is still within 6 cm / 1.5 deg / 0.05
scale of the old shipped offset (`CarPresets.MigrateInterior`), logs it and saves the file. It happens once (identity
never matches again), and a value tuned for the new interior is never touched.

**Changed (unreleased): Phoenix's shared setup follows Poofafysh's retune.** `SavedSettings/DriverCam_cars/Phoenix.cfg`
now carries the values from Poofafysh's own installed Phoenix setup (eye 35 cm lower: `Driver.OffsetZ` 0.037
instead of 0.390, `Driver.OffsetY` -0.100, `Driver.Pitch` 1, `Driver.LookIntoTurn` 1, `View.SteerAngle` 30, re-tuned
`Part.*` offsets, `Part.Interior` at identity), and `cockpit_Phoenix.dcm` is rebuilt around it, so the shared setup and
the shipped cabin match. Aste-risks: this replaces your previous shared Phoenix tune; a player whose own
`DriverCam_cars/Phoenix.cfg` still has the old eye point sees the cabin from 35 cm higher than it was built for.

Cabin trim (group `Trim`, a new Edit-mode part at identity): the auto-fit's bare boxes (A-pillar bars, roof slab,
header, visors) are replaced by a headliner (thin curved sheet, rails dropping at the sides, rounded nose to the header,
rolled rear edge), tapered A-pillar trims, door-top caps with a window seal along the belt, B-pillars, C-pillars round
the rear quarter windows (2+2 / hatch) or sail panels to the bulkhead (two-seaters), window frames (framed doors), a
valance hanging from each side edge of the headliner (down past the window frame / pillar tops, so no slit to the
outside shows when the head turns to the side), a rear window surround, rounded sun visors with hinge rods, grab
handles (over the door on framed doors; folded flat against the headliner on frameless ones) and a dome light, all
built where the tuned roof, pillars and doors showed. `specs.SPECS[car]["trim"]` sets the car's greenhouse (`rear` = `2+2` / `hatch` / `bulkhead`,
`frameless`, B-pillar width, quarter window, rear window angle, sill width, pedals) and `pal["headliner"]` its colour.
The rear-view mirror gets a rounded housing and a stem to the headliner in its own group.
Mirror sightlines: no cabin piece may hide a mirror from the tuned eye. Every `Interior` / `Trim` piece that reaches
into the frustum from the eye to a mirror (its outline seen from the eye, 4 mm inside the housing / bezel) gets that
frustum cut out of it: the rear-view mirror cuts the headliner (Justice: also a visor), the side mirrors the A-pillar
trims, door caps and window seals (Delivery / Phoenix: also door cards). Behind the rear-view mirror's cut a closed
recess (walls on the frustum's sides, a back behind the whole mirror) is added, so from an eye slightly off the tuned
one (Driver.Offset* changed) the gap round the mirror shows headliner, not the sky. The cut follows the tuned eye and
mirror offsets: after moving the seat or a mirror a lot, rebuild. The `Interior` group adds
pedals, a dead pedal, kick panels, a tapered tunnel, seat back panels, two shaped rear seats and a parcel shelf (2+2),
or a carpeted bulkhead with a ledge (two-seaters) or a cargo cover (hatch); the side walls (door cards, rear side
panels) clear the seats, and the dash runs from card to card. Shell and fitted-door triangles that poke into the cabin
(more than 6 mm through a door card), stick up over the door caps, show outboard of the caps from the belt line up
(door tops, rear quarters behind the B-pillar) or sit hidden behind the new walls are clipped; the game's own body is
drawn there anyway (`View.HideCarBody` false), so what remains of Shell / Doors is a few dozen triangles per car. A group
whose geometry changed is shifted so its Part offset still puts it where it was. A `Part.Doors` scaled under 0.35
(hidden on purpose) leaves the doors out and the door cards follow the shell. 5,090-6,290 triangles per cockpit after
`optimize_dcm.py` (57,595 for all ten), 26-37 draws.

Rebuild one car (Blender 4.1 or newer, run headless; renders go to a scratch folder, never the repo):

```
blender -b --factory-startup --python Assets/interiors/build_interior.py -- Rotary base <out dir> <preview dir> SavedSettings/DriverCam_cars/Rotary.cfg
```

The input is each car's layout-1 auto-fit (the cockpit `autofit.py` fitted to the game's body before any modelled
interior): the build measures the old dash and the tuned pillars / roof / doors from it, and keeps its Shell, Doors and
side-mirror groups (clipped). It is not stored as a second copy in the repo: `base` (or any layout-2 cockpit, e.g. the
repo's own) makes the script read it with `git show d113f22:source/DriverCam/Assets/cockpits/cockpit_<Car>.dcm`
(DriverCam 0.9.1, the last layout-1 cockpits) into a scratch file in the temp folder (`DCM_BASE_DIR` overrides). The
built file's header says which groups come from the auto-fit of the game body. Build against the shared setup in
`SavedSettings/DriverCam_cars/<Car>.cfg` for anything you ship (`live` reads your own installed setup instead: only
for a local try-out, since the cut-outs round the mirrors follow the eye and mirror offsets of the setup used).

Then strip the triangles no camera can see and check that nothing visible changed (run both after every rebuild;
`optimize_dcm.py` is safe to run on an already optimised file):

```
blender -b --factory-startup --python Assets/interiors/optimize_dcm.py -- <in cockpit_Car.dcm> <out cockpit_Car.dcm> [--dry]
blender -b --factory-startup --python Assets/interiors/compare_views.py -- <before.dcm> <after.dcm> <out dir> [0,-70,70,180]
```
`dcm_io.py` imports triangles with the game's front faces and its renders cull back faces like the game (URP Lit,
`_Cull = 2`), so `compare_views.py` / `preview.py` show a missing surface as a hole.
`optimize_dcm.py` removes only triangles whose both sides are sealed off by their own group (inside a solid, or in a gap
under 8 cm), probed with 28 points x 32 directions per side, so the result holds for any seat position, head turn or
per-car part layout. Pivot parts (wheel, needles, digital gauges), mirror glass, `paint` and textured tags are never
touched, and every other line is kept byte for byte. `compare_views.py` renders both files from the eye (ahead, left,
right, behind) and counts differing pixels; it must print `IDENTICAL`. On the 0.11.0 cockpits: 54,320 -> 53,270
triangles (1.9%), all 40 views pixel-identical. Keep the renders out of the repo.

## Performance (0.11.0)

Each mirror is a whole extra camera: URP culls the scene again, renders its own shadow map and submits every draw
again for it, which costs far more than its few pixels. 0.11.0 trims that, and builds the cockpit with fewer draws:

| Setting (`[View]`, not saved per car) | Default | Effect |
|---|---|---|
| `MirrorDrawDistance` | 150 m | far clip of the mirror cameras (was 400 m) |
| `MirrorShadows` | false | no shadow-map pass per mirror (was always on: the mirrors lose their shadows) |
| `SideMirrorRate` | 2 | the side mirrors redraw every 2nd frame, taking turns (1 = every frame, as before; up to 4) |

Always on (no visible change): the mirror cameras skip URP's depth / colour copies, post-processing, anti-aliasing (the
URP defaults for a new camera already had post-processing and anti-aliasing off) and per-frame volume updates, and a
mirror whose glass is outside the view (head turned away with HeadLook) isn't drawn at all; that check uses the
final camera pose of the same frame, so a mirror never shows a stale picture when it comes into view. With the
defaults, mirror cameras per frame go from 3 (each with a shadow pass) to at most 2 (none).

Cockpit build: the static parts of a group whose `mat` lines are identical are merged into one mesh (fewer renderers
and outline passes, same material), static meshes share identical corners (about 40% fewer vertices) and use 16-bit
indices, and the per-vertex maths reads struct fields instead of calling Unity's (slow) interop math. For the ten
cockpits: 342 -> 324 renderers, about 643 -> 607 draws with the outline, 162,960 -> 96,257 vertices.
`HudLayout` and the mirror code use `Shared/FastMath.cs` for the same reason. The IMGUI panel skips Unity's Layout pass
(`useGUILayout = false`; it only uses `GUI.*`).

## Working gauges

The speedometer and tachometer of every fitted cockpit work (`[View] WorkingGauges`, on by default; off = the dials rest
at zero). They are not saved per car, so switching them never rebuilds the cockpit.

- **Speed** is the car's real speed in the game HUD's own unit and numbers: `|VehicleMovement.CurrentSpeed|` (m/s) x 3.96
  in km/h or x 2.4607 in mph, the same multipliers the HUD uses (it shows the real speed x 1.1). The unit is read from
  `Singleton.Instance.SettingsManager.GameplaySettings.CurrentUnitSystem` once a second (the game's default is mph), and
  the dial faces swap with it: each car has `<Car>_gauges.png` (km/h) and `<Car>_gauges_mph.png` (mph). The W8's
  digital readout shows `floor()` of that number, so it always matches the HUD.
- **RPM** comes from EngineAudio when it runs (AppDomain data `rogue.engineaudio`, the engine you hear), mapped so its
  red line lands on the dial's red line. Without EngineAudio (or with F1 off) DriverCam's own `GaugeRpm` follows the
  game's gearbox the same way: idle 900, climbs through each gear, drops to 58% of the red line on an upshift, holds
  93% in the last gear at top speed, revs with the throttle standing still.
- Needles move smoothly (about 80 ms), rest at zero when stopped, and peg 2 degrees past the end of the scale. Cost: a
  few transform rotations a frame while the driver view shows the cockpit; the digital panel's UVs are rewritten only
  when its number or bar count changes. Read only towards the game; 3 errors switch the gauges off for the session.
- Log: `Gauges for <Car>: speedometer 0-<max> <unit>, tachometer 0-<max> (red <red>), rpm from EngineAudio | the
  built-in engine model.` per car, and again when the RPM source changes.

Dial ranges (`specs.DIALS`, one table for the printed faces and the needle lines; dial max is about 1.2 x the highest
speed the HUD shows, 200-220 km/h / 125-140 mph):

| Car | km/h | mph | tach (x1000) | red |
|---|---|---|---|---|
| Rotary, Saber | 0-280 | 0-180 | 9 | 8 |
| Shadow | 0-320 | 0-200 | 10 | 8 |
| Bond, Delivery, Justice | 0-280 | 0-160 | 7 | 5.5 |
| Phoenix | 0-240 | 0-160 | 6 | 5 |
| Centaur | 0-280 | 0-160 | 8 | 6 |
| Centipede | 0-300 | 0-180 | 8 | 6.8 |
| Vektor (digital) | 3 digits | 3 digits | 18 bars, 9 | 7 |

`.dcm` lines for gauges (in a pivot part, after its `p` line; older DriverCam versions ignore them and show the needles
at zero). The pivot sits on the dial's centre with forward into the dial and up = the dial's up; the part is built at
rest (pointing at `a0`):

```
n <source> <kmh|mph|-> <lo> <hi> <a0> <a1> [red]     needle: angle = a0 + (a1 - a0) * (v - lo) / (hi - lo), maths degrees
                                                    (counter-clockwise as the driver sees it); a speedometer has one per unit
dg <source> <unit|-> <count> <u0> <v0> <du>          the part's next <count> quads are digits; glyph k = UVs + (k * du, 0), 10 = blank
db <source> <count> <lo> <hi> <red> <dlu> <dlv> <dru> <drv>   the next <count> quads are bars, lit at lo + (i + 1) / count * (hi - lo):
                                                    UVs + (dlu, dlv), or + (dru, drv) at or above <red>
tex gauges_mph <png>                                 header: the faces in mph
```
Sources: `speed`, `rpm` (others leave the needle at rest).

Rebuild the faces and cockpits (on the original inputs, as above), then check the needles:

```
python Assets/interiors/gauges.py Assets/cockpits
blender -b --factory-startup --python Assets/interiors/build_interior.py -- <Car> <original cockpit_Car.dcm> Assets/cockpits "" <original Car.cfg>
blender -b --factory-startup --python Assets/interiors/gauge_preview.py -- Assets/cockpits/cockpit_<Car>.dcm <out dir> SavedSettings/DriverCam_cars/<Car>.cfg kmh|mph
```
`gauge_preview.py` renders every gauge at 0, half and full scale, from the eye and straight on (wheel hidden), posing the
needles with the same maths as DriverCam. Keep its renders out of the repo.
