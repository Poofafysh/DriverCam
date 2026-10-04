using System;
using System.Collections.Generic;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// The PURSUIT icons, drawn in code (no files to install): a siren dome on its base with three light rays, a flat
    /// white glyph like the game's own category icons; the HUD version has a soft red glow. Built once, owned here and
    /// destroyed when the plugin unloads.
    /// </summary>
    internal static class PursuitIcons
    {
        internal static Sprite Hud { get; private set; }    // ~64 x 50 with glow: HUD popups
        internal static Sprite Stat { get; private set; }   // ~48 x 34: results and Victory rows

        private static readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();

        internal static void Load()
        {
            Destroy();
            Hud = Draw(64, 50, glow: true);
            Stat = Draw(48, 34, glow: false);
        }

        internal static void Destroy()
        {
            foreach (var o in _owned) if (o != null) UnityEngine.Object.Destroy(o);
            _owned.Clear();
            Hud = null; Stat = null;
        }

        private static Sprite Draw(int w, int h, bool glow)
        {
            float pad = glow ? 4f : 1f;
            float s = h - 2f * pad;                  // glyph box: s x s, centred
            float ox = (w - s) * 0.5f, oy = pad;
            float X(float u) => ox + u * s;          // local functions are fine here: not an injected MonoBehaviour
            float Y(float v) => oy + v * s;

            // base bar, dome (half disc) and three rays, in glyph units (y up)
            float bx0 = X(0.16f), bx1 = X(0.84f), by0 = Y(0.04f), by1 = Y(0.20f);
            float cx = X(0.5f), cy = Y(0.20f), r = 0.27f * s;
            var rays = new[]
            {
                (X(0.50f), Y(0.62f), X(0.50f), Y(0.92f)),
                (X(0.20f), Y(0.50f), X(0.04f), Y(0.70f)),
                (X(0.80f), Y(0.50f), X(0.96f), Y(0.70f)),
            };
            float ray = Math.Max(1.2f, s / 18f), glowWidth = 3f;

            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    // distance outside each shape (<= 0 inside)
                    float dBase = Math.Max(Math.Max(bx0 - fx, fx - bx1), Math.Max(by0 - fy, fy - by1));
                    float dDome = Math.Max((float)Math.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy)) - r, cy - fy);
                    float dRay = float.MaxValue;
                    foreach (var (ax, ay, bx, by) in rays) dRay = Math.Min(dRay, Seg(fx, fy, ax, ay, bx, by) - ray);
                    float d = Math.Min(Math.Min(dBase, dDome), dRay);
                    float white = Math.Max(0f, Math.Min(1f, 0.5f - d));
                    float g = glow && d > 0f ? Math.Max(0f, 1f - d / glowWidth) * 0.7f : 0f;
                    // white over red glow
                    float a = white + g * (1 - white);
                    float rr = a <= 0 ? 0 : (white * 255 + g * (1 - white) * 255) / a;
                    float gg = a <= 0 ? 0 : (white * 255 + g * (1 - white) * 60) / a;
                    float bb = a <= 0 ? 0 : (white * 255 + g * (1 - white) * 50) / a;
                    px[y * w + x] = new Color32((byte)rr, (byte)gg, (byte)bb, (byte)(Math.Min(1f, a) * 255));
                }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            tex.filterMode = FilterMode.Bilinear;
            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            sprite.name = glow ? "Police_Pursuit_HudIcon" : "Police_Pursuit_StatIcon";
            _owned.Add(tex); _owned.Add(sprite);
            return sprite;
        }

        private static float Seg(float x, float y, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay, wx = x - ax, wy = y - ay;
            float t = Math.Max(0f, Math.Min(1f, (wx * vx + wy * vy) / (vx * vx + vy * vy + 1e-6f)));
            float dx = wx - t * vx, dy = wy - t * vy;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
