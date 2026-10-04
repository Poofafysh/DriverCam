using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Runtime.Manager;
using Game.Runtime.Multiplayer;
using UnityEngine;

namespace TrafficDensity
{
    /// <summary>
    /// Shared reads for SafeLanes and SpeedLimits: the traffic car list of either spawner, the lanes, and which cars
    /// another plugin (Police) drives. Main thread only.
    ///
    /// Verified in GameAssembly.dll (IDA, 2026-10-04) and against the interop by the compiler:
    /// - Single-player traffic is DefaultAISpawner.activeAiCars; multiplayer traffic (host only) is
    ///   MultiplayerAISpawner.instantiatedCars (MultiplayerAISpawner derives from AISpawnerBase, not DefaultAISpawner).
    ///   Both hold the same AIVehicleController / AIPathFollower / AIVehicleLaneHandler components.
    /// - Lanes: AIVehicleLaneHandler.MoveToLaneIndex (0x18068EF60) clamps the index to RunWorldManager.laneOffsetList
    ///   and eases CurrentLaneOffset to laneOffsetList[index]; one list for the whole race (RoadTileContainer:
    ///   roadWidth 20, roadLaneCount 4).
    /// - Police (rogue.police) steers its daredevil rivals (Police.Daredevils.Owned, HashSet of car pointers) and its
    ///   chasers (rubber banding switched off on their AIPathFollower; every traffic prefab ships with it on).
    /// </summary>
    internal static class Road
    {
        private static IntPtr _spPtr;
        private static DefaultAISpawner _def;          // kept: the wrappers hold the lists alive
        private static MultiplayerAISpawner _mp;

        private static RunWorldManager _world;
        private static float _nextWorldSearch, _nextLaneRead;
        internal static readonly List<float> Lanes = new List<float>();
        internal static float LaneWidth = 5f;

        /// <summary>The active traffic list of the current spawner (single-player or multiplayer host), or null.</summary>
        internal static Il2CppSystem.Collections.Generic.List<AIVehicleController> Cars()
        {
            var sp = AISpawnerBase.Instance;
            if (sp == null) { _spPtr = IntPtr.Zero; _def = null; _mp = null; return null; }
            if (sp.Pointer != _spPtr)
            {
                _spPtr = sp.Pointer;
                ForgetScene();   // a new spawner = a new race: lanes and world read fresh
                _def = sp.TryCast<DefaultAISpawner>();
                _mp = _def == null ? sp.TryCast<MultiplayerAISpawner>() : null;
            }
            if (_def != null) return _def.activeAiCars;
            if (_mp != null) return _mp.instantiatedCars;
            return null;
        }

        /// <summary>Pointer of the current spawner (zero when there is none): a new one = a new race / scene.</summary>
        internal static IntPtr SpawnerPtr => _spPtr;

        /// <summary>The run's world manager (lanes, road width, biome), searched at most every 2 s while missing.</summary>
        internal static RunWorldManager World()
        {
            if (_world != null) return _world;
            float now = Time.unscaledTime;
            if (now < _nextWorldSearch) return null;
            _nextWorldSearch = now + 2f;   // never a scene search every frame
            _world = UnityEngine.Object.FindFirstObjectByType<RunWorldManager>();
            return _world;
        }

        /// <summary>Copies the lane centres (refreshed every 5 s, or now with force). False if unknown.</summary>
        internal static bool RefreshLanes(bool force = false)
        {
            float now = Time.unscaledTime;
            if (!force && Lanes.Count >= 1 && now < _nextLaneRead) return Lanes.Count >= 1;
            _nextLaneRead = now + 5f;
            var w = World();
            if (w == null) return Lanes.Count >= 1;
            var list = w.laneOffsetList;
            if (list == null || list.Count < 1) return Lanes.Count >= 1;
            Lanes.Clear();
            for (int i = 0; i < list.Count; i++) Lanes.Add(list[i]);
            if (Lanes.Count >= 2)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var l in Lanes) { if (l < lo) lo = l; if (l > hi) hi = l; }
                LaneWidth = Mathf.Max(2.5f, (hi - lo) / (Lanes.Count - 1));
            }
            else
            {
                float rw = w.CurrentRoadWidth;
                LaneWidth = rw > 1f && !float.IsNaN(rw) ? rw : 5f;
            }
            return true;
        }

        /// <summary>Scene change: forget the world manager and lanes (read fresh for the next race).</summary>
        internal static void ForgetScene()
        {
            _world = null;
            _nextWorldSearch = 0f;
            Lanes.Clear();
            _nextLaneRead = 0f;
        }

        // ------------------------------------------------------------------ cars another plugin drives

        private static HashSet<IntPtr> _dareOwned;
        private static float _nextDareLook;

        /// <summary>Police's daredevil rivals (Police.Daredevils.Owned), or null when Police isn't installed.</summary>
        private static HashSet<IntPtr> DaredevilsOwned()
        {
            if (_dareOwned != null) return _dareOwned;
            float now = Time.unscaledTime;
            if (now < _nextDareLook) return null;
            _nextDareLook = now + 10f;   // Police may load after us: look again now and then (cheap)
            try
            {
                var t = Type.GetType("Police.Daredevils, Police", false);
                var f = t?.GetField("Owned", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _dareOwned = f?.GetValue(null) as HashSet<IntPtr>;
            }
            catch { _dareOwned = null; }
            return _dareOwned;
        }

        /// <summary>
        /// True for a car Police drives: one of its daredevil rivals, or a chaser (rubber banding off: Police's chase
        /// switches it off and gives it back afterwards; every traffic prefab ships with it on). Never touch these.
        /// </summary>
        internal static bool DrivenByPolice(IntPtr carPtr, AIPathFollower pf)
        {
            var owned = DaredevilsOwned();
            if (owned != null && owned.Contains(carPtr)) return true;
            return pf != null && !pf.rubberBandingEnabled;
        }
    }
}
