using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RogueHub
{
    /// <summary>
    /// The GTA-style quick menu on the left while driving: a header with the position counter, up to 9 favourite rows
    /// (label left, live value right), a one-line description of the selected row and a footer with the controls.
    /// Rows are favourites picked with X in the hub; the last row always opens the full hub.
    /// </summary>
    internal sealed class QuickView
    {
        internal const int MaxRows = 9;
        // x 230: right of DriverCam's on-screen button (20, 420, 180 x 45 px at its default size) and of the game's
        // health / ability rings; y 150: under the game's timer; 9 rows end above RacingLine's card (bottom-left)
        private const float X = 230f, Y = 150f, W = 450f, HeadH = 58f, RowH = 50f, DescH = 66f, FootH = 40f;

        private readonly Ui _ui;
        private readonly RectTransform _panel;
        private RectTransform _desc, _foot;
        private Txt _title, _count, _descT, _footT;
        private readonly Image[] _rowBg = new Image[MaxRows], _rowBar = new Image[MaxRows];
        private readonly Txt[] _label = new Txt[MaxRows], _value = new Txt[MaxRows];
        internal readonly List<Setting> Items = new List<Setting>();   // null = "Open hub"
        internal int Sel;

        internal QuickView()
        {
            _ui = Ui.Create("RogueHub.Quick", 580, false);
            try { _panel = Ui.Box(_ui.Stage, "Quick", X, Y, W, 600); }
            catch { _ui.Destroy(); throw; }
            try { Build(); }
            catch { _ui.Destroy(); throw; }
        }

        private void Build()
        {
            var head = Ui.Panel(_panel, "Head", 0, 0, W, HeadH, Pal.RowSel, Pal.Cyan);
            _title = Ui.Text(head, "Title", 20, 0, 200, HeadH, 26, Pal.Text, TextAlignmentOptions.MidlineLeft);
            _title.Set("QUICK");
            _count = Ui.Text(head, "Count", W - 220, 0, 200, HeadH, 17, new Color(0.75f, 0.91f, 1f), TextAlignmentOptions.MidlineRight);
            for (int i = 0; i < MaxRows; i++)
            {
                float y = HeadH + i * RowH;
                _rowBg[i] = Ui.Img(_panel, "Row" + i, Ui.Solid, new Color(0.04f, 0.063f, 0.17f, 0.9f), 0, y, W, RowH);
                _rowBar[i] = Ui.Img(_rowBg[i].transform, "Bar", Ui.Solid, Pal.Cyan, 0, 0, 6, RowH);
                _label[i] = Ui.Text(_rowBg[i].transform, "Label", 22, 0, W - 190, RowH, 22, Pal.Text, TextAlignmentOptions.MidlineLeft, head: false);
                _value[i] = Ui.Text(_rowBg[i].transform, "Value", W - 180, 0, 160, RowH, 18, Pal.Text, TextAlignmentOptions.MidlineRight);
            }
            _desc = Ui.Panel(_panel, "Desc", 0, 0, W, DescH, new Color(0.04f, 0.063f, 0.17f, 0.95f), Pal.Cyan2);
            _descT = Ui.Text(_desc, "T", 20, 6, W - 40, DescH - 12, 17, Pal.Dim, TextAlignmentOptions.MidlineLeft, head: false, wrap: true);
            _foot = Ui.Box(_panel, "Foot", 0, 0, W, FootH);
            Ui.Img(_foot, "Bg", Ui.Solid, new Color(0.03f, 0.047f, 0.13f, 0.92f), 0, 0, W, FootH);
            _footT = Ui.Text(_foot, "T", 16, 0, W - 32, FootH, 16, new Color(0.81f, 0.88f, 1f), TextAlignmentOptions.MidlineLeft, head: false);
        }

        internal void Destroy() => _ui.Destroy();
        internal bool Visible => _ui.Root != null && _ui.Root.activeSelf;

        internal void Open(List<Setting> items)
        {
            Items.Clear();
            foreach (var s in items) { if (Items.Count >= MaxRows - 1) break; Items.Add(s); }
            Items.Add(null);   // Open hub
            if (Sel >= Items.Count) Sel = 0;
            int n = Items.Count;
            for (int i = 0; i < MaxRows; i++) Ui.Show(_rowBg[i], i < n);
            Ui.Place(_desc, 0, HeadH + n * RowH, W, DescH);
            Ui.Place(_foot, 0, HeadH + n * RowH + DescH, W, FootH);
            _ui.SetVisible(true);
            Refresh();
        }

        internal void Close() => _ui.SetVisible(false);

        internal void Refresh()
        {
            int n = Items.Count;
            _count.Set($"LB+RB   {Sel + 1} / {n}");
            for (int i = 0; i < n && i < MaxRows; i++)
            {
                var s = Items[i];
                bool sel = i == Sel;
                _rowBg[i].color = sel ? Pal.RowSel : new Color(0.04f, 0.063f, 0.17f, 0.9f);
                Ui.Show(_rowBar[i], sel);
                if (s == null)
                {
                    _label[i].Set("Open the hub");
                    _value[i].Set(">");
                    _value[i].Color(Pal.Cyan);
                    continue;
                }
                _label[i].Set(s.Label);
                if (s.Kind == Kind.Action) { _value[i].Set("USE"); _value[i].Color(Pal.Orange); continue; }
                string v = Catalog.Show(s);
                bool choice = s.Kind == Kind.Number || s.Kind == Kind.Choice;
                _value[i].Set(choice && sel ? $"<noparse><</noparse> {v} <noparse>></noparse>" : v);
                _value[i].Color(s.Kind == Kind.Bool ? ((bool)s.Entry.BoxedValue ? Pal.Green : Pal.Dim) : Pal.Text);
            }
            var cur = Sel < n ? Items[Sel] : null;
            _descT.Set(cur == null ? "Every setting of every mod, with descriptions. The game pauses while it's open."
                                   : $"<b>{cur.Module.Name}</b>  {FirstSentence(cur.Description)}");
            _footT.Set(In.PadUsed ? "D-pad: pick, change    A use    B close" : "Arrows: pick, change    Enter use    ` close");
        }

        internal static string FirstSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int dot = s.IndexOf(". ", System.StringComparison.Ordinal);
            string first = dot > 0 ? s.Substring(0, dot + 1) : s;
            return first.Length > 150 ? first.Substring(0, 147) + "..." : first;
        }
    }
}
