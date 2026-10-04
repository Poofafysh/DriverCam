# Curb / wall collision — how it actually works

Sources: Unity Project rip (`E:\DrivingRogue_UnityProject`), Il2CppDumper dump, IDA decompile of `GameAssembly.dll`.

## 1. There is no physical curb

- Each road tile is its own additive scene (`RoadTileSO.targetScene`). Every tile contains:
  - `RoadGroundCollider` / `PresetRoad` — MeshCollider, layer **Street (11)**, **perfectly flat (y = 0 everywhere)**. It extends ~4–5 m beyond the road edge under the sidewalks.
  - `Guardrail_Regular` (+ `… Batch 1`) and `Guardrail_Wider_1` (+ `… Batch 1`) — MeshColliders, layer **Guardrail (15)**, renderer **disabled**, **vertical walls from y = −1 m to y = +15 m**.
- The visible curb / sidewalk (`N_1.1_RoadSide`, `Sides_1.1`) has **no collider**. Sidewalk props (benches, lamps, bikes, planters) also have **no colliders** in the tiles checked.

Measured cross-section (tile `634ff06c…`, 132 samples along the road edge):

| Outward from visible road edge | Thing |
|---|---|
| 0 m | edge of `PresetRoad` (curb face) |
| **+0.50 m** (p10 0.42, p90 0.73) | `Guardrail_Regular` wall — the one you hit |
| +1.50 m (p10 1.42, p90 1.74) | `Guardrail_Wider_1` wall (redundant backup) |

### Where the visible curb really is (16 tiles, 311 samples)

The visible curb face is part of the `*_RoadSide` visual meshes: a ~0.35-0.42 m step after a flat gutter strip.

| Measurement | Median | p10-p90 / per tile |
|---|---|---|
| Curb face outward from `Guardrail_Regular` | **1.09 m** | per-tile medians 1.08-1.59 m |
| `Guardrail_Wider` minus curb face | **0.17 m** | tracks the curb closely on most tiles |
| Regular ↔ Wider separation | 1.3-2.0 m | min 1.0, max 2.8 |

So in tile `634ff06c` the full cross-section is: asphalt edge 0 → inner wall +0.5 → curb face ≈ +1.4 → outer wall +1.5. The active wall is about **1 m before the visible curb**. CurbFeel estimates the curb per vertex as `Wider − 0.15 m`.

## 2. What on the car touches the wall

Layer collision matrix (`ProjectSettings/DynamicsManager.asset`):
- Guardrail ↔ Player, Vehicle, BarrierCollider, Street, Obstacles, Racer, …
- Wheel (21) ↔ **Street only** (so the low "ground ring" never touches walls)
- BarrierCollider (16) ↔ **Guardrail only**

Car colliders (heights relative to road, y=0; from `PlayerVehicle_Base` + `*_Body` prefabs):

| Collider | Layer | Shape | Lateral reach from centreline |
|---|---|---|---|
| Barrier Collider (Joint) — `VehicleBarrierCapsule` + ConfigurableJoint (X/Z drive spring 100000) | BarrierCollider | capsule r 1.7 | **±1.70 m** |
| Barrier Left / Right | BarrierCollider | capsule r 0.6 at x ±1.0 | **±1.60 m** |
| Barrier Collider (Static) | BarrierCollider | capsule r 1.2 | ±1.20 m |
| `Collider` / `ColliderOld` | Player | capsule r 1.05 / 1.1 | ±1.05–1.10 m |
| Wheels (visual) | — | — | outer edge ≈ ±1.0 (Bond) – ±1.15 (Saber) m |

**Result:** the guardrail hull reaches 0.5–0.7 m past the tyres. With the wall 0.5 m past the curb, contact happens while the tyre is still ~0–0.2 m *inside* the road edge. That is the "hard wall at the curb".

## 3. What happens on contact (`VehicleDamage.OnCollision` @ 0x18075B740)

1. `isHeadOn = CheckHeadOnCollision()` (@ 0x180759EB0): `|90° − angle(contactNormal, carForward)| > headOnAngle` (**20°**).
2. If the other object's layer is in `VehicleBaseParameters.guardrailsLayer` (+0x48) and `time − lastCollisionTime ≥ collisionTimeThreshold` (**0.25 s**):
   - `HandleBigCollision(speedReduction, wallBaseDamage×overall, collision, CollisionType.Graze /*always 0 for walls*/, usingShield)`
   - speedReduction = isHeadOn ? `headOnWallCollisionSpeedReductionFactor` (**0.25**) : `grazingCollisionSpeedReductionFactor` (**0.08**)
