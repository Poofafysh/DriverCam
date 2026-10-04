using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Manager;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Police
{
    /// <summary>What the police logic needs from the player's car each tick. Read only.</summary>
    internal struct PlayerState
    {
        public IntPtr Car;          // VehicleManager object (a new one = a new race or a new car)
        public float Distance;      // metres along the run's path (PlayerPathFollower.GetDistanceTravelled)
        public float Lane;          // lane offset in metres (PlayerPathFollower.GetLaneOffset)
        public float Speed;         // m/s (VehicleMovement.CurrentSpeed)
        public float TopSpeed;      // m/s (VehicleMovement.OriginalMaxSpeed)
        public float MaxNow;        // m/s (VehicleMovement.CurrentMaxSpeed = OriginalMaxSpeed x modifiers + flat: cards, boosts), NaN if unknown
        public bool Drifting;       // VehicleMovement.Drifting
        public bool LevelEnded;     // VehicleManager.LevelWasEnded
        public int Hits;            // the game's own collision count (-1 = unknown)
        public int NearMisses;      // the game's own near-miss count (-1 = unknown)
    }

    /// <summary>
    /// The only place that touches game types. Check() looks every member up by name in the interop assembly once at
    /// startup; a member missing after a game update switches off only the features that need it. Each accessor sits in
    /// its own non-inlined method, so code referring to a missing member is never compiled unless its check passed.
    /// Game objects are held in fields typed as Unity base classes (MonoBehaviour / ScriptableObject): a field of a game
    /// type would stop this class loading at all if a game update removed that type.
    /// </summary>
    internal static partial class GameApi
    {
        internal static bool TrafficOk { get; private set; }   // spawner, traffic cars, their path follower (GameApi.Traffic.cs)
        internal static bool PlayerOk { get; private set; }    // player car: distance, lane, speed, top speed, level end
        internal static bool ScoreOk { get; private set; }     // the game's collision and near-miss counts
        internal static bool TimerOk { get; private set; }     // race timer (caught penalty) (GameApi.Timer.cs)
        internal static bool ModeOk { get; private set; }      // GameState.IsMultiplayerMode

        internal static void Check()
        {
            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null) { try { asm = Assembly.Load("Assembly-CSharp"); } catch { /* reported as missing below */ } }

            TrafficOk = Has(asm, "AISpawnerBase", missing, "Instance")
                     && Has(asm, "DefaultAISpawner", missing, "activeAiCars")
                     && Has(asm, "AIVehicleController", missing, "PathFollower", "IsActive", "AvoidanceRoadDistance", "AvoidanceForwardVelocity",
                            "AvoidanceLaneOffset", "VehicleCollider")
                     && Has(asm, "AIPathFollower", missing, "rubberBandingEnabled", "MaxSpeed", "DistanceTravelled", "WasHit", "speedSmoothness", "behindDistanceDespawn", "Speed");
            PlayerOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "PlayerPathFollower", "VehicleMovement", "LevelWasEnded")
                    && Has(asm, "Game.Runtime.Vehicle.PlayerPathFollower", missing, "GetDistanceTravelled", "GetLaneOffset")
                    && Has(asm, "Game.Runtime.Vehicle.VehicleMovement", missing, "CurrentSpeed", "OriginalMaxSpeed", "CurrentMaxSpeed", "Drifting");
            ScoreOk = Has(asm, "Game.Runtime.Manager.LevelScoreManager", missing, "scoreProviderList")
                   && Has(asm, "Game.Runtime.Data.CollisionScoreProviderSO", missing, "TotalHits")
                   && Has(asm, "Game.Runtime.Data.NearMissScoreProviderSO", missing, "TotalNearMiss");
            TimerOk = Has(asm, "Singleton", missing, "Instance", "TimerManager")
                   && Has(asm, "Game.Runtime.Manager.TimerManager", missing, "raceTimer", "CountdownMode", "IsTimerPlaying", "RemainingTime")
                   && Has(asm, "Game.Runtime.Systems.Timer.RaceTimer", missing, "RemoveCountdownTime");
            ModeOk = Has(asm, "Game.Runtime.GameState", missing, "IsMultiplayerMode");
            CheckBoss(asm, missing);
            CheckDaredevil(asm, missing);
            CheckScore(asm, missing);   // GameApi.Score.cs: PURSUIT category, results / Victory rows, run position
            CheckNet(asm, missing);     // GameApi.Net.cs: multiplayer host (spawner, players) and guest (cars by netId)

            if (missing.Count == 0) Plugin.Log.LogInfo("[Police] game check OK: traffic, player, collision/near-miss counts, race timer, game mode, boss car models, daredevils, pursuit score, results/victory rows, multiplayer host + guest");
            else Plugin.Log.LogWarning($"[Police] game check: missing {string.Join(", ", missing)}. Patrols {On(TrafficOk && PlayerOk)}, " +
                                       $"crash notice + lead bar events {On(ScoreOk)}, caught penalty {On(TimerOk)}, game mode {On(ModeOk)}, boss car looks {On(BossOk && SkinOk)}, daredevils {On(DaredevilOk)}, " +
                                       $"pursuit score {On(PursuitOk)}, results row {On(ResultsOk)}, victory row {On(VictoryOk)}, run reset {On(RunOk)}, multiplayer host {On(NetOk)}, multiplayer guest {On(NetViewOk)} " +
                                       "(without the game mode the plugin assumes multiplayer; without the multiplayer parts it stays off in multiplayer).");
        }

        private static string On(bool ok) => ok ? "on" : "OFF";

        private static bool Has(Assembly asm, string typeName, List<string> missing, params string[] members)
        {
            var t = asm?.GetType(typeName);
            if (t == null) { missing.Add(typeName); return false; }
            bool ok = true;
            foreach (var m in members)
                if (t.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0)
                { missing.Add($"{t.Name}.{m}"); ok = false; }
            return ok;
        }

        /// <summary>True in multiplayer, and whenever the mode can't be read (unreadable = multiplayer = off).</summary>
        internal static bool IsMultiplayer()
        {
            if (!ModeOk) return true;
            try { return ReadMultiplayerFlag(); }
            catch { return true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadMultiplayerFlag() => Game.Runtime.GameState.IsMultiplayerMode;

        // ------------------------------------------------------------------ player

        private static MonoBehaviour _veh, _follower, _movement;
        private static IntPtr _vehPtr;

        /// <summary>
        /// Reads the local player's car. False when there is none (menus, loading). Only call when PlayerOk.
        /// scores = false skips the collision / near-miss counts (Hits / NearMisses stay -1): the daredevils, which read
        /// you every frame, never use them (0.6.0 perf: about 6 native calls a frame).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ReadPlayer(ref PlayerState p, bool scores = true)
        {
            var veh = VehicleManager.Instance;
            if (veh == null) { _veh = null; _vehPtr = IntPtr.Zero; return false; }
            if (_veh == null || _vehPtr != veh.Pointer || _follower == null || _movement == null)
            {
                _veh = veh; _vehPtr = veh.Pointer;
                _follower = veh.PlayerPathFollower; _movement = veh.VehicleMovement;
                if (_follower == null || _movement == null) { _veh = null; return false; }
            }
            var follower = (PlayerPathFollower)_follower;
            var move = (VehicleMovement)_movement;
            p.Car = _vehPtr;
            p.Distance = follower.GetDistanceTravelled();
            p.Lane = follower.GetLaneOffset();
            p.Speed = move.CurrentSpeed;
            p.TopSpeed = move.OriginalMaxSpeed;
            float maxNow = move.CurrentMaxSpeed;
            p.MaxNow = maxNow > 1f && !float.IsNaN(maxNow) && !float.IsInfinity(maxNow) ? maxNow : float.NaN;
            p.Drifting = move.Drifting;
            p.LevelEnded = veh.LevelWasEnded;
            p.Hits = -1; p.NearMisses = -1;
            if (scores && ScoreOk) ReadScoreCounts(ref p);
            return true;
        }

        // ------------------------------------------------------------------ the game's own score counts (read only)

        private static MonoBehaviour _scoreManager;
        private static float _nextScoreManagerSearch;
        // each holds the TryCast result, so its runtime type is the concrete wrapper: plain managed casts later, no
        // per-tick TryCast allocations
        private static ScriptableObject _collision, _nearMiss;
        private static IntPtr _providersOwner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadScoreCounts(ref PlayerState p)
        {
            if (_scoreManager == null)
            {
                if (Time.unscaledTime < _nextScoreManagerSearch) return;
                _nextScoreManagerSearch = Time.unscaledTime + 2f;   // never a scene search every frame
                _scoreManager = UnityEngine.Object.FindFirstObjectByType<LevelScoreManager>();
                if (_scoreManager == null) return;
            }
            var mgr = (LevelScoreManager)_scoreManager;
            if (_providersOwner != mgr.Pointer || (_collision == null && _nearMiss == null)) CacheProviders(mgr);
            if (_collision != null) p.Hits = ((Game.Runtime.Data.CollisionScoreProviderSO)_collision).TotalHits;
            if (_nearMiss != null) p.NearMisses = ((Game.Runtime.Data.NearMissScoreProviderSO)_nearMiss).TotalNearMiss;
        }

        private static float _nextProviderScan;

        private static void CacheProviders(LevelScoreManager mgr)
        {
            if (_providersOwner == mgr.Pointer && Time.unscaledTime < _nextProviderScan) return;   // list not filled yet: retry every 2 s
            _nextProviderScan = Time.unscaledTime + 2f;
            _providersOwner = mgr.Pointer; _collision = null; _nearMiss = null;
            var list = mgr.scoreProviderList;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                // the game's own categories only: RacingLine's copy is a TopSpeed provider, so it never matches these
                if (_collision == null && p.TryCast<Game.Runtime.Data.CollisionScoreProviderSO>() is var col && col != null) _collision = col;
                else if (_nearMiss == null && p.TryCast<Game.Runtime.Data.NearMissScoreProviderSO>() is var nm && nm != null) _nearMiss = nm;
            }
        }

        /// <summary>Scene changed: find the score manager again.</summary>
        internal static void ForgetScene()
        {
            _scoreManager = null; _collision = null; _nearMiss = null; _providersOwner = IntPtr.Zero;
            _nextScoreManagerSearch = 0f; _nextProviderScan = 0f;
        }
    }
}
