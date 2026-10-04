using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Police
{
    /// <summary>
    /// Pursuit HUD (uGUI via RogueShared.UiKit), replacing the flat IMGUI boxes:
    /// - Pursuit panel, top-centre below TrafficDensity's toast: siren glows (red left / blue right, alternating at 2 Hz),
    ///   "PURSUIT", unit count, time left, and a BUSTED &lt;-&gt; EVADE meter with a glossy fill and a needle at the lead.
    /// - Banner under it: big shadowed text with a coloured underline that pops in (scale 1.25 -> 1) and fades out.
    /// Built once, values pushed only when they change. Panel and banner fade with CanvasGroups.
    /// </summary>
    internal sealed class PursuitHud
    {
        private RogueShared.UiKit _kit;
        private RectTransform _panel, _banner, _fillRt, _needle;
        private CanvasGroup _panelGroup, _bannerGroup;
        private Image _fill, _glowL, _glowR, _bannerLine;
        private RogueShared.UiKit.Label _title, _units, _time, _busted, _evade, _bannerText;
        private float _panelAlpha, _bannerStart = -10f, _bannerUntil;
        private bool _panelOn;
        private int _shownUnits = -1, _shownSec = -1;

        private const float MeterW = 440f;
        private static readonly Color PanelColor = new Color(0.06f, 0.07f, 0.1f, 0.88f);
        private static readonly Color RedC = new Color(1f, 0.16f, 0.12f, 1f), BlueC = new Color(0.2f, 0.42f, 1f, 1f);
        private static readonly Color GoodC = new Color(0.3f, 1f, 0.45f, 1f), WarnC = new Color(1f, 0.75f, 0.2f, 1f);

        internal bool Ok => _kit != null;

        internal static PursuitHud Create()
        {
            var h = new PursuitHud();
            try { h.Build(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Police] new HUD unavailable, using the simple one: {e.Message}");
                h.Destroy();
            }
            return h;
        }

        private void Build()
        {
            _kit = RogueShared.UiKit.Create("Police.HUD", 490);
            var k = _kit;
            // ---- pursuit panel
            _panel = k.Rect(k.Canvas, "Pursuit", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -112f), new Vector2(560f, 96f));
            _panelGroup = _panel.gameObject.AddComponent<CanvasGroup>();
            _panelGroup.alpha = 0f;
            _glowL = k.Image(_panel, "SirenL", k.Glow, RedC, false);
            RogueShared.UiKit.Place(_glowL.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(6f, 0f), new Vector2(150f, 150f));
            _glowR = k.Image(_panel, "SirenR", k.Glow, BlueC, false);
            RogueShared.UiKit.Place(_glowR.rectTransform, new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-6f, 0f), new Vector2(150f, 150f));
            k.ShadowedPanel(_panel, "Back", PanelColor);

            _title = k.Text(_panel, "Title", 22f, Color.white, TextAlignmentOptions.TopLeft);
            RogueShared.UiKit.Fill(_title.Rect, 22f, 0f, 0f, 10f);
            _title.Set("PURSUIT");
            _time = k.Text(_panel, "Time", 22f, Color.white, TextAlignmentOptions.Top);
            RogueShared.UiKit.Fill(_time.Rect, 0f, 0f, 0f, 10f);
            _units = k.Text(_panel, "Units", 18f, new Color(1f, 1f, 1f, 0.75f), TextAlignmentOptions.TopRight);
            RogueShared.UiKit.Fill(_units.Rect, 0f, 0f, 22f, 13f);

            var meter = k.Rect(_panel, "Meter", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(MeterW, 20f));
            var back = k.Image(meter, "Back", k.Pill, new Color(0f, 0f, 0f, 0.6f));
            RogueShared.UiKit.Fill(back.rectTransform, -3f, -3f, -3f, -3f);
            // faint zone tints: busted end red, evade end green
            var zl = k.Image(meter, "ZoneBusted", k.Pill, new Color(1f, 0.2f, 0.15f, 0.22f));
            RogueShared.UiKit.Place(zl.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(MeterW * 0.3f, 20f));
            var zr = k.Image(meter, "ZoneEvade", k.Pill, new Color(0.3f, 1f, 0.45f, 0.22f));
            RogueShared.UiKit.Place(zr.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(MeterW * 0.3f, 20f));
            _fill = k.Image(meter, "Fill", k.Gloss, WarnC);
            _fillRt = _fill.rectTransform;
            RogueShared.UiKit.Place(_fillRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(MeterW * 0.5f, 20f));
            var needle = k.Image(meter, "Needle", k.Pill, Color.white);
            _needle = needle.rectTransform;
            RogueShared.UiKit.Place(_needle, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(MeterW * 0.5f, 0f), new Vector2(6f, 30f));
            _busted = k.Text(meter, "Busted", 14f, RedC, TextAlignmentOptions.Left);
            RogueShared.UiKit.Fill(_busted.Rect, 10f, 0f, 0f, 0f);
            _busted.Set("BUSTED");
            _evade = k.Text(meter, "Evade", 14f, GoodC, TextAlignmentOptions.Right);
            RogueShared.UiKit.Fill(_evade.Rect, 0f, 0f, 10f, 0f);
            _evade.Set("EVADE");

            // ---- banner
            _banner = k.Rect(k.Canvas, "Banner", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -226f), new Vector2(620f, 70f));
            _bannerGroup = _banner.gameObject.AddComponent<CanvasGroup>();
            _bannerGroup.alpha = 0f;
            var bshadow = k.Image(_banner, "Shade", k.Shadow, new Color(0f, 0f, 0f, 0.55f));
            RogueShared.UiKit.Fill(bshadow.rectTransform, 40f, -10f, 40f, -10f);
            _bannerText = k.Text(_banner, "Text", 40f, Color.white, TextAlignmentOptions.Center);
            _bannerLine = k.Image(_banner, "Line", k.Pill, RedC);
            RogueShared.UiKit.Place(_bannerLine.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 2f), new Vector2(320f, 5f));
        }

        /// <summary>Shows a banner (about 2.5 s).</summary>
        internal void Banner(string text, Color color)
        {
            if (_kit == null) return;
            _bannerText.Set(text);
            _bannerText.SetColor(color);
            _bannerLine.color = color;
            _bannerStart = Time.unscaledTime;
            _bannerUntil = _bannerStart + 2.5f;
        }

        /// <summary>Every frame. chasing = show the panel; lead 0-100; units = chasers; secondsLeft = time until the chase ends.</summary>
        internal void Tick(bool chasing, float lead, int units, int secondsLeft)
        {
            if (_kit == null) return;
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            _panelOn = chasing;
            _panelAlpha = Mathf.MoveTowards(_panelAlpha, chasing ? 1f : 0f, dt * 5f);
            _panelGroup.alpha = _panelAlpha;
            if (_panelAlpha > 0f)
            {
                bool redPhase = ((int)(now * 4f) & 1) == 0;
                float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(now * Mathf.PI * 2f));
                _glowL.color = new Color(RedC.r, RedC.g, RedC.b, redPhase ? 0.85f * pulse : 0.08f);
                _glowR.color = new Color(BlueC.r, BlueC.g, BlueC.b, redPhase ? 0.08f : 0.85f * pulse);
                if (chasing)
                {
                    float f = Mathf.Clamp01(lead / 100f);
                    _fillRt.sizeDelta = new Vector2(Mathf.Max(8f, MeterW * f), 20f);
                    _needle.anchoredPosition = new Vector2(MeterW * f, 0f);
                    _fill.color = f >= 0.7f ? GoodC : f <= 0.3f ? RedC : WarnC;
                    if (units != _shownUnits) { _shownUnits = units; _units.Set(units == 1 ? "1 UNIT" : $"{units} UNITS"); }   // strings only on change
                    if (secondsLeft != _shownSec)
                    {
                        _shownSec = secondsLeft;
                        _time.Set($"{secondsLeft / 60}:{secondsLeft % 60:00}");
                        _time.SetColor(secondsLeft <= 5 ? RedC : Color.white);
                    }
                }
            }
            // banner: pop in, hold, fade
            float age = now - _bannerStart;
            float a = now < _bannerUntil ? Mathf.Clamp01(age / 0.12f) * Mathf.Clamp01((_bannerUntil - now) / 0.4f) : 0f;
            _bannerGroup.alpha = a;
            if (a > 0f)
            {
                float s = 1f + 0.25f * Mathf.Clamp01(1f - age / 0.18f);
                _banner.localScale = new Vector3(s, s, 1f);
            }
        }

        internal void Destroy()
        {
            _kit?.Destroy();
            _kit = null;
        }
    }
}
