using System;
using System.Collections.Generic;
using UnityEngine;

namespace TrafficDensity
{
    /// <summary>
    /// Keeps dense traffic from crashing into itself and jamming (README: "Why extra traffic crashed and jammed").
    ///
    /// The game's NPC AI has no real collision avoidance; it only works at stock density because the road is empty:
    ///   - NPC-NPC crash = both cars stop for good with hazard lights, and only vanish if more than
    ///     AICollisionHandler.collisionDespawnDistance (150 m) ahead of the player -> wrecks block lanes = jams
    ///   - lane changes look 50 m ahead but only freeLaneCheckDistanceBehind (6 m) behind -> cut-ins
    ///   - braking never goes below brakeFactorRange.x (0.2, Daredevil 0.6) throttle -> creeps into stopped cars
    ///   - the spawner accepts a lane if no car is within obstacleDistanceCheck (20 m)
    ///
    /// Every quarter second this walks the spawner's active cars and writes those values. No Harmony patches.
    /// A car is fully checked when it (re)appears in the active list (fresh car or pool reuse), when a [Fixes] value
    /// changes, and otherwise every RecheckSeconds; in between it is skipped (no Il2Cpp reads).
    /// Whenever a value differs from ours and isn't the one we wrote last (fresh car, pool reuse,
    /// AIObstructionDetector.Reset) it is captured as that car's original first, so switching the fixes off writes back
    /// exactly what the game had - on pooled cars too, and also after a [Fixes] value was changed live.
    /// </summary>
    internal static class Fixes
    {
        private const float RecheckSeconds = 2f;

        private sealed class Saved
        {
            public AIVehicleController Car;
            // the game's own values (NaN = not changed by us)
            public float Despawn = float.NaN, Behind = float.NaN, BrakeMin = float.NaN, CheckEvery = float.NaN, RubberBand = float.NaN;
            // what we wrote last (NaN = nothing), to tell our own old value from a game reset when a setting changes
            public float WDespawn = float.NaN, WBehind = float.NaN, WBrakeMin = float.NaN, WCheckEvery = float.NaN, WRubberBand = float.NaN;
            public int Gen = -1;          // settings generation last applied
            public int SeenTick = -2;     // last Apply tick this car was in the active list
            public float NextCheck;       // unscaled time of the next full check
        }

        private static readonly Dictionary<IntPtr, Saved> _saved = new Dictionary<IntPtr, Saved>();
        private static DefaultAISpawner _spawner;
        private static float _spawnerGap = float.NaN;
        private static bool _loggedOn;
        private static int _tick, _gen;
        private static float _cDespawn = float.NaN, _cBehind = float.NaN, _cBrake = float.NaN, _cCheck = float.NaN, _cRubber = float.NaN;

        internal static bool Active => _saved.Count > 0 || !float.IsNaN(_spawnerGap);

        /// <summary>Apply (want = true) or restore (want = false) for the current spawner.</summary>
        internal static void Tick(DefaultAISpawner sp, bool want)
        {
            if (sp == null) { Forget(); return; }
            if (_spawner == null || _spawner.Pointer != sp.Pointer) { Forget(); _spawner = sp; }
            if (want) Apply(sp); else Restore();
        }

        /// <summary>Bumps the settings generation when any [Fixes] value changed (config edit / reload), so every car is re-checked.</summary>
        private static void TrackSettings()
        {
            float a = Plugin.WreckClearDistance.Value, b = Plugin.LaneChangeCheckBehind.Value, c = Plugin.MinBrake.Value,
                  d = Plugin.ObstructionCheckInterval.Value, e = Plugin.RubberBandSpeedFactor.Value;
            if (a.Equals(_cDespawn) && b.Equals(_cBehind) && c.Equals(_cBrake) && d.Equals(_cCheck) && e.Equals(_cRubber)) return;
            _cDespawn = a; _cBehind = b; _cBrake = c; _cCheck = d; _cRubber = e;
            _gen++;
        }

