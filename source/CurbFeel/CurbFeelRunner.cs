using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CurbFeel
{
#if !HOT
    /// <summary>Plugin build: the injected MonoBehaviour that forwards Unity's Update/OnGUI to CurbFeelCore.
    /// (The hot-module build has no injected type; the HotReload host calls CurbFeelCore through HotModule.cs.)</summary>
    public class CurbFeelRunner : MonoBehaviour
    {
        public CurbFeelRunner(IntPtr ptr) : base(ptr) { }

        private void Update() => CurbFeelCore.Update();

        private void OnGUI() => CurbFeelCore.OnGUI();
    }
#endif

    /// <summary>Drives every feature, draws the status panel, and handles the reload / toggle / panel keys.</summary>
    internal static class CurbFeelCore
    {
        private static readonly HullTrimmer Hull = new();
        private static readonly WallShifter Walls = new();
        private static readonly TrafficTuner Traffic = new();
        private static float _nextHull, _nextWalls, _nextTraffic;

        public static void Update()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null)
                {
                    if (Pressed(kb, Settings.ReloadKey.Value, Key.F9)) Reload();
                    if (Pressed(kb, Settings.ToggleKey.Value, Key.F10)) Toggle();
                    if (Pressed(kb, Settings.OverlayKey.Value, Key.F8)) Overlay.Cycle();
                }

                if (!Settings.Enabled.Value) return;
                float now = Time.unscaledTime;
                if (now >= _nextHull) { _nextHull = now + 0.5f; Hull.Tick(); }
                if (now >= _nextWalls) { _nextWalls = now + 0.25f; Walls.Tick(); }
                if (now >= _nextTraffic) { _nextTraffic = now + 0.5f; Traffic.Tick(); }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                _nextHull = _nextWalls = _nextTraffic = Time.unscaledTime + 5f;   // back off instead of spamming
            }
        }

        public static void OnGUI()
        {
            try
            {
                if (Overlay.Draw()) Reapply("panel click");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                Settings.OverlayMode.Value = "Hidden";
            }
        }

        private static bool Pressed(Keyboard kb, string name, Key fallback)
        {
            if (!Enum.TryParse(name, true, out Key key) || key == Key.None) key = fallback;
            var control = kb[key];
            return control != null && control.wasPressedThisFrame;
        }

        /// <summary>Undo every change CurbFeel made in the game (hull, walls, ramps, traffic boxes, near-miss range).</summary>
        public static void RevertAll()
        {
            Hull.Revert();
            Walls.Revert();
            Traffic.Revert();
            Stats.ResetApplied();
        }

        /// <summary>Hot-module unload, after RevertAll: drop cached game objects so nothing of this build stays referenced.</summary>
        public static void ReleaseCaches()
        {
            Walls.DestroyDebugMaterial();
            ScrapePatches.ClearCaches();
            SidewalkMap.ClearCache();
        }

        /// <summary>Undo everything; the next ticks re-apply with the current settings.</summary>
        private static void Reapply(string why)
        {
            RevertAll();
            Plugin.Log.LogInfo($"[CurbFeel] {why}: {StateLine()}");
        }

        private static void Reload()
        {
            RevertAll();
            Plugin.Cfg.Reload();
            Plugin.Log.LogInfo($"[CurbFeel] config reloaded: {StateLine()}");
        }

        private static void Toggle()
        {
            Settings.Enabled.Value = !Settings.Enabled.Value;
            Reapply(Settings.Enabled.Value ? "ON" : "OFF (stock walls, hull, damage, traffic)");
        }

        internal static string StateLine() =>
            $"master={On(Settings.Enabled.Value)} hull={On(Settings.HullEnabled.Value)} walls={On(Settings.WallsEnabled.Value)} " +
            $"ramp={On(Settings.RampEnabled.Value)} scrape={On(Settings.ScrapeEnabled.Value)} traffic={On(Settings.TrafficEnabled.Value)} " +
            $"overCurb={Settings.AllowedOverCurb.Value} rampH={Settings.RampHeight.Value} trafficW={Settings.TrafficWidthScale.Value}";

        private static string On(bool b) => b ? "ON" : "off";
    }
}
