using System;
using System.Collections.Generic;
using Game.Runtime.Multiplayer;
using Game.Runtime.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace TrafficDensity
{
    /// <summary>
    /// Stops NPC lane changes that would cut into you or into another car (README: "Safe lane changes").
    ///
    /// How the game decides a lane change (GameAssembly.dll, IDA 2026-10-04):
    /// - AIObstructionDetector.Update (0x180688440): every check interval, once the last change is
    ///   timeToWaitAfterChangingLanes old and a car ahead is closer than distanceToChangeLanes (and not faster), it takes
    ///   GetFreeLaneIndexList(2) (lanes up to TWO away) and calls MoveToLaneIndex(list[0]).
    /// - AIVehicleLaneHandler.Update (0x18068F950): _timeToReturnToLane after a change it steps one lane back towards
    ///   initialLane whenever IsLaneIndexFree says so (every frame until it goes).
    /// - IsLaneIndexFree (0x18068E5B0) walks RoadAvoidanceController.AvoidanceObjects (traffic, obstacles AND the player:
    ///   VehicleManager.OnEnable registers it) plus ObjectsChangingLanes, and only compares road distances: the other
    ///   object's [start, end] against [me - freeLaneCheckDistanceBehind, me + freeLaneCheckDistance] (6 m / 50 m).
    ///   Closing speed is never used, so you at 140 mph 30 m back count as "free" and the car pulls out in front of you.
    /// - MoveToLaneIndex (0x18068EF60) starts the eased change (CurrentLaneOffset -> laneOffsetList[index]) and
    ///   returns at once for a car that was hit.
    ///
    /// The fix is one Harmony prefix on MoveToLaneIndex that keeps the game's choice but cancels it when the target lane
    /// (and every lane crossed on the way) isn't clear with real margins: a fixed gap plus closing speed x seconds, for
    /// every player (bigger margins) and every traffic car, ahead and behind, oncoming cars included. A car also waits
    /// CooldownSeconds between changes and (OneLaneAtATime) never jumps two lanes at once. A cancelled change leaves the
    /// car in its lane, where the game's obstruction braking slows it behind the car ahead.
    /// Nothing is written to the game, so switching it off is instant and leaves nothing to restore.
    /// Cars Police drives (its daredevil rivals and chasers) and a call that keeps the current lane always pass.
    /// </summary>
    internal static class SafeLanes
    {
        private const float SnapshotAge = 0.1f;      // seconds a traffic snapshot is reused between decisions
        private const float BlockRetry = 0.4f;       // a car whose change was stopped is answered "no" this long without a new check
        private const float PlayerPredict = 1.0f;    // seconds of your sideways movement taken into account

        private sealed class CarState { public float LastChange = -100f, BlockedUntil = -1f; }
        private static readonly Dictionary<IntPtr, CarState> _cars = new Dictionary<IntPtr, CarState>();

        // snapshot of the traffic (struct arrays: no Il2Cpp reads inside the per-lane loop)
        private struct Obj { public IntPtr Ptr; public float Road, Lane, NextLane, Speed, HalfLen, HalfWidth; public bool Changing; }
        private static Obj[] _objs = new Obj[128];
        private static int _objN;
        private static float _snapTime = -1f;
        private static Obj[] _players = new Obj[8];
        private static int _playerN;
        private static float _lastPlayerLane = float.NaN, _lastPlayerTime = -1f, _playerLaneVel;

        // stats (logged every 60 s when anything happened)
        private static int _allowed, _byPlayer, _byCar, _byCooldown, _byJump;
        private static float _nextLog;
        private static bool _inside, _broken;
        private static int _errors;
        private static IntPtr _spawner;

        internal static bool Want => Plugin.Enabled.Value && Plugin.SafeLanesEnabled.Value && Leaderboard.FeaturesAllowed;

        /// <summary>The Harmony prefix. True = let the game change lanes, false = cancel this change.</summary>
        internal static bool Allow(AIVehicleLaneHandler lh, int moveIndex)
        {
            if (_inside || _broken || !Want) return true;
            _inside = true;
            try { return Decide(lh, moveIndex); }
            catch (Exception e)
            {
                if (++_errors >= 5) { _broken = true; Plugin.Log.LogError($"[Traffic] safe lane changes switched off after repeated errors: {e}"); }
                else Plugin.Log.LogWarning($"[Traffic] safe lane check failed (the game's change went ahead): {e.Message}");
                return true;
            }
            finally { _inside = false; }
        }

        private static bool Decide(AIVehicleLaneHandler lh, int moveIndex)
        {
            if (lh == null || !TrafficRunner.MayChangeTraffic()) return true;   // a client never decides traffic; host only with AllowInMultiplayer
            int cur = lh.CurrentLaneIndex;
            if (!Road.RefreshLanes()) return true;
            int last = Road.Lanes.Count - 1;
            int target = moveIndex < 0 ? 0 : moveIndex > last ? last : moveIndex;   // the game clamps the same way
            if (target == cur) return true;                                         // keeps its lane (also Police's hand-back)

            var car = lh.controller;
            if (car == null) return true;
            var pf = car.PathFollower;
            if (pf == null || pf.WasHit) return true;                               // the game ignores a hit car's change anyway
            IntPtr ptr = car.Pointer;
            if (Road.DrivenByPolice(ptr, pf)) return true;                          // Police steers it: never touch

            float now = Time.time;
            if (!_cars.TryGetValue(ptr, out var st)) _cars[ptr] = st = new CarState();
            if (now < st.BlockedUntil) return false;                                // stopped a moment ago: same answer, no new check

            if (now - st.LastChange < Plugin.LaneCooldown.Value) return Block(st, now, ref _byCooldown, "cooldown", car, target, 0f);
            int step = target > cur ? 1 : -1;
            if (Plugin.OneLaneAtATime.Value && Math.Abs(target - cur) > 1) return Block(st, now, ref _byJump, "two-lane jump", car, target, 0f);

            Snapshot(now);
            float myRoad = car.AvoidanceRoadDistance;
            float mySpeed = car.AvoidanceForwardVelocity;
            if (car.IsReversePath) mySpeed = -mySpeed;   // signed along the regular path (RacingLine's frame)
            Vector2 size = car.AvoidanceObjectSize;
            float myHalfLen = Mathf.Clamp(size.y * 0.5f, 1.5f, 12.5f);
            if (float.IsNaN(myRoad) || float.IsNaN(mySpeed)) return true;

            for (int lane = cur + step; ; lane += step)
            {
                float lt = Road.Lanes[lane];
                // you (every player in multiplayer): bigger margins, your sideways movement predicted
                for (int i = 0; i < _playerN; i++)
                {
                    ref Obj p = ref _players[i];
                    float lat = Road.LaneWidth * 0.5f + p.HalfWidth;
                    float predicted = i == 0 ? p.Lane + _playerLaneVel * PlayerPredict : p.Lane;   // [0] = the local player
                    bool inLane = Math.Abs(p.Lane - lt) < lat || Math.Abs(predicted - lt) < lat;
                    if (inLane && TooClose(myRoad, mySpeed, myHalfLen, p, Plugin.PlayerGapMetres.Value, Plugin.PlayerGapSeconds.Value, out float gap))
                        return Block(st, now, ref _byPlayer, "you", car, lane, gap);
                }
                // other traffic: in that lane now, or changing into it
                for (int i = 0; i < _objN; i++)
                {
                    ref Obj o = ref _objs[i];
                    if (o.Ptr == ptr) continue;
                    float lat = Road.LaneWidth * 0.5f + o.HalfWidth;
                    bool inLane = Math.Abs(o.Lane - lt) < lat || (o.Changing && Math.Abs(o.NextLane - lt) < lat);
                    if (inLane && TooClose(myRoad, mySpeed, myHalfLen, o, Plugin.CarGapMetres.Value, Plugin.CarGapSeconds.Value, out float gap))
                        return Block(st, now, ref _byCar, "another car", car, lane, gap);
                }
                if (lane == target) break;
            }

            st.LastChange = now;
            _allowed++;
            return true;
        }

        /// <summary>
        /// True when o is nearer than gap + closing speed x seconds (bodies excluded), ahead or behind. Speeds are signed
        /// along the regular path, so an oncoming car closes at both speeds added.
        /// </summary>
        private static bool TooClose(float myRoad, float mySpeed, float myHalfLen, in Obj o, float gapM, float gapS, out float clear)
        {
            float r = o.Road - myRoad;                       // + = o ahead (regular path)
            float closing = r >= 0f ? mySpeed - o.Speed : o.Speed - mySpeed;   // + = the gap shrinks
            clear = Math.Abs(r) - myHalfLen - o.HalfLen;     // bumper to bumper
            float need = Math.Max(0f, gapM) + Math.Max(0f, closing) * Math.Max(0f, gapS);
            return clear < need;
        }

        private static bool Block(CarState st, float now, ref int counter, string why, AIVehicleController car, int lane, float gap)
        {
            counter++;
            st.BlockedUntil = now + BlockRetry;
            Leaderboard.MarkUsed();   // the traffic was changed in this run (General.KeepOffLeaderboards)
            if (Plugin.SafeLanesLogEach.Value)
                Plugin.Log.LogInfo($"[Traffic] lane change stopped ({why}): car at {car.AvoidanceRoadDistance:0} m to lane {lane}" +
                                   (gap != 0f ? $", only {gap:0} m clear" : ""));
            return false;
        }

        /// <summary>Players and traffic, read at most every SnapshotAge seconds and only when a change is being decided.</summary>
        private static void Snapshot(float now)
        {
            if (now - _snapTime < SnapshotAge && now >= _snapTime) return;
            float dt = now - _snapTime;
            _snapTime = now;
            _objN = 0;
            var cars = Road.Cars();
            if (cars != null)
            {
                int count = cars.Count;
                if (_objs.Length < count) _objs = new Obj[count + 16];
                for (int i = 0; i < count; i++)
                {
                    var c = cars[i];
                    if (c == null || !c.IsActive) continue;
                    float road = c.AvoidanceRoadDistance, lane = c.AvoidanceLaneOffset, v = c.AvoidanceForwardVelocity;
                    if (float.IsNaN(road) || float.IsNaN(lane) || float.IsNaN(v)) continue;
                    Vector2 s = c.AvoidanceObjectSize;
                    var lh = c.LaneHandler;
                    bool changing = lh != null && lh.IsChangingLanes;
                    int next = c.NextLaneIndex;
                    _objs[_objN++] = new Obj
                    {
                        Ptr = c.Pointer, Road = road, Lane = lane, Speed = c.IsReversePath ? -v : v,
                        HalfLen = Mathf.Clamp(s.y * 0.5f, 1.5f, 12.5f), HalfWidth = Mathf.Clamp(s.x * 0.5f, 0.5f, 2f),
                        Changing = changing, NextLane = next >= 0 && next < Road.Lanes.Count ? Road.Lanes[next] : lane,
                    };
                }
            }

            _playerN = 0;
            var me = VehicleManager.Instance;
            if (me != null) AddPlayer(me.AvoidanceRoadDistance, me.AvoidanceLaneOffset, me.AvoidanceForwardVelocity, me.AvoidanceObjectSize);
            if (TrafficRunner.InMultiplayer())
            {
                var list = NetworkPlayer.instances;   // every player's network car (the host sees all of them)
                if (list != null)
                    for (int i = 0; i < list.Count && _playerN < _players.Length; i++)
                    {
                        var p = list[i];
                        if (p == null) continue;
                        AddPlayer(p.AvoidanceRoadDistance, p.AvoidanceLaneOffset, p.AvoidanceForwardVelocity, p.AvoidanceObjectSize);
                    }
            }

            // your sideways speed (local player), for the 1 s prediction
            if (_playerN > 0)
            {
                float lane = _players[0].Lane;
                if (!float.IsNaN(_lastPlayerLane) && dt > 0.01f && dt < 1f) _playerLaneVel = Mathf.Clamp((lane - _lastPlayerLane) / dt, -15f, 15f);
                else _playerLaneVel = 0f;
                _lastPlayerLane = lane;
                _lastPlayerTime = now;
            }
        }

        private static void AddPlayer(float road, float lane, float speed, Vector2 size)
        {
            if (_playerN >= _players.Length || float.IsNaN(road) || float.IsNaN(lane) || float.IsNaN(speed)) return;
            _players[_playerN++] = new Obj
            {
                Road = road, Lane = lane, Speed = speed,   // players always drive the regular path's way
                HalfLen = Mathf.Clamp(size.y * 0.5f, 1.5f, 6f), HalfWidth = Mathf.Clamp(size.x * 0.5f, 0.8f, 2.5f),
            };
        }

        /// <summary>From TrafficRunner (4x a second): forget cars on a new spawner, log the 60 s summary.</summary>
        internal static void Tick()
        {
            Road.Cars();   // notices a new spawner
            if (Road.SpawnerPtr != _spawner)
            {
                _spawner = Road.SpawnerPtr;
                _cars.Clear();
                _lastPlayerLane = float.NaN;
                _snapTime = -1f;
            }
            float now = Time.unscaledTime;
            if (now < _nextLog) return;
            _nextLog = now + 60f;
            int stopped = _byPlayer + _byCar + _byCooldown + _byJump;
            if (_allowed + stopped == 0) return;
            Plugin.Log.LogInfo($"[Traffic] safe lane changes (last 60 s): {_allowed} allowed, {stopped} stopped " +
                               $"(near you {_byPlayer}, near another car {_byCar}, cooldown {_byCooldown}, two-lane jumps {_byJump})");
            _allowed = _byPlayer = _byCar = _byCooldown = _byJump = 0;
            if (_cars.Count > 400) _cars.Clear();   // pooled cars come back with new pointers only on a new scene; keep it bounded anyway
        }

        internal static string Status() => _broken ? "safe lanes: off after errors" : null;
    }

    /// <summary>The one game hook of SafeLanes (see SafeLanes for the decision).</summary>
    [HarmonyPatch]
    internal static class SafeLanesPatch
    {
        [HarmonyPatch(typeof(AIVehicleLaneHandler), nameof(AIVehicleLaneHandler.MoveToLaneIndex))]
        [HarmonyPrefix]
        private static bool MoveToLaneIndexPrefix(AIVehicleLaneHandler __instance, int __0) => SafeLanes.Allow(__instance, __0);
    }
}
