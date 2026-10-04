using System;
using UnityEngine;

namespace RogueShared
{
    /// <summary>
    /// Shared world-space effect helpers (linked into plugins as source, like Perf.cs): a transparent, vertex-coloured,
    /// unlit material made from the game's own URP "Particles/Unlit" shader, and small procedural textures.
    ///
    /// Why that shader: the build ships ~90 materials of "Universal Render Pipeline/Particles/Unlit" with exactly the
    /// keyword set {_SURFACE_TYPE_TRANSPARENT} and alpha blending (SrcBlend 5 / DstBlend 10), so that shader variant is
    /// compiled in. Its default colour mode multiplies the texture by the vertex colour, so one material can draw a
    /// ribbon that changes colour along its length. Shader.Find returns null if the shader isn't loaded; callers then
    /// fall back to "Universal Render Pipeline/Unlit" (opaque look, still visible) or to their IMGUI drawing.
    /// Every engine member used here is present in the game's dump (not stripped): Shader.Find, new Material(Shader),
    /// Material.SetFloat/SetColor/SetTexture(string), EnableKeyword, SetOverrideTag, renderQueue, Texture2D.SetPixels32/Apply.
    /// </summary>
    internal static class Fx
    {
        internal const string ParticlesUnlit = "Universal Render Pipeline/Particles/Unlit";
        internal const string Unlit = "Universal Render Pipeline/Unlit";

        /// <summary>
        /// A new alpha-blended material (caller owns it and destroys it). additive = glow look (SrcAlpha, One).
        /// Null if no usable shader is loaded. transparent tells the caller whether it really blends.
        /// </summary>
        internal static Material Transparent(string name, Texture tex, bool additive, out bool transparent)
        {
            transparent = false;
            var sh = Shader.Find(ParticlesUnlit);
            if (sh != null)
            {
                var m = new Material(sh) { name = name };
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", additive ? 2f : 0f);
                m.SetFloat("_SrcBlend", 5f);                  // SrcAlpha
                m.SetFloat("_DstBlend", additive ? 1f : 10f); // One / OneMinusSrcAlpha
                m.SetFloat("_ZWrite", 0f);
                m.SetFloat("_Cull", 0f);                      // both sides
                m.SetFloat("_ColorMode", 0f);                 // multiply by vertex colour
                m.SetColor("_BaseColor", Color.white);
                if (tex != null) m.SetTexture("_BaseMap", tex);
                m.renderQueue = 3000;
                transparent = true;
                return m;
            }
            sh = Shader.Find(Unlit);
            if (sh == null) return null;
            var o = new Material(sh) { name = name };
            o.SetColor("_BaseColor", Color.white);
            if (tex != null) o.SetTexture("_BaseMap", tex);
            return o;
        }

        /// <summary>
        /// Chevron strip for a ground ribbon (U across, V along; repeat V). White arrows pointing +V with soft edges, a
        /// faint body between them and a brighter rim along both sides, so the line reads at any distance.
        /// </summary>
        internal static Texture2D ChevronTexture(int w = 64, int h = 128)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w * 2f - 1f;           // -1..1 across
                    float v = (y + 0.5f) / h;                      // 0..1 along
                    float edge = 1f - Mathf.Clamp01((Mathf.Abs(u) - 0.86f) / 0.14f);   // soft outer edge
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(Mathf.Abs(u) - 0.78f) / 0.08f) * 0.55f;
                    // chevron: a V whose tip points up; distance from the band v = 0.35 + 0.3 * (1 - |u|)
                    float tip = 0.30f + 0.32f * (1f - Mathf.Abs(u));
                    float d = Mathf.Abs(v - tip);
                    float chev = Mathf.Clamp01(1f - (d - 0.07f) / 0.05f) * (Mathf.Abs(u) < 0.74f ? 1f : 0f);
                    float body = 0.38f;
                    float a = Mathf.Max(body, Mathf.Max(chev, rim)) * edge;
                    byte c = (byte)(chev > 0.5f ? 255 : 235);
                    px[y * w + x] = new Color32(c, c, c, (byte)(Mathf.Clamp01(a) * 255f));
                }
            return Make(px, w, h, TextureWrapMode.Repeat, "RogueShared.Chevron");
        }

        /// <summary>Round soft glow (white, alpha falls off from the centre), for halos and light flares.</summary>
        internal static Texture2D GlowTexture(int size = 64)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - r);
                    a = a * a * (3f - 2f * a);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            return Make(px, size, size, TextureWrapMode.Clamp, "RogueShared.Glow");
        }

        internal static Texture2D Make(Color32[] px, int w, int h, TextureWrapMode wrap, string name)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name, wrapMode = wrap, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }

        /// <summary>Destroys a Unity object we created. Never throws.</summary>
        internal static void Kill(UnityEngine.Object o)
        {
            try { if (o != null) UnityEngine.Object.Destroy(o); } catch { /* the scene takes it */ }
        }
    }
}
