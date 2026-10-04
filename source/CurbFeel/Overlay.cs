using BepInEx.Configuration;
using UnityEngine;

namespace CurbFeel
{
    /// <summary>
    /// Top-right status panel. Full: every feature with live counters (rows are clickable when the cursor is visible).
    /// Compact: one line. Hidden. Cycle with OverlayKey (F8).
    /// OnGUI runs several times per frame; the panel only does work on Repaint (drawing) and MouseDown/MouseUp (the
    /// buttons' clicks), and its text is rebuilt at most every TextRefresh seconds instead of on every call.
    /// </summary>
    internal static class Overlay
    {
        public enum Mode { Full, Compact, Hidden }

        private static readonly Color On = new(0.35f, 1f, 0.45f);
        private static readonly Color Off = new(1f, 0.4f, 0.4f);
        private static readonly Color Dim = new(0.75f, 0.75f, 0.75f);
        private static readonly Color Flash = new(1f, 0.85f, 0.2f);

        private const float TextRefresh = 0.2f;

        private static string _modeRaw;
        private static Mode _mode = Mode.Full;

        public static Mode Current
        {
            get
            {
                string raw = Settings.OverlayMode.Value;
                if (!string.Equals(raw, _modeRaw, System.StringComparison.Ordinal))
                {
                    _modeRaw = raw;
                    if (string.Equals(raw, "compact", System.StringComparison.OrdinalIgnoreCase)) _mode = Mode.Compact;
                    else if (string.Equals(raw, "hidden", System.StringComparison.OrdinalIgnoreCase)) _mode = Mode.Hidden;
                    else _mode = Mode.Full;
                }
                return _mode;
            }
        }

        public static void Cycle()
        {
            var cur = Current;
            var next = cur == Mode.Full ? Mode.Compact : cur == Mode.Compact ? Mode.Hidden : Mode.Full;
            Settings.OverlayMode.Value = next.ToString();
        }

        // ---------------------------------------------------------------- cached text
        private static float _textAt = -100f, _textEventTime = float.NaN;
        private static bool _textMaster, _textFlashing;
        private static string _compactLine, _title, _trafficLine, _lanesLine, _footer;
        private static readonly string[] RowLabel = new string[5], RowInfo = new string[5];

        private static void RefreshText(bool master, bool flashing)
        {
            float now = Time.unscaledTime;
            if (now - _textAt < TextRefresh && master == _textMaster && flashing == _textFlashing && Stats.LastEventTime == _textEventTime)
                return;
            _textAt = now; _textMaster = master; _textFlashing = flashing; _textEventTime = Stats.LastEventTime;

            _compactLine = master
                ? $"CurbFeel ON   walls {Stats.WallPairs}  ramps {Stats.Ramps}  swipes {Stats.SideSwipes}  scrapes {Stats.SoftScrapes}"
                : "CurbFeel OFF (stock)   F10 to enable";
            _title = master ? $"CurbFeel {Plugin.Version}:  ON" : $"CurbFeel {Plugin.Version}:  OFF (everything stock)";

            SetRow(0, "A  Car hull", Settings.HullEnabled, master,
                Stats.PlayerBodyHalfWidth > 0 ? $"body {Stats.PlayerBodyHalfWidth:F2} m, {Stats.HullCapsules} capsules / {Stats.HullCars} cars" : "waiting for a car");
            SetRow(1, "B  Walls moved", Settings.WallsEnabled, master,
                Stats.WallPairs > 0 ? $"{Stats.WallPairs} walls, avg {Stats.AvgOverCurb:F1} m past curb (max {Settings.AllowedOverCurb.Value:F1}, {Stats.CappedShare:P0} limited by buildings)" + (Stats.UnpairedWalls > 0 ? $" ({Stats.UnpairedWalls} unpaired)" : "") : "no road tiles loaded yet");
            SetRow(2, "D  Curb ramp", Settings.RampEnabled, master,
                Stats.Ramps > 0 ? $"{Stats.Ramps} ramps, " + (Settings.RampHeight.Value > 0 ? $"{Settings.RampHeight.Value:F2} m high" : "height = sidewalk (auto)") : "no road tiles loaded yet");
            SetRow(3, "C  Soft wall scrapes", Settings.ScrapeEnabled, master,
                $"soft {Stats.SoftScrapes}   hard {Stats.HardWallHits}   (<= {Settings.ShallowAngle.Value:0} deg)");
            SetRow(4, "E  Traffic side-swipes", Settings.TrafficEnabled, master,
                $"swipes {Stats.SideSwipes}   hits {Stats.TrafficHits}   (<= {Settings.SideSwipeAngle.Value:0} deg)");

            _trafficLine = $"   traffic boxes x{Settings.TrafficWidthScale.Value:F2} on {Stats.TrafficResized}/{Stats.TrafficCars} cars, near-miss {(Stats.NearMissRange >= 0 ? Stats.NearMissRange.ToString("F2") + " m" : "-")}";
            _lanesLine = "   lanes: " + (string.IsNullOrEmpty(Stats.Lanes) ? "-" : Stats.Lanes);
            _footer = flashing ? ">> " + Stats.LastEvent : "F10 on/off   F9 reload cfg   F8 panel size";
        }

