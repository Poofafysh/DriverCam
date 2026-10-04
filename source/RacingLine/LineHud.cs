using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RacingLine
{
    /// <summary>
    /// The Racing Line card (uGUI, RogueShared.UiKit), bottom-left, shown with the line (F5):
    ///   RACING LINE                 +1,234   (live points of the corner being driven, else the race total)
    ///   [GOLD] GRIP  x1.45 streak            (last corner's grade, held 4 s)
    ///   |-----[ ]--o--------|                (where you are vs the line: centre band = full credit, dot = you)
    /// A panel with a soft drop shadow, gradient and lit edge; text in the game's HUD font with a shadow. Built once,
    /// updated only when values change. Destroyed with the plugin.
    /// </summary>
    internal sealed class LineHud
    {
        private RogueShared.UiKit _kit;
        private RectTransform _card;
        private RogueShared.UiKit.Label _title, _points, _grade, _streak, _gripTag;
        private Image _gradePill, _gaugeBack, _gaugeBand, _gaugeDot, _accent;
        private CanvasGroup _group;
        private bool _visible;
        private float _alpha;

        private static readonly Color PanelColor = new Color(0.07f, 0.09f, 0.13f, 0.86f);
        private static readonly Color Accent = new Color(0.25f, 1f, 0.5f, 1f);
        private static readonly Color GoldC = new Color(1f, 0.78f, 0.2f, 1f), SilverC = new Color(0.82f, 0.86f, 0.92f, 1f), BronzeC = new Color(0.86f, 0.52f, 0.28f, 1f);
        private static readonly Color Dim = new Color(1f, 1f, 1f, 0.55f);

        internal bool Ok => _kit != null;

        internal static LineHud Create()
        {
            var h = new LineHud();
            try { h.Build(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RacingLine] HUD card unavailable (text readout stays): {e.Message}");
                h.Destroy();
            }
            return h;
        }

        private void Build()
        {
            _kit = RogueShared.UiKit.Create("RacingLine.HUD", 480);
            var k = _kit;
            _card = k.Rect(k.Canvas, "Card", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(28f, 150f), new Vector2(380f, 112f));
            _group = _card.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            k.ShadowedPanel(_card, "Back", PanelColor);
            _accent = k.Image(_card, "Accent", k.Pill, Accent);
            RogueShared.UiKit.Place(_accent.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(5f, 88f));

            _title = k.Text(_card, "Title", 17f, Dim, TextAlignmentOptions.TopLeft);
            RogueShared.UiKit.Fill(_title.Rect, 22f, 0f, 0f, 10f);
            _title.Set("RACING LINE");
            _points = k.Text(_card, "Points", 30f, Color.white, TextAlignmentOptions.TopRight);
            RogueShared.UiKit.Fill(_points.Rect, 0f, 0f, 16f, 4f);

            _gradePill = k.Image(_card, "GradePill", k.Pill, GoldC);
            RogueShared.UiKit.Place(_gradePill.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -44f), new Vector2(92f, 26f));
            _grade = k.Text(_gradePill.rectTransform, "Grade", 15f, new Color(0.08f, 0.08f, 0.1f, 1f), TextAlignmentOptions.Center, true, false);
            _gripTag = k.Text(_card, "Grip", 15f, Accent, TextAlignmentOptions.TopLeft);
            RogueShared.UiKit.Fill(_gripTag.Rect, 124f, 0f, 0f, 48f);
            _streak = k.Text(_card, "Streak", 16f, Color.white, TextAlignmentOptions.TopRight);
            RogueShared.UiKit.Fill(_streak.Rect, 0f, 0f, 16f, 48f);

            _gaugeBack = k.Image(_card, "Gauge", k.Pill, new Color(0f, 0f, 0f, 0.55f));
            RogueShared.UiKit.Place(_gaugeBack.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(6f, 14f), new Vector2(320f, 12f));
            _gaugeBand = k.Image(_gaugeBack.rectTransform, "Band", k.Pill, new Color(0.25f, 1f, 0.5f, 0.45f));
            RogueShared.UiKit.Place(_gaugeBand.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(60f, 12f));
            _gaugeDot = k.Image(_gaugeBack.rectTransform, "Dot", k.Glow, Color.white, false);
            RogueShared.UiKit.Place(_gaugeDot.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26f, 26f));
            _gradePill.gameObject.SetActive(false);
        }

        /// <summary>Fades the card in / out (about 0.2 s). Call every frame.</summary>
        internal void SetVisible(bool on)
        {
            if (_kit == null) return;
            _visible = on;
            float target = on ? 1f : 0f;
            if (Mathf.Approximately(_alpha, target)) return;
            _alpha = Mathf.MoveTowards(_alpha, target, Time.unscaledDeltaTime * 5f);
            _group.alpha = _alpha;
        }

        private string _lastGrade = "";
        private long _shownPoints = long.MinValue;
        private bool _shownLive;
        private int _shownStreak = int.MinValue;

        /// <summary>
        /// points = number to show (live corner points while in a corner, else the race total); live = in a corner;
        /// grade / grip = last corner's result while recent (null = hide); signed = metres off the line (+ right, NaN hides
        /// the dot); full = the full-credit half-band in metres; range = gauge half-width in metres.
        /// </summary>
        internal void Show(double points, bool live, string grade, bool grip, float streak, float signed, float full, float range)
        {
            if (_kit == null || !_visible) return;
            long pts = (long)Math.Round(points);
            if (pts != _shownPoints || live != _shownLive)   // strings only when the shown number changes
            {
                _shownPoints = pts; _shownLive = live;
                _points.Set(live ? $"+{pts:N0}" : $"{pts:N0}");
            }
            _points.SetColor(live ? Accent : Color.white);
            _accent.color = live ? Accent : new Color(1f, 1f, 1f, 0.25f);

            string g = grade ?? "";
            if (g != _lastGrade)
            {
                _lastGrade = g;
                _gradePill.gameObject.SetActive(grade != null);
                if (grade != null)
                {
                    _grade.Set(grade);
                    _gradePill.color = grade == "GOLD" ? GoldC : grade == "SILVER" ? SilverC : BronzeC;
                }
            }
            _gripTag.Set(grade != null && grip ? "GRIP LINE" : "");   // constant strings: no allocation
            int st = streak > 1.001f ? Mathf.RoundToInt(streak * 100f) : 0;
            if (st != _shownStreak) { _shownStreak = st; _streak.Set(st > 0 ? $"x{st / 100f:0.00} streak" : ""); }

            float w = 320f;
            float band = Mathf.Clamp(full / Mathf.Max(0.5f, range), 0.02f, 1f) * w;
            _gaugeBand.rectTransform.sizeDelta = new Vector2(band, 12f);
            if (float.IsNaN(signed)) _gaugeDot.color = new Color(1f, 1f, 1f, 0f);
            else
            {
                float x = Mathf.Clamp(signed / range, -1f, 1f) * (w * 0.5f - 6f);
                _gaugeDot.rectTransform.anchoredPosition = new Vector2(x, 0f);
                float d = Mathf.Abs(signed);
                _gaugeDot.color = d <= full ? Accent : Color.Lerp(new Color(1f, 0.8f, 0.2f, 1f), new Color(1f, 0.3f, 0.25f, 1f), Mathf.Clamp01((d - full) / Mathf.Max(0.5f, range - full)));
            }
        }

        internal void Destroy()
        {
            _kit?.Destroy();
            _kit = null;
        }
    }
}