3. `HandleBigCollision` (@ 0x18075A6A0):
   - `VehicleMovement.RequestReduceSpeed(speedReduction)` — lose 8 % / 25 % of speed
   - `damage = collisionDamageBySpeedFactor.Evaluate(speedFactor) × (grazeDamageMultiplier × overall) × (wallBaseDamage × overall)`
     = curve × (0.26 × 0.8) × (17 × 0.8) ≈ **2.83 HP × curve**
   - curve keys: (0 → 0), (0.01 → 0.1), (1 → 1) — even a crawl does 10 %
   - `VehicleHealth.TakeDamage(damage)` (card `DamageMultiplier` etc. applied inside)
   - camera shake, sparks, `ResetDrifting`
4. Sustained contact: `CheckWallContinuousDamage` (@ 0x18075A3F0) — every **0.5 s** while `barrierContinuousStartTime` is set: `TakeDamage(wallDamagePerSecond × overall)` = 0.22 × 0.8 = **0.176 HP per tick**.
5. Push-off: `VehicleMovement.bounceOffGuardrailMultiplier` **0.2**, `bounceOffDampMaxTime` 0.7 s, `bounceOffDampMaxVelocity` 1.

Relevant `VehicleBaseParameters` offsets: guardrailsLayer 0x48, overallDamageMultiplier 0x23C, wallBaseDamage 0x240, wallDamagePerSecond 0x244, headOnDamageMultiplier 0x250, grazeDamageMultiplier 0x254, collisionDamageBySpeedFactor 0x258, grazingCollisionSpeedReductionFactor 0x260, headOnWallCollisionSpeedReductionFactor 0x264.

Non-wall obstacles use `collisionInfoList`: ObstacleSmall (20) 17 dmg / 10 % speed, ObstacleMedium (22) 22 / 15 %, TrafficSmall (0) 13 / 10 %, TrafficBig (5) 20 / 20 %, Cone (30) 0 / 5 %, Sign (31) 0 / 8 %.

## 3b. Traffic contacts (lane splitting)

- Traffic: `AIVehicleController` + `BoxCollider` on layer **Vehicle (10)**. `AISkinSelector.InitializeSkin` (@ 0x18068BE70) copies the model's own box (`AIVehicleSkin.boxCollider`) size/center onto the controller box and disables the model box.
- Model boxes vs visible body: Taxi 2.22 / 2.33 m, Mini 2.50 / 2.76, Subaru Libero 1.90 / 2.17, Bus 2.86 / 2.87, Golf 2.52, M3 2.34, Range Rover 2.52, Fiesta 2.13, Minivan 2.71. Already at or inside the visuals.
- Player side: Player-layer capsules r 1.05 / 1.1 (2.1-2.2 m wide) vs a ~2.55 m body. Also already inside the visuals.
- Road: `RoadTileContainer.roadWidth` 20 m, `roadLaneCount` 4, so likely 5 m lanes, which leaves ~2.5-2.8 m between two cars in adjacent lanes. CurbFeel logs the real offsets (`[Traffic] lane offsets in use`).
- **The real blocker:** `VehicleDamage.HandleVehicleCollision` (@ 0x18075AD40) runs on *every* traffic contact. It applies `RequestReduceSpeed`, damage = curve(speed) × graze/head-on multiplier × base, `ResetDrifting` (combo lost), camera shake and possible ghost phasing. There is no light-touch case.
- Near-miss: `VehicleStuntHandler.withinNearMissExtraRange` 0.1 m, `nearMissBodyRotationMultiplier` 1.4. `npcContinuousCollisionToGlitch` 3 s.

## 4. Levers for "ride up on the curb a little"

| Lever | Effect | Risk |
|---|---|---|
| A. Shrink the car's BarrierCollider/Player capsules to the bodywork | Wheels can cross the curb line ~0.3–0.5 m before contact | Low. Local physics only |
| B. Push `Guardrail_Regular` outward (or disable it and rely on `Guardrail_Wider` at +1.5 m) | More sidewalk room | Props have no colliders, so too much = driving through lamp posts |
| C. Retune damage/speed loss for shallow scrapes (`grazeDamageMultiplier`, `grazingCollisionSpeedReductionFactor`, `wallDamagePerSecond`, curve) or skip damage for shallow-angle wall contacts | Scraping the curb stops being punishing | Low (data values) |
| D. Add an invisible bevelled "curb ramp" MeshCollider on the Street layer along the road edge, generated from `PresetRoad`'s boundary | Wheel raycasts + ground ring physically climb the curb (real ride-up feel) | Medium. Needs testing per tile |

