using System.Collections.Generic;
using BepInEx.Configuration;
using Game.Runtime.UI;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace DriverCam;

/// <summary>
/// Moves and scales the health bar, speedometer and ability bar. Offsets are applied on top of wherever the game
/// puts each element (its own animations keep working): each frame the previous offset is taken back out first.
/// Cheap per frame: nothing is read or written while the layout is off and already restored, a value is only written
/// when it differs (a RectTransform write re-dirties the game's HUD canvas), the root canvas is looked up once per
/// element, and the vector maths reads struct fields (Unity's vector operators are slow interop calls).
/// </summary>
internal static class HudLayout
{
    sealed class Element
    {
        public string Name;
        public RectTransform Rect;
        public ConfigEntry<float> X, Y;
        public Vector2 AppliedOffset;
        public float AppliedScale = 1f;
        public RectTransform Root;    // the element's root canvas, found once (null until needed)
    }

    static readonly List<Element> _elements = new();
    static float _nextScan;

    static bool Alive(Element e) => e.Rect != null && !e.Rect.WasCollected;

    public static void Update(bool driverView)
    {
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 2f;
            Scan();
        }

        bool apply = Plugin.HudEnabled.Value && (driverView || !Plugin.HudOnlyInDriverView.Value);
        foreach (var e in _elements)
        {
            if (!Alive(e)) continue;
            // off and already back at the game's own layout: nothing to take out, nothing to write
            if (!apply && e.AppliedOffset.x == 0f && e.AppliedOffset.y == 0f && e.AppliedScale == 1f) continue;
            var rect = e.Rect;

            // take our previous adjustment back out to recover the game's own position/scale this frame
            var pos = rect.anchoredPosition;
            var sc = rect.localScale;
            float baseX = pos.x - e.AppliedOffset.x, baseY = pos.y - e.AppliedOffset.y;
            float inv = e.AppliedScale > 1e-4f ? 1f / e.AppliedScale : 1f;

            float offX = 0f, offY = 0f;
            float scale = 1f;
            if (apply)
            {
                // pull towards the middle of the screen, measured in the canvas' own units
                var root = RootOf(e);
                if (root != null)
                {
                    float localX = root.InverseTransformPoint(rect.position).x - e.AppliedOffset.x;
                    offX = -localX * FM.Clamp01(Plugin.HudPullToCenter.Value);
                }
                offX += e.X.Value;
                offY += e.Y.Value;
                scale = FM.Clamp(Plugin.HudSize.Value, 0.3f, 2f);
            }

            float newX = baseX + offX, newY = baseY + offY;
            if (newX != pos.x || newY != pos.y) rect.anchoredPosition = FM.V2(newX, newY);
            if (scale != e.AppliedScale)
            {
                float k = inv * scale;
                rect.localScale = FM.V3(sc.x * k, sc.y * k, sc.z * k);
            }
            e.AppliedOffset = FM.V2(offX, offY);
            e.AppliedScale = scale;
        }
    }

    static RectTransform RootOf(Element e)
    {
        if (e.Root != null && !e.Root.WasCollected) return e.Root;
        var canvas = e.Rect.GetComponentInParent<Canvas>();
        e.Root = canvas != null ? canvas.rootCanvas.transform.Cast<RectTransform>() : null;
        return e.Root;
    }

    static void Scan()
    {
        _elements.RemoveAll(e => !Alive(e));
        Add<GameHUD_HealthBar>("Health", Plugin.HudHealthX, Plugin.HudHealthY);
        Add<GameHUD_Speedometer>("Speedometer", Plugin.HudSpeedoX, Plugin.HudSpeedoY);
        Add<GameHUD_Ability>("Ability", Plugin.HudAbilityX, Plugin.HudAbilityY);
    }

    static void Add<T>(string name, ConfigEntry<float> x, ConfigEntry<float> y) where T : Component
    {
        foreach (var c in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
        {
            var rect = c.transform.TryCast<RectTransform>();
            if (rect == null || _elements.Exists(e => Alive(e) && e.Rect.Pointer == rect.Pointer)) continue;
            _elements.Add(new Element { Name = name, Rect = Keep.Hold(rect), X = x, Y = y });
        }
    }
}
