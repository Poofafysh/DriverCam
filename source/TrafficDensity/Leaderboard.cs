using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace TrafficDensity
{
    /// <summary>
    /// Optional (General.KeepOffLeaderboards, OFF by default): keeps a run in which safe lane changes or speed limits
    /// changed the traffic off the Steam leaderboards. User 2026-10-04: "Add a toggle, default: uploads count".
    ///
    /// Same pattern as RacingLine's ExitBoost guard: the "used this run" mark is saved in the config (General.UsedThisRun,
    /// kept across a quit and continue) and cleared when a new run starts (a new player car, then RunWorldManager at
    /// stage 0 / race 0, checked once a second, at most 30 tries). A run is marked only when a feature actually changed
    /// something (a lane change stopped, a speed limit written). PitStop, Sandbox, CurbFeel and RacingLine guard the same
    /// method the same way: each prefix only ever skips the upload, so they combine. Installed by hand so a missing type
    /// can't break loading. With the setting on and no guard, both features stay off (the promise couldn't be kept).
    /// </summary>
    internal static class Leaderboard
    {
        /// <summary>The prefix is in place (only then can a changed run be kept off the leaderboards).</summary>
        internal static bool Installed { get; private set; }

        private static bool _loggedNoGuard;
        private static IntPtr _runCar;
        private static bool _runCheckPending;
        private static int _runChecks;
        private static float _nextRunCheck;

        /// <summary>Safe lane changes / speed limits changed the traffic since the current run began (saved in the config).</summary>
        internal static bool UsedThisRun
        {
            get => Plugin.UsedThisRun.Value;
            set { if (Plugin.UsedThisRun.Value != value) Plugin.UsedThisRun.Value = value; }
        }

        /// <summary>A feature changed the traffic: marks the run (cheap when already marked).</summary>
        internal static void MarkUsed() { if (!Plugin.UsedThisRun.Value) Plugin.UsedThisRun.Value = true; }

        /// <summary>
        /// False when KeepOffLeaderboards is on but the guard isn't installed: then SafeLanes and SpeedLimits stay off
        /// (as RacingLine's exit boost and PitStop's F2), logged once.
        /// </summary>
        internal static bool FeaturesAllowed
        {
            get
            {
                if (!Plugin.KeepOffLeaderboards.Value || Installed) return true;
                if (!_loggedNoGuard)
                {
                    _loggedNoGuard = true;
                    Plugin.Log.LogWarning("[Traffic] safe lane changes and speed limits stay off: the leaderboard guard isn't installed and KeepOffLeaderboards is on");
                }
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony)
        {
            try
            {
                // harmony-target: LeaderboardsManager.PublishEntry (cooperates with PitStop, Sandbox, CurbFeel, RacingLine)
                var target = AccessTools.Method(typeof(Game.Runtime.Manager.LeaderboardsManager), nameof(Game.Runtime.Manager.LeaderboardsManager.PublishEntry));
                if (target == null) { Plugin.Log.LogWarning("[Traffic] leaderboard guard not installed: PublishEntry not found"); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Leaderboard), nameof(Prefix)));
                Installed = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Traffic] leaderboard guard not installed: {e.Message}"); }
        }

        private static bool Prefix()
        {
            try
            {
                if (!Plugin.KeepOffLeaderboards.Value || !UsedThisRun) return true;
                Plugin.Log.LogInfo("[Traffic] leaderboard upload skipped: safe lane changes or speed limits changed the traffic in this run (General.KeepOffLeaderboards)");
                return false;
            }
            catch { return true; }   // never break the game's own flow
        }

        /// <summary>From TrafficRunner (4x a second): a new player car starts the new-run check (stage 0 / race 0 clears the mark).</summary>
        internal static void Tick()
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
        }

        private static bool CheckNewRun()
        {
            try
            {
                var world = UnityEngine.Object.FindFirstObjectByType<Game.Runtime.Manager.RunWorldManager>();   // only while a check is pending (at most 30 x, 1 Hz)
                if (world == null) return false;
                if (world.currentStageIndex == 0 && world.currentRaceIndex == 0)
                {
                    if (UsedThisRun) Plugin.Log.LogInfo("[Traffic] new run: leaderboard mark cleared");
                    UsedThisRun = false;
                }
                return true;
            }
            catch { return false; }
        }
    }
}
