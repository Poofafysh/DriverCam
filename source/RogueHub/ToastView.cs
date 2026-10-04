using System;
using System.Collections.Generic;
using RogueShared;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RogueHub
{
    /// <summary>
    /// One notification stack for every mod, top right (clear of the game's score stack): a card per message with a
    /// coloured edge (info cyan, good green, warn orange, bad red), the mod's initial, a title and one detail line.
    /// Messages come from HubLink.Toast (the shared queue) and from the hub itself. A repeat of a visible message
    /// merges into it with a count. At most 4 cards; each fades out over its last 0.4 s.
    /// </summary>
    internal sealed class ToastView
    {
        private const int MaxCards = 4;
        private const float W = 500f, H = 76f, Gap = 10f, X = 1920f - 42f - W, Y0 = 300f;

        private sealed class Card
        {
            internal RectTransform Rt;
            internal CanvasGroup Group;
            internal Image Edge, Icon;
            internal Txt Letter, Title, Detail;
            internal string Key = "";
            internal int Count;
            internal float Until;
            internal bool Active;   // mirrors the card's activeSelf without asking Unity every frame
        }

        private readonly Ui _ui;
        private readonly List<Card> _cards = new List<Card>();
        private readonly Queue<object[]> _pending = new Queue<object[]>();

        internal ToastView()
        {
            _ui = Ui.Create("RogueHub.Toasts", 600, false);
            try { Build(); }
            catch { _ui.Destroy(); throw; }
            _ui.SetVisible(true);
        }

        private void Build()
        {
            for (int i = 0; i < MaxCards; i++)
            {
                var c = new Card();
                c.Rt = Ui.Panel(_ui.Stage, "Toast" + i, X, Y0 + i * (H + Gap), W, H, new Color(0.04f, 0.06f, 0.17f, 0.94f), Pal.Edge);
                c.Group = c.Rt.gameObject.AddComponent<CanvasGroup>();
                c.Edge = Ui.Img(c.Rt, "Kind", Ui.Solid, Pal.Cyan, 0, 0, 7, H);
                c.Icon = Ui.Img(c.Rt, "Icon", Ui.Pill, Pal.Cyan, 22, 16, 44, 44);
                c.Letter = Ui.Text(c.Icon.transform, "L", 0, 0, 44, 44, 22, Pal.Ink, TextAlignmentOptions.Center);
                c.Title = Ui.Text(c.Rt, "Title", 80, 10, W - 96, 30, 22, Pal.Text, TextAlignmentOptions.MidlineLeft, head: false);
                c.Detail = Ui.Text(c.Rt, "Detail", 80, 40, W - 96, 26, 18, Pal.Dim, TextAlignmentOptions.MidlineLeft, head: false);
                c.Rt.gameObject.SetActive(false);
                c.Active = false;
                _cards.Add(c);
            }
        }

        internal void Destroy() => _ui.Destroy();

        /// <summary>A message from the hub itself.</summary>
        internal void Push(string guid, string title, string detail, string kind) =>
            _pending.Enqueue(new object[] { guid, title, detail, kind });

        /// <summary>Per frame: pull HubLink's queue, place new cards, age and fade the shown ones.</summary>
        internal void Tick(bool enabled)
        {
            try
            {
                var q = (Queue<object[]>)HubLink.Registry()["toasts"];
                while (q.Count > 0) _pending.Enqueue(q.Dequeue());
            }
            catch { /* registry missing: nothing queued */ }
            float now = Time.unscaledTime;
            while (_pending.Count > 0)
            {
                var m = _pending.Dequeue();
                if (enabled) Show(m, now);
            }
            bool any = false;
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                if (!c.Active) continue;
                float left = c.Until - now;
                if (left <= 0f) { c.Rt.gameObject.SetActive(false); c.Active = false; c.Key = ""; continue; }
                c.Group.alpha = Mathf.Clamp01(left / 0.4f);
                any = true;
            }
            if (any) Restack();
        }

        private void Show(object[] m, float now)
        {
            string guid = m[0] as string ?? "", title = m[1] as string ?? "", detail = m[2] as string ?? "", kind = m[3] as string ?? "info";
            if (title.Length == 0 && detail.Length == 0) return;
            string key = guid + "|" + title + "|" + detail;
            float life = Mathf.Clamp(Plugin.ToastSeconds.Value, 1f, 10f);
            foreach (var c in _cards)
                if (c.Active && c.Key == key)
                {
                    c.Count++;
                    c.Title.Set(c.Count > 1 ? $"{title}  <color=#8ea2c9>x{c.Count}</color>" : title);
                    c.Until = now + life;
                    return;
                }
            Card slot = null;
            foreach (var c in _cards) if (!c.Active) { slot = c; break; }
            if (slot == null)
            {
                slot = _cards[0];
                foreach (var c in _cards) if (c.Until < slot.Until) slot = c;   // replace the oldest
            }
            Color col = kind == "good" ? Pal.Green : kind == "warn" ? Pal.Orange : kind == "bad" ? Pal.Red : Pal.Cyan;
            slot.Key = key; slot.Count = 1; slot.Until = now + life;
            slot.Edge.color = col; slot.Icon.color = col;
            slot.Letter.Set(Initial(guid));
            slot.Title.Set(title.Length > 0 ? title : detail);
            slot.Detail.Set(title.Length > 0 ? detail : "");
            slot.Group.alpha = 1f;
            slot.Rt.SetAsLastSibling();
            slot.Rt.gameObject.SetActive(true);
            slot.Active = true;
        }

        private readonly List<Card> _shown = new List<Card>();
        private static readonly Comparison<Card> NewestFirst = (a, b) => b.Until.CompareTo(a.Until);

        /// <summary>Newest card at the top (no allocation per frame).</summary>
        private void Restack()
        {
            _shown.Clear();
            foreach (var c in _cards) if (c.Active) _shown.Add(c);
            _shown.Sort(NewestFirst);
            for (int i = 0; i < _shown.Count; i++)
            {
                var pos = new Vector2(X, -(Y0 + i * (H + Gap)));
                if (_shown[i].Rt.anchoredPosition != pos) _shown[i].Rt.anchoredPosition = pos;
            }
        }

        private static string Initial(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "R";
            string tail = guid.Substring(guid.LastIndexOf('.') + 1);
            return tail.Length > 0 ? char.ToUpperInvariant(tail[0]).ToString() : "R";
        }
    }
}
