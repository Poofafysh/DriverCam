# Bikes

BepInEx 6 IL2CPP plugin for **Driving Rogue**: new vehicles in the garage, after the game's cars. Two sport motorcycles,
the **BMW S1000RR** and the blue **Sport Bike**, and a car, the **M2 G87** widebody (0.2.0).

Current version: **0.2.0** (bikes phase 1: selectable, look right, spin their wheels and lean into corners;
the rider comes from the Driver plugin; bike handling and a narrow body come later; see the design doc "Sport Bikes:
Lean, Grip and Braking")

## What it does

- **New garage vehicles.** Each has its own name and stats:

  | Vehicle | Speed | Accel | Handling | Durability |
  |---|---|---|---|---|
  | BMW S1000RR | 0.95 | 0.90 | 0.75 | 0.30 |
  | Sport Bike | 0.85 | 0.95 | 0.85 | 0.35 |
  | M2 G87 (car) | 0.82 | 0.80 | 0.85 | 0.65 |

  They're always unlocked and cost nothing.
- **A bike on a hidden car.** Underneath, each bike drives on a copy of a donor car (`DonorCar`, default Saber): the
  game's own driving, four physics wheels and collider. That's why traffic, scoring, daredevils, police, RacingLine,
  damage and the timer all keep working. The bike also uses the donor's parts and vinyl groups (they don't show on the
  bike), trait and engine sound.
- **The look.** The bike model is shown at real size (about 2 m long), centred between the hidden car's axles, with its
  wheels on the road. The front wheel spins and steers with the game's front-left wheel, the rear wheel spins with the
  rear-left. The bike leans into corners by the physical lean for its speed and turn rate
  (tan lean = speed x yaw rate / g), up to `MaxLean` (50°), and stays upright at a crawl. The car's own meshes, and any
  part the game adds, are hidden.
- **The M2 G87** is a car model fitted the CarSkins way: scaled so its wheelbase matches the donor's, front axle on the
  donor's front axle, its four wheels on the donor's own wheel pivots (they spin and steer with them). No lean.
- **Models:**
  - the bikes are each about 3,600 triangles with one 1024 px texture, built by `Assets/build_bike.py` (Blender);
  - the M2 is about 11,000 triangles in flat colours (paint, carbon, glass, lights, tyres, rims), built by
    `Assets/build_car.py`.

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
| `Look.MaxLean` | 50 | Largest lean in corners, degrees (0-65) |
| `Look.LeanScale` | 1 | Lean strength (1 = physical lean) |
| `State.RodeBikeThisRun` | false | Written by the plugin: the current run used a bike (leaderboard uploads skipped). Not a setting |

## Log (`/game-log Bikes`)

- `Bikes x.y.z loaded: 10 hooks; the bikes are built when the garage opens (donor car Saber; single-player, off the leaderboards).`
- `[Bikes] model SportBike loaded (bike): N triangles, wheelbase 1.41 m` (the same for BMW_S1000RR, and
  `loaded (car)` for BMW_M2_G87).
- `[Bikes] Sport Bike body: N car meshes to hide, bike at real size (...)` and
  `[Bikes] Sport Bike built on the Saber (id rogue.bikes.sportbike, stats ...)`; for the M2:
  `[Bikes] M2 G87 body: N car meshes to hide, car model at scale S (wheelbase G vs M)`.
- `[Bikes] 3 bike(s) added to the garage after the N cars (garage opened)`, and
  `[Bikes] bikes taken out of the garage (multiplayer)` / `(switched off)`.
- `[Bikes] driving Sport Bike: bike model, wheels follow the game's, steering follows` the first time you drive one
  (`car or missing model` for the M2).
- `[Bikes] this run uses a bike: it stays off the leaderboards`, `[Bikes] leaderboard upload skipped: this run used a bike`,
  `[Bikes] new run on a car: leaderboard uploads allowed again`, `[Bikes] bikes stay in the garage for now (...): a bike is
  selected or being driven`, and
  `[Bikes] multiplayer: sending the donor car (list position N) instead of a bike`.
- Trouble:
  - `[Bikes] hooks failed to install ...`: the bikes stay out (nothing reaches saves or leaderboards).
  - `[Bikes] <bike>: not added (...)`: a model or the donor car is missing.
  - `[Bikes] error (n/5)`, then `[Bikes] switched off for this session after repeated errors (the bikes leave the
    garage)`: the error breaker.

## Limits (phase 1)

- **Handling is the donor car's.** No lean steering, stoppies or wheelies yet (design doc phases 2-3).
- **The hit box is car-sized**, so you can't filter through traffic yet.
- **No rider yet.** It will come from the Driver plugin with a sport-bike pose.
- **Not tested in game yet.** Not tested:
  - how the bike sits on each donor car;
  - the garage turntable;
  - DriverCam's driver view on a bike (it fits a cockpit to the donor car).
- **Which models are in the repo:** the M2 G87 model is (the user chose to publish it, under its CC BY-NC-SA licence).
  The two bike models stay local (`.gitignore`).

## Credits

- **Sport Bike:** ["Low Poly Motorcycle"](https://sketchfab.com/3d-models/low-poly-motorcycle-e03e5c442ab0492389df5a6f61a99f1f)
  by **pyrzegeclb** on Sketchfab, licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Changes: decimated
  to about 3,600 triangles, wheels split out, and the colours baked onto a new texture by `Assets/build_bike.py`.
- **BMW S1000RR:** ["BMW S1000 RR"](https://sketchfab.com/3d-models/bmw-s1000-rr-1873aed292d6465694119bd81860a0c2) by
  **VTX** (@VTX_car) on Sketchfab, licensed [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/): a
  hand-crafted model based on the real bike. Changes: small parts dropped, remeshed, decimated and re-textured by
  `Assets/build_bike.py`.
- **M2 G87:** ["2026 Zacoe BMW G87 M2 Widebody Carbon Fiber Kit"](https://sketchfab.com/3d-models/2026-zacoe-bmw-g87-m2-widebody-carbon-fiber-kit-d15e7b05cd7543b19b36bdcbc5f27b59)
  by **Ddiaz Design** (sergiodd) on Sketchfab, licensed [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).
  Changes: interior, engine and calipers removed, decimated to about 11,000 triangles and given flat colours by
  `Assets/build_car.py`. `Assets/BMW_M2_G87.csm` is shared under the same CC BY-NC-SA 4.0 licence (non-commercial).