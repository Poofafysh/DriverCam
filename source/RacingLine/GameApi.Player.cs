using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Manager;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace RacingLine
{
    /// <summary>What the scorer needs from the player's car each frame. Read only.</summary>
    internal struct PlayerState
    {
        public float Distance, Offset, Speed, TopSpeed;
        public float Vx, Vz;            // horizontal velocity (for the car's own path curvature)
        public float Throttle, Brake, Steer;
        public bool Grounded, InControl, Drifting;
        public int Hits;                // the game's own collision count (-1 = unknown)
        public int NearMisses;          // the game's own near-miss count (-1 = unknown)
    }

    internal static partial class GameApi
    {
        // untyped for the same reason as _world; cached per car (re-fetched when VehicleManager.Instance changes)
        private static MonoBehaviour _scoreManager;
        private static float _nextScoreManagerSearch;
        private static MonoBehaviour _veh, _follower, _movement, _input;
        private static Component _rb;
        private static IntPtr _vehPtr;

        /// <summary>Counts player-car changes (a new VehicleManager.Instance object): a new car or a new level.</summary>
        internal static int CarChanges { get; private set; }

        /// <summary>
        /// Reads the local player's car through VehicleManager.Instance: inputs, logical speed, top speed, drift, grounded,
        /// in-control, distance and offset along the path, the game's collision and near-miss counts. False when there is
        /// no player car. Only call when PlayerOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ReadPlayer(ref PlayerState p)
        {
            var veh = VehicleManager.Instance;
            if (veh == null) { _veh = null; return false; }
            if (_veh == null || _vehPtr != veh.Pointer || _follower == null || _movement == null || _input == null || _rb == null)
            {
                if (_vehPtr != veh.Pointer) CarChanges++;
                _veh = veh; _vehPtr = veh.Pointer;
                _follower = veh.PlayerPathFollower; _movement = veh.VehicleMovement; _input = veh.VehicleInputHandler; _rb = veh.Rigidbody;
                if (_follower == null || _movement == null || _input == null || _rb == null) { _veh = null; return false; }
            }
            var follower = (PlayerPathFollower)_follower;
            var move = (VehicleMovement)_movement;
            var input = (VehicleInputHandler)_input;
            var rb = (Rigidbody)_rb;

            p.Distance = follower.GetDistanceTravelled();
            p.Offset = follower.GetLaneOffset();
            p.Speed = move.CurrentSpeed;
            p.TopSpeed = move.OriginalMaxSpeed;
            p.Grounded = move.IsGrounded;
            p.Drifting = move.Drifting;
            p.Throttle = input.Throttle;
            p.Brake = input.BrakeInput;
            p.Steer = input.TurnInput;
            p.InControl = !veh.LevelWasEnded && input.CanControl && input.CanTakeInput && !veh.WaitingFirstInput;
            Vector3 v = rb.linearVelocity;
            p.Vx = v.x; p.Vz = v.z;

            p.Hits = -1; p.NearMisses = -1;
            if (ScoreOk) ReadScoreCounts(ref p);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadScoreCounts(ref PlayerState p)
        {
            var mgr = ScoreManager();
            if (mgr == null) return;
            CacheGameProviders(mgr);
            // the fields hold the already-cast wrappers (CacheGameProviders), so these are plain managed casts: no
            // per-frame TryCast wrapper allocations
            if (_gameDrift != null && ((Game.Runtime.Data.DriftScoreProviderSO)_gameDrift).IsBeingPerformed) p.Drifting = true;
            if (_gameCollision != null) p.Hits = ((Game.Runtime.Data.CollisionScoreProviderSO)_gameCollision).TotalHits;
            if (_gameNearMiss != null) p.NearMisses = ((Game.Runtime.Data.NearMissScoreProviderSO)_gameNearMiss).TotalNearMiss;
        }

        /// <summary>The level's score manager, searched for at most every 2 s.</summary>
        private static LevelScoreManager ScoreManager()
        {
            if (_scoreManager == null)
            {
                if (Time.unscaledTime < _nextScoreManagerSearch) return null;
                _nextScoreManagerSearch = Time.unscaledTime + 2f;
                _scoreManager = UnityEngine.Object.FindFirstObjectByType<LevelScoreManager>();
                if (_scoreManager == null) return null;
            }
            return (LevelScoreManager)_scoreManager;
        }

        // the game's own Drift / Collision / NearMiss categories (never our copy), cached per score manager. Typed as
        // ScriptableObject (see _world), but each holds the TryCast result, so its runtime type is the concrete wrapper.
        private static ScriptableObject _gameDrift, _gameCollision, _gameNearMiss;
        private static IntPtr _gameProvidersOwner;

        private static void CacheGameProviders(LevelScoreManager mgr)
        {
            if (_gameProvidersOwner == mgr.Pointer && (_gameCollision != null || _gameDrift != null)) return;
            _gameProvidersOwner = mgr.Pointer; _gameDrift = null; _gameCollision = null; _gameNearMiss = null;
            var list = mgr.scoreProviderList;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null || p.GetId() == NativeId) continue;
                if (_gameCollision == null && p.TryCast<Game.Runtime.Data.CollisionScoreProviderSO>() is var col && col != null) _gameCollision = col;
                else if (_gameDrift == null && p.TryCast<Game.Runtime.Data.DriftScoreProviderSO>() is var drift && drift != null) _gameDrift = drift;
                else if (_gameNearMiss == null && p.TryCast<Game.Runtime.Data.NearMissScoreProviderSO>() is var nm && nm != null) _gameNearMiss = nm;
            }
        }
    }
}