## 5. Walls vs the visible map (54-tile census, 2026-10-03)

All 54 tile scenes of the AssetRipper export were parsed (each tile has four biome groups: Residential, Commercial,
Industrial, Park; one is active at runtime), meshes decoded from the `.asset` YAML, and rays traced against real
triangles at 0.75 / 1.1 m from the road-facing side of the 2 m thick wall slabs. Distances from the curb face,
positive outward (p10 / p50 / p90).

**Where the curb is**, relative to the inner face of `Guardrail_Wider`:

| Street type | Sidewalk meshes | Curb face | Curb |
|---|---|---|---|
| Residential / Commercial | `Sides_*`, `Base_*` (slab y 0.38, ~6.2 m deep), `Sidewalk_Mesh*`, `Sidewalk_Plane*` | **-1.70 m** (p10 -2.04, p90 -1.55), ~0.7 m on the road side of `Guardrail_Regular` | 0.21 m lip rising to 0.40 m |
| Park / Industrial | `N_/E_/H_/P_*_RoadSide` | **+0.10 m** (p10 -0.12, p90 +0.4), after a 1.6 m gutter | 0.35-0.42 m high, ~0.4 m wide |

So the old `Wider - 0.15` rule was right on park / industrial roads but ~1.55 m too far out on city streets: walls
ended ~4.5 m past the curb there, inside a solid object at 28% (Residential) / 20% (Commercial) of points.

**What stands beside the road:**

| Object | Size L x H | Biome | From the curb |
|---|---|---|---|
| `guardrail_1 (N)` (guardrail_2.prefab), visible | 7.2 x 1.35 segments | Res / Com | 0.54 / 1.5 / 7.2 (Res), 0.74 / 2.6 / 9.8 (Com) |
| `GuardRailGenerator_*_bakedHDA` | one 120-200 m mesh, 2.5 high | Ind | 0.62 / 0.81 / 1.04 |
| `fencegenerator_*`, `i_metal_fence*`, `i_metal_gate*`, `i_brick*` | 3-200 m | Ind | 5.3 / 6.5 / 7.0 |
| `RockWall*` (+ `_Tall` 18 m) | 2.1 x 1.5 | Park | 0.07 / 0.18 / 0.42 (on the curb line) |
| `StoneBarricade_*`, `Park_WoodBars` | 2.7-4.2 x 0.9-2.5 | Park / Ind | 0.2-6.8 |
| `side_pole` bollards | 0.37 x 1.29 | Res / Com | |
| `CementBlock*` planters | 2.5 x 0.85 (tree), 0.8 x 0.3 (bush) | all | 0.46 / 1.6 / 7.5 (Res) |
| buildings | | Res / Com | 2.0 / 5.5 / 9.2 (Res), 2.0 / 6.8 / 10.8 (Com) |
| `Cliff*`, `*_Tunnel` | 21-76 m | Park | |

Not solid but treated as solid before: `Light_Cone` (9 x 9 x 8 m volumetric lamp cones), `CityTree` canopies (7.7-16 m),
billboards, `Bush`, `GrassRW`. Tiles without a curb: river / bridge tiles (`3e971f71`, `59489cc0`: embankment, fence
2-3 m below the road), tunnel sections (Park), `6cbf107c`, `e7941c7b`. `475004f4` (garage) has its own BoxCollider
walls on layer 15 ("Inner Wall Collider", "Sidewalk Collider"), which CurbFeel leaves alone.

**Rules CurbFeel 0.5 uses** (SidewalkMap): curb from the sidewalk mesh's first steep face 0.1-0.16 m above the road
(fallbacks -1.70 / +0.10 by street style); obstacles = visible guardrails, rails, fences, rock walls, barricades, wood
bars, bollards, planters, bus stops, tunnels, cliffs, walls (by name, any size) plus generic scenery at least 0.9 m
tall and 1.5 m long; oriented boxes from mesh bounds, real triangles for long hard meshes (> 12 m); tree trunks as
0.6 m posts; lamps, signs, cones, foliage, billboards, decals, terrain ignored. No size cap, no "box contains the probe"
skip (inside = hit at distance 0). Probes start at each wall cross-section's road face.
