using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RacingLine
{
    /// <summary>
    /// The Racing Line icons (design doc: "Icons we need"). PNGs in plugins/RacingLine/ (names in config) win; otherwise
    /// a placeholder "apex arc" is drawn in code in the game's style: flat white glyph, soft blue glow on the HUD version.
    /// We own every texture and sprite made here and destroy them on unload (Safety rule 9).
    /// </summary>
    internal static class Icons
    {
        internal static Sprite Hud { get; private set; }    // ~56 x 44 + glow, HUD popups
        internal static Sprite Stat { get; private set; }   // ~44 x 32, no glow, results row
        internal static string Source { get; private set; } = "none";

        private static readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();

        internal static void Load(string folder)
        {
            Destroy();
            Hud = FromPng(Path.Combine(folder, Plugin.HudIconFile.Value)) ?? Draw(64, 50, glow: true);
            Stat = FromPng(Path.Combine(folder, Plugin.StatIconFile.Value)) ?? Draw(48, 34, glow: false);
            Source = File.Exists(Path.Combine(folder, Plugin.HudIconFile.Value)) ? "PNG files" : "built-in placeholder";
        }

        internal static void Destroy()
        {
            foreach (var o in _owned) if (o != null) UnityEngine.Object.Destroy(o);
            _owned.Clear();
            Hud = null; Stat = null;
        }

        private static Sprite FromPng(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(tex); Plugin.Log.LogWarning($"[RacingLine] icon {path} is not a valid PNG, using the placeholder"); return null; }
                return MakeSprite(tex);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RacingLine] icon {path} failed to load ({e.Message}), using the placeholder"); return null; }
        }

        private static Sprite MakeSprite(Texture2D tex)
        {
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            tex.filterMode = FilterMode.Bilinear;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _owned.Add(tex); _owned.Add(sprite);
            return sprite;
        }

        /// <summary>
        /// Placeholder glyph, seen from above: a right-hand corner. A thin arc is the inside kerb; the bold curve is the
        /// racing line sweeping outside, apex (dot), outside.
        /// </summary>
        private static Sprite Draw(int w, int h, bool glow)
        {
            float pad = glow ? 4f : 1f;
            float sx = w - 2 * pad, sy = h - 2 * pad;
            // racing line: quadratic Bezier, y up
            var line = new List<(float x, float y)>();
            (float x, float y) p0 = (0.06f, 0.10f), p1 = (0.92f, 0.08f), p2 = (0.86f, 0.96f);
            for (int i = 0; i <= 48; i++)
            {
                float t = i / 48f, u = 1 - t;
                line.Add((pad + sx * (u * u * p0.x + 2 * u * t * p1.x + t * t * p2.x), pad + sy * (u * u * p0.y + 2 * u * t * p1.y + t * t * p2.y)));
            }
            // inside kerb: quarter circle around the bottom-right corner
            var kerb = new List<(float x, float y)>();
            for (int i = 0; i <= 24; i++)
            {
                double a = Math.PI / 2 + (Math.PI / 2) * i / 24.0;   // 90..180 degrees
                kerb.Add((pad + sx * (1f + 0.42f * (float)Math.Cos(a)), pad + sy * (0f + 0.55f * (float)Math.Sin(a))));
            }
            var apex = line[26];

            float stroke = Math.Max(1.6f, h / 16f), kerbStroke = Math.Max(0.8f, h / 40f), dot = stroke * 1.25f, glowWidth = 3f;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float dl = Dist(line, fx, fy), dk = Dist(kerb, fx, fy);
                    float da = (float)Math.Sqrt((fx - apex.x) * (fx - apex.x) + (fy - apex.y) * (fy - apex.y));
                    float white = Math.Max(Cover(dl, stroke), Math.Max(Cover(dk, kerbStroke) * 0.8f, Cover(da, dot)));
                    float g = 0f;
                    if (glow)
                    {
                        float edge = Math.Min(dl - stroke, Math.Min(dk - kerbStroke, da - dot));
                        g = edge <= 0 ? 0f : Math.Max(0f, 1f - edge / glowWidth) * 0.7f;
                    }
                    // white over blue glow
                    float a = white + g * (1 - white);
                    float r = a <= 0 ? 0 : (white * 255 + g * (1 - white) * 40) / a;
                    float gr = a <= 0 ? 0 : (white * 255 + g * (1 - white) * 135) / a;
                    float b = a <= 0 ? 0 : (white * 255 + g * (1 - white) * 245) / a;
                    px[y * w + x] = new Color32((byte)r, (byte)gr, (byte)b, (byte)(Math.Min(1f, a) * 255));
                }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            return MakeSprite(tex);
        }

        private static float Cover(float d, float halfWidth) => Math.Max(0f, Math.Min(1f, halfWidth + 0.5f - d));

        private static float Dist(List<(float x, float y)> poly, float x, float y)
        {
            float best = float.MaxValue;
            for (int i = 0; i < poly.Count - 1; i++)
            {
                var a = poly[i]; var b = poly[i + 1];
                float vx = b.x - a.x, vy = b.y - a.y, wx = x - a.x, wy = y - a.y;
                float t = Math.Max(0f, Math.Min(1f, (wx * vx + wy * vy) / (vx * vx + vy * vy + 1e-6f)));
                float dx = wx - t * vx, dy = wy - t * vy;
                float d = dx * dx + dy * dy;
                if (d < best) best = d;
            }
            return (float)Math.Sqrt(best);
        }
    }
}
