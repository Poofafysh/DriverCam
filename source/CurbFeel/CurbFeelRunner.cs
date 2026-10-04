using System;
using RogueShared;
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

        // The panel only uses GUI.* (no GUILayout), so skip Unity's extra Layout pass of OnGUI.
        private void Awake() => useGUILayout = false;

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
        // -10 = "never ran": makes Due() phase the first ticks even if the game is less than a second old
        private static float _nextHull = -10f, _nextWalls = -10f, _nextTraffic = -10f;

        // Periods and phase offsets of the periodic ticks: staggered so walls / hull / traffic never run on the same frame.
        private const float WallsPeriod = 0.25f, HullPeriod = 0.5f, TrafficPeriod = 0.5f;
        private const float WallsPhase = 0f, HullPhase = 0.17f, TrafficPhase = 0.33f;

        private static readonly KeyBinding ReloadBinding = new(Key.F9);
        private static readonly KeyBinding ToggleBinding = new(Key.F10);
        private static readonly KeyBinding OverlayBinding = new(Key.F8);

        public static void Update()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null)
                {
                    if (ReloadBinding.Pressed(kb, Settings.ReloadKey.Value)) Reload();
                    if (ToggleBinding.Pressed(kb, Settings.ToggleKey.Value)) Toggle();
                    if (OverlayBinding.Pressed(kb, Settings.OverlayKey.Value)) Overlay.Cycle();
                }

                ScrapePatches.EnsureContinuousPatch();

                if (!Settings.Enabled.Value) return;
                float now = Time.unscaledTime;
                if (Due(ref _nextWalls, WallsPeriod, WallsPhase, now)) using (Perf.Scope("CurbFeel.Walls")) Walls.Tick();
                if (Due(ref _nextHull, HullPeriod, HullPhase, now)) using (Perf.Scope("CurbFeel.Hull")) Hull.Tick();
                if (Due(ref _nextTraffic, TrafficPeriod, TrafficPhase, now)) using (Perf.Scope("CurbFeel.Traffic")) Traffic.Tick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                float t = Time.unscaledTime + 5f;   // back off instead of spamming, keeping the stagger
                _nextWalls = t + WallsPhase; _nextHull = t + HullPhase; _nextTraffic = t + TrafficPhase;
            }
        }

        /// <summary>
        /// Fixed-rate timer with a phase offset. The first call, or one after a gap of over a second (master switch off,
        /// a loading hitch), re-phases to now + phase so the ticks stay staggered. Steps by the period instead of
        /// now + period so frame-time jitter doesn't slowly drift the ticks onto the same frame.
        /// </summary>
        private static bool Due(ref float next, float period, float phase, float now)
        {
            if (now - next > 1f) next = now + phase;
            if (now < next) return false;
            next += period;
            if (next <= now) next = now + period;
            return true;
        }

        public static void OnGUI()
        {
            try
            {
                bool clicked;
                using (Perf.Scope("CurbFeel.Overlay")) clicked = Overlay.Draw();
                if (clicked) Reapply("panel click");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                Settings.OverlayMode.Value = "Hidden";
            }
        }

        /// <summary>A configured key name, parsed only when the setting's text changes (not every frame).</summary>
        private sealed class KeyBinding
        {
            private readonly Key _fallback;
            private string _name;
            private Key _key;

            public KeyBinding(Key fallback) { _fallback = fallback; _key = fallback; }

            public bool Pressed(Keyboard kb, string name)
            {
                if (!string.Equals(name, _name, StringComparison.Ordinal))
                {
                    _name = name;
                    if (!Enum.TryParse(name, true, out _key) || _key == Key.None) _key = _fallback;
                }
                var control = kb[_key];
                return control != null && control.wasPressedThisFrame;
            }
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