        private static void Apply(DefaultAISpawner sp)
        {
            float gap = Plugin.SpawnGap.Value;
            if (gap >= 0f)
            {
                float cur = sp.obstacleDistanceCheck;
                if (Differs(cur, gap)) { if (float.IsNaN(_spawnerGap)) _spawnerGap = cur; sp.obstacleDistanceCheck = gap; }
            }
            else RestoreSpawner();

            TrackSettings();
            int tick = ++_tick;
            float now = Time.unscaledTime;

            var cars = sp.activeAiCars;
            if (cars == null) return;
            int count = cars.Count;
            for (int i = 0; i < count; i++)
            {
                var car = cars[i];
                if (car == null) continue;
                // our stored wrapper keeps each car alive, so a pointer can't be reused while its entry exists
                if (!_saved.TryGetValue(car.Pointer, out var s) || s.Car == null) _saved[car.Pointer] = s = new Saved { Car = car };
                bool fresh = s.SeenTick != tick - 1;   // not active last tick: new, or back from the pool (values may be reset)
                s.SeenTick = tick;
                if (!fresh && s.Gen == _gen && now < s.NextCheck) continue;
                ApplyCar(car, s);
                s.Gen = _gen;
                s.NextCheck = now + RecheckSeconds;
            }

            if (!_loggedOn)
            {
                _loggedOn = true;
                Plugin.Log.LogInfo($"[Traffic] fixes on (game values: spawn gap {Show(_spawnerGap, sp.obstacleDistanceCheck)}): wrecks cleared beyond {Describe(Plugin.WreckClearDistance.Value, "m")}, " +
                                   $"lane-change check behind {Describe(Plugin.LaneChangeCheckBehind.Value, "m")}, min brake {Describe(Plugin.MinBrake.Value, "")}, " +
                                   $"obstruction check every {Describe(Plugin.ObstructionCheckInterval.Value, "s")}, spawn gap {Describe(gap, "m")}, rubber band {Describe(Plugin.RubberBandSpeedFactor.Value, "")}");
            }
        }

        /// <summary>
        /// cur is the game's value (to remember) unless it's the value we wrote last - then a setting changed and the
        /// original we already hold stays.
        /// </summary>
        private static bool IsGameValue(float cur, float orig, float written) => float.IsNaN(orig) || float.IsNaN(written) || Differs(cur, written);

        private static void ApplyCar(AIVehicleController car, Saved s)
        {
            float t;

            var ch = car.CollisionHandler;
            if (ch != null)
            {
                t = Plugin.WreckClearDistance.Value;
                if (t >= 0f)
                {
                    float cur = ch.collisionDespawnDistance;
                    if (Differs(cur, t)) { if (IsGameValue(cur, s.Despawn, s.WDespawn)) s.Despawn = cur; ch.collisionDespawnDistance = t; s.WDespawn = t; }
                }
                else if (!float.IsNaN(s.Despawn)) { ch.collisionDespawnDistance = s.Despawn; s.Despawn = s.WDespawn = float.NaN; }
            }

            var lh = car.LaneHandler;
            if (lh != null)
            {
                t = Plugin.LaneChangeCheckBehind.Value;
                if (t >= 0f)
                {
                    float cur = lh.freeLaneCheckDistanceBehind;
                    if (Differs(cur, t)) { if (IsGameValue(cur, s.Behind, s.WBehind)) s.Behind = cur; lh.freeLaneCheckDistanceBehind = t; s.WBehind = t; }
                }
                else if (!float.IsNaN(s.Behind)) { lh.freeLaneCheckDistanceBehind = s.Behind; s.Behind = s.WBehind = float.NaN; }
            }

            var od = car.ObstructionDetector;
            if (od != null)
            {
                t = Plugin.MinBrake.Value;
                var r = od.brakeFactorRange;
                if (t >= 0f)
                {
                    if (Differs(r.x, t)) { if (IsGameValue(r.x, s.BrakeMin, s.WBrakeMin)) s.BrakeMin = r.x; od.brakeFactorRange = new Vector2(t, r.y); s.WBrakeMin = t; }
                }
                else if (!float.IsNaN(s.BrakeMin)) { od.brakeFactorRange = new Vector2(s.BrakeMin, r.y); s.BrakeMin = s.WBrakeMin = float.NaN; }

                // Awake copies _timeBetweenCloserChecks into checkCloseInterval once, so set the live interval too
                t = Plugin.ObstructionCheckInterval.Value;
                if (t > 0f)
                {
                    float cur = od._timeBetweenCloserChecks;
                    if (Differs(cur, t)) { if (IsGameValue(cur, s.CheckEvery, s.WCheckEvery)) s.CheckEvery = cur; SetCheckInterval(od, t); s.WCheckEvery = t; }
                }
                else if (!float.IsNaN(s.CheckEvery)) { SetCheckInterval(od, s.CheckEvery); s.CheckEvery = s.WCheckEvery = float.NaN; }
            }

            var pf = car.PathFollower;
            if (pf != null)
            {
                t = Plugin.RubberBandSpeedFactor.Value;
                if (t >= 0f)
                {
                    float cur = pf.rubberBandSpeedFactor;
                    if (Differs(cur, t)) { if (IsGameValue(cur, s.RubberBand, s.WRubberBand)) s.RubberBand = cur; pf.rubberBandSpeedFactor = t; s.WRubberBand = t; }
                }
                else if (!float.IsNaN(s.RubberBand)) { pf.rubberBandSpeedFactor = s.RubberBand; s.RubberBand = s.WRubberBand = float.NaN; }
            }
        }

