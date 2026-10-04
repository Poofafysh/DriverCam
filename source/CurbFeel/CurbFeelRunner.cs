using System;
using BepInEx.Configuration;
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

        // plugin unload (game quit): the ramps, wall views, their meshes and the cached materials go now, not with the process
        private void OnDestroy()
        {
            try { CurbFeelCore.RevertAll(); } catch { /* shutting down */ }
            try { CurbFeelCore.ReleaseCaches(); } catch { /* shutting down */ }
        }
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
                if (_changedAt > 0f && Time.unscaledTime - _changedAt >= ChangeSettle)
                {
                    _changedAt = 0f;
                    Reapply("settings changed");
                }

                if (!Settings.Enabled.Value) return;
                float now = Time.unscaledTime;
                if (Due(ref _nextWalls, WallsPeriod, WallsPhase, now)) using (Perf.Scope("CurbFeel.Walls")) Walls.Tick();
                if (Due(ref _nextHull, HullPeriod, HullPhase, now)) using (Perf.Scope("CurbFeel.Hull")) Hull.Tick();
                if (Due(ref _nextTraffic, TrafficPeriod, TrafficPhase, now)) using (Perf.Scope("CurbFeel.Traffic")) Traffic.Tick();
                // a newly loaded tile's sidewalk map, read a little every frame (only while one is being built)
                if (SidewalkMap.Building) using (Perf.Scope("CurbFeel.Map")) SidewalkMap.Pump(Settings.MapBudgetMs.Value);
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
                if (HubLink.HubOpen) return;   // Rogue Hub on screen: no panel over it (and no clicks through it)
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

        // ------------------------------------------------------------------ settings edited live (Rogue Hub, panel)
        // A setting that shapes walls / hull / ramps / traffic boxes only takes effect when they are fitted again, so a
        // change marks them; ChangeSettle seconds after the last change (a slider held down sends many) everything is
        // reverted and the next ticks re-fit with the new values. Panel / log / key settings need nothing.
        private const float ChangeSettle = 0.5f;
        private static float _changedAt;
        private static ConfigFile _hooked;
        private static EventHandler<SettingChangedEventArgs> _onChanged;

        internal static void HookConfig(ConfigFile cfg)
        {
            UnhookConfig();
            _hooked = cfg;
            _onChanged = OnSettingChanged;
            cfg.SettingChanged += _onChanged;
            HubLink.Status(Plugin.Guid, HubStatus);
            HubLink.Action(Plugin.Guid, "refit", "Re-fit walls now", "Puts every wall, ramp and hit box back to stock and fits them again with the current settings.", HubRefit);
        }

        internal static void UnhookConfig()
        {
            if (_hooked != null && _onChanged != null) _hooked.SettingChanged -= _onChanged;
            _hooked = null;
            _onChanged = null;
            _changedAt = 0f;
        }

        private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            try
            {
                string key = e?.ChangedSetting?.Definition?.Key ?? "";
                string section = e?.ChangedSetting?.Definition?.Section ?? "";
                if (section == "General" && key != "Enabled") return;   // panel, log, keys: nothing to re-fit
                _changedAt = Time.unscaledTime;
            }
            catch { /* never break a settings write */ }
        }

        /// <summary>Rogue Hub: the live line on CurbFeel's card.</summary>
        private static string HubStatus()
        {
            if (!Settings.Enabled.Value) return "off: stock walls, hull, damage and traffic";
            return Stats.WallPairs > 0
                ? $"walls moved on {Stats.WallPairs} road edges, {Stats.Ramps} curb ramps"
                : "waiting for road tiles";
        }

        /// <summary>Rogue Hub button.</summary>
        private static string HubRefit()
        {
            Reapply("re-fit from Rogue Hub");
            return "CurbFeel: walls re-fitted with the current settings";
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
            _changedAt = 0f;   // this re-fit covers the change that triggered it: no second one half a second later
            Plugin.Log.LogInfo($"[CurbFeel] {why}: {StateLine()}");
        }

        private static void Reload()
        {
            RevertAll();
            Plugin.Cfg.Reload();
            _changedAt = 0f;   // Reload raised SettingChanged for every changed value; the revert above already covers them
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
