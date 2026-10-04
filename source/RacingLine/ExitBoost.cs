using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace RacingLine
{
    /// <summary>
    /// Clean-exit boost (0.7.0, the user's choice 2026-10-04: make braking and taking a corner on grip more rewarding than
    /// drifting, without slowing drifts further). When a corner zone ends with a grade (BRONZE or better), no drift and no
    /// collision, your car gets a short boost on the exit: top speed and acceleration up a little for ExitBoost.Seconds,
    /// scaled by the grade (GOLD 1, SILVER 0.6, BRONZE 0.3). It is the game's own mechanism, the same one as its drift-end
    /// boost: a SpeedModifier registered with VehicleManager.SpeedModifierHandler.RegisterTemporaryModifier (fades in and
    /// out, removes itself). Our modifier copies the neutral fields (intensity, time scale, turning, drift normalisation)
    /// from the game's VehicleMovement.driftEndBoostModifier, then gets our top-speed and acceleration multipliers.
    /// Your own car only (also in multiplayer). Exit paths: the modifier removes itself after its time; a new boost
    /// replaces the old one; switching the boost off, the race ending or the plugin unloading unregisters it; a new car
    /// took the old one's modifier with it. Nothing is registered while paused.
    /// Leaderboards (the user's pattern for the drift change: toggles for both): ExitBoost.KeepOffLeaderboards keeps a run
    /// in which a boost fired off the Steam leaderboards (a prefix on LeaderboardsManager.PublishEntry); ON by default,
    /// because the boost is a speed advantage (the user can switch it off). The flag is saved in the config
    /// (ExitBoost.UsedThisRun, like PitStop's refill flag), so a quit and continue keeps the mark.
    /// </summary>
    internal static class ExitBoost
    {
        private static SpeedModifier _active;        // our last registered modifier (kept: the GC must not take its wrapper)
        private static IntPtr _activeCar;
        private static float _activeUntil;
        private static bool _loggedTemplate, _loggedNoGuard;
        /// <summary>A boost fired since the current run began (saved in the config, so a quit and continue keeps it).</summary>
        internal static bool UsedThisRun { get => Plugin.ExitBoostUsedThisRun.Value; set { if (Plugin.ExitBoostUsedThisRun.Value != value) Plugin.ExitBoostUsedThisRun.Value = value; } }
        private static IntPtr _runCar;
        private static bool _runCheckPending;
        private static int _runChecks;
        private static float _nextRunCheck;

        /// <summary>A corner zone just ended. Fires the boost for a clean grip corner with a grade.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void OnCorner(CornerResult c)
        {
            if (!Plugin.ExitBoostEnabled.Value || c == null || !c.Grip || !c.Clean || c.Grade == null || Time.timeScale <= 0f) return;
            if (Plugin.ExitBoostKeepOffLeaderboards.Value && !ExitBoostLeaderboardGuard.Installed)
            {
                // the promise "a boosted run isn't uploaded" can't be kept without the guard: no boost then (as PitStop's F2)
                if (!_loggedNoGuard) { _loggedNoGuard = true; Plugin.Log.LogWarning("[RacingLine] exit boost stays off: the leaderboard guard isn't installed and KeepOffLeaderboards is on"); }
                return;
            }
            float g = c.Grade == "GOLD" ? 1f : c.Grade == "SILVER" ? 0.6f : 0.3f;
            float top = Mathf.Clamp(Plugin.ExitBoostTopSpeed.Value, 0f, 0.2f) * g, acc = Mathf.Clamp(Plugin.ExitBoostAcceleration.Value, 0f, 1f) * g;
            float secs = Mathf.Clamp(Plugin.ExitBoostSeconds.Value, 0.3f, 4f);
            if (top <= 0f && acc <= 0f) return;
            var veh = VehicleManager.Instance;
            if (veh == null || veh.LevelWasEnded) return;
            var handler = veh.SpeedModifierHandler;
            var move = veh.VehicleMovement;
            if (handler == null || move == null) return;
            Remove();   // one boost at a time: the new corner replaces the old one
            var m = new SpeedModifier();
            var t = move.driftEndBoostModifier;
            if (t != null)
            {
                if (!_loggedTemplate)
                {
                    _loggedTemplate = true;
                    Plugin.Log.LogInfo($"[RacingLine] exit boost: the game's drift-end boost is intensity {t.intensity:0.##}, top speed x{t.maxSpeedMultiplier:0.##} +{t.maxSpeedIncrement:0.##}, " +
                                       $"speed x{t.currentSpeedMultiplier:0.##} +{t.currentSpeedIncrement:0.##}, acceleration x{t.accelerationMultiplier:0.##}, time x{t.timeScaleMultiplier:0.##}, " +
                                       $"turning x{t.regularTurningMultiplier:0.##} / drift x{t.driftTurningMultiplier:0.##}, drift normalisation x{t.driftNormalizationMultiplier:0.##}");
                }
                m.intensity = t.intensity;
                m.timeScaleMultiplier = t.timeScaleMultiplier;
                m.regularTurningMultiplier = t.regularTurningMultiplier;
                m.driftTurningMultiplier = t.driftTurningMultiplier;
                m.driftNormalizationMultiplier = t.driftNormalizationMultiplier;
            }
            else
            {
                m.intensity = 1f; m.timeScaleMultiplier = 1f; m.regularTurningMultiplier = 1f; m.driftTurningMultiplier = 1f; m.driftNormalizationMultiplier = 1f;
            }
            if (m.intensity <= 0f) m.intensity = 1f;
            if (m.timeScaleMultiplier <= 0f) m.timeScaleMultiplier = 1f;
            if (m.regularTurningMultiplier <= 0f) m.regularTurningMultiplier = 1f;
            if (m.driftTurningMultiplier <= 0f) m.driftTurningMultiplier = 1f;
            if (m.driftNormalizationMultiplier <= 0f) m.driftNormalizationMultiplier = 1f;
            m.maxSpeedMultiplier = 1f + top; m.maxSpeedIncrement = 0f;
            m.currentSpeedMultiplier = 1f; m.currentSpeedIncrement = 0f;
            m.accelerationMultiplier = 1f + acc;
            handler.RegisterTemporaryModifier(m, secs, new Il2CppSystem.Nullable<float>(0.15f), new Il2CppSystem.Nullable<float>(Mathf.Min(0.6f, secs * 0.4f)));
            _active = m; _activeCar = veh.Pointer; _activeUntil = Time.time + secs + 1f;
            UsedThisRun = true;
            if (Plugin.LogCorners.Value)
                Plugin.Log.LogInfo($"[RacingLine] exit boost: corner {c.Index + 1}{c.Type} {c.Grade} grip -> top speed +{top * 100f:0.#}%, acceleration +{acc * 100f:0}% for {secs:0.0} s");
        }

        /// <summary>Every frame: the run flag's new-run check, and the boost taken back when it must not stay.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Tick()
        {
            if (Time.timeScale <= 0f) return;
            var veh = VehicleManager.Instance;
            IntPtr car = veh == null ? IntPtr.Zero : veh.Pointer;
            if (car != _runCar) { _runCar = car; if (car != IntPtr.Zero) { _runCheckPending = true; _runChecks = 0; } }
            if (_runCheckPending && Time.unscaledTime >= _nextRunCheck)
            {
                _nextRunCheck = Time.unscaledTime + 1f;
                if (CheckNewRun() || ++_runChecks >= 30) _runCheckPending = false;
            }
            if (_active == null) return;
            if (car != _activeCar) { _active = null; return; }                       // another car: the old one took its modifier with it
            if (veh.LevelWasEnded || !Plugin.ExitBoostEnabled.Value) Remove();
            else if (Time.time > _activeUntil) _active = null;                        // the game removed it itself
        }

        /// <summary>Unregisters our modifier if it may still be on the car. Never throws.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Remove()
        {
            if (_active == null) return;
            try
            {
                var veh = VehicleManager.Instance;
                if (veh != null && veh.Pointer == _activeCar && Time.time <= _activeUntil)
                {
                    var h = veh.SpeedModifierHandler;
                    if (h != null) h.UnregisterModifier(_active);
                }
            }
            catch { /* the car is gone: the modifier went with it */ }
            _active = null;
        }

        private static bool CheckNewRun()
        {
            try
            {
                var world = UnityEngine.Object.FindFirstObjectByType<Game.Runtime.Manager.RunWorldManager>();   // only while a check is pending
                if (world == null) return false;
                if (world.currentStageIndex == 0 && world.currentRaceIndex == 0) UsedThisRun = false;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Optional (ExitBoost.KeepOffLeaderboards): skips the game's leaderboard upload for a run in which an exit boost
    /// fired. PitStop, Sandbox and CurbFeel guard the same method the same way (each prefix only ever skips, so they
    /// combine). Installed by hand so a missing type can't break loading.
    /// </summary>
    internal static class ExitBoostLeaderboardGuard
    {
        /// <summary>The prefix is in place (only then can a boosted run be kept off the leaderboards).</summary>
        internal static bool Installed { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install()
        {
            try
            {
                // harmony-target: LeaderboardsManager.PublishEntry (cooperates with PitStop, Sandbox, CurbFeel)
                var target = AccessTools.Method(typeof(Game.Runtime.Manager.LeaderboardsManager), nameof(Game.Runtime.Manager.LeaderboardsManager.PublishEntry));
                if (target == null) { Plugin.Log.LogWarning("[RacingLine] exit boost leaderboard guard not installed: PublishEntry not found"); return; }
                new Harmony(Plugin.Guid).Patch(target, prefix: new HarmonyMethod(typeof(ExitBoostLeaderboardGuard), nameof(Prefix)));
                Installed = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RacingLine] exit boost leaderboard guard not installed: {e.Message}"); }
        }

        private static bool Prefix()
        {
            try
            {
                if (!Plugin.ExitBoostKeepOffLeaderboards.Value || !ExitBoost.UsedThisRun) return true;
                Plugin.Log.LogInfo("[RacingLine] leaderboard upload skipped: this run used the clean-exit boost (ExitBoost.KeepOffLeaderboards)");
                return false;
            }
            catch { return true; }   // never break the game's own flow
        }
    }
}
