using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Police
{
    /// <summary>One traffic car in the racing line's frame (Daredevils' avoidance snapshot).</summary>
    internal struct RoadCar
    {
        public IntPtr Ptr;
        public float Road;         // metres along the run's regular path (AvoidanceRoadDistance)
        public float Lane;         // metres from the centre line, + = right (AvoidanceLaneOffset)
        public float HalfWidth, HalfLength;
        public float Speed;        // m/s in the player's direction (- = oncoming)
        /// <summary>
        /// The sideways band the car may occupy soon (metres, + = right): its lane now and where it is heading (a lane
        /// change). ReadRoad sets both to Lane; a tracker may widen them from the car's sideways speed. The driving code
        /// keeps clear of the whole band.
        /// </summary>
        public float LaneLo, LaneHi;
        public bool Wreck;         // crashed (AIPathFollower.WasHit): a stopped obstacle with a widened box (see ReadRoad)
    }

    /// <summary>
    /// Daredevils (all verified in GameAssembly.dll with IDA, 2026-10-03):
    /// - A daredevil is an ordinary pooled traffic car whose AISkinSelector.CarBehaviorType is Daredevil (2): picked by
    ///   DefaultAISpawner.OnGetVehicleFromPool from RunRaceSO.aiBehaviors counts, then AIPathFollower.SetVehicle gives it
    ///   MaxSpeed = speedFactor (1.8) x 27.8 m/s, skips rubber banding for it (HandleSpeed) and, on the forward path, turns
    ///   on its AIPlayerWarning (the devil icon + honk when it is within 100 m behind the player). A pooled car keeps its
    ///   first skin and type (InitializeSkin does nothing once a skin exists), so the type is a stable flag.
    /// - Lateral position: AIPathFollower.HandleMovement (FixedUpdate) puts the car at path point + right x
    ///   laneHandler.CurrentLaneOffset; AIVehicleLaneHandler.CheckLaneOffset (Update) eases CurrentLaneOffset from
    ///   previousLaneOffset to targetLaneOffset only while they differ. Writing all three (and LaneTransitionSpeedMultiplier
    ///   = 1, initialLane = CurrentLaneIndex so it never "returns to its lane") in LateUpdate makes our offset the one the
    ///   next FixedUpdate uses. The devil icon and other cars read CurrentLaneOffset, so they follow. HandleMovement also
    ///   turns the car by MyAngle, which CheckLaneOffset only zeroes when a lane change completes: held at 0 while we steer.
    /// - Handing a car back (HandBack): initialLane restored, then MoveToLaneIndex(CurrentLaneIndex), the game's own eased
    ///   lane change from wherever the car is (it sets previousLaneOffset = CurrentLaneOffset; it does nothing for a crashed
    ///   car), whose completion zeroes MyAngle and ends the lane-change bookkeeping.
    /// - Speed: HandleSpeed smooth-damps Speed to TargetSpeed = MaxSpeed (x LaneTransitionSpeedMultiplier); HandleMovement
    ///   moves Speed x pedalFactor x curvatureFactor. curvatureFactor eases to targetCurvatureFactor = 1 - curvature/5
    ///   (Update); we hold both at 1 because the racing line's own speed profile already slows for corners. pedalFactor /
    ///   targetPedalFactor (AIObstructionDetector.UpdateObstructionInFront: a gentle, long-range slow-down behind any car
    ///   whose AvoidanceLaneRange overlaps ours, our offset included) are held at 1 too unless asked otherwise: the
    ///   rival's own braking envelope replaces it (later and harder braking, like a racing driver). Daredevils keep the
    ///   game's braking on as a backup when their traffic snapshot was full.
    /// - Despawn: HandleDistanceFromPlayer releases a car behindDistanceDespawn behind / aheadDistanceDespawn ahead of the
    ///   player; raised while a daredevil is a rival (WriteDespawn), the captured values given back when it is let go.
    /// - Crashes: the collision handler (0x1807C5D30) calls AIPathFollower.SetWasHit(true) (its only caller);
    ///   SetVehicle (pool reuse, 0x18068A630) clears WasHit (field +0xB8). So a wreck the game returns to the pool should
    ///   still read WasHit = true until the pool hands it out again (2026-10-04; other direct writers of +0xB8 not
    ///   searched). Daredevils also keep their own crashed flag, so a release never depends on it.
    /// The serialized fields we change (MaxSpeed, which SetVehicle rewrites on reuse anyway; speedSmoothness;
    /// rubberBandingEnabled is only written back) are captured first and given back when we let the car go. A rival's top
    /// speed comes from originalMaxSpeed (SetVehicle's value, untouched by the player's slow motion).
    /// </summary>
    internal static partial class GameApi
    {
        internal static bool DaredevilOk { get; private set; }

        private const int DaredevilType = 2;   // CarBehaviorType.Daredevil

        internal static void CheckDaredevil(Assembly asm, List<string> missing)
        {
            DaredevilOk = TrafficOk
                       && Has(asm, "AIVehicleController", missing, "SkinSelector", "LaneHandler", "IsReversePath")
                       && Has(asm, "AISkinSelector", missing, "CarBehaviorType")
                       && Has(asm, "AIVehicleLaneHandler", missing, "CurrentLaneOffset", "previousLaneOffset", "targetLaneOffset",
                              "LaneTransitionSpeedMultiplier", "initialLane", "CurrentLaneIndex", "MyAngle", "MoveToLaneIndex")
                       && Has(asm, "AIPathFollower", missing, "curvatureFactor", "targetCurvatureFactor", "originalMaxSpeed",
                              "pedalFactor", "targetPedalFactor", "aheadDistanceDespawn");
        }

        /// <summary>True for a daredevil (a Police patrol is never picked from these). Only call when DaredevilOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsDaredevil(MonoBehaviour carObj)
        {
            var sel = ((AIVehicleController)carObj).SkinSelector;
            return sel != null && (int)sel.CarBehaviorType == DaredevilType;
        }

        /// <summary>IsDaredevil through the per-car cache (the type itself is read every time).</summary>
        private static bool IsDaredevil(AIVehicleController car, CarParts info)
        {
            var sel = Selector(car, info);
            return sel != null && (int)sel.CarBehaviorType == DaredevilType;
        }

        /// <summary>
        /// Adds forward-path daredevils within [-behind, +ahead] m of the player that aren't in <paramref name="known"/>
        /// and haven't crashed. Only call when DaredevilOk, after Spawner().
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void FindDaredevils(float playerDist, float behind, float ahead, HashSet<IntPtr> known, List<MonoBehaviour> found)
        {
            found.Clear();
            if (_spawner == null) return;
            var cars = ((DefaultAISpawner)_spawner).activeAiCars;
            if (cars == null) return;
            int count = cars.Count;
            for (int i = 0; i < count; i++)
            {
                var car = cars[i];
                if (car == null || known.Contains(car.Pointer) || !car.IsActive || car.IsReversePath) continue;
                // distance first (0.6.0 perf: it drops most cars before any part is fetched), then the cached parts;
                // the same pure reads of one frame as before, so the same cars are found
                float rel = car.AvoidanceRoadDistance - playerDist;
                if (!(rel >= -behind && rel <= ahead)) continue;
                var info = Info(car);
                if (!IsDaredevil(car, info)) continue;
                var pf = PathFollower(car, info);
                if (pf == null || pf.WasHit) continue;
                found.Add(car);
            }
        }

        /// <summary>True for a car on the oncoming (reverse) path. Only call when DaredevilOk (IsReversePath is checked there).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsReversePath(MonoBehaviour carObj) => ((AIVehicleController)carObj).IsReversePath;

        /// <summary>AIPathFollower.WasHit read now (false for a null or destroyed path follower).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool WasHitNow(MonoBehaviour pfObj)
        {
            try { return pfObj != null && ((AIPathFollower)pfObj).WasHit; }
            catch { return false; }
        }

        /// <summary>The car's lane handler (fetch once and keep it). Null if it has none.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MonoBehaviour LaneHandlerOf(MonoBehaviour carObj) => ((AIVehicleController)carObj).LaneHandler;

        /// <summary>
        /// Drives a daredevil for the next physics steps: lateral offset (m, + = right) and MaxSpeed (m/s). Holds every
        /// lane-change field on that offset so the game's own lane logic can't pull it elsewhere. Local car only.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Steer(MonoBehaviour pfObj, MonoBehaviour laneObj, float offset, float maxSpeed, bool ownBraking)
        {
            var pf = (AIPathFollower)pfObj;
            var lane = (AIVehicleLaneHandler)laneObj;
            lane.previousLaneOffset = offset;
            lane.targetLaneOffset = offset;
            lane.CurrentLaneOffset = offset;
            if (lane.LaneTransitionSpeedMultiplier != 1f) lane.LaneTransitionSpeedMultiplier = 1f;
            int idx = lane.CurrentLaneIndex;
            if (lane.initialLane != idx) lane.initialLane = idx;
            if (lane.MyAngle != 0f) lane.MyAngle = 0f;   // a lane change we took over (or the game started) leaves a turn
            pf.curvatureFactor = 1f;
            pf.targetCurvatureFactor = 1f;
            if (ownBraking)
            {
                pf.pedalFactor = 1f;      // the rival's braking envelope replaces the game's gentle obstruction braking
                pf.targetPedalFactor = 1f;
            }
            if (!float.IsNaN(maxSpeed)) pf.MaxSpeed = maxSpeed;
        }

        /// <summary>The despawn distances (serialized fields of the pooled car: captured, written, given back).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ReadDespawn(MonoBehaviour pfObj, out float behind, out float ahead)
        {
            var pf = (AIPathFollower)pfObj;
            behind = pf.behindDistanceDespawn;
            ahead = pf.aheadDistanceDespawn;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void WriteDespawn(MonoBehaviour pfObj, float behind, float ahead)
        {
            var pf = (AIPathFollower)pfObj;
            if (!float.IsNaN(behind) && pf.behindDistanceDespawn != behind) pf.behindDistanceDespawn = behind;
            if (!float.IsNaN(ahead) && pf.aheadDistanceDespawn != ahead) pf.aheadDistanceDespawn = ahead;
        }

        /// <summary>Cuts the car's running speed to at most <paramref name="max"/> m/s at once (Speed is state, not a setting).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ClampSpeed(MonoBehaviour pfObj, float max)
        {
            var pf = (AIPathFollower)pfObj;
            if (!float.IsNaN(max) && pf.Speed > max) pf.Speed = Mathf.Max(0f, max);
        }

        /// <summary>The lane the car returns to after its lane changes (captured before we steer, given back after).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ReadHomeLane(MonoBehaviour laneObj) => ((AIVehicleLaneHandler)laneObj).initialLane;

        /// <summary>SetVehicle's top speed for this car (m/s), not shifted by the player's slow motion.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float ReadOriginalMaxSpeed(MonoBehaviour pfObj) => ((AIPathFollower)pfObj).originalMaxSpeed;

        /// <summary>
        /// Gives a car we steered back to the game's lanes: its home lane restored, then the game's own lane change into
        /// the lane it is assigned to (eased from where it is now). Local car only.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void HandBack(MonoBehaviour laneObj, int homeLane)
        {
            var lane = (AIVehicleLaneHandler)laneObj;
            lane.initialLane = homeLane;
            lane.MoveToLaneIndex(lane.CurrentLaneIndex);
        }

        /// <summary>
        /// Copies the active traffic cars (wrecks included as stopped obstacles) within [-behind, +ahead] m of <paramref name="around"/> into buf and
        /// returns how many (the daredevils' avoidance snapshot). LaneLo / LaneHi start at Lane (TrafficTracker.Update
        /// widens them). A crashed car (AIPathFollower.WasHit) stays in as a stopped obstacle (Speed 0, Wreck): physics has
        /// it, so its AvoidanceRoadDistance / AvoidanceLaneOffset (path values) may be off its physical place; its band and
        /// half length are widened by <see cref="WreckMargin"/> m to cover that. Only call when TrafficOk, after Spawner().
        /// </summary>
        internal const float WreckMargin = 2f;   // m added to a wreck's band (each side) and half length

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ReadRoad(float around, float behind, float ahead, RoadCar[] buf)
        {
            if (_spawner == null) return 0;
            var cars = ((DefaultAISpawner)_spawner).activeAiCars;
            if (cars == null) return 0;
            int n = 0, count = cars.Count;
            for (int i = 0; i < count && n < buf.Length; i++)
            {
                var car = cars[i];
                if (car == null || !car.IsActive) continue;
                float road = car.AvoidanceRoadDistance;
                float rel = road - around;
                if (!(rel >= -behind && rel <= ahead)) continue;
                var info = Info(car);                    // cached path follower / box wrappers (values still read now)
                var pf = PathFollower(car, info);
                if (pf == null) continue;
                bool wreck = pf.WasHit;                  // physics moves it: its path values may not be where it is
                float lane = car.AvoidanceLaneOffset, v = car.AvoidanceForwardVelocity;
                var box = Box(car, info);
                if (float.IsNaN(lane) || (float.IsNaN(v) && !wreck)) continue;
                Vector3 size = box != null ? box.size : new Vector3(2f, 1.5f, 4.5f);
                buf[n++] = new RoadCar
                {
                    Ptr = car.Pointer,
                    Road = road,
                    Lane = lane,
                    LaneLo = wreck ? lane - WreckMargin : lane,
                    LaneHi = wreck ? lane + WreckMargin : lane,
                    HalfWidth = Mathf.Clamp(size.x * 0.5f, 0.5f, 2f),
                    HalfLength = Mathf.Clamp(size.z * 0.5f, 1.5f, 12.5f) + (wreck ? WreckMargin : 0f),
                    Speed = wreck ? 0f : car.IsReversePath ? -v : v,
                    Wreck = wreck,
                };
            }
            return n;
        }
    }
}
