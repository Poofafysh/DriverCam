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
