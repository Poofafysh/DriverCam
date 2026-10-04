using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Manager;
using Game.Runtime.Systems.LevelGeneration;
using UnityEngine;

namespace RacingLine
{
    /// <summary>
    /// The only file that touches game types (design doc: "What the plugin reads: the contract"). Read only.
    ///
    /// Check() looks every member up by name in the interop assembly once at startup. A member missing after a game
    /// update switches off only the features that need it. Each accessor sits in its own non-inlined method, so code
    /// referring to a missing member is never compiled unless its check passed.
    /// </summary>
    internal static partial class GameApi
    {
        internal static bool PathOk { get; private set; }
        internal static bool WidthOk { get; private set; }
        internal static bool PlayerOk { get; private set; }    // GameApi.Player.cs
        internal static bool ScoreOk { get; private set; }     // GameApi.Native.cs: drift flag, hit count, native provider
        internal static bool ResultsOk { get; private set; }   // GameApi.Results.cs
        internal static bool ModeOk { get; private set; }      // GameState.IsMultiplayerMode
        // TrafficOk: GameApi.Traffic.cs (spawner, traffic cars)

        // typed as MonoBehaviour, not RunWorldManager: a field of a game type would stop this class loading at all if
        // a game update removed that type, and then Check() couldn't even report it
        private static MonoBehaviour _world;
        private static float _nextWorldSearch;

        internal static void Check()
        {
            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null) { try { asm = Assembly.Load("Assembly-CSharp"); } catch { /* reported as missing below */ } }
            PathOk = Has(asm, "Game.Runtime.Systems.LevelGeneration.RoadPathGenerator", missing, "Instance", "RegularPath")
                  && Has(asm, "IRoadPath", missing, "TotalLength", "GetPositionFromDistance", "GetDirectionFromDistance");
            WidthOk = Has(asm, "Game.Runtime.Manager.RunWorldManager", missing, "CurrentRoadWidth", "laneOffsetList", "currentStageIndex", "currentRaceIndex");
            CheckVictory(asm, missing);
            PlayerOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "Rigidbody", "PlayerPathFollower", "VehicleMovement",
                               "VehicleInputHandler", "LevelWasEnded", "WaitingFirstInput")
                    && Has(asm, "Game.Runtime.Vehicle.PlayerPathFollower", missing, "GetDistanceTravelled", "GetLaneOffset")
                    && Has(asm, "Game.Runtime.Vehicle.VehicleMovement", missing, "CurrentSpeed", "OriginalMaxSpeed", "Drifting", "IsGrounded")
                    && Has(asm, "Game.Runtime.Vehicle.VehicleInputHandler", missing, "Throttle", "BrakeInput", "TurnInput", "CanControl", "CanTakeInput");
            ScoreOk = Has(asm, "Game.Runtime.Manager.LevelScoreManager", missing, "scoreProviderList")
                   && Has(asm, "Game.Runtime.Data.AScoreProviderSO", missing, "GetId", "SetId", "Initialize", "AddToScore", "IsBeingPerformed",
                          "scoreName", "scoreHudIcon", "statListIcon", "targetCoinReward", "targetActionValueMinCurvature", "targetActionValueMaxCurvature",
                          "contributeToCombo", "CurrentScore", "CoinReward")
                   && Has(asm, "Game.Runtime.Data.TopSpeedScoreProviderSO", missing, "timeThreshold", "targetSpeedFactor", "scoreMultiplier", "cancelOnCollision", "totalTopSpeedTime")
                   && Has(asm, "Game.Runtime.Data.CollisionScoreProviderSO", missing, "TotalHits")
                   && Has(asm, "Game.Runtime.Data.NearMissScoreProviderSO", missing, "TotalNearMiss")
                   && Has(asm, "Game.Runtime.Data.DriftScoreProviderSO", missing);
            ResultsOk = Has(asm, "Game.Runtime.UI.RankingStatsController", missing, "rankingStatList")
                     && Has(asm, "Game.Runtime.UI.RankingStatItem", missing, "Setup");
            ModeOk = Has(asm, "Game.Runtime.GameState", missing, "IsMultiplayerMode");
            CheckTraffic(asm, missing);

