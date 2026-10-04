using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Police
{
    /// <summary>One traffic car as the police logic sees it. Read each tick for our (at most a few) patrols only.</summary>
    internal struct CarState
    {
        public bool Active;         // AIVehicleController.IsActive (isActiveAndEnabled: false once back in the pool)
        public float Road;          // AvoidanceRoadDistance: metres along the run's path, same frame as the player's distance
        public float Speed;         // AvoidanceForwardVelocity: m/s actually driven (Speed x pedal x curvature factors)
        public float Lane;          // AvoidanceLaneOffset: lane offset in metres
        public float Travelled;     // AIPathFollower.DistanceTravelled (raw; a jump = the pool reused the car)
        public bool WasHit;         // AIPathFollower.WasHit: crashed (stops for good)
    }

    /// <summary>
    /// Traffic (all verified in GameAssembly.dll):
    /// - AIVehicleController.AvoidanceRoadDistance = PathFollower.DistanceTravelled (TotalLength - it on a reverse path),
    ///   the same value AIPathFollower.GetDistance() uses for its own rubber banding against
    ///   PlayerPathFollower.distanceTravelled, so it is directly comparable with the player's GetDistanceTravelled().
    /// - AvoidanceForwardVelocity = Speed x pedalFactor x curvatureFactor; HandleMovement adds exactly that x dt to
    ///   DistanceTravelled, so it is the car's real speed along the road in m/s.
    /// - AIPathFollower.HandleSpeed: TargetSpeed = MaxSpeed, or with rubberBandingEnabled a lerp towards
    ///   MaxSpeed x rubberBandSpeedFactor ahead of the player / MaxSpeed x behindSpeedFactor behind; Speed smooth-damps to
    ///   it. It never writes MaxSpeed. MaxSpeed is set by SetVehicle (spawn / pool reuse) and shifted by
    ///   -/+ 0.2 x originalMaxSpeed when the player's slow motion starts / ends.
    /// - HandleDistanceFromPlayer releases a car to the pool when it is more than behindDistanceDespawn behind (or
    ///   aheadDistanceDespawn ahead of) the player.
    /// </summary>
    internal static partial class GameApi
    {
        private static MonoBehaviour _spawner;   // the DefaultAISpawner (TryCast result), untyped like every game object here
        private static IntPtr _spawnerPtr;

        /// <summary>The current traffic spawner's pointer, or zero (menus, loading, a non-default spawner). Only call when TrafficOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IntPtr Spawner()
        {
            var sp = AISpawnerBase.Instance;
            if (sp == null) { _spawner = null; _spawnerPtr = IntPtr.Zero; return IntPtr.Zero; }
            if (_spawner == null || _spawnerPtr != sp.Pointer)
            {
                _spawnerPtr = sp.Pointer;
                _spawner = sp.TryCast<DefaultAISpawner>();   // once per spawner object
            }
            return _spawner != null ? _spawnerPtr : IntPtr.Zero;
        }

        /// <summary>
        /// Fills <paramref name="found"/> with active, undamaged traffic cars whose road distance is
        /// [<paramref name="minAhead"/>, <paramref name="maxAhead"/>] metres ahead of the player, skipping cars in
        /// <paramref name="exclude"/> and cars in the player's lane within <paramref name="laneClear"/> metres.
        /// Walks the spawner's list once (copied into a local reference first). Only call when TrafficOk, after Spawner().
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void FindCandidates(float playerDist, float playerLane, float minAhead, float maxAhead, float laneClear,
                                            HashSet<IntPtr> exclude, List<MonoBehaviour> found)
        {
            found.Clear();
            if (_spawner == null) return;
            var cars = ((DefaultAISpawner)_spawner).activeAiCars;
            if (cars == null) return;
            int count = cars.Count;
            for (int i = 0; i < count; i++)
            {
                var car = cars[i];
                if (car == null || exclude.Contains(car.Pointer) || !car.IsActive) continue;
                var pf = car.PathFollower;
                if (pf == null || pf.WasHit) continue;
                float ahead = car.AvoidanceRoadDistance - playerDist;
                if (ahead < minAhead || ahead > maxAhead || float.IsNaN(ahead)) continue;
                if (ahead < laneClear && Mathf.Abs(car.AvoidanceLaneOffset - playerLane) < 1.5f) continue;
                found.Add(car);
            }
        }

        /// <summary>The car's AIPathFollower (fetch once per patrol and keep it). Null if it has none.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MonoBehaviour PathFollowerOf(MonoBehaviour car) => ((AIVehicleController)car).PathFollower;

        /// <summary>The car's own box collider (for its height), or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Collider ColliderOf(MonoBehaviour car) => ((AIVehicleController)car).VehicleCollider;

        /// <summary>Reads one patrol car. False if it is gone (destroyed with the scene). Only call when TrafficOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ReadCar(MonoBehaviour carObj, MonoBehaviour pfObj, ref CarState s)
        {
            if (carObj == null || pfObj == null) return false;
            var car = (AIVehicleController)carObj;
            var pf = (AIPathFollower)pfObj;
            s.Active = car.IsActive;
            s.Road = car.AvoidanceRoadDistance;
            s.Speed = car.AvoidanceForwardVelocity;
            s.Lane = car.AvoidanceLaneOffset;
            s.Travelled = pf.DistanceTravelled;
            s.WasHit = pf.WasHit;
            return true;
        }

        /// <summary>The path follower's rubber-banding switch and MaxSpeed (m/s).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ReadChase(MonoBehaviour pfObj, out bool rubberBanding, out float maxSpeed)
        {
            var pf = (AIPathFollower)pfObj;
            rubberBanding = pf.rubberBandingEnabled;
            maxSpeed = pf.MaxSpeed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float ReadMaxSpeed(MonoBehaviour pfObj) => ((AIPathFollower)pfObj).MaxSpeed;

        /// <summary>Writes the rubber-banding switch, and MaxSpeed unless it is NaN. Local car only (single-player).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void WriteChase(MonoBehaviour pfObj, bool rubberBanding, float maxSpeed)
        {
            var pf = (AIPathFollower)pfObj;
            if (pf.rubberBandingEnabled != rubberBanding) pf.rubberBandingEnabled = rubberBanding;
            if (!float.IsNaN(maxSpeed)) pf.MaxSpeed = maxSpeed;
        }

        internal static void ForgetSpawner() { _spawner = null; _spawnerPtr = IntPtr.Zero; }
    }
}
