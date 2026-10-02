using System.Collections.Generic;
using BepInEx.Configuration;
using Game.Runtime.UI;
using UnityEngine;

namespace DriverCam;

/// <summary>
/// Moves and scales the health bar, speedometer and ability bar. Offsets are applied on top of wherever the game
/// puts each element (its own animations keep working): each frame the previous offset is taken back out first.
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
            var rect = e.Rect;

            // take our previous adjustment back out to recover the game's own position/scale this frame
            var basePos = rect.anchoredPosition - e.AppliedOffset;
            var baseScale = e.AppliedScale > 1e-4f ? rect.localScale / e.AppliedScale : rect.localScale;

            Vector2 offset = Vector2.zero;
            float scale = 1f;
            if (apply)
            {
                // pull towards the middle of the screen, measured in the canvas' own units
                var canvas = rect.GetComponentInParent<Canvas>();
                var root = canvas != null ? canvas.rootCanvas.transform.Cast<RectTransform>() : null;
                if (root != null)
                {
                    var local = (Vector2)root.InverseTransformPoint(rect.position) - e.AppliedOffset;
                    offset.x = -local.x * Mathf.Clamp01(Plugin.HudPullToCenter.Value);
                }
                offset += new Vector2(e.X.Value, e.Y.Value);
                scale = Mathf.Clamp(Plugin.HudSize.Value, 0.3f, 2f);
            }

            rect.anchoredPosition = basePos + offset;
            rect.localScale = baseScale * scale;
            e.AppliedOffset = offset;
            e.AppliedScale = scale;
        }
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