        private static void SetRow(int i, string name, ConfigEntry<bool> entry, bool master, string info)
        {
            RowLabel[i] = $"{name}: {(entry.Value ? (master ? "ON" : "on*") : "OFF")}";
            RowInfo[i] = info;
        }

        /// <summary>Forget the cached text so the next draw rebuilds it (after a click changed a setting).</summary>
        private static void Invalidate() => _textAt = -100f;

        /// <summary>Called from CurbFeelCore.OnGUI. Returns true if a feature was clicked (caller reapplies).</summary>
        public static bool Draw()
        {
            var mode = Current;
            if (mode == Mode.Hidden) return false;

            // Only Repaint draws; GUI.Button reacts to MouseDown/MouseUp. Layout, key, scroll etc. need nothing from us.
            var ev = Event.current;
            if (ev == null) return false;
            var type = ev.type;
            if (type != EventType.Repaint && type != EventType.MouseDown && type != EventType.MouseUp) return false;

            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f) * Settings.OverlayScale.Value;
            int font = Mathf.RoundToInt(15 * s);
            var skin = GUI.skin;
            skin.label.fontSize = font;
            skin.button.fontSize = font;
            skin.box.fontSize = font;
            var prevColor = GUI.color;

            bool master = Settings.Enabled.Value;
            bool flashing = Time.unscaledTime - Stats.LastEventTime < 1.5f;
            RefreshText(master, flashing);
            float rowH = 24 * s, w = 430 * s, pad = 8 * s;
            float x = Screen.width - w - 12 * s, y = 12 * s;

            if (mode == Mode.Compact)
            {
                GUI.Box(new Rect(x, y, w, rowH + pad), "");
                GUI.color = master ? On : Off;
                GUI.Label(new Rect(x + pad, y + pad * 0.5f, w - 2 * pad, rowH), _compactLine);
                if (flashing) { GUI.color = Flash; GUI.Label(new Rect(x + pad, y + rowH + pad, w, rowH), Stats.LastEvent); }
                GUI.color = prevColor;
                return false;
            }

            int rows = 10;
            GUI.Box(new Rect(x, y, w, rows * rowH + 2 * pad), "");
            float cy = y + pad;
            bool clicked = false;

            // title / master
            GUI.color = master ? On : Off;
            if (GUI.Button(new Rect(x + pad, cy, w - 2 * pad, rowH), _title))
            {
                Settings.Enabled.Value = !master; clicked = true;
            }
            cy += rowH + 2 * s;

            clicked |= Row(ref cy, x, w, rowH, pad, 0, Settings.HullEnabled, master);
            clicked |= Row(ref cy, x, w, rowH, pad, 1, Settings.WallsEnabled, master);
            clicked |= Row(ref cy, x, w, rowH, pad, 2, Settings.RampEnabled, master);
            clicked |= Row(ref cy, x, w, rowH, pad, 3, Settings.ScrapeEnabled, master);
            clicked |= Row(ref cy, x, w, rowH, pad, 4, Settings.TrafficEnabled, master);

            GUI.color = Dim;
            GUI.Label(new Rect(x + pad, cy, w - 2 * pad, rowH), _trafficLine);
            cy += rowH;
            GUI.Label(new Rect(x + pad, cy, w - 2 * pad, rowH), _lanesLine);
            cy += rowH;

            GUI.color = flashing ? Flash : Dim;
            GUI.Label(new Rect(x + pad, cy, w - 2 * pad, rowH), _footer);

            GUI.color = prevColor;
            if (clicked) Invalidate();
            return clicked;
        }

        private static bool Row(ref float cy, float x, float w, float rowH, float pad, int i, ConfigEntry<bool> entry, bool master)
        {
            bool on = entry.Value && master;
            float bw = 210 * (rowH / 24f);
            GUI.color = on ? On : entry.Value ? Dim : Off;
            bool clicked = GUI.Button(new Rect(x + pad, cy, bw, rowH), RowLabel[i]);
            if (clicked) entry.Value = !entry.Value;
            GUI.color = Dim;
            GUI.Label(new Rect(x + pad + bw + 6, cy, w - bw - 2 * pad - 6, rowH), RowInfo[i]);
            cy += rowH + 2;
            return clicked;
        }
    }
}
