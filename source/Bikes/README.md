# Bikes

BepInEx 6 IL2CPP plugin for **Driving Rogue**: two sport motorcycles as new vehicles in the garage. The **BMW S1000RR**
and the blue **Sport Bike** come after the game's cars.

Current version: **0.1.0** (phase 1: the bikes are selectable, look right, spin their wheels and lean into corners;
bike handling, a narrow body and a rider come later; see the design doc "Sport Bikes: Lean, Grip and Braking")

## What it does

- **Two new garage vehicles.** Each bike has its own name and stats:

  | Bike | Speed | Accel | Handling | Durability |
  |---|---|---|---|---|
  | BMW S1000RR | 0.95 | 0.90 | 0.75 | 0.30 |
  | Sport Bike | 0.85 | 0.95 | 0.85 | 0.35 |

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
- **Models:** each about 3,600 triangles with one 1024 px texture, built from the downloaded models by
  `Assets/build_bike.py` (Blender).

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

  The seats come from the real bikes' specs and the Sport Bike model's seat top (0.896 m). The pegs sit about 0.2 m
  behind the seat for a sport-bike tuck.

## Kept out of your records

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

- `Bikes 0.1.0 loaded: 10 hooks; the bikes are built when the garage opens (donor car Saber; single-player, off the leaderboards).`
- `[Bikes] model BMW_S1000RR loaded: N triangles, wheelbase 1.44 m` (the same for SportBike).
- `[Bikes] BMW S1000RR body: N car meshes to hide, bike at real size (...)` and
  `[Bikes] BMW S1000RR built on the Saber (id rogue.bikes.s1000rr, stats ...)`.
- `[Bikes] 2 bike(s) added to the garage after the N cars (garage opened)`, and
  `[Bikes] bikes taken out of the garage (multiplayer)` / `(switched off)`.
- `[Bikes] riding BMW S1000RR: bike model found, wheels follow the game's, steering follows` the first time you drive
  one.
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
- **Licences.** The two models came from Sketchfab downloads, and their author and licence are not recorded yet. They
  are kept out of the repo (`.gitignore`) until that's checked. The S1000RR looks converted from another game's mod.
