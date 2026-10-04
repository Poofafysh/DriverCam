using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RogueHub
{
    /// <summary>Colours of the game's own menus (navy panels, cyan edges, orange / pink accents).</summary>
    internal static class Pal
    {
        internal static readonly Color Panel = new Color(0.05f, 0.078f, 0.204f, 0.95f);
        internal static readonly Color Head = new Color(0.17f, 0.2f, 0.34f, 1f);
        internal static readonly Color Card = new Color(0.094f, 0.133f, 0.314f, 0.9f);
        internal static readonly Color Cyan = new Color(0.16f, 0.78f, 1f, 1f);
        internal static readonly Color Cyan2 = new Color(0.055f, 0.5f, 0.72f, 1f);
        internal static readonly Color Edge = new Color(0.17f, 0.29f, 0.48f, 1f);
        internal static readonly Color Text = new Color(0.93f, 0.96f, 1f, 1f);
        internal static readonly Color Dim = new Color(0.56f, 0.64f, 0.79f, 1f);
        internal static readonly Color Orange = new Color(1f, 0.61f, 0.13f, 1f);
        internal static readonly Color Pink = new Color(1f, 0.18f, 0.66f, 1f);
        internal static readonly Color Green = new Color(0.23f, 0.89f, 0.49f, 1f);
        internal static readonly Color Red = new Color(1f, 0.3f, 0.42f, 1f);
        internal static readonly Color Gold = new Color(1f, 0.8f, 0.24f, 1f);
        internal static readonly Color RowSel = new Color(0.11f, 0.44f, 0.7f, 0.95f);
        internal static readonly Color RowLine = new Color(0.17f, 0.29f, 0.48f, 0.55f);
        internal static readonly Color Track = new Color(0.15f, 0.2f, 0.37f, 1f);
        internal static readonly Color Group = new Color(0.16f, 0.78f, 1f, 0.07f);
        internal static readonly Color Ink = new Color(0.04f, 0.06f, 0.19f, 1f);

        internal static Color A(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }

    /// <summary>A TMP label that only touches TMP when its text or colour changes.</summary>
    internal sealed class Txt
    {
        internal readonly TextMeshProUGUI T;
        private string _text = "\u0001";
        private Color _color;

        internal Txt(TextMeshProUGUI t) { T = t; _color = t.color; }

        internal void Set(string s)
        {
            s ??= "";
            if (s == _text) return;
            _text = s;
            T.text = s;
        }

        internal void Color(Color c)
        {
            if (c == _color) return;
            _color = c;
            T.color = c;
        }

        internal void Show(bool on)
        {
            var go = T.gameObject;
            if (go.activeSelf != on) go.SetActive(on);
        }
    }

    /// <summary>
    /// The hub's own small uGUI toolkit: a ScreenSpaceOverlay canvas scaled like the game's (1920x1080, matched to
    /// height), a 1920x1080 stage centred on it, procedural sprites (cut-corner panels and edges like the game's menus,
    /// discs, a slanted slider thumb) and TMP text in the game's own fonts. Positions are given from the parent's
    /// top-left corner in reference pixels. Everything the toolkit makes is kept referenced (sprites and textures in a
    /// static list for the session, objects under one root destroyed by Destroy()).
    /// </summary>
    internal sealed class Ui
    {
        internal GameObject Root;
        internal RectTransform Stage;
        internal Canvas Canvas;

        internal static TMP_FontAsset HeadFont, BodyFont;
        internal static Sprite Solid, Chamf, ChamfEdge, Pill, Disc, Thumb;
        private static readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
        private static bool _assets;

        internal static Ui Create(string name, int sortingOrder, bool blockClicks)
        {
            MakeAssets();
            var ui = new Ui();
            var go = new GameObject(name);
            go.SetActive(false);   // hidden until the view is complete and shown: a half-built canvas never blocks the screen
            UnityEngine.Object.DontDestroyOnLoad(go);
            ui.Root = go;
            ui.Canvas = go.AddComponent<Canvas>();
            ui.Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            ui.Canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            // a raycaster makes our full-screen backdrop catch the mouse, so clicks never reach the game's menu under it
            if (blockClicks) go.AddComponent<GraphicRaycaster>();
            var rt = go.GetComponent<RectTransform>();
            ui.Stage = new GameObject("Stage").AddComponent<RectTransform>();
            ui.Stage.SetParent(rt, false);
            ui.Stage.anchorMin = ui.Stage.anchorMax = new Vector2(0.5f, 0.5f);
            ui.Stage.pivot = new Vector2(0.5f, 0.5f);
            ui.Stage.sizeDelta = new Vector2(1920f, 1080f);
            ui.Stage.anchoredPosition = Vector2.zero;
            return ui;
        }

        internal void Destroy()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root);
            Root = null; Stage = null; Canvas = null;
        }

        internal void SetVisible(bool on)
        {
            if (Root != null && Root.activeSelf != on) Root.SetActive(on);
        }

        // ------------------------------------------------------------------ builders (top-left origin, reference px)

        internal static RectTransform Box(Transform parent, string name, float x, float y, float w, float h)
        {
            var rt = new GameObject(name).AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            Place(rt, x, y, w, h);
            return rt;
        }

        internal static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        internal static Image Img(Transform parent, string name, Sprite sp, Color c, float x, float y, float w, float h, bool sliced = true)
        {
            var rt = Box(parent, name, x, y, w, h);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sp;
            img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>A cut-corner panel: fill plus a thin edge.</summary>
        internal static RectTransform Panel(Transform parent, string name, float x, float y, float w, float h, Color fill, Color edge)
        {
            var bg = Img(parent, name, Chamf, fill, x, y, w, h);
            var e = Img(bg.transform, "Edge", ChamfEdge, edge, 0, 0, w, h);
            Stretch(e.rectTransform);
            return bg.rectTransform;
        }

        internal static Txt Text(Transform parent, string name, float x, float y, float w, float h, float size, Color c,
                                 TextAlignmentOptions align, bool head = true, bool wrap = false)
        {
            var rt = Box(parent, name, x, y, w, h);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            var font = head ? HeadFont : BodyFont;
            if (font == null) font = HeadFont;
            if (font != null) t.font = font;
            t.fontSize = size;
            t.color = c;
            t.alignment = align;
            t.fontStyle = head ? FontStyles.Normal : FontStyles.Bold;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = wrap ? TextOverflowModes.Truncate : TextOverflowModes.Ellipsis;
            t.richText = true;
            t.raycastTarget = false;
            t.text = "";
            return new Txt(t);
        }

        internal static void Show(Component c, bool on)
        {
            if (c == null) return;
            var go = c.gameObject;
            if (go.activeSelf != on) go.SetActive(on);
        }

        // ------------------------------------------------------------------ fonts and sprites (made once per session)

        private static void MakeAssets()
        {
            if (HeadFont == null) PickFonts();   // again later if the game's fonts weren't loaded yet
            if (_assets) return;
            _assets = true;
            Solid = Sprite9(MakeTex(4, 4, (x, y) => 1f), 1);
            Chamf = Sprite9(ChamferTex(64, 14, false), 18);
            ChamfEdge = Sprite9(ChamferTex(64, 14, true), 18);
            Pill = Sprite9(RoundTex(32, 15f), 15);
            Disc = Sprite9(RoundTex(32, 16f), 0, sliced: false);
            Thumb = Sprite9(MakeTex(16, 28, (x, y) =>
            {
                float skew = (y / 27f - 0.5f) * 6f;               // slanted like the game's slider handles
                return x >= 2 + skew && x <= 13 + skew ? 1f : 0f;
            }), 0, sliced: false);
        }

        /// <summary>The game's HUD font for headings (exact name first: the game ships nine conthrax variants) and a plainer one for body text.</summary>
        private static void PickFonts()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_FontAsset>());
                var fonts = new List<TMP_FontAsset>();
                var names = new List<string>();
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    var o = all[i];
                    var f = o == null ? null : o.TryCast<TMP_FontAsset>();
                    if (f == null) continue;
                    fonts.Add(f);
                    names.Add(f.name);
                }
                Plugin.Log.LogInfo($"[RogueHub] TMP fonts: {string.Join(", ", names)}");
                HeadFont = Find(fonts, true, "conthrax-sb SDF");
                if (HeadFont == null) HeadFont = Find(fonts, false, "conthrax-sb SDF", "conthrax", "Bebas", "ethnocentric");
                BodyFont = Find(fonts, false, "Exo", "Rajdhani", "Roboto", "Montserrat", "Inter", "Oxanium", "LiberationSans");
                if (HeadFont == null && fonts.Count > 0) HeadFont = fonts[0];
                if (BodyFont == null) BodyFont = HeadFont;
                Plugin.Log.LogInfo($"[RogueHub] fonts: headings {(HeadFont == null ? "TMP default" : HeadFont.name)}, text {(BodyFont == null ? "TMP default" : BodyFont.name)}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] font lookup failed, TMP default used: {e.Message}"); }
        }

        private static TMP_FontAsset Find(List<TMP_FontAsset> fonts, bool exact, params string[] names)
        {
            foreach (var n in names)
                foreach (var f in fonts)
                {
                    string fn = f.name ?? "";
                    if (exact ? string.Equals(fn, n, StringComparison.OrdinalIgnoreCase) : fn.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return f;
                }
            return null;
        }

        private static Sprite Sprite9(Texture2D tex, int border, bool sliced = true)
        {
            Owned.Add(tex);
            var s = sliced
                ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect,
                                new Vector4(border, border, border, border), false)
                : Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            s.name = "RogueHub." + tex.name;
            Owned.Add(s);
            return s;
        }

        private static Texture2D MakeTex(int w, int h, Func<int, int, float> alpha, string name = "Tex")
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(x, y)) * 255f));
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "RogueHub." + name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Square with the top-left and bottom-right corners cut (the game's panel shape); edge = only a 2 px outline.</summary>
        private static Texture2D ChamferTex(int size, int cut, bool edgeOnly)
        {
            return MakeTex(size, size, (x, yUp) =>
            {
                float px = x + 0.5f, py = size - (yUp + 0.5f);                  // y from the top
                // signed distance (inside < 0) to the 6-sided shape, approximated per edge
                float dLeft = px, dRight = size - px, dTop = py, dBottom = size - py;
                float dCutTL = (px + py - cut) / 1.41421f;                     // top-left diagonal
                float dCutBR = ((size - px) + (size - py) - cut) / 1.41421f;   // bottom-right diagonal
                float inside = Mathf.Min(Mathf.Min(dLeft, dRight), Mathf.Min(Mathf.Min(dTop, dBottom), Mathf.Min(dCutTL, dCutBR)));
                if (inside < 0f) return 0f;
                float aa = Mathf.Clamp01(inside + 0.5f);
                if (!edgeOnly) return aa;
                return inside <= 2.2f ? aa : 0f;
            }, edgeOnly ? "ChamfEdge" : "Chamf");
        }

        private static Texture2D RoundTex(int size, float radius)
        {
            return MakeTex(size, size, (x, y) =>
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                return Mathf.Clamp01(radius - d + 0.5f);
            }, "Round" + (int)radius);
        }
    }
}
