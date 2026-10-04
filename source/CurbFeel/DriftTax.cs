using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CurbFeel
{
    /// <summary>
    /// F. Drift (0.8.0, the player's request 2026-10-04: "nerf drifting a bit, start at a modest 5% speed loss no matter
    /// what when you start drifting UNLESS and ONLY unless you have the mod that negates that").
    /// Every frame, your own car only (VehicleManager.Instance; also in multiplayer, where it is your local car): the
    /// moment VehicleMovement.Drifting turns on, the car loses StartSpeedLoss of its speed through the game's own
    /// VehicleMovement.RequestReduceSpeed (the same call CurbFeel's side-swipes use). The one exception is a card that
    /// negates drift speed loss: the game's VehicleStats.MiscStats.SpeedLossDriftingMultiplier at (or below) zero. Any
    /// other value, smaller or larger, still pays the full StartSpeedLoss. Nothing is written while paused or after the
    /// race ends; a pause (or switching F.Drift back on) in the middle of a drift is not a new start. Nothing to restore:
    /// a speed loss is a one-off event, like a scrape. Leaderboards (the user's choice, 2026-10-04: "make toggleable
    /// options for both the slower drift and the leaderboard guard"): with F.Drift.KeepOffLeaderboards on, a run in
    /// which a drift start cost speed is not uploaded (DriftLeaderboardGuard below); off (default) it uploads as usual.
    /// Errors: logged at most every 5 s, never every frame.
    /// </summary>
    internal static class DriftTax
    {
        private static IntPtr _car;
        private static bool _wasDrifting, _loggedMultiplier, _off = true;
        private static float _nextError;

        public static void Tick()
        {
            try { Step(); }
            catch (Exception e)
            {
                if (Time.unscaledTime >= _nextError) { _nextError = Time.unscaledTime + 5f; Plugin.Log.LogWarning($"[Drift] error (next one in 5 s at the earliest): {e.Message}"); }
            }
        }

        private static void Step()
        {
            if (Time.timeScale <= 0f) return;   // paused: no writes, and the drift state is kept (resuming mid-drift is no new start)
            var veh = VehicleManager.Instance;
            if (veh == null) { _car = IntPtr.Zero; _wasDrifting = false; return; }
            if (veh.Pointer != _car) { _car = veh.Pointer; _wasDrifting = false; _runCheckPending = true; _runChecks = 0; }   // a new car / race: no edge carried over
            // the run check runs whether or not F.Drift is on (the leaderboard flag must start over with every run), retried
            // at most once a second until the run position can be read
            if (_runCheckPending && Time.unscaledTime >= _nextRunCheck) { _nextRunCheck = Time.unscaledTime + 1f; if (CheckNewRun() || ++_runChecks >= 30) _runCheckPending = false; }   // at most 30 tries (no run manager, e.g. multiplayer)
            bool on = Settings.Enabled.Value && Settings.DriftEnabled.Value && Settings.DriftStartSpeedLoss.Value > 0f;   // master switch (F10) included
            if (!on) { _off = true; return; }
            var move = veh.VehicleMovement;
            if (move == null) return;
            bool drifting = move.Drifting;
            if (_off) { _off = false; _wasDrifting = drifting; return; }   // just switched on: a drift already going is not a start
            bool started = drifting && !_wasDrifting;
            _wasDrifting = drifting;
            if (!started || veh.LevelWasEnded) return;

            float mult = DriftLossMultiplier(veh);
            if (!_loggedMultiplier) { _loggedMultiplier = true; Plugin.Log.LogInfo($"[Drift] first drift: the game's drift speed-loss multiplier is {mult:0.##} (0 = a card negates it)"); }
            if (mult <= 0.001f)
            {
                Stats.Event("Drift start: no speed loss (a card negates it)");
                return;
            }
            float loss = Mathf.Clamp(Settings.DriftStartSpeedLoss.Value, 0f, 0.2f);
            move.RequestReduceSpeed(loss, 0);
            UsedThisRun = true;
            Stats.DriftTaxes++;
            Stats.Event($"Drift start: -{loss * 100f:0.#}% speed");
            if (Settings.VerboseLog.Value) Plugin.Log.LogInfo($"[Drift] drift started: -{loss * 100f:0.#}% speed (multiplier {mult:0.##})");
        }

        /// <summary>A drift start cost speed since the current run began (for the optional leaderboard guard).</summary>
        internal static bool UsedThisRun;

        private static bool _runCheckPending;
        private static int _runChecks;
        private static float _nextRunCheck;

        /// <summary>
        /// Per new car (retried at most once a second): the first race of a run (stage 0, race 0) starts the run's flag
        /// over. Returns true once the run position was read.
        /// </summary>
        private static bool CheckNewRun()
        {
            try
            {
                var world = UnityEngine.Object.FindFirstObjectByType<Game.Runtime.Manager.RunWorldManager>();   // only while a check is pending
                if (world == null) return false;   // not ready yet: look again in a second
                if (world.currentStageIndex == 0 && world.currentRaceIndex == 0) UsedThisRun = false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>The game's drift speed-loss multiplier from your car's stats (cards change it), or 1 if unreadable.</summary>
        private static float DriftLossMultiplier(VehicleManager veh)
        {
            try
            {
                var stats = veh.VehicleStats;
                var misc = stats != null ? stats.MiscStats : null;
                return misc != null ? misc.SpeedLossDriftingMultiplier : 1f;
            }
            catch { return 1f; }   // unreadable: no card can be seen, so the loss applies
        }

    }

    /// <summary>
    /// Optional (F.Drift.KeepOffLeaderboards): skips the game's leaderboard upload for a run in which a drift start cost
    /// speed. Every upload goes through LeaderboardsManager.PublishEntry (PitStop and Sandbox guard the same method the
    /// same way: each prefix only ever skips, so they combine). Installed by hand so a missing type can't break loading.
    /// </summary>
    internal static class DriftLeaderboardGuard
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony)
        {
            try
            {
                // harmony-target: LeaderboardsManager.PublishEntry (cooperates with PitStop, Sandbox)
                var target = AccessTools.Method(typeof(Game.Runtime.Manager.LeaderboardsManager), nameof(Game.Runtime.Manager.LeaderboardsManager.PublishEntry));
                if (target == null) { Plugin.Log.LogWarning("[Drift] leaderboard guard not installed: LeaderboardsManager.PublishEntry not found"); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(DriftLeaderboardGuard), nameof(Prefix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Drift] leaderboard guard not installed: {e.Message}"); }
        }

        private static bool Prefix()
        {
            try
            {
                if (!Settings.DriftKeepOffLeaderboards.Value || !DriftTax.UsedThisRun) return true;
                Plugin.Log.LogInfo("[Drift] leaderboard upload skipped: this run used the drift start speed loss (F.Drift.KeepOffLeaderboards)");
                return false;
            }
            catch { return true; }   // never break the game's own flow
        }
    }
}
