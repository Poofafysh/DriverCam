# Bikes

BepInEx 6 IL2CPP plugin for **Driving Rogue**: new vehicles in the garage, after the game's cars. Two sport motorcycles,
the **BMW S1000RR** and the blue **Sport Bike**, and a car, the **M2 G87** widebody (0.2.0).

Current version: **0.3.1** (the M2 turns: grip 1.45-1.8 g, cornering before throttle, speed scrubs in a corner taken too fast; 0.3.0: real handling: bikes turn by leaning, RIDE 4 / 5 style, and the M2 has tyre grip, so speed carried into a corner matters; the bike rocking up and down is gone; bike tyre marks and smoke; 0.2.6: bike-sized hitbox: the donor car's colliders squeezed to 0.8 x 2.2 m while you ride; bikes really lean now (the lean follows the heading's change, the game's angular velocity stays near 0); the game's glitch copy of the donor car hidden on Bikes vehicles; 0.2.5: the rider sits 15 cm further back on both bikes: seat and pegs moved, grips unchanged; 0.2.4: the M2 tops out at 200 mph on the HUD with every other stat at the maximum; 0.2.3: see-through glass and a cabin for the M2, with a clean drawn cluster face and navigation screen and cabin sockets for DriverCam's mirrors and cluster readout; bikes phase 1: selectable, look right, spin their wheels and lean into corners;
the rider comes from the Driver plugin; bike handling and a narrow body come later; see the design doc "Sport Bikes:
Lean, Grip and Braking")

## What it does

- **New garage vehicles.** Each has its own name and stats:

  | Vehicle | Speed | Accel | Handling | Durability |
  |---|---|---|---|---|
  | BMW S1000RR | 0.95 | 0.90 | 0.75 | 0.30 |
  | Sport Bike | 0.85 | 0.95 | 0.85 | 0.35 |
  | M2 G87 (car) | 1.00 (200 mph) | 1.00 | 1.00 | 1.00 |

  They're always unlocked and cost nothing.
- **M2 top speed (0.2.4): 200 mph on the HUD** (322 km/h with the game set to km/h). How the game gets there (IDA, GameAssembly.dll):
  - the HUD (`VehicleVisuals.Update`, 0x76F370) shows `floor(VehicleMovement.CurrentSpeed x 2.237 x 1.1)` mph
    (`x 3.6 x 1.1` km/h: `MetricEnumExtensions.GetMultiplierNonLogical`, 0x7C2C60);
  - with no boost, the speed settles on `VehicleMovement.OriginalMaxSpeed` (`UpdateSpeed`, 0x74FFF0, caps `TargetSpeed`
    at it), which `LoadVehicleAttributes` (0x74C690) sets to `VehicleStats.MaxSpeed` = `MaxSpeedKph / 3.6`;
  - `MaxSpeedKph` = `VehicleStatsRange.GetMaxSpeed(clamp01(base + card/upgrade factor))` (0x6B9DE0: `max(50, x + (y - x) f)`),
    the range being `VehicleContainerSO.StatsRange`, 130-200 in the shipped data. The factor is clamped to 0..1, so the
    stats alone top out at 200 "km/h" = 136 mph on the HUD.
  - So the M2's speed factor is 1.0 (the plugin bisects the game's `GetMaxSpeed` for 200.5 mph and finds it out of
    range) and a `LoadVehicleAttributes` postfix multiplies the M2's `OriginalMaxSpeed` by 293.3 / 200 = 1.4667: 81.48 m/s,
    200.5 mph on the meter, shown as 200 (322 km/h). The postfix runs every time the game reloads the stats, which set the
    value fresh, so it never compounds. Boost pads, slipstream and drift-exit boosts still raise the speed above it for a
    while (`totalSpeedModifier`); speed cards can't (the factor is already at 1). Any speed-lowering card scales with it.
  - The garage bars use `(factor + 0.2) / 1.4` clamped to 0..1 (`GetStatVisualFactor`, 0x6BAB00): full bars at 1.0.
- **A bike on a hidden car.** Underneath, each bike drives on a copy of a donor car (`DonorCar`, default Saber): the
  game's own driving, four physics wheels and collider. That's why traffic, scoring, daredevils, police, RacingLine,
  damage and the timer all keep working. The bike also uses the donor's parts and vinyl groups (they don't show on the
  bike), trait and engine sound.