            if (missing.Count == 0) Plugin.Log.LogInfo("[RacingLine] game check OK: path, road width, player, scoring, results screen, game mode, traffic");
            else Plugin.Log.LogWarning($"[RacingLine] game check: missing {string.Join(", ", missing)}. Line {On(PathOk)}, road width {On(WidthOk)}, " +
                                       $"player {On(PlayerOk)}, scoring {On(ScoreOk)}, results row {On(ResultsOk)}, game mode {On(ModeOk)}, " +
                                       $"traffic-aware line {On(TrafficOk)}. Re-check the contract table after a game update.");
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

        internal const float MaxPathLength = 100000f;   // 100 km: anything longer is bad data, not a road

        /// <summary>
        /// The run's centre-line path, read fresh from the live generator every time and never cached:
        /// RoadPathGenerator.OnDestroy disposes the path's native spline without clearing the reference, so only a live
        /// (Unity non-null) generator makes the path safe to touch. Null in menus and while loading.
        /// </summary>
        private static IRoadPath LivePath()
        {
            var gen = RoadPathGenerator.Instance;
            if (gen == null) return null;   // Unity null: destroyed generators count as gone
            return gen.RegularPath;
        }

        /// <summary>
        /// Reads the run's path: ptr identifies the path object (a new run makes a new one), length is its current length.
        /// False if there is none, or its length is not a finite 0-100 km. Only call when PathOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool RefreshPath(out IntPtr ptr, out float length)
        {
            ptr = IntPtr.Zero; length = 0f;
            var path = LivePath();
            if (path == null) return false;
            ptr = path.Pointer;
            length = path.TotalLength;
            return length > 0f && length <= MaxPathLength && !float.IsNaN(length) && !float.IsInfinity(length);
        }

        /// <summary>
        /// Samples the path at distance i * step for i in [from, from + count): position and the road's right-hand normal
        /// (flattened). Re-reads the live path first and returns false if it is gone or is no longer expectedPtr (a new run),
        /// or if the game returns a non-finite value. Only call when PathOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Sample(IntPtr expectedPtr, float step, int from, int count, float[] px, float[] py, float[] pz, float[] nx, float[] nz)
        {
            var path = LivePath();
            if (path == null || path.Pointer != expectedPtr) return false;
            for (int i = from; i < from + count; i++)
            {
                float d = i * step;
                Vector3 p = path.GetPositionFromDistance(d);
                Vector3 dir = path.GetDirectionFromDistance(d);
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(dir.x) || !Finite(dir.z)) return false;
                float rx = dir.z, rz = -dir.x;                     // Cross(up, dir), flattened: lane offsets are horizontal
                float len = (float)Math.Sqrt(rx * rx + rz * rz);
                if (len < 1e-4f) { rx = i > 0 ? nx[i - 1] : 1f; rz = i > 0 ? nz[i - 1] : 0f; }
                else { rx /= len; rz /= len; }
                px[i] = p.x; py[i] = p.y; pz[i] = p.z;
                nx[i] = rx; nz[i] = rz;
            }
            return true;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>Usable road width in metres, or -1 if unknown. Only call when WidthOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float RoadWidth()
        {
            if (_world == null)
            {
                if (Time.unscaledTime < _nextWorldSearch) return -1f;
                _nextWorldSearch = Time.unscaledTime + 2f;   // never a scene search every frame
                _world = UnityEngine.Object.FindFirstObjectByType<RunWorldManager>();
                if (_world == null) return -1f;
            }
            var world = (RunWorldManager)_world;
            float w = world.CurrentRoadWidth;
            if (w > 0f && !float.IsNaN(w)) return w;

            // fallback: outermost lane centres plus half a lane on each side
            var lanes = world.laneOffsetList;
            if (lanes == null || lanes.Count < 2) return -1f;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < lanes.Count; i++) { lo = Mathf.Min(lo, lanes[i]); hi = Mathf.Max(hi, lanes[i]); }
            float laneWidth = (hi - lo) / (lanes.Count - 1);
            return hi - lo + laneWidth;
        }

        internal static void ForgetScene() => _world = null;
    }
}
