using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
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
        public const string Version = "0.1.0";
        public const int PoolMax = 100;

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Multiplier;
        internal static ConfigEntry<int> MaxCars;
        internal static ConfigEntry<bool> AllowInMultiplayer;
        internal static ConfigEntry<string> Steps;
        internal static ConfigEntry<bool> ShowToast;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Apply the traffic multiplier.");
            Multiplier = Config.Bind("General", "Multiplier", 1.5f,
                "NPC traffic multiplier on top of the game's own amount (which already includes traffic hazard cards). 1 = stock, 2 = twice the cars, 0.5 = half.");
            MaxCars = Config.Bind("General", "MaxCars", 60, $"Upper limit on NPC cars at once (the game's traffic pool holds at most {PoolMax}). Very high counts cost frame rate.");
            AllowInMultiplayer = Config.Bind("General", "AllowInMultiplayer", false,
                "Also apply in multiplayer lobbies (if you host, everyone sees the extra traffic). Off = stock traffic in multiplayer.");
            Steps = Config.Bind("Keys", "Steps", "0.5,0.75,1,1.25,1.5,2,2.5,3,4",
                "Multiplier values Ctrl+PageUp / Ctrl+PageDown step through. Ctrl+Home resets to 1.");
            ShowToast = Config.Bind("Keys", "ShowToast", true, "Show a short on-screen message when the multiplier changes or a race starts.");

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

        internal static bool InMultiplayer()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name.IndexOf("Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static float EffectiveMultiplier()
        {
            if (!Plugin.Enabled.Value) return 1f;
            if (!Plugin.AllowInMultiplayer.Value && InMultiplayer()) return 1f;
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
            try
            {
                var kb = Keyboard.current;
                if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
                {
                    if (kb.pageUpKey.wasPressedThisFrame) Step(+1);
                    else if (kb.pageDownKey.wasPressedThisFrame) Step(-1);
                    else if (kb.homeKey.wasPressedThisFrame) Set(1f);
                }
                // keep the cap right if the config file was edited / reloaded or the spawner was replaced
                if (Time.unscaledTime >= _nextCheck)
                {
                    _nextCheck = Time.unscaledTime + 1f;
                    if (SpawnerPatch.Spawner == null) { SpawnerPatch.BaseCount = -1; return; }
                    Apply();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                _nextCheck = Time.unscaledTime + 5f;
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
            string mp = !Plugin.AllowInMultiplayer.Value && InMultiplayer() ? " (multiplayer: stock)" : "";
            string cars = target >= 0 ? $": {target} cars (stock {SpawnerPatch.BaseCount})" : " (applies at the next race)";
            Toast($"Traffic x{value:0.##}{cars}{mp}");
            Plugin.Log.LogInfo($"[Traffic] multiplier set to x{value:0.##}{cars}{mp}");
        }

        private void OnGUI()
        {
            if (Time.unscaledTime > _toastUntil || string.IsNullOrEmpty(_toast)) return;
            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            GUI.skin.box.fontSize = Mathf.RoundToInt(18 * s);
            float w = 460 * s, h = 36 * s;
            GUI.Box(new Rect((Screen.width - w) / 2f, 70 * s, w, h), _toast);   // top-centre: clear of DriverCam (left) and CurbFeel (top-right)
        }
    }
}