- **The look.** The bike model is shown at real size (about 2 m long), centred between the hidden car's axles, with its
  wheels on the road. The front wheel spins and steers with the game's front-left wheel, the rear wheel spins with the
  rear-left. The bike leans into corners by the physical lean for its speed and turn rate
  (tan lean = speed x yaw rate / g, the yaw rate from the heading's change per frame), up to `MaxLean` (50°), and
  stays upright at a crawl. The car's own meshes, and any part the game adds, are hidden.
- **Bike-sized hitbox (0.2.6):** while you ride, every solid collider of the hidden donor car (hull capsules, ground box,
  barrier capsules, also one the game detached) is narrowed to 0.8 m and shortened to 2.2 m round the axles' midpoint,
  height kept; the rigidbody's centre of mass and inertia stay the game's. Put back exactly when you're off the bike,
  Bikes is switched off, after errors or on unload. Log: `[Bikes] bike hitbox: N car collider(s) squeezed to 0.8 x 2.2 m`
  and `[Bikes] car hitbox and glitch effect restored (...)`.
- **The M2 G87** is a car model fitted the CarSkins way: scaled so its wheelbase matches the donor's, front axle on the
  donor's front axle, its four wheels on the donor's own wheel pivots (they spin and steer with them). No lean.
- **Models:**
  - the S1000RR is about 18,300 triangles in flat colours (a white / blue / red livery, black trim, metal, from a small
    palette texture; the cockpit is denser for the rider's view, and every face the rider's eye or an outside view
    sees from behind is two-sided, so the inner fairing and clocks have no holes) with a see-through smoked windscreen, and the Sport
    Bike about 10,800 with one 2048 px texture, both built by `Assets/build_bike.py` (Blender);
  - the M2 is about 50,000 triangles (each wheel about 3,900, with rounded tyres) in flat colours (paint, gloss black trim, carbon, underbody, glass, lights, tyres,
    rims), built by `Assets/build_car.py`. Its windows are see-through (a model's `mat` line can end in an alpha value)
    and it has its interior (about 14,000 triangles: dash, steering wheel, seats, headliner) with one texture,
    `BMW_M2_G87_interior.jpg` (1024 x 1280: soft-touch black, labels and lit button symbols on top; below, a drawn
    display strip for the curved display: a dark digital cluster face and a navigation map screen, planar-mapped onto
    the display's faces, 0.2.2). An empty `Bikes.Eye` node under `Bikes.Car` marks the driver's eye in that cabin
    (left-hand drive, model metres -0.37, 1.12, -0.40); DriverCam's driver view (0.11.2) sits there and shows only the
    M2's own cabin, without the donor car's cockpit.
  - 0.2.2: empty cabin sockets for DriverCam under `Bikes.Car` (`Garage.Socket`; position = the surface's centre
    3-4 mm toward the driver, +z into the surface, `localScale` (w, h, 1) = its size in model metres, measured from
    `BMW_M2_G87.csm`): `Bikes.MirrorC` (rear-view mirror glass, 0.225 x 0.058), `Bikes.MirrorL` / `Bikes.MirrorR`
    (door mirror glass, 0.15 x 0.09) and `Bikes.Cluster` (the instrument cluster: where DriverCam 0.11.3 puts its live
    digital speed / rpm readout, 0.17 wide). Nothing else reads them.
  - 0.2.3: the M2's steering wheel (rim, spokes, hub, paddles; about 900 triangles) is its own part `SteeringWheel` in
    `BMW_M2_G87.csm` (split off the cabin by `build_car.py`; a part's `a x y z` line is its spin axis), on its own node
    `Bikes.SteeringWheel` under `Bikes.Car`: at the rim centre (model metres -0.373, 0.859, 0.194), +z along the column
    toward the dash (about 25 degrees down), the mesh under it as `Bikes.SteeringWheelMesh`. Bikes leaves it straight;
    DriverCam 0.11.4's driver view turns it with the steering (and the Driver plugin's hands follow the rim).
  - 0.2.3: the car model (`Bikes.Car` and its wheels) and a bike (`Bikes.Lean`, with anything under it) sit on the layer
    of the donor's own body mesh (`Garage.MatchLayer`, on the template and again when one is first driven). The game's
    velocity motion blur (its renderer feature `MotionBlurVelocityFeature`, the in-game Motion Blur setting) blurs every
    pixel along the car's speed except a mask drawn from the vehicle layer; on the Default layer the M2's cabin, screen and
    wheel were smeared diagonally across DriverCam's driver view.

## Handling (0.3.0, `Handling.cs`)

The game's own handling is arcade: each physics step it turns the car by steer x turn speed and sets the velocity to
its target speed along a direction that follows the nose. For the player's Bikes vehicle a model takes over the turn
and the velocity (the game's speed logic, boosts, collisions, gravity, downforce and the HUD keep working through it):

- **Bikes (after RIDE 4 / 5):** the stick asks for a **lean angle**, the lean builds at a lean rate that is quick at
  town speeds and slow at 250 km/h, and the **lean makes the turn** (yaw rate = g x tan(lean) / speed, a coordinated
  turn). How far it can lean is what the tyres can hold: brake hard and it can't lean as far (one grip budget for
  braking and cornering), and never past `Look.MaxLean`. At walking pace it steers by the bars instead. The visible
  lean is this lean, a smooth physics state (0.2.6 measured it from the game's turning frame by frame, which rocked the
  bike and the first-person camera up and down).
- **The M2 G87:** sporty tyre grip of about 1.45 g, rising with speed like downforce (to 1.8 g), 1.2 g braking.
  Cornering comes first: on the throttle the engine only gets the grip the corner leaves (0.3.0 gave it to the
  throttle, and the car couldn't turn). Asking for more turn than the grip holds scrubs speed at the front tyres, as
  if you lifted, so the car slows into the corner and the line tightens. Braking still takes grip from cornering. The steering
  turns the front wheels (less lock at speed) and the turn follows the wheelbase until the grip runs out: past that it
  **understeers**, so speed carried into a corner widens the line and you have to brake before it. Acceleration is
  traction-limited at low speed, then power over speed, minus drag (boosts add power). On the throttle at low and
  middle speed the rear can step out (power oversteer); the car slides when its nose outruns its course, and a slide
  scrubs speed.
- **Speed:** the game's target speed (with its boosts and slowdowns) is your throttle / brake; the model follows it
  within real acceleration and braking, and writes the real speed back, so the HUD, the engine sound and the game's
  logic see it.
- **When the game's handling is used instead:** in multiplayer (the handling is single-player only), while reversing (the Reverse plugin), in the air (the game's own turn and
  velocity; the model then picks up from what the body does on landing), off a Bikes vehicle,
  with `Handling.Bikes` / `Handling.Car` off, or for the session after 3 errors. A collision or the game's own
  path-angle limit resets the model to what the body really does.

## Bike effects (0.3.0, `BikeFx.cs`)

The donor car's body brings the game's car effects, placed for four wheels: skid marks at each wheel, drift / brake /
launch smoke, wall-ride sparks. On a bike they are hidden and replaced: one tyre mark under the front tyre (12 cm) and
one under the rear (19 cm), and smoke from the rear tyre. They follow the handling model: the front marks when you brake
at the grip limit, the rear marks and smoke in a rear slide, a burnout or very hard braking. The wall-ride sparks move
in to the pegs. Log: `[Bikes] bike effects: N car effect(s) hidden; tyre marks front / rear, rear smoke yes, 2 wall-ride
spark(s) moved to the pegs`.

## For the rider (Driver plugin)

- **Stable names:** the bike body copy is named `Bikes.<Key>_Body` (Key = `S1000RR` / `SportBike`). Under the car's
  `Content/Body` node it holds `Bikes.Lean`, which leans. Inside that are `Bikes.Frame`, `Bikes.SteerF` → `Bikes.WheelF`,
  `Bikes.WheelR` and `Bikes.Bars`: a mesh-less handlebar pivot at the grips' centre, turned with the steering.
- **AppDomain data `rogue.bikes.rider.<Key>`:** a `float[11]` written once at load, in the bike frame (metres; origin on
  the ground midway between the axles, +z forward): seat xyz, right grip xyz, right peg xyz, hip height above the seat,
  knee half-width.

  | Key | Seat | Right grip | Right peg |
  |---|---|---|---|
  | S1000RR | 0, 0.82, -0.18 | 0.32, 0.86, 0.38 | 0.17, 0.36, -0.38 |
  | SportBike | 0, 0.90, -0.20 | 0.33, 0.92, 0.40 | 0.18, 0.38, -0.40 |

  The S1000RR seat comes from the real bike's spec (0.82 m), the Sport Bike's from its model's seat top (0.896 m). The
  pegs sit about 0.2 m behind the seat for a sport-bike tuck.

## Kept out of your records

Everything below covers all three vehicles, the M2 G87 included ("a bike" here means any of them).

- **Saves:** a bike is never written to the save. When the game saves, a selected bike is stored as its donor car, and
  bike ids are removed from the unlocked list and the per-vehicle save data. If the mod is removed, the save only
  names stock cars.
- **Resumed runs:** the run snapshot also names the donor car, so a run started on a bike resumes on the donor car,
  and never crashes with the mod removed.
- **Steam leaderboards:** a run that used a bike at any point is never uploaded (the entry would carry a vehicle id
  nobody else has). That includes a run resumed on the donor car, thanks to a run flag saved in the config
  (`State.RodeBikeThisRun`), which clears when a new run starts on a car. This cooperates with PitStop's and Sandbox's
  leaderboard prefixes.
- **Achievements:** bikes are never in the unlocked list, so vehicle-count achievements don't move.
- **Multiplayer:** the bikes leave the garage while a multiplayer game is on. If a bike is selected they stay until
  it isn't, and other players are sent the donor car.
- **Safety net:** the hooks are installed all together or not at all. If any is missing, the bikes never enter the
  garage. The error breaker also takes them out.

How we know the save clean-up works (IDA, GameAssembly.dll):
- `VehicleGarageManager.SaveData(manager)` (0x18096A650) and `SnapshotData(manager)` (0x18096BF90) are real functions,
  called from the manager's save and snapshot getters. So the Harmony postfixes run.
- Both build a fresh unlocked array and a fresh per-vehicle dictionary from the vehicle list. Removing bike entries
  never touches the game's live data.
- The save loader (`InitializeSaveable`) skips unknown ids.
- `Vehicle_SO`'s constructor creates its save record, so a copied bike starts with an empty one.

## Settings (`rogue.bikes.cfg`)

| Setting | Default | Meaning |
|---|---|---|
| `General.Enabled` | true | The bikes are in the garage (single-player). Off = they leave the garage at once |
| `General.DonorCar` | Saber | The car each bike drives on underneath (speed class, handling, parts, trait). Applies at the next game start |
| `Handling.Bikes` | true | Bikes turn by leaning (RIDE 4 / 5 style); off = the game's car handling |
| `Handling.Car` | true | The M2 handles like a real car (tyre grip, understeer, slip); off = the game's handling |
| `Look.MaxLean` | 50 | Largest lean in corners, degrees (0-65); with the handling on, also the most the bike leans to turn |
| `Look.LeanScale` | 1 | Lean strength (1 = physical lean) |
| `State.RodeBikeThisRun` | false | Written by the plugin: the current run used a bike (leaderboard uploads skipped). Not a setting |

## Log (`/game-log Bikes`)

- `[Bikes] handling: bike (steer = lean, turn from the lean) on the player's vehicle` (or `car (tyre grip, understeer,
  slip)`), once; `[Bikes] handling: the game's own handling again (...)` when it stands aside.

- `Bikes x.y.z loaded: 13 hooks (11 without the handling hooks); the bikes are built when the garage opens (donor car Saber; single-player, off the leaderboards).`
- `[Bikes] model SportBike loaded (bike): N triangles, wheelbase 1.41 m` (the same for BMW_S1000RR, and
  `loaded (car)` for BMW_M2_G87).
- `[Bikes] Sport Bike body: N car meshes to hide, bike at real size (...)` and
  `[Bikes] Sport Bike built on the Saber (id rogue.bikes.sportbike, stats ...)`; for the M2 first
  `[Bikes] M2 G87: stat range 130-200 km/h; speed factor 1.0000 (the game's GetMaxSpeed), the range tops out at 200.0 so top
  speed x1.4667 -> 200 mph / 322 km/h on the HUD (target 200 mph)`, then `stats 1.000/1.00/1.00/1.00`, and:
  `[Bikes] M2 G87 body: N car meshes to hide, car model at scale S (wheelbase G vs M); steering wheel turns; on the car's
  layer L` (`part of the body (static)` with a model from before 0.2.3).
- Only if the game's transparent shader is missing: `[Bikes] no transparent shader: BMW_M2_G87 glass glass drawn opaque`.
- `[Bikes] 3 bike(s) added to the garage after the N cars (garage opened)`, and
  `[Bikes] bikes taken out of the garage (multiplayer)` / `(switched off)`.
- `[Bikes] driving Sport Bike: bike model, wheels follow the game's, steering follows, layer L` the first time you drive
  one (`car or missing model` for the M2; L = the layer the model was put on, -1 = no car mesh found to copy it from).
- `[Bikes] this run uses a bike: it stays off the leaderboards`, `[Bikes] leaderboard upload skipped: this run used a bike`,
  `[Bikes] new run on a car: leaderboard uploads allowed again`, `[Bikes] bikes stay in the garage for now (...): a bike is
  selected or being driven`, and
  `[Bikes] multiplayer: sending the donor car (list position N) instead of a bike`.
- Trouble:
  - `[Bikes] hooks failed to install ...`: the bikes stay out (nothing reaches saves or leaderboards).
  - `[Bikes] <bike>: not added (...)`: a model or the donor car is missing.
  - `[Bikes] error (n/5)`, then `[Bikes] switched off for this session after repeated errors (the bikes leave the
    garage)`: the error breaker.

## Limits

- **No stoppies, wheelies or crashes (falling off) yet**; the bike can't lowside or highside: past the grip it runs wide.
- **The handling numbers are first guesses** (grip, lean rate, power): not tuned in game yet.
- The donor car's own stats still set the target speed it chases and its boosts.
- **Not tested in game yet.** Not tested:
  - how the bike sits on each donor car;
  - the garage turntable;
  - DriverCam's driver view on a bike and in the M2 (DriverCam 0.11.2 recognises `Bikes.Lean` / `Bikes.Car` and
    skips the donor car's cockpit; the M2's eye point is estimated from its interior mesh, not checked in game).
- **Which models are in the repo:** the M2 G87 model is (the user chose to publish it, under its CC BY-NC-SA licence).
  The two bike models stay local (`.gitignore`).

## Credits

- **Sport Bike:** ["Low Poly Motorcycle"](https://sketchfab.com/3d-models/low-poly-motorcycle-e03e5c442ab0492389df5a6f61a99f1f)
  by **pyrzegeclb** on Sketchfab, licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Changes: decimated
  to about 10,800 triangles, wheels split out, smooth-shaded, and the colours baked onto a new texture by
  `Assets/build_bike.py`.
- **BMW S1000RR:** ["BMW S1000 RR"](https://sketchfab.com/3d-models/bmw-s1000-rr-1873aed292d6465694119bd81860a0c2) by
  **VTX** (@VTX_car) on Sketchfab, licensed [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/): a
  hand-crafted model based on the real bike. Changes: small parts and unseen faces dropped, decimated, given flat
  colours (white / blue / red livery) and a see-through windscreen by `Assets/build_bike.py`.
- **M2 G87:** ["2026 Zacoe BMW G87 M2 Widebody Carbon Fiber Kit"](https://sketchfab.com/3d-models/2026-zacoe-bmw-g87-m2-widebody-carbon-fiber-kit-d15e7b05cd7543b19b36bdcbc5f27b59)
  by **Ddiaz Design** (sergiodd) on Sketchfab, licensed [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).
  Changes: engine and calipers removed, paint hidden under the kit removed, decimated to about 50,000 triangles
  (14,000 of them the interior), given flat colours, see-through glass and an interior texture made from the source's
  own interior textures by `Assets/build_car.py`. `Assets/BMW_M2_G87.csm` and `Assets/BMW_M2_G87_interior.jpg` are shared under the same CC BY-NC-SA 4.0 licence
  (non-commercial).