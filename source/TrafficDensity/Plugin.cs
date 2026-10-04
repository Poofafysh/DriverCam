using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RogueShared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TrafficDensity
{
    /// <summary>
    /// Multiplies how many NPC traffic cars are on the road.
    ///
    /// How the game sizes traffic (DefaultAISpawner.Start @ 0x1806A6EE0):
    ///   aiSpawnCount = RoundToInt(race NPC count x spawnCountMultiplier)   // race: 6 (stage 1) .. 14 (stage 9) cars;
    ///                                                                      // spawnCountMultiplier = traffic hazard card
    ///   (spawnCountMultiplierDebug is only applied in debug builds, so it does nothing in the release game)
    ///   pool max size 100
    /// The spawner tops traffic up to aiSpawnCount all race long (TryRespawnVehicle), so changing it live works:
    /// raising it spawns more cars ahead/behind; lowering it stops spawning until cars leave.
    /// </summary>
    [BepInPlugin(Guid, "TrafficDensity", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.trafficdensity";
        public const string Version = "0.2.1";
        public const int PoolMax = 100;

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Multiplier;
        internal static ConfigEntry<int> MaxCars;
        internal static ConfigEntry<bool> AllowInMultiplayer;
        internal static ConfigEntry<string> Steps;
        internal static ConfigEntry<bool> ShowToast;
        internal static ConfigEntry<bool> FixesEnabled;
        internal static ConfigEntry<float> WreckClearDistance;
        internal static ConfigEntry<float> LaneChangeCheckBehind;
        internal static ConfigEntry<float> MinBrake;
        internal static ConfigEntry<float> ObstructionCheckInterval;
        internal static ConfigEntry<float> SpawnGap;
        internal static ConfigEntry<float> RubberBandSpeedFactor;
        internal static ConfigEntry<bool> PerfEnabled;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Master switch for the traffic multiplier and the AI fixes ([Fixes]).");
            Multiplier = Config.Bind("General", "Multiplier", 1.5f,
                "NPC traffic multiplier on top of the game's own amount (which already includes traffic hazard cards). 1 = stock, 2 = twice the cars, 0.5 = half.");
            MaxCars = Config.Bind("General", "MaxCars", 60, $"Upper limit on NPC cars at once (the game's traffic pool holds at most {PoolMax}). Very high counts cost frame rate.");
            AllowInMultiplayer = Config.Bind("General", "AllowInMultiplayer", false,
                "Also apply the multiplier and the AI fixes in multiplayer when you are the host. The host runs everyone's traffic, so all players get the extra cars and the changed NPC behaviour. Off = stock traffic in multiplayer. As a client nothing is ever changed: the host controls traffic.");
            Steps = Config.Bind("Keys", "Steps", "0.5,0.75,1,1.25,1.5,2,2.5,3,4",
                "Multiplier values Ctrl+PageUp / Ctrl+PageDown step through. Ctrl+Home resets to 1.");
            ShowToast = Config.Bind("Keys", "ShowToast", true, "Show a short on-screen message when the multiplier changes or a race starts.");

            const string game = " -1 = leave the game's value.";
            FixesEnabled = Config.Bind("Fixes", "Enabled", true,
                "Stop NPC traffic crashing into each other and jamming (matters most with extra traffic). Off = the game's own AI values.");
            WreckClearDistance = Config.Bind("Fixes", "WreckClearDistance", 40f,
                "NPC cars that crash into each other further than this many metres ahead of you are removed at once instead of blocking the lane until you pass (game: 150)." + game);
            LaneChangeCheckBehind = Config.Bind("Fixes", "LaneChangeCheckBehind", 25f,
                "How far behind an NPC checks the target lane before changing lanes, in metres (game: 6, so they cut in on cars beside them)." + game);
            MinBrake = Config.Bind("Fixes", "MinBrake", 0f,
                "Lowest throttle an NPC keeps when a car is close ahead, 0-1. 0 lets it slow right down behind a slow or stopped car (game: 0.2, Daredevil 0.6)." + game);
            ObstructionCheckInterval = Config.Bind("Fixes", "ObstructionCheckInterval", 0.2f,
                "Seconds between an NPC's checks for a car ahead (game: 0.5). Must be above 0." + game);
            SpawnGap = Config.Bind("Fixes", "SpawnGap", 30f,
                "Clear road the spawner wants around a new NPC in its lane, in metres (game: 20)." + game);
            RubberBandSpeedFactor = Config.Bind("Fixes", "RubberBandSpeedFactor", -1f,
                "Speed of NPCs far ahead of you, as a fraction of normal (game: 0.6). Higher = less bunching, but changes the game's pacing. -1 = leave the game's value.");

            PerfEnabled = Config.Bind("Perf", "Enabled", false,
                "Shared timing overlay for all Rogue mods (each mod times its own work into it). F4 toggles the overlay while this is on. Logs one summary line every 10 s. Off = no timing at all.");

            ClassInjector.RegisterTypeInIl2Cpp<TrafficRunner>();
            AddComponent<TrafficRunner>();
            new Harmony(Guid).PatchAll(typeof(SpawnerPatch));
            Log.LogInfo($"TrafficDensity {Version} loaded: x{Multiplier.Value:0.##} (Ctrl+PageUp/PageDown to change, Ctrl+Home = stock).");
        }

        internal static float[] StepValues()
        {
            var list = new List<float>();
            foreach (var s in (Steps.Value ?? "").Split(','))
                if (float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= 0f) list.Add(v);
            if (list.Count == 0) list.AddRange(new[] { 0.5f, 1f, 1.5f, 2f, 3f });
            return list.Distinct().OrderBy(v => v).ToArray();
        }
    }

    /// <summary>Remembers the game's own count for the current race, then applies the multiplier.</summary>
    [HarmonyPatch]
    internal static class SpawnerPatch
    {
        internal static DefaultAISpawner Spawner;
        internal static int BaseCount = -1;

        [HarmonyPatch(typeof(DefaultAISpawner), nameof(DefaultAISpawner.Start))]
        [HarmonyPostfix]
        private static void StartPostfix(DefaultAISpawner __instance)
        {
            Spawner = __instance;
            BaseCount = __instance.aiSpawnCount;   // game value incl. traffic hazard multiplier
            TrafficRunner.InvalidateRole();        // new race: read the multiplayer role fresh
            int target = TrafficRunner.Apply();
            Plugin.Log.LogInfo($"[Traffic] race start: game wants {BaseCount} NPC cars -> {target} (x{TrafficRunner.EffectiveMultiplier():0.##})");
            TrafficRunner.Toast($"Traffic x{TrafficRunner.EffectiveMultiplier():0.##}: {target} cars (stock {BaseCount})");
        }
    }

    public class TrafficRunner : MonoBehaviour
    {
        public TrafficRunner(IntPtr ptr) : base(ptr) { }

        private static string _toast = "";
        private static float _toastUntil;
        private float _nextCheck;
        private float _nextFix;

        // shared Perf helper (source/Shared/Perf.cs): TrafficDensity owns the overlay + log; [Perf] Enabled switches timing for every mod
        private static bool _perfClaimed, _perfOwner, _perfOverlay = true;
        private static readonly Action<string> _perfLog = s => Plugin.Log.LogInfo(s);

        /// <summary>
        /// Connected to someone else's game: Mirror client running without a local server. The host simulates all
        /// traffic and syncs it to clients (NetworkAIVehicle), so a client must never try to change it.
        /// </summary>
        internal static bool IsRemoteClient() { RefreshRole(); return _remoteClient; }

        internal static bool InMultiplayer() { RefreshRole(); return _inMultiplayer; }

        // Mirror reads + GameState.IsMultiplayerMode + the scene-name scan, cached for 1 s (asked several times a second). Main thread only.
        private static float _roleUntil = -1f;
        private static bool _remoteClient, _inMultiplayer;

        /// <summary>Forget the cached multiplayer role (race start), so the next check reads it fresh.</summary>
        internal static void InvalidateRole() => _roleUntil = -1f;

        private static void RefreshRole()
        {
            float now = Time.unscaledTime;
            if (now < _roleUntil) return;
            _roleUntil = now + 1f;
            bool remote;
            try { remote = Mirror.NetworkClient.active && !Mirror.NetworkServer.active; }
            catch { remote = false; }
            bool mp = remote || GameSaysMultiplayer();
            for (int i = 0; !mp && i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name.IndexOf("Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0) mp = true;
            _remoteClient = remote;
            _inMultiplayer = mp;
        }

        /// <summary>The game's own multiplayer flag; false if it can't be read (the scene-name and Mirror checks still apply).</summary>
        private static bool GameSaysMultiplayer()
        {
            try { return ReadGameMultiplayerFlag(); }   // separate method: a missing GameState fails here, not in RefreshRole
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadGameMultiplayerFlag() => Game.Runtime.GameState.IsMultiplayerMode;

        /// <summary>We own the traffic simulation and the person allowed changes: offline, or hosting with AllowInMultiplayer.</summary>
        internal static bool MayChangeTraffic() => !IsRemoteClient() && (Plugin.AllowInMultiplayer.Value || !InMultiplayer());

        internal static float EffectiveMultiplier()
        {
            if (!Plugin.Enabled.Value || !MayChangeTraffic()) return 1f;
            return Mathf.Max(0f, Plugin.Multiplier.Value);
        }

        /// <summary>Sets the live spawn cap from the game's count; returns the new cap.</summary>
        internal static int Apply()
        {
            var sp = SpawnerPatch.Spawner;
            if (sp == null || SpawnerPatch.BaseCount < 0) return -1;
            int cap = Mathf.Clamp(Plugin.MaxCars.Value, 0, Plugin.PoolMax);
            int target = Mathf.Clamp(Mathf.RoundToInt(SpawnerPatch.BaseCount * EffectiveMultiplier()), 0, Mathf.Max(cap, SpawnerPatch.BaseCount));
            if (sp.aiSpawnCount != target) sp.aiSpawnCount = target;
            return target;
        }

        internal static void Toast(string text)
        {
            if (!Plugin.ShowToast.Value) return;
            _toast = text;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        private void Update()
        {
            PerfUpdate();
            using (Perf.Scope("Traffic.Update"))
            {
                try
                {
                    var kb = Keyboard.current;
                    if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
                    {
                        if (kb.pageUpKey.wasPressedThisFrame) Step(+1);
                        else if (kb.pageDownKey.wasPressedThisFrame) Step(-1);
                        else if (kb.homeKey.wasPressedThisFrame) Set(1f);
                    }
                    // AI fixes on active cars (new cars get them within a quarter second; they spawn 90+ m away)
                    if (Time.unscaledTime >= _nextFix)
                    {
                        _nextFix = Time.unscaledTime + 0.25f;
                        try
                        {
                            using (Perf.Scope("Traffic.Fixes"))
                                Fixes.Tick(SpawnerPatch.Spawner, Plugin.Enabled.Value && Plugin.FixesEnabled.Value && MayChangeTraffic());
                        }
                        catch (Exception e) { Plugin.Log.LogError(e); _nextFix = Time.unscaledTime + 5f; }
                    }
                    // keep the cap right if the config file was edited / reloaded or the spawner was replaced
                    if (Time.unscaledTime >= _nextCheck)
                    {
                        _nextCheck = Time.unscaledTime + 1f;
                        if (SpawnerPatch.Spawner == null) { SpawnerPatch.BaseCount = -1; return; }
                        using (Perf.Scope("Traffic.Apply")) Apply();
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(e);
                    _nextCheck = _nextFix = Time.unscaledTime + 5f;
                }
            }
        }

        /// <summary>Owner side of the shared Perf helper: follow [Perf] Enabled, close the frame, F4 overlay, 10 s log line.</summary>
        private static void PerfUpdate()
        {
            try
            {
                if (!_perfClaimed)
                {
                    _perfClaimed = true;
                    _perfOwner = Perf.ClaimOwner("TrafficDensity");
                    if (!_perfOwner) Plugin.Log.LogInfo($"[Perf] overlay owned by {Perf.Owner}; [Perf] settings here are ignored");
                }
                if (!_perfOwner) return;
                bool want = Plugin.PerfEnabled.Value;
                if (want != Perf.Enabled)
                {
                    Perf.SetEnabled(want);
                    Plugin.Log.LogInfo(want ? "[Perf] timing on for all Rogue mods (F4 toggles the overlay)" : "[Perf] timing off");
                }
                if (!want) return;
                Perf.Tick();
                var kb = Keyboard.current;
                if (kb != null && kb.f4Key.wasPressedThisFrame) _perfOverlay = !_perfOverlay;
                Perf.LogEvery(_perfLog, 10);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Perf] switched off after an error: {e}");
                _perfOwner = false;
                Perf.SetEnabled(false);
            }
        }

        private static void Step(int dir)
        {
            var steps = Plugin.StepValues();
            float cur = Plugin.Multiplier.Value;
            float next = dir > 0 ? steps.FirstOrDefault(v => v > cur + 1e-4f) : steps.LastOrDefault(v => v < cur - 1e-4f);
            if (dir > 0 && next <= cur) next = steps.Last();
            if (dir < 0 && next >= cur) next = steps.First();
            Set(next);
        }

        private static void Set(float value)
        {
            Plugin.Multiplier.Value = value;   // saved to rogue.trafficdensity.cfg
            int target = Apply();
            string mp = IsRemoteClient() ? " (multiplayer client: the host controls traffic)"
                      : !Plugin.AllowInMultiplayer.Value && InMultiplayer() ? " (multiplayer: stock)" : "";
            string cars = target >= 0 ? $": {target} cars (stock {SpawnerPatch.BaseCount})" : " (applies at the next race)";
            Toast($"Traffic x{value:0.##}{cars}{mp}");
            Plugin.Log.LogInfo($"[Traffic] multiplier set to x{value:0.##}{cars}{mp}");
        }

        private void OnGUI()
        {
            if (_perfOwner && _perfOverlay && Perf.Enabled)
            {
                // top-left corner, 12 px in (scaled by screen height): at most about 520 x 290 px at 1080p (12 rows),
                // above DriverCam's button (x 20, y 420) and clear of CurbFeel (top-right) and the toast (top-centre)
                try { Perf.DrawOverlay(); }
                catch (Exception e) { Plugin.Log.LogError($"[Perf] overlay hidden after an error: {e}"); _perfOverlay = false; }
            }
            if (Time.unscaledTime > _toastUntil || string.IsNullOrEmpty(_toast)) return;
            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            GUI.skin.box.fontSize = Mathf.RoundToInt(18 * s);
            float w = 460 * s, h = 36 * s;
            GUI.Box(new Rect((Screen.width - w) / 2f, 70 * s, w, h), _toast);   // top-centre: clear of DriverCam (left) and CurbFeel (top-right)
        }
    }
}
