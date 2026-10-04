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
                ForgetCars();                                // its cars are new objects
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
                if (Daredevils.Owned.Contains(car.Pointer)) continue;
                // the distance test first (it drops most cars), then the cached per-car parts: the same tests as
                // before, all pure reads of one frame, so the same cars are found
                float ahead = car.AvoidanceRoadDistance - playerDist;
                if (ahead < minAhead || ahead > maxAhead || float.IsNaN(ahead)) continue;
                var info = Info(car);
                if (DaredevilOk && IsDaredevil(car, info)) continue;   // daredevils are rivals, never patrols
                var pf = PathFollower(car, info);
                if (pf == null || pf.WasHit) continue;
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

        /// <summary>
        /// The chase values of a path follower: rubber-banding switch, MaxSpeed (m/s), speedSmoothness (the SmoothDamp time
        /// HandleSpeed uses to reach TargetSpeed: lower = quicker acceleration) and behindDistanceDespawn (how far behind
        /// the player HandleDistanceFromPlayer returns it to the pool). All serialized fields of the pooled car: always
        /// captured first and given back (RestoreChase).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ReadChase(MonoBehaviour pfObj, out bool rubberBanding, out float maxSpeed, out float smoothness, out float behindDespawn)
        {
            var pf = (AIPathFollower)pfObj;
            rubberBanding = pf.rubberBandingEnabled;
            maxSpeed = pf.MaxSpeed;
            smoothness = pf.speedSmoothness;
            behindDespawn = pf.behindDistanceDespawn;
        }

        /// <summary>
        /// A unit joining a chase launches: its current Speed is raised to at least <paramref name="speed"/> (m/s, capped by
        /// its chase MaxSpeed), so it doesn't crawl up from traffic pace. Speed is the follower's running state (the game
        /// smooth-damps it towards TargetSpeed every frame and resets it on spawn), not a setting, so nothing to restore.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Launch(MonoBehaviour pfObj, float speed)
        {
            var pf = (AIPathFollower)pfObj;
            float v = Mathf.Min(speed, pf.MaxSpeed);
            if (!float.IsNaN(v) && v > pf.Speed) pf.Speed = v;
        }

        /// <summary>Writes speedSmoothness / behindDistanceDespawn (NaN = leave as is).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void WriteChaseExtras(MonoBehaviour pfObj, float smoothness, float behindDespawn)
        {
            var pf = (AIPathFollower)pfObj;
            if (!float.IsNaN(smoothness) && pf.speedSmoothness != smoothness) pf.speedSmoothness = smoothness;
            if (!float.IsNaN(behindDespawn) && pf.behindDistanceDespawn != behindDespawn) pf.behindDistanceDespawn = behindDespawn;
        }

        /// <summary>The car's box collider centre and size in car space (zero size if none).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void BoxOf(MonoBehaviour car, out Vector3 centre, out Vector3 size)
        {
            var box = ((AIVehicleController)car).VehicleCollider;
            if (box == null) { centre = Vector3.zero; size = Vector3.zero; return; }
            centre = box.center; size = box.size;
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

        internal static void ForgetSpawner() { _spawner = null; _spawnerPtr = IntPtr.Zero; ForgetCars(); }

        // ------------------------------------------------------------------ per-car parts, cached (0.6.0 perf)
        // Every getter on a traffic car is a native call and every one that returns an object makes a new wrapper. A
        // traffic car's path follower, box collider and skin selector are its own components for its whole life (the
        // pool reuses the same car object), so the scans keep their wrappers by car pointer instead of fetching three
        // new ones per car per scan. Their VALUES (WasHit, the box size, the behaviour type) are still read every time,
        // so every scan sees exactly what it saw before. A cached component that is gone (== null) is fetched again, and
        // the whole table is dropped when the spawner changes, every CarsLifetime seconds and when it grows past MaxCars.

        private sealed class CarParts { public MonoBehaviour Pf, Sel; public BoxCollider Box; }

        private const float CarsLifetime = 5f;
        private const int MaxCars = 512;
        private static readonly Dictionary<IntPtr, CarParts> s_cars = new Dictionary<IntPtr, CarParts>();
        private static float s_carsUntil;

        private static void ForgetCars() { s_cars.Clear(); s_carsUntil = 0f; }

        private static CarParts Info(AIVehicleController car)
        {
            float now = Time.unscaledTime;
            if (now >= s_carsUntil || now < s_carsUntil - 2f * CarsLifetime || s_cars.Count >= MaxCars) { s_cars.Clear(); s_carsUntil = now + CarsLifetime; }
            var key = car.Pointer;
            if (!s_cars.TryGetValue(key, out var info)) { info = new CarParts(); s_cars[key] = info; }
            return info;
        }

        /// <summary>car.PathFollower through the cache (null when it has none, exactly as the getter).</summary>
        private static AIPathFollower PathFollower(AIVehicleController car, CarParts info)
        {
            if (info.Pf != null) return (AIPathFollower)info.Pf;
            var pf = car.PathFollower;
            if (pf != null) info.Pf = pf;
            return pf;
        }

        /// <summary>car.VehicleCollider through the cache.</summary>
        private static BoxCollider Box(AIVehicleController car, CarParts info)
        {
            if (info.Box != null) return info.Box;
            var box = car.VehicleCollider;
            if (box != null) info.Box = box;
            return box;
        }

        /// <summary>car.SkinSelector through the cache.</summary>
        private static AISkinSelector Selector(AIVehicleController car, CarParts info)
        {
            if (info.Sel != null) return (AISkinSelector)info.Sel;
            var sel = car.SkinSelector;
            if (sel != null) info.Sel = sel;
            return sel;
        }
    }
}