        private static void SetCheckInterval(AIObstructionDetector od, float seconds)
        {
            od._timeBetweenCloserChecks = seconds;
            var interval = od.checkCloseInterval;
            if (interval != null) interval.SetInterval(seconds);
        }

        private static bool Differs(float a, float b) => Mathf.Abs(a - b) > 1e-4f;

        private static void Restore()
        {
            if (!Active) return;
            RestoreSpawner();
            int n = 0;
            foreach (var s in _saved.Values)
            {
                var car = s.Car;
                if (car == null) continue;   // destroyed with the scene
                try
                {
                    var ch = car.CollisionHandler; var lh = car.LaneHandler; var od = car.ObstructionDetector; var pf = car.PathFollower;
                    if (ch != null && !float.IsNaN(s.Despawn)) ch.collisionDespawnDistance = s.Despawn;
                    if (lh != null && !float.IsNaN(s.Behind)) lh.freeLaneCheckDistanceBehind = s.Behind;
                    if (od != null && !float.IsNaN(s.BrakeMin)) { var r = od.brakeFactorRange; od.brakeFactorRange = new Vector2(s.BrakeMin, r.y); }
                    if (od != null && !float.IsNaN(s.CheckEvery)) SetCheckInterval(od, s.CheckEvery);
                    if (pf != null && !float.IsNaN(s.RubberBand)) pf.rubberBandSpeedFactor = s.RubberBand;
                    n++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Traffic] restore skipped a car: {e.Message}"); }
            }
            _saved.Clear();
            _loggedOn = false;
            Plugin.Log.LogInfo($"[Traffic] fixes off: game values restored on {n} car(s)");
        }

        private static void RestoreSpawner()
        {
            if (float.IsNaN(_spawnerGap)) return;
            if (_spawner != null) _spawner.obstacleDistanceCheck = _spawnerGap;
            _spawnerGap = float.NaN;
        }

        /// <summary>Spawner gone (scene change): its cars went with it, nothing to restore.</summary>
        private static void Forget()
        {
            _saved.Clear();
            _spawner = null;
            _spawnerGap = float.NaN;
            _loggedOn = false;
        }

        private static string Describe(float v, string unit) => v < 0f ? "game" : $"{v:0.##}{unit}";
        private static string Show(float orig, float current) => (float.IsNaN(orig) ? current : orig).ToString("0.##");
    }
}
