using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace RacingLine
{
    /// <summary>
    /// NPC traffic for the traffic-aware line (TrafficLine). Read only, at most every 0.1 s.
    ///
    /// Frames, all verified in GameAssembly.dll (IDA, 2026-10-03):
    /// - Distance: AIVehicleController.AvoidanceRoadDistance (0x18068D9C0) = pathFollower.DistanceTravelled, or
    ///   CurrentPath.TotalLength - DistanceTravelled when isReversePath. A reverse spawner drives its cars along
    ///   RoadPathGenerator.ReversePath (AISpawnerBase.OnPathCreated 0x18068CAD0), so either way it is metres along the
    ///   regular path, the same value AIPathFollower compares with PlayerPathFollower.distanceTravelled for rubber banding:
    ///   directly comparable with the player's GetDistanceTravelled().
    /// - Lateral: AvoidanceLaneOffset (0x18068D940) = laneHandler.CurrentLaneOffset. AIPathFollower.HandleMovement
    ///   (0x180689370) places the car at pathPosition + right x laneOffset, with right = transform.right (the path rotation,
    ///   LookRotation(tangent, up): right = Cross(up, tangent)), or -transform.right on a reverse path, whose cars face the
    ///   other way. So + = right of the REGULAR path's direction for every car: the same frame as Line.E (normal =
    ///   Cross(up, direction), GameApi.Sample) and as the player's GetLaneOffset() (0x18072A380:
    ///   follower.InverseTransformPoint(vehicle).x, the follower posed at LookRotation(tangent, up) by HandleForwardMovement).
    ///   No conversion needed.
    /// - Size: AvoidanceObjectSize (0x18068D960) = (vehicleCollider.size.x, vehicleCollider.size.z): x = width, y = length
    ///   (VehicleLength 0x18068DCA0 is size.z; the game clamps lengths to 5-25 m).
    /// - Speed: AvoidanceForwardVelocity (0x18068D8F0) = Speed x pedalFactor x curvatureFactor, exactly what HandleMovement
    ///   adds to DistanceTravelled per second along the car's own path. A reverse car's regular-path distance falls at that
    ///   rate, so its speed here is negated (oncoming: closing speed = player speed + its speed).
    /// - IsRacerVehicle (and AvoidanceIsStatic) share 0x18068D930 = "return false" on traffic cars, and racers are not in
    ///   activeAiCars, so there is nothing to skip there. Cars that were hit (AIPathFollower.WasHit) are skipped: physics
    ///   moves them, so their path distance and lane no longer say where they are.
    /// Il2CppInterop 1.5's Il2CppObjectPool hands back the same wrapper for the same native car, so walking the list
    /// allocates nothing for cars already seen.
    /// </summary>
    internal static partial class GameApi
    {
        internal static bool TrafficOk { get; private set; }

        private static MonoBehaviour _spawner;   // the DefaultAISpawner (TryCast result, null for another spawner type); untyped like _world
        private static IntPtr _spawnerPtr;

        internal static void CheckTraffic(Assembly asm, List<string> missing)
        {
            TrafficOk = Has(asm, "AISpawnerBase", missing, "Instance")
                     && Has(asm, "DefaultAISpawner", missing, "activeAiCars")
                     && Has(asm, "AIVehicleController", missing, "IsActive", "IsReversePath", "PathFollower", "AvoidanceRoadDistance",
                            "AvoidanceLaneOffset", "AvoidanceObjectSize", "AvoidanceForwardVelocity")
                     && Has(asm, "AIPathFollower", missing, "WasHit");
        }

        /// <summary>
        /// Copies the active, undamaged traffic cars whose road distance is within [-behind, +ahead] metres of the player
        /// into buf (at most buf.Length) and returns how many. 0 in menus, while loading, or with a non-default spawner.
        /// Only call when TrafficOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ReadTraffic(float playerDist, float behind, float ahead, TrafficCar[] buf)
        {
            var sp = AISpawnerBase.Instance;
            if (sp == null) { _spawner = null; _spawnerPtr = IntPtr.Zero; return 0; }
            if (_spawnerPtr != sp.Pointer)
            {
                _spawnerPtr = sp.Pointer;
                _spawner = sp.TryCast<DefaultAISpawner>();   // once per spawner object
            }
            if (_spawner == null) return 0;
            var cars = ((DefaultAISpawner)_spawner).activeAiCars;
            if (cars == null) return 0;

            int n = 0, count = cars.Count;
            for (int i = 0; i < count && n < buf.Length; i++)
            {
                var car = cars[i];
                if (car == null || !car.IsActive) continue;
                float road = car.AvoidanceRoadDistance;
                float rel = road - playerDist;
                if (!(rel >= -behind && rel <= ahead)) continue;   // also drops NaN
                var pf = car.PathFollower;
                if (pf == null || pf.WasHit) continue;
                float lane = car.AvoidanceLaneOffset;
                Vector2 size = car.AvoidanceObjectSize;
                float v = car.AvoidanceForwardVelocity;
                if (!Finite(lane) || !Finite(v) || !Finite(size.x) || !Finite(size.y)) continue;
                buf[n++] = new TrafficCar
                {
                    Id = (long)car.Pointer,
                    Road = road,
                    Lane = lane,
                    HalfWidth = Mathf.Clamp(size.x * 0.5f, 0.5f, 2f),
                    HalfLength = Mathf.Clamp(size.y * 0.5f, 1.5f, 12.5f),
                    Speed = car.IsReversePath ? -v : v,
                };
            }
            return n;
        }
    }
}
