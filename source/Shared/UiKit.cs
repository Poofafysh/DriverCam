using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RogueShared
{
    /// <summary>
    /// Shared HUD toolkit (linked into plugins as source): a screen-space uGUI canvas with procedurally drawn sprites
    /// (rounded panels with a vertical gradient, a lit top edge and a dark rim; soft drop shadows; glossy bar fills; glows)
    /// and TextMeshPro labels in the game's own HUD font with a drop shadow. Replaces the flat IMGUI boxes.
    ///
    /// Everything is created by us, owned by one root GameObject (DontDestroyOnLoad) and destroyed by Destroy(). Sprites
    /// and textures are made once per plugin and kept in a list (so the GC never collects their wrappers) until Destroy.
    /// Members used are present in the game's dump: Canvas.renderMode/sortingOrder, CanvasScaler.uiScaleMode/
    /// referenceResolution/matchWidthOrHeight, RectTransform anchors/pivot/sizeDelta/anchoredPosition, Image.sprite/type/
    /// fillMethod/fillAmount/fillOrigin, Graphic.color/raycastTarget, TMP_Text font/fontSize/alignment/color/fontStyle/
    /// textWrappingMode/characterSpacing/overflowMode/text, CanvasGroup.alpha, Sprite.Create(..., border, ...),
    /// Resources.FindObjectsOfTypeAll.
    /// </summary>
    internal sealed class UiKit
    {
        internal GameObject Root { get; private set; }
        internal RectTransform Canvas { get; private set; }
        internal TMP_FontAsset Font { get; private set; }

        internal Sprite Panel, Shadow, Gloss, Glow, Pill;
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();

        /// <summary>The game's HUD font, picked once from the loaded TMP fonts (null = TMP's default).</summary>
        private static readonly string[] FontPreference = { "conthrax-sb SDF", "conthrax", "Bebas", "ethnocentric", "LiberationSans" };

        internal static UiKit Create(string name, int sortingOrder)
        {
            var kit = new UiKit();
            try
            {
                kit.MakeSprites();
                kit.Font = FindFont();
                var go = new GameObject(name);
                kit.Root = go;
                UnityEngine.Object.DontDestroyOnLoad(go);
                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = sortingOrder;
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 1f;   // scale with height, like the game's HUD
                kit.Canvas = go.GetComponent<RectTransform>();
                return kit;
            }
            catch
            {
                kit.Destroy();
                throw;
            }
        }

        internal void Destroy()
        {
            Fx.Kill(Root);
            Root = null; Canvas = null;
            foreach (var o in _owned) Fx.Kill(o);
            _owned.Clear();
            Panel = Shadow = Gloss = Glow = Pill = null;
        }

        private static TMP_FontAsset FindFont()
        {
            try
            {
                // the Type overload (the generic one would need an IL2CPP generic instantiation the game may not have)
                var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_FontAsset>());
                if (all == null || all.Length == 0) return null;
                foreach (var want in FontPreference)
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i];
                        if (o == null || o.name == null || !o.name.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                        var f = o.TryCast<TMP_FontAsset>();
                        if (f != null) return f;
                    }
                var first = all[0];
                return first == null ? null : first.TryCast<TMP_FontAsset>();
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ sprites

        private void MakeSprites()
        {
            Panel = Sliced(RoundedPanel(64, 12), 16);
            Shadow = Sliced(SoftShadow(64, 22), 28);
            Gloss = Sliced(GlossBar(16, 32), 6);
            Pill = Sliced(RoundedPanel(32, 15, flat: true), 15);
            var glowTex = Fx.GlowTexture(64);
            _owned.Add(glowTex);
            Glow = Sprite.Create(glowTex, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f), 100f);
            _owned.Add(Glow);
        }

        private Sprite Sliced(Texture2D tex, int border)
        {
            _owned.Add(tex);
            var s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0u,
                                  SpriteMeshType.FullRect, new Vector4(border, border, border, border), false);
            _owned.Add(s);
            return s;
        }

        /// <summary>Rounded rectangle: vertical gradient (lighter top), a 1-px lit top edge, a darker rim. White-ish so Image.color tints it.</summary>
        private static Texture2D RoundedPanel(int size, float radius, bool flat = false)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = RoundedDistance(x + 0.5f, y + 0.5f, size, size, radius);   // < 0 inside
                    float a = Mathf.Clamp01(0.5f - d);
                    if (a <= 0f) { px[y * size + x] = new Color32(255, 255, 255, 0); continue; }
                    float t = (y + 0.5f) / size;                       // 0 bottom .. 1 top
                    float shade = flat ? 1f : Mathf.Lerp(0.72f, 1f, t);
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(d + 1.2f) / 1.2f);   // ~2 px inner rim
                    float lit = flat ? 0f : Mathf.Clamp01(1f - Mathf.Abs(y - (size - 2.5f)) / 1.2f) * 0.6f;
                    float v = Mathf.Clamp01(shade - rim * 0.35f + lit);
                    byte c = (byte)(v * 255f);
                    px[y * size + x] = new Color32(c, c, c, (byte)(a * 255f));
                }
            return Fx.Make(px, size, size, TextureWrapMode.Clamp, "RogueShared.Panel");
        }

        /// <summary>Black, blurred rounded rectangle for drop shadows (alpha only).</summary>
        private static Texture2D SoftShadow(int size, float blur)
        {
            var px = new Color32[size * size];
            float inset = blur;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = RoundedDistance(x + 0.5f, y + 0.5f, size, size, inset) ;   // distance to an inset box
                    float a = Mathf.Clamp01(1f - (d + inset) / inset);
                    a = a * a;
                    px[y * size + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                }
            return Fx.Make(px, size, size, TextureWrapMode.Clamp, "RogueShared.Shadow");
        }

        /// <summary>Glossy bar fill: bright band in the upper third, darker bottom, rounded ends via 9-slice.</summary>
        private static Texture2D GlossBar(int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundedDistance(x + 0.5f, y + 0.5f, w, h, 5f);
                    float a = Mathf.Clamp01(0.5f - d);
                    float t = (y + 0.5f) / h;
                    float v = Mathf.Lerp(0.62f, 0.95f, t);
                    if (t > 0.58f && t < 0.82f) v = Mathf.Min(1f, v + 0.18f);   // highlight band
                    byte c = (byte)(v * 255f);
                    px[y * w + x] = new Color32(c, c, c, (byte)(a * 255f));
                }
            return Fx.Make(px, w, h, TextureWrapMode.Clamp, "RogueShared.Gloss");
        }

        /// <summary>Signed distance to a rounded rectangle filling (0,0)-(w,h) with corner radius r.</summary>
        private static float RoundedDistance(float x, float y, float w, float h, float r)
        {
            float qx = Mathf.Abs(x - w * 0.5f) - (w * 0.5f - r);
            float qy = Mathf.Abs(y - h * 0.5f) - (h * 0.5f - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        // ------------------------------------------------------------------ builders

        /// <summary>A plain RectTransform container.</summary>
        internal RectTransform Rect(Transform parent, string name, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            Place(rt, anchor, pivot, pos, size);
            return rt;
        }

        internal static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = pivot;
            rt.sizeDelta = size; rt.anchoredPosition = pos;
        }

        /// <summary>Stretches rt over its parent with the given inset (left, bottom, right, top).</summary>
        internal static void Fill(RectTransform rt, float l = 0f, float b = 0f, float r = 0f, float t = 0f)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
        }

        internal Image Image(Transform parent, string name, Sprite sprite, Color color, bool sliced = true)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>().SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sliced ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>A panel with a soft drop shadow under it (shadow offset down-right). Returns the panel image.</summary>
        internal Image ShadowedPanel(Transform parent, string name, Color color, float shadowAlpha = 0.6f)
        {
            var holder = new GameObject(name);
            var hrt = holder.AddComponent<RectTransform>();
            hrt.SetParent(parent, false);
            Fill(hrt);
            var sh = Image(hrt, "Shadow", Shadow, new Color(0f, 0f, 0f, shadowAlpha));
            Fill(sh.rectTransform, -14f, -20f, -14f, -8f);
            sh.rectTransform.anchoredPosition = new Vector2(4f, -6f);
            var p = Image(hrt, "Panel", Panel, color);
            Fill(p.rectTransform);
            return p;
        }

        internal Label Text(Transform parent, string name, float size, Color color, TextAlignmentOptions align, bool bold = true, bool shadow = true)
        {
            var holder = new GameObject(name);
            var hrt = holder.AddComponent<RectTransform>();
            hrt.SetParent(parent, false);
            Fill(hrt);
            TextMeshProUGUI back = null;
            if (shadow)
            {
                back = MakeText(hrt, "Shadow", size, new Color(0f, 0f, 0f, 0.7f), align, bold);
                Fill(back.rectTransform);
                back.rectTransform.anchoredPosition = new Vector2(2f, -2f);
            }
            var front = MakeText(hrt, "Text", size, color, align, bold);
            Fill(front.rectTransform);
            return new Label(hrt, front, back);
        }

        private TextMeshProUGUI MakeText(Transform parent, string name, float size, Color color, TextAlignmentOptions align, bool bold)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>().SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            t.text = "";
            return t;
        }

        /// <summary>A label and its drop shadow; text is only pushed to TMP when it changes (no mesh rebuild per frame).</summary>
        internal sealed class Label
        {
            internal readonly RectTransform Rect;
            private readonly TextMeshProUGUI _front, _back;
            private string _text = "";
            private Color _color;

            internal Label(RectTransform rect, TextMeshProUGUI front, TextMeshProUGUI back)
            {
                Rect = rect; _front = front; _back = back; _color = front.color;
            }

            internal void Set(string text)
            {
                text ??= "";
                if (text == _text) return;
                _text = text;
                _front.text = text;
                if (_back != null) _back.text = text;
            }

            internal void SetColor(Color c)
            {
                if (c == _color) return;
                _color = c;
                _front.color = c;
                if (_back != null) _back.color = new Color(0f, 0f, 0f, 0.7f * c.a);
            }

            internal void SetSize(float size)
            {
                if (Mathf.Abs(_front.fontSize - size) < 0.01f) return;
                _front.fontSize = size;
                if (_back != null) _back.fontSize = size;
            }
        }
    }
}
