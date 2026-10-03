using BepInEx.Configuration;
using UnityEngine;

namespace CurbFeel
{
    /// <summary>
    /// Top-right status panel. Full: every feature with live counters (rows are clickable when the cursor is visible).
    /// Compact: one line. Hidden. Cycle with OverlayKey (F8).
    /// </summary>
    internal static class Overlay
    {
        public enum Mode { Full, Compact, Hidden }

        private static readonly Color On = new(0.35f, 1f, 0.45f);
        private static readonly Color Off = new(1f, 0.4f, 0.4f);
        private static readonly Color Dim = new(0.75f, 0.75f, 0.75f);
        private static readonly Color Flash = new(1f, 0.85f, 0.2f);

        public static Mode Current
        {
            get
            {
                switch ((Settings.OverlayMode.Value ?? "").ToLowerInvariant())
                {
                    case "compact": return Mode.Compact;
                    case "hidden": return Mode.Hidden;
                    default: return Mode.Full;
                }
            }
        }

        public static void Cycle()
        {
            var next = Current == Mode.Full ? Mode.Compact : Current == Mode.Compact ? Mode.Hidden : Mode.Full;
            Settings.OverlayMode.Value = next.ToString();
        }

        /// <summary>Called from CurbFeelCore.OnGUI. Returns true if a feature was clicked (caller reapplies).</summary>
        public static bool Draw()
        {
            var mode = Current;
            if (mode == Mode.Hidden) return false;

            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f) * Settings.OverlayScale.Value;
            int font = Mathf.RoundToInt(15 * s);
            GUI.skin.label.fontSize = font;
            GUI.skin.button.fontSize = font;
            GUI.skin.box.fontSize = font;
            var prevColor = GUI.color;

            bool master = Settings.Enabled.Value;
            bool flashing = Time.unscaledTime - Stats.LastEventTime < 1.5f;
            float rowH = 24 * s, w = 430 * s, pad = 8 * s;
            float x = Screen.width - w - 12 * s, y = 12 * s;

            if (mode == Mode.Compact)
            {
                GUI.Box(new Rect(x, y, w, rowH + pad), "");
                GUI.color = master ? On : Off;
                string line = master
                    ? $"CurbFeel ON   walls {Stats.WallPairs}  ramps {Stats.Ramps}  swipes {Stats.SideSwipes}  scrapes {Stats.SoftScrapes}"
                    : "CurbFeel OFF (stock)   F10 to enable";
                GUI.Label(new Rect(x + pad, y + pad * 0.5f, w - 2 * pad, rowH), line);
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
            if (GUI.Button(new Rect(x + pad, cy, w - 2 * pad, rowH), master ? $"CurbFeel {Plugin.Version}:  ON" : $"CurbFeel {Plugin.Version}:  OFF (everything stock)"))
            {
                Settings.Enabled.Value = !master; clicked = true;
            }
            cy += rowH + 2 * s;

            clicked |= Row(ref cy, x, w, rowH, pad, "A  Car hull", Settings.HullEnabled, master,
                Stats.PlayerBodyHalfWidth > 0 ? $"body {Stats.PlayerBodyHalfWidth:F2} m, {Stats.HullCapsules} capsules / {Stats.HullCars} cars" : "waiting for a car");
            clicked |= Row(ref cy, x, w, rowH, pad, "B  Walls moved", Settings.WallsEnabled, master,
                Stats.WallPairs > 0 ? $"{Stats.WallPairs} walls, avg {Stats.AvgOverCurb:F1} m past curb (max {Settings.AllowedOverCurb.Value:F1}, {Stats.CappedShare:P0} limited by buildings)" + (Stats.UnpairedWalls > 0 ? $" ({Stats.UnpairedWalls} unpaired)" : "") : "no road tiles loaded yet");
            clicked |= Row(ref cy, x, w, rowH, pad, "D  Curb ramp", Settings.RampEnabled, master,
                Stats.Ramps > 0 ? $"{Stats.Ramps} ramps, " + (Settings.RampHeight.Value > 0 ? $"{Settings.RampHeight.Value:F2} m high" : "height = sidewalk (auto)") : "no road tiles loaded yet");
            clicked |= Row(ref cy, x, w, rowH, pad, "C  Soft wall scrapes", Settings.ScrapeEnabled, master,
                $"soft {Stats.SoftScrapes}   hard {Stats.HardWallHits}   (<= {Settings.ShallowAngle.Value:0} deg)");
            clicked |= Row(ref cy, x, w, rowH, pad, "E  Traffic side-swipes", Settings.TrafficEnabled, master,
                $"swipes {Stats.SideSwipes}   hits {Stats.TrafficHits}   (<= {Settings.SideSwipeAngle.Value:0} deg)");

            GUI.color = Dim;
            GUI.Label(new Rect(x + pad, cy, w - 2 * pad, rowH),
                $"   traffic boxes x{Settings.TrafficWidthScale.Value:F2} on {Stats.TrafficResized}/{Stats.TrafficCars} cars, near-miss {(Stats.NearMissRange >= 0 ? Stats.NearMissRange.ToString("F2") + " m" : "-")}");
            cy += rowH;
            GUI.Label(new Rect(x + pad, cy, w - 2 * pad, rowH), "   lanes: " + (string.IsNullOrEmpty(Stats.Lanes) ? "-" : Stats.Lanes));
            cy += rowH;

            GUI.color = flashing ? Flash : Dim;
            GUI.Label(new Rect(x + pad, cy, w - 2 * pad, rowH), flashing ? ">> " + Stats.LastEvent : "F10 on/off   F9 reload cfg   F8 panel size");

            GUI.color = prevColor;
            return clicked;
        }

        private static bool Row(ref float cy, float x, float w, float rowH, float pad, string name, ConfigEntry<bool> entry, bool master, string info)
        {
            bool on = entry.Value && master;
            float bw = 210 * (rowH / 24f);
            GUI.color = on ? On : entry.Value ? Dim : Off;
            bool clicked = GUI.Button(new Rect(x + pad, cy, bw, rowH), $"{name}: {(entry.Value ? (master ? "ON" : "on*") : "OFF")}");
            if (clicked) entry.Value = !entry.Value;
            GUI.color = Dim;
            GUI.Label(new Rect(x + pad + bw + 6, cy, w - bw - 2 * pad - 6, rowH), info);
            cy += rowH + 2;
            return clicked;
        }
    }
}
