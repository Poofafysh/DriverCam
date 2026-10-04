using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RogueHub
{
    /// <summary>
    /// The hub menu (version 1, "Neon Garage"): tabs on top, the tab's plugins as cards on the left, the selected
    /// plugin's settings in the middle (toggle, slider + number box, list, key cap, button), a description panel on the
    /// right and a controller / keyboard prompt bar at the bottom. Typing letters searches every setting; typing digits
    /// on a number edits it; A on a number opens a number pad for the controller. Plain C# (no injected type): the
    /// Runner calls Tick() every frame while the hub is open.
    /// </summary>
    internal sealed class HubView
    {
        // ------------------------------------------------------------------ layout (stage px, top-left origin)
        private const float ColY = 138f, ColH = 840f;
        private const float ModX = 42f, ModW = 440f, SetX = 500f, SetW = 900f, InfX = 1418f, InfW = 460f;
        private const float CardH = 112f, CardGap = 12f, HeadH = 56f, RowH = 56f;
        private const int Cards = 6, Rows = 13;
        private const float TabX = 500f, TabW = 150f, TabGap = 10f;

        private enum Focus { Modules, Settings }
        private enum Edit { None, Typing, NumPad, KeyCapture }

        private sealed class RowItem
        {
            internal Setting S;
            internal string Group;          // group header text (S == null, not selectable)
            internal Module AdvModule;      // the ADVANCED (n) toggle of this module
        }

        private sealed class RowUi
        {
            internal Image Bg, Bar, Dot, ChipBg, Track, Fill, Thumb, BoxBg, BoxEdge, TogTrack, TogKnob, KeyBg, BtnBg;
            internal Txt Label, Group, Chip, Box, Choice, Key, Btn, Value;
            internal RectTransform Rt;
        }

        private sealed class CardUi
        {
            internal RectTransform Rt;
            internal Image Bg, Edge, Bar, PillBg;
            internal Txt Name, Status, Pill, Version;
        }

        private readonly Ui _ui;
        private Image _backdrop;
        private readonly List<Image> _tabBg = new List<Image>(), _tabEdge = new List<Image>();
        private readonly List<Txt> _tabTxt = new List<Txt>();
        private readonly List<RectTransform> _tabRt = new List<RectTransform>();
        private Txt _searchChip, _setTitle, _setCount, _infTitle, _infDesc, _infStatus;
        private readonly Txt[] _kvKey = new Txt[4], _kvVal = new Txt[4];
        private readonly CardUi[] _cards = new CardUi[Cards];
        private readonly RowUi[] _rows = new RowUi[Rows];
        private readonly List<Image> _glyphBg = new List<Image>();
        private readonly List<Txt> _glyph = new List<Txt>(), _prompt = new List<Txt>();
        private RectTransform _pad;
        private Txt _padTitle, _padDisplay;
        private readonly List<Image> _padKeyBg = new List<Image>();
        private readonly List<RectTransform> _padKeyRt = new List<RectTransform>();
        private static readonly string[] PadKeys = { "7", "8", "9", "4", "5", "6", "1", "2", "3", "-", "0", ".", "DEL", "OK" };

        // state
        internal bool CloseRequested;
        private int _tab = 3;                 // WORLD
        private int _mod, _modTop;
        private Focus _focus = Focus.Modules;
        private readonly List<Module> _tabMods = new List<Module>();
        private readonly List<RowItem> _items = new List<RowItem>();
        private int _sel = -1, _top;
        private readonly HashSet<string> _advOpen = new HashSet<string>();
        private string _query = "";
        private Edit _edit = Edit.None;
        private string _buf = "";
        private int _padSel;
        private float _flashUntil;
        private int _dragRow = -1;
        private float _nextLive;
        private bool _dirty = true;
        internal Action<string, string, string, string> Toast;   // guid, title, detail, kind

        internal HubView()
        {
            _ui = Ui.Create("RogueHub.Hub", 590, true);
            try { Build(); }
            catch { _ui.Destroy(); throw; }   // a half-built hub (inactive) never stays behind
        }

        private void Build()
        {
            // full-screen dim backdrop: also catches the mouse so the paused game's buttons can't be clicked through it
            _backdrop = new GameObject("Backdrop").AddComponent<Image>();
            _backdrop.rectTransform.SetParent(_ui.Root.transform, false);
            _backdrop.rectTransform.SetAsFirstSibling();
            Ui.Stretch(_backdrop.rectTransform);
            _backdrop.sprite = Ui.Solid;
            _backdrop.color = new Color(0.01f, 0.015f, 0.05f, 0.72f);
            _backdrop.raycastTarget = true;
            var st = _ui.Stage;

            // top bar
            var logo = Ui.Panel(st, "Logo", 42, 30, 400, 84, new Color(0.07f, 0.11f, 0.27f, 0.97f), Pal.Cyan);
            var lt = Ui.Text(logo, "T", 0, 0, 400, 84, 34, Pal.Text, TextAlignmentOptions.Center);
            lt.Set("<color=#29c6ff>ROGUE</color> <color=#ff2fa8>//</color> HUB");
            for (int i = 0; i < Catalog.Areas.Length; i++)
            {
                float x = TabX + i * (TabW + TabGap);
                var bg = Ui.Img(st, "Tab" + i, Ui.Chamf, new Color(0.06f, 0.094f, 0.24f, 0.92f), x, 36, TabW, 72);
                var edge = Ui.Img(bg.transform, "Edge", Ui.ChamfEdge, Pal.Edge, 0, 0, TabW, 72);
                var t = Ui.Text(bg.transform, "T", 0, 0, TabW, 72, 17, Pal.Dim, TextAlignmentOptions.Center);
                t.Set(Catalog.Areas[i]);
                _tabBg.Add(bg); _tabEdge.Add(edge); _tabTxt.Add(t); _tabRt.Add(bg.rectTransform);
            }
            var chip = Ui.Panel(st, "Search", 1640, 36, 238, 72, new Color(0.06f, 0.094f, 0.24f, 0.92f), Pal.Edge);
            _searchChip = Ui.Text(chip, "T", 14, 0, 210, 72, 15, Pal.Dim, TextAlignmentOptions.MidlineLeft);

            // modules column
            var mods = Ui.Panel(st, "Modules", ModX, ColY, ModW, ColH, Pal.Panel, Pal.Cyan);
            for (int i = 0; i < Cards; i++)
            {
                var c = new CardUi();
                c.Bg = Ui.Img(mods, "Card" + i, Ui.Solid, Pal.Card, 14, 14 + i * (CardH + CardGap), ModW - 28, CardH);
                c.Rt = c.Bg.rectTransform;
                c.Edge = Ui.Img(c.Rt, "Edge", Ui.ChamfEdge, Pal.Edge, 0, 0, ModW - 28, CardH);
                c.Bar = Ui.Img(c.Rt, "Bar", Ui.Solid, Pal.Cyan, 0, 0, 7, CardH);
                c.Name = Ui.Text(c.Rt, "Name", 24, 14, ModW - 160, 38, 22, Pal.Text, TextAlignmentOptions.MidlineLeft);
                c.Status = Ui.Text(c.Rt, "Status", 24, 54, ModW - 70, 46, 17, Pal.Dim, TextAlignmentOptions.TopLeft, head: false, wrap: true);
                c.PillBg = Ui.Img(c.Rt, "Pill", Ui.Pill, Pal.Green, ModW - 28 - 92, 16, 76, 32);
                c.Pill = Ui.Text(c.PillBg.transform, "T", 0, 0, 76, 32, 15, Pal.Ink, TextAlignmentOptions.Center);
                c.Version = Ui.Text(c.Rt, "V", ModW - 28 - 160, CardH - 30, 144, 24, 13, Pal.A(Pal.Dim, 0.7f), TextAlignmentOptions.MidlineRight, head: false);
                _cards[i] = c;
            }

            // settings column
            var set = Ui.Panel(st, "Settings", SetX, ColY, SetW, ColH, Pal.Panel, Pal.Cyan);
            var sh = Ui.Img(set, "Head", Ui.Solid, Pal.Head, 2, 2, SetW - 4, HeadH);
            _setTitle = Ui.Text(sh.transform, "T", 22, 0, SetW - 200, HeadH, 20, Pal.Text, TextAlignmentOptions.MidlineLeft);
            _setCount = Ui.Text(sh.transform, "C", SetW - 190, 0, 166, HeadH, 17, Pal.Dim, TextAlignmentOptions.MidlineRight, head: false);
            for (int i = 0; i < Rows; i++) _rows[i] = MakeRow(set, HeadH + 4 + i * RowH);

            // info column
            var inf = Ui.Panel(st, "Info", InfX, ColY, InfW, ColH, Pal.Panel, Pal.Cyan);
            var ih = Ui.Img(inf, "Head", Ui.Solid, Pal.Head, 2, 2, InfW - 4, HeadH);
            _infTitle = Ui.Text(ih.transform, "T", 22, 0, InfW - 44, HeadH, 19, Pal.Text, TextAlignmentOptions.MidlineLeft);
            _infDesc = Ui.Text(inf, "Desc", 26, HeadH + 18, InfW - 52, 300, 20, new Color(0.84f, 0.88f, 0.98f), TextAlignmentOptions.TopLeft, head: false, wrap: true);
            _infDesc.T.richText = false;   // plugin descriptions are plain text ("x < 5" must not become a tag)
            for (int i = 0; i < 4; i++)
            {
                float y = HeadH + 340 + i * 46;
                Ui.Img(inf, "Line" + i, Ui.Solid, Pal.RowLine, 26, y + 44, InfW - 52, 1.5f);
                _kvKey[i] = Ui.Text(inf, "K" + i, 26, y, 170, 44, 18, Pal.Dim, TextAlignmentOptions.MidlineLeft, head: false);
                _kvVal[i] = Ui.Text(inf, "V" + i, 190, y, InfW - 216, 44, 18, Pal.Text, TextAlignmentOptions.MidlineRight, head: false);
            }
            var live = Ui.Panel(inf, "Live", 26, HeadH + 560, InfW - 52, 200, new Color(0.16f, 0.78f, 1f, 0.05f), Pal.Cyan2);
            _infStatus = Ui.Text(live, "T", 18, 14, InfW - 88, 172, 17, Pal.Dim, TextAlignmentOptions.TopLeft, head: false, wrap: true);

            // prompt bar
            Ui.Img(st, "Prompts", Ui.Solid, new Color(0.03f, 0.047f, 0.13f, 0.94f), 42, 996, 1836, 60);
            Ui.Img(st, "PromptLine", Ui.Solid, Pal.Cyan2, 42, 996, 1836, 2);
            for (int i = 0; i < 8; i++)
            {
                var g = Ui.Img(st, "G" + i, Ui.Pill, new Color(0.91f, 0.93f, 0.99f), 0, 1012, 40, 30);
                _glyphBg.Add(g);
                _glyph.Add(Ui.Text(g.transform, "T", 0, 0, 40, 30, 14, Pal.Ink, TextAlignmentOptions.Center));
                _glyph[_glyph.Count - 1].T.richText = false;   // glyphs like "<>" are literal
                _prompt.Add(Ui.Text(st, "P" + i, 0, 1006, 260, 42, 19, new Color(0.81f, 0.88f, 1f), TextAlignmentOptions.MidlineLeft, head: false));
            }

            // number pad (controller typing), over the info column
            _pad = Ui.Panel(st, "NumPad", InfX + 30, 230, 400, 560, new Color(0.04f, 0.07f, 0.21f, 0.99f), Pal.Orange);
            _padTitle = Ui.Text(_pad, "L", 22, 16, 356, 28, 15, Pal.Orange, TextAlignmentOptions.MidlineLeft);
            Ui.Img(_pad, "DispBg", Ui.Solid, new Color(0.02f, 0.04f, 0.13f, 1f), 22, 52, 356, 66);
            _padDisplay = Ui.Text(_pad, "D", 34, 52, 332, 66, 34, Pal.Text, TextAlignmentOptions.MidlineRight);
            for (int i = 0; i < PadKeys.Length; i++)
            {
                int col = i < 12 ? i % 3 : (i == 12 ? 0 : 1);
                int row = i < 12 ? i / 3 : 4;
                float w = i == 13 ? 232 : 112;
                var k = Ui.Img(_pad, "Key" + i, Ui.Solid, new Color(0.094f, 0.133f, 0.31f, 1f), 22 + col * 122, 134 + row * 70, w, 60);
                var kt = Ui.Text(k.transform, "T", 0, 0, w, 60, 22, Pal.Text, TextAlignmentOptions.Center);
                kt.Set(PadKeys[i]);
                _padKeyBg.Add(k);
                _padKeyRt.Add(k.rectTransform);
            }
            Ui.Show(_pad, false);
        }

        private RowUi MakeRow(RectTransform parent, float y)
        {
            var r = new RowUi();
            r.Bg = Ui.Img(parent, "Row", Ui.Solid, Color.clear, 2, y, SetW - 4, RowH);
            r.Rt = r.Bg.rectTransform;
            var t = r.Rt;
            Ui.Img(t, "Line", Ui.Solid, Pal.RowLine, 0, RowH - 1.5f, SetW - 4, 1.5f);
            r.Bar = Ui.Img(t, "Bar", Ui.Solid, Pal.Cyan, 0, 0, 6, RowH);
            r.Dot = Ui.Img(t, "Dot", Ui.Disc, Pal.Orange, 14, RowH / 2 - 5, 10, 10, false);
            r.Label = Ui.Text(t, "Label", 34, 0, 380, RowH, 21, Pal.Text, TextAlignmentOptions.MidlineLeft, head: false);
            r.Group = Ui.Text(t, "Group", 22, 0, SetW - 60, RowH, 16, Pal.Cyan, TextAlignmentOptions.MidlineLeft);
            r.ChipBg = Ui.Img(t, "Chip", Ui.Pill, Pal.Orange, 410, RowH / 2 - 15, 96, 30);
            r.Chip = Ui.Text(r.ChipBg.transform, "T", 0, 0, 96, 30, 13, Pal.Ink, TextAlignmentOptions.Center);
            r.Track = Ui.Img(t, "Track", Ui.Solid, Pal.Track, 520, RowH / 2 - 4, 210, 8);
            r.Fill = Ui.Img(r.Track.transform, "Fill", Ui.Solid, Pal.Cyan, 0, 0, 0, 8);
            r.Thumb = Ui.Img(t, "Thumb", Ui.Thumb, Color.white, 520, RowH / 2 - 14, 16, 28, false);
            r.BoxBg = Ui.Img(t, "Box", Ui.Solid, new Color(0.04f, 0.067f, 0.2f, 1f), 748, RowH / 2 - 20, 132, 40);
            r.BoxEdge = Ui.Img(r.BoxBg.transform, "Edge", Ui.ChamfEdge, new Color(0.17f, 0.35f, 0.54f, 1f), 0, 0, 132, 40);
            r.Box = Ui.Text(r.BoxBg.transform, "T", 8, 0, 116, 40, 17, Pal.Text, TextAlignmentOptions.MidlineRight);
            r.TogTrack = Ui.Img(t, "Toggle", Ui.Pill, Pal.Green, SetW - 4 - 24 - 72, RowH / 2 - 17, 72, 34);
            r.TogKnob = Ui.Img(r.TogTrack.transform, "Knob", Ui.Disc, Color.white, 42, 4, 26, 26, false);
            r.Choice = Ui.Text(t, "Choice", 430, 0, SetW - 4 - 24 - 430, RowH, 17, Pal.Text, TextAlignmentOptions.MidlineRight);
            r.KeyBg = Ui.Img(t, "Key", Ui.Pill, new Color(0.91f, 0.93f, 0.99f), SetW - 4 - 24 - 150, RowH / 2 - 18, 150, 36);
            r.Key = Ui.Text(r.KeyBg.transform, "T", 0, 0, 150, 36, 16, Pal.Ink, TextAlignmentOptions.Center);
            r.BtnBg = Ui.Img(t, "Btn", Ui.Chamf, Pal.Orange, SetW - 4 - 24 - 200, RowH / 2 - 20, 200, 40);
            r.Btn = Ui.Text(r.BtnBg.transform, "T", 0, 0, 200, 40, 15, new Color(0.1f, 0.05f, 0f), TextAlignmentOptions.Center);
            r.Value = Ui.Text(t, "Value", 430, 0, SetW - 4 - 24 - 430, RowH, 17, Pal.Dim, TextAlignmentOptions.MidlineRight, head: false);
            return r;
        }

        internal void Destroy() => _ui.Destroy();
        internal bool Visible => _ui.Root != null && _ui.Root.activeSelf;

        /// <summary>Typing a search or a value, or waiting for a key: the open key doesn't close the hub then.</summary>
        internal bool Busy => _edit != Edit.None || _query.Length > 0;

        // ------------------------------------------------------------------ open / close

        internal void Open()
        {
            CloseRequested = false;
            _openFrame = Time.frameCount;
            _edit = Edit.None;
            _query = "";
            Ui.Show(_pad, false);
            Catalog.Refresh();
            if (_tab < 0 || _tab >= Catalog.Areas.Length) _tab = 3;
            LoadTab(keepModule: true);
            _ui.SetVisible(true);
            _dirty = true;
            Redraw();
        }

        internal void Close()
        {
            CancelEdit();
            _dragRow = -1;
            _ui.SetVisible(false);
            Saver.Flush();
        }

        private void LoadTab(bool keepModule = false)
        {
            _tabMods.Clear();
            string area = Catalog.Areas[_tab];
            if (area != "FAVES") _tabMods.AddRange(Catalog.Modules.Where(m => m.Area == area));
            if (!keepModule) { _mod = 0; _modTop = 0; }
            int count = area == "FAVES" ? 1 : _tabMods.Count;
            if (_mod >= count) _mod = 0;
            BuildRows();
        }

        private bool FavesTab => Catalog.Areas[_tab] == "FAVES" && _query.Length == 0;
        private Module CurModule => _query.Length > 0 || FavesTab || _mod >= _tabMods.Count ? null : _tabMods[_mod];

        private void BuildRows()
        {
            _items.Clear();
            if (_query.Length > 0)
            {
                var words = _query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int found = 0;
                foreach (var m in Catalog.Modules)
                {
                    bool header = false;
                    foreach (var s in m.Settings)
                    {
                        string hay = (s.Label + " " + s.Section + " " + m.Name + " " + s.Description + " " + (s.Entry?.Definition.Key ?? "")).ToLowerInvariant();
                        if (!words.All(w => hay.Contains(w))) continue;
                        if (!header) { _items.Add(new RowItem { Group = m.Name }); header = true; }
                        _items.Add(new RowItem { S = s });
                        if (++found >= 80) break;
                    }
                    if (found >= 80) break;
                }
            }
            else if (FavesTab)
            {
                _items.Add(new RowItem { Group = "QUICK MENU ROWS (X REMOVES ONE)" });
                foreach (var id in Favs.List())
                    if (Catalog.ById.TryGetValue(id, out var s)) _items.Add(new RowItem { S = s });
            }
            else
            {
                var m = CurModule;
                if (m != null)
                {
                    string section = null;
                    bool advShown = false;
                    foreach (var s in m.Settings)
                    {
                        if (s.Advanced && !advShown)
                        {
                            advShown = true;
                            _items.Add(new RowItem { AdvModule = m });
                            if (!_advOpen.Contains(m.Guid)) break;
                            section = null;
                        }
                        if (s.Section != section)
                        {
                            section = s.Section;
                            _items.Add(new RowItem { Group = s.Advanced ? "ADVANCED  ·  " + section : section });
                        }
                        _items.Add(new RowItem { S = s });
                    }
                }
            }
            if (_sel < 0 || _sel >= _items.Count || !Selectable(_sel)) _sel = NextSelectable(-1, 1);
            ClampTop();
            _dirty = true;
        }

        private bool Selectable(int i) => i >= 0 && i < _items.Count && (_items[i].S != null || _items[i].AdvModule != null);

        private int NextSelectable(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < _items.Count; i += dir) if (Selectable(i)) return i;
            return from >= 0 && from < _items.Count && Selectable(from) ? from : -1;
        }

        private void ClampTop()
        {
            if (_sel >= 0)
            {
                if (_sel < _top + 1) _top = Math.Max(0, _sel - 1);
                if (_sel >= _top + Rows - 1) _top = _sel - Rows + 2;
            }
            _top = Mathf.Clamp(_top, 0, Math.Max(0, _items.Count - Rows));
        }

        private Setting CurSetting => _sel >= 0 && _sel < _items.Count ? _items[_sel].S : null;

        // ------------------------------------------------------------------ per frame

        private int _openFrame;

        internal void Tick()
        {
            bool typing = _edit == Edit.Typing || _edit == Edit.KeyCapture;
            In.Read(typing);
            if (Time.frameCount == _openFrame) { if (_dirty) Redraw(); return; }   // the press that opened the hub doesn't act inside it
            if (_edit == Edit.KeyCapture) TickKeyCapture();
            else if (_edit == Edit.Typing) TickTyping();
            else if (_edit == Edit.NumPad) TickNumPad();
            else TickNav();
            TickMouse();
            if (Time.unscaledTime >= _nextLive) { _nextLive = Time.unscaledTime + 0.25f; _dirty = true; }
            if (_dirty) Redraw();
        }

        private void TickNav()
        {
            if (In.Start) { CloseRequested = true; return; }
            // keyboard typing: digits on a number = edit it; anything else = search
            if (In.Typed.Length > 0 && !In.PadUsed)
            {
                var cur = CurSetting;
                char c0 = In.Typed[0];
                if (_focus == Focus.Settings && cur != null && cur.Kind == Kind.Number && (char.IsDigit(c0) || c0 == '-' || c0 == '.'))
                {
                    _edit = Edit.Typing; _buf = In.Typed; _dirty = true; return;
                }
                _query += In.Typed;
                _focus = Focus.Settings; _sel = -1; _top = 0;
                BuildRows();
                return;
            }
            if (In.Back && _query.Length > 0)
            {
                _query = _query.Substring(0, _query.Length - 1);
                _sel = -1; _top = 0;
                BuildRows();
                return;
            }
            if (In.Esc && _query.Length > 0) { _query = ""; _focus = Focus.Modules; BuildRows(); return; }

            if (In.LB || In.ShiftTab) { SwitchTab(-1); return; }
            if (In.RB || In.Tab) { SwitchTab(+1); return; }

            if (_focus == Focus.Modules)
            {
                int count = FavesTab ? 1 : _tabMods.Count;
                if (In.Up && _mod > 0) { _mod--; OnModuleChanged(); }
                else if (In.Down && _mod < count - 1) { _mod++; OnModuleChanged(); }
                else if ((In.A || In.Right) && _items.Count > 0) { _focus = Focus.Settings; if (!Selectable(_sel)) _sel = NextSelectable(-1, 1); _dirty = true; }
                else if (In.X && CurModule?.Enabled != null) { Catalog.Nudge(CurModule.Enabled, 1, 1); _dirty = true; }
                else if (In.B) CloseRequested = true;
                return;
            }

            // settings focus
            if (In.B) { if (_query.Length > 0) { _query = ""; BuildRows(); } _focus = Focus.Modules; _dirty = true; return; }
            if (In.Up) { int n = NextSelectable(_sel, -1); if (n != _sel) { _sel = n; ClampTop(); _dirty = true; } return; }
            if (In.Down) { int n = NextSelectable(_sel, 1); if (n != _sel) { _sel = n; ClampTop(); _dirty = true; } return; }
            var item = _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;
            if (item == null) return;
            if (item.AdvModule != null)
            {
                if (In.A || In.Right || In.Left) ToggleAdvanced(item.AdvModule);
                return;
            }
            var s = item.S;
            if (s == null) return;
            if (In.Left || In.Right)
            {
                if (s.Kind == Kind.Number || s.Kind == Kind.Choice || s.Kind == Kind.Bool) { Catalog.Nudge(s, In.Right ? 1 : -1, In.FastMult); _dirty = true; }
                return;
            }
            if (In.A) { Activate(s); return; }
            if (In.Y && s.Entry != null) { Catalog.Reset(s); _dirty = true; return; }
            if (In.X) { ToggleFav(s); return; }
        }

        private void SwitchTab(int dir)
        {
            _query = "";
            _tab = (_tab + dir + Catalog.Areas.Length) % Catalog.Areas.Length;
            _focus = Focus.Modules;
            _sel = -1; _top = 0;
            LoadTab();
        }

        private void OnModuleChanged()
        {
            if (_mod < _modTop) _modTop = _mod;
            if (_mod >= _modTop + Cards) _modTop = _mod - Cards + 1;
            _sel = -1; _top = 0;
            BuildRows();
        }

        private void ToggleAdvanced(Module m)
        {
            if (!_advOpen.Remove(m.Guid)) _advOpen.Add(m.Guid);
            BuildRows();
        }

        private void Activate(Setting s)
        {
            switch (s.Kind)
            {
                case Kind.Bool: Catalog.Nudge(s, 1, 1); break;
                case Kind.Choice: Catalog.Nudge(s, 1, 1); break;
                case Kind.Number:
                    if (In.PadUsed) { _edit = Edit.NumPad; _buf = ""; _padSel = 10; Ui.Show(_pad, true); }
                    else { _edit = Edit.Typing; _buf = ""; }
                    break;
                case Kind.KeyBind: _edit = Edit.KeyCapture; break;
                case Kind.Text: if (!In.PadUsed) { _edit = Edit.Typing; _buf = Convert.ToString(s.Entry.BoxedValue, CultureInfo.InvariantCulture) ?? ""; } break;
                case Kind.Action: RunAction(s); break;
            }
            _dirty = true;
        }

        internal void RunAction(Setting s)
        {
            string result;
            try { result = s.Run?.Invoke(); }
            catch (Exception e) { result = "failed: " + e.Message; }
            if (!string.IsNullOrEmpty(result)) Toast?.Invoke(s.Module.Guid, s.Label, result, "info");
        }

        private void ToggleFav(Setting s)
        {
            if (s.Kind == Kind.Text || s.Kind == Kind.KeyBind || s.Kind == Kind.ReadOnly)
            {
                Toast?.Invoke(Plugin.Guid, "Can't add to the quick menu", "Only switches, numbers, lists and buttons fit there", "warn");
                return;
            }
            bool added = Favs.Toggle(s.Id);
            Toast?.Invoke(Plugin.Guid, added ? "Added to the quick menu" : "Removed from the quick menu", s.Label, added ? "good" : "info");
            if (FavesTab) BuildRows();
            _dirty = true;
        }

        // ------------------------------------------------------------------ editing

        private void TickTyping()
        {
            var s = CurSetting;
            if (s == null) { CancelEdit(); return; }
            foreach (char c in In.Typed)
            {
                if (s.Kind == Kind.Number && !(char.IsDigit(c) || c == '-' || c == '.' || c == ',')) continue;
                if (_buf.Length < 40) _buf += c;
            }
            if (In.Back && _buf.Length > 0) _buf = _buf.Substring(0, _buf.Length - 1);
            if (In.Esc) { CancelEdit(); return; }
            if (In.Enter) { Commit(s); return; }
            _dirty = true;
        }

        private void TickNumPad()
        {
            var s = CurSetting;
            if (s == null) { CancelEdit(); return; }
            int col = _padSel < 12 ? _padSel % 3 : (_padSel == 12 ? 0 : 1);
            int row = _padSel < 12 ? _padSel / 3 : 4;
            if (In.Up) row = Math.Max(0, row - 1);
            if (In.Down) row = Math.Min(4, row + 1);
            if (In.Left) col = Math.Max(0, col - 1);
            if (In.Right) col = Math.Min(row == 4 ? 1 : 2, col + 1);
            if (row == 4 && col > 1) col = 1;
            _padSel = row == 4 ? (col == 0 ? 12 : 13) : row * 3 + col;
            if (In.A) PressPad(_padSel, s);
            else if (In.Y) PressPad(12, s);
            else if (In.X || In.Start) PressPad(13, s);
            else if (In.B) CancelEdit();
            _dirty = true;
        }

        private void PressPad(int key, Setting s)
        {
            string k = PadKeys[key];
            if (k == "DEL") { if (_buf.Length > 0) _buf = _buf.Substring(0, _buf.Length - 1); }
            else if (k == "OK") Commit(s);
            else if (_buf.Length < 12) _buf += k;
        }

        private void TickKeyCapture()
        {
            var s = CurSetting;
            if (s == null) { CancelEdit(); return; }
            var key = In.AnyKey();
            if (key == Key.None) { if (In.B && In.PadUsed) CancelEdit(); return; }
            if (key == Key.Escape) { CancelEdit(); return; }
            if (key == Key.Delete || key == Key.Backspace) key = Key.None;
            if (s.KeyIsString) Catalog.Write(s, key.ToString());
            else if (s.Type == typeof(Key)) Catalog.Write(s, key);
            else Catalog.Write(s, key.ToString());   // KeyCode and friends: same names for the common keys
            _edit = Edit.None;
            _dirty = true;
        }

        private void Commit(Setting s)
        {
            string text = _buf.Trim();
            _edit = Edit.None;
            Ui.Show(_pad, false);
            if (text.Length == 0) { _dirty = true; return; }
            if (s.Kind == Kind.Text) { Catalog.Write(s, text); _dirty = true; return; }
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double shown))
            {
                Toast?.Invoke(Plugin.Guid, "Not a number", text, "warn");
                _dirty = true;
                return;
            }
            if (Catalog.SetNumber(s, shown / s.Scale)) _flashUntil = Time.unscaledTime + 0.7f;   // clamped: flash the box red
            _dirty = true;
        }

        private void CancelEdit()
        {
            _edit = Edit.None;
            _buf = "";
            Ui.Show(_pad, false);
            _dirty = true;
        }

        // ------------------------------------------------------------------ mouse

        private void TickMouse()
        {
            if (!In.Click && !In.RightClick && !In.MouseHeld && In.Wheel == 0f) { _dragRow = -1; return; }
            Vector2 p = In.Mouse;
            if (_dragRow >= 0 && In.MouseHeld && !In.Click) { DragSlider(_dragRow, p); return; }
            if (!In.MouseHeld) _dragRow = -1;
            if (In.Wheel != 0f)
            {
                _top = Mathf.Clamp(_top - Math.Sign(In.Wheel) * 2, 0, Math.Max(0, _items.Count - Rows));
                _dirty = true;
            }
            if (!In.Click && !In.RightClick) return;
            if (_edit == Edit.NumPad)
            {
                for (int i = 0; i < _padKeyRt.Count; i++)
                    if (Hit(_padKeyRt[i], p)) { _padSel = i; PressPad(i, CurSetting); _dirty = true; return; }
                return;
            }
            for (int i = 0; i < _tabRt.Count; i++)
                if (In.Click && Hit(_tabRt[i], p)) { if (i != _tab) { SwitchTab(i - _tab); } return; }
            for (int i = 0; i < Cards; i++)
            {
                if (!_cards[i].Rt.gameObject.activeSelf || !Hit(_cards[i].Rt, p)) continue;
                int idx = _modTop + i;
                if (idx != _mod) { _mod = idx; OnModuleChanged(); }
                _focus = Focus.Settings;
                if (!Selectable(_sel)) _sel = NextSelectable(-1, 1);
                _dirty = true;
                return;
            }
            for (int i = 0; i < Rows; i++)
            {
                int idx = _top + i;
                if (idx >= _items.Count || !Hit(_rows[i].Rt, p)) continue;
                if (!Selectable(idx)) return;
                if (_edit != Edit.None) CancelEdit();
                _focus = Focus.Settings;
                _sel = idx;
                _dirty = true;
                var item = _items[idx];
                if (item.AdvModule != null) { ToggleAdvanced(item.AdvModule); return; }
                var s = item.S;
                if (In.RightClick) { ToggleFav(s); return; }
                float x = LocalX(_rows[i].Rt, p);
                switch (s.Kind)
                {
                    case Kind.Bool: Catalog.Nudge(s, 1, 1); break;
                    case Kind.Choice: if (x > 430) Catalog.Nudge(s, x < 430 + (SetW - 458) / 2f ? -1 : 1, 1); break;
                    case Kind.Number:
                        if (x >= 748) { _edit = Edit.Typing; _buf = ""; }
                        else if (s.HasRange && x >= 510 && x <= 740 && !s.AtGame) { _dragRow = i; DragSlider(i, p); }
                        else if (s.HasGame && x >= 410 && x < 510) Catalog.ToggleGame(s);
                        break;
                    case Kind.KeyBind: if (x >= SetW - 200) _edit = Edit.KeyCapture; break;
                    case Kind.Text: if (x >= 430) { _edit = Edit.Typing; _buf = Convert.ToString(s.Entry.BoxedValue, CultureInfo.InvariantCulture) ?? ""; } break;
                    case Kind.Action: if (x >= SetW - 230) RunAction(s); break;
                }
                return;
            }
        }

        private void DragSlider(int rowUi, Vector2 p)
        {
            int idx = _top + rowUi;
            if (idx >= _items.Count) return;
            var s = _items[idx].S;
            if (s == null || s.Kind != Kind.Number || !s.HasRange) return;
            float t = Mathf.Clamp01((LocalX(_rows[rowUi].Rt, p) - 520f) / 210f);
            double v = s.Min + t * (s.Max - s.Min);
            v = Math.Round(v / s.Step) * s.Step;
            Catalog.SetNumber(s, v);
            _dirty = true;
        }

        private static bool Hit(RectTransform rt, Vector2 screen) =>
            rt != null && rt.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rt, screen, null);

        private static float LocalX(RectTransform rt, Vector2 screen)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screen, null, out Vector2 local);
            return local.x;   // pivot is the top-left corner
        }

        // ------------------------------------------------------------------ drawing

        private void Redraw()
        {
            _dirty = false;
            for (int i = 0; i < _tabBg.Count; i++)
            {
                bool on = i == _tab && _query.Length == 0;
                _tabBg[i].color = on ? new Color(0.11f, 0.4f, 0.66f, 1f) : new Color(0.06f, 0.094f, 0.24f, 0.92f);
                _tabEdge[i].color = on ? Pal.Cyan : Pal.Edge;
                _tabTxt[i].Color(on ? Color.white : Pal.Dim);
            }
            _searchChip.Set(_query.Length > 0 ? $"<color=#ffffff>SEARCH:</color> {_query.ToUpperInvariant()}_"
                                              : In.PadUsed ? "LB / RB  TABS" : "TYPE TO SEARCH");
            DrawCards();
            DrawRows();
            DrawInfo();
            DrawPad();
            DrawPrompts();
        }

        private void DrawCards()
        {
            bool faves = FavesTab, search = _query.Length > 0;
            int count = search || faves ? 1 : _tabMods.Count;
            for (int i = 0; i < Cards; i++)
            {
                var c = _cards[i];
                int idx = _modTop + i;
                bool show = idx < count;
                Ui.Show(c.Rt, show);
                if (!show) continue;
                bool sel = idx == _mod || search;
                c.Bg.color = sel ? (_focus == Focus.Modules ? new Color(0.1f, 0.3f, 0.52f, 0.95f) : new Color(0.09f, 0.2f, 0.4f, 0.95f)) : Pal.Card;
                c.Edge.color = sel ? Pal.Cyan : Pal.Edge;
                Ui.Show(c.Bar, sel);
                if (search)
                {
                    c.Name.Set("SEARCH");
                    int matches = 0;
                    foreach (var x in _items) if (x.S != null) matches++;
                    c.Status.Set($"{matches} settings match \"{_query}\"");
                    Ui.Show(c.PillBg, false); c.Version.Set("");
                    continue;
                }
                if (faves)
                {
                    c.Name.Set("QUICK MENU");
                    c.Status.Set($"{Favs.List().Count} rows. LB+RB or ` opens it while driving.");
                    Ui.Show(c.PillBg, false); c.Version.Set("");
                    continue;
                }
                var m = _tabMods[idx];
                c.Name.Set(m.Name);
                string status = "";
                if (m.Status != null) { try { status = m.Status() ?? ""; } catch { status = ""; } }
                if (status.Length == 0) status = $"{CountSettings(m.Settings, basicOnly: true)} settings";
                c.Status.Set(status);
                c.Version.Set("v" + m.Version);
                if (m.Enabled != null)
                {
                    bool on = (bool)m.Enabled.Entry.BoxedValue;
                    Ui.Show(c.PillBg, true);
                    c.PillBg.color = on ? Pal.Green : new Color(0.23f, 0.27f, 0.4f);
                    c.Pill.Set(on ? "ON" : "OFF");
                    c.Pill.Color(on ? Pal.Ink : new Color(0.66f, 0.71f, 0.84f));
                }
                else Ui.Show(c.PillBg, false);
            }
            if (count == 0)
            {
                Ui.Show(_cards[0].Rt, true);
                _cards[0].Name.Set("NOTHING HERE");
                _cards[0].Status.Set("No installed mod belongs to this tab.");
                Ui.Show(_cards[0].PillBg, false); _cards[0].Version.Set("");
                Ui.Show(_cards[0].Bar, false);
            }
        }

        private void DrawRows()
        {
            int selectable = 0;
            foreach (var x in _items) if (x.S != null || x.AdvModule != null) selectable++;
            int pos = 0;
            for (int i = 0; i <= _sel && i < _items.Count; i++) if (Selectable(i)) pos++;
            string title = _query.Length > 0 ? "SEARCH RESULTS" : FavesTab ? "QUICK MENU" : CurModule != null ? CurModule.Name + "  ›  SETTINGS" : "";
            _setTitle.Set(title);
            _setCount.Set(selectable > 0 ? $"{Math.Max(pos, 1)} / {selectable}" : "");
            for (int r = 0; r < Rows; r++)
            {
                var ui = _rows[r];
                int idx = _top + r;
                bool show = idx < _items.Count;
                Ui.Show(ui.Rt, show);
                if (!show) continue;
                var item = _items[idx];
                bool sel = idx == _sel && _focus == Focus.Settings;
                HideControls(ui);
                ui.Bg.color = sel ? Pal.RowSel : item.Group != null ? Pal.Group : Color.clear;
                Ui.Show(ui.Bar, sel);
                if (item.Group != null)
                {
                    ui.Group.Show(true); ui.Label.Show(false);
                    ui.Group.Set(item.Group);
                    ui.Group.Color(Pal.Cyan);
                    continue;
                }
                if (item.AdvModule != null)
                {
                    ui.Group.Show(true); ui.Label.Show(false);
                    bool open = _advOpen.Contains(item.AdvModule.Guid);
                    ui.Group.Set($"ADVANCED ({item.AdvModule.AdvancedCount})   {(open ? "v  HIDE" : ">  SHOW")}");
                    ui.Group.Color(sel ? Color.white : Pal.Dim);
                    continue;
                }
                ui.Group.Show(false); ui.Label.Show(true);
                var s = item.S;
                string label = s.Label;
                if (_query.Length > 0 && s.Advanced) label += "  <color=#8ea2c9><size=15>(advanced)</size></color>";
                if (FavesTab) label = $"{label}  <color=#8ea2c9><size=15>{s.Module.Name}</size></color>";
                ui.Label.Set(label);
                Ui.Show(ui.Dot, s.Changed);
                DrawControl(ui, s, sel);
            }
        }

        private static void HideControls(RowUi ui)
        {
            Ui.Show(ui.Dot, false); Ui.Show(ui.ChipBg, false); Ui.Show(ui.Track, false); Ui.Show(ui.Thumb, false);
            Ui.Show(ui.BoxBg, false); Ui.Show(ui.TogTrack, false); Ui.Show(ui.KeyBg, false); Ui.Show(ui.BtnBg, false);
            ui.Choice.Show(false); ui.Value.Show(false);
        }

        private void DrawControl(RowUi ui, Setting s, bool sel)
        {
            switch (s.Kind)
            {
                case Kind.Bool:
                {
                    bool on = (bool)s.Entry.BoxedValue;
                    Ui.Show(ui.TogTrack, true);
                    ui.TogTrack.color = on ? Pal.Green : new Color(0.23f, 0.27f, 0.4f);
                    ui.TogKnob.rectTransform.anchoredPosition = new Vector2(on ? 42 : 4, -4);
                    break;
                }
                case Kind.Choice:
                    ui.Choice.Show(true);
                    string v = Catalog.Show(s).ToUpperInvariant();
                    ui.Choice.Set(sel ? $"<color=#29c6ff><noparse><</noparse></color>  {v}  <color=#29c6ff><noparse>></noparse></color>" : v);
                    break;
                case Kind.Number:
                {
                    bool editing = sel && _edit != Edit.None;
                    if (s.HasGame)
                    {
                        Ui.Show(ui.ChipBg, true);
                        ui.ChipBg.color = s.AtGame ? Pal.Orange : new Color(0.23f, 0.27f, 0.4f);
                        ui.Chip.Set(s.GameLabel);
                        ui.Chip.Color(s.AtGame ? Pal.Ink : new Color(0.62f, 0.69f, 0.84f));
                    }
                    if (s.HasRange)
                    {
                        Ui.Show(ui.Track, true); Ui.Show(ui.Thumb, true);
                        double shownV = s.AtGame && !double.IsNaN(s.GameShows) ? s.GameShows : s.Num;
                        float t = (float)Math.Max(0, Math.Min(1, (shownV - s.Min) / (s.Max - s.Min)));
                        ui.Fill.rectTransform.sizeDelta = new Vector2(210f * t, 8f);
                        ui.Fill.color = s.AtGame ? new Color(0.23f, 0.27f, 0.4f) : Pal.Cyan;
                        ui.Thumb.rectTransform.anchoredPosition = new Vector2(520f + 210f * t - 8f, -(RowH / 2 - 14));
                        ui.Thumb.color = s.AtGame ? new Color(0.37f, 0.42f, 0.54f) : Color.white;
                    }
                    Ui.Show(ui.BoxBg, true);
                    bool flash = sel && Time.unscaledTime < _flashUntil;
                    ui.BoxEdge.color = flash ? Pal.Red : editing ? Pal.Orange : sel ? Pal.Cyan : new Color(0.17f, 0.35f, 0.54f, 1f);
                    if (editing) ui.Box.Set((_buf.Length > 0 ? _buf : "") + "<color=#ff9b21>|</color>");
                    else
                    {
                        string txt = s.AtGame ? (double.IsNaN(s.GameShows) ? s.GameLabel : Catalog.Fmt(s, s.GameShows)) : Catalog.Fmt(s, s.Num);
                        ui.Box.Set(s.Unit.Length > 0 && !s.AtGame ? $"{txt} <size=12><color=#8ea2c9>{s.Unit}</color></size>" : txt);
                    }
                    ui.Box.Color(s.AtGame && !editing ? Pal.Dim : Pal.Text);
                    break;
                }
                case Kind.KeyBind:
                    Ui.Show(ui.KeyBg, true);
                    bool capturing = sel && _edit == Edit.KeyCapture;
                    ui.KeyBg.color = capturing ? Pal.Orange : new Color(0.91f, 0.93f, 0.99f);
                    ui.Key.Set(capturing ? "PRESS A KEY" : Catalog.Show(s));
                    break;
                case Kind.Text:
                    ui.Value.Show(true);
                    bool typingText = sel && _edit == Edit.Typing;
                    ui.Value.Set(typingText ? _buf + "<color=#ff9b21>|</color>" : Catalog.Show(s));
                    ui.Value.Color(typingText ? Pal.Text : Pal.Dim);
                    break;
                case Kind.Action:
                    Ui.Show(ui.BtnBg, true);
                    ui.Btn.Set(sel ? (In.PadUsed ? "A  RUN" : "ENTER  RUN") : "RUN");
                    break;
                default:
                    ui.Value.Show(true);
                    ui.Value.Set(Catalog.Show(s));
                    ui.Value.Color(Pal.Dim);
                    break;
            }
        }

        private void DrawInfo()
        {
            var item = _focus == Focus.Settings && _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;
            var s = item?.S;
            var m = CurModule;
            for (int i = 0; i < 4; i++) { _kvKey[i].Set(""); _kvVal[i].Set(""); }
            if (s != null)
            {
                _infTitle.Set(s.Label.ToUpperInvariant());
                _infDesc.Set(s.Description);
                int k = 0;
                if (s.Kind == Kind.Action) { Kv(k++, "Button", "runs once"); }
                else
                {
                    Kv(k++, "Default", Catalog.DefaultText(s));
                    string range = Catalog.Range(s);
                    if (range.Length > 0) Kv(k++, "Range", range);
                    if (s.Applies.Length > 0) Kv(k++, "Applies", s.Applies);   // only when the plugin says (most apply at once, some later)
                }
                bool fav = Favs.Contains(s.Id);
                Kv(k, "Quick menu", fav ? "<color=#3be27d>in it</color> (X removes)" : "X adds it");
                _infStatus.Set($"<b>{s.Module.Name}</b>  v{s.Module.Version}\n{ModuleStatus(s.Module)}\n<color=#5f78a8>{s.Module.Guid}.cfg  ·  [{s.Entry?.Definition.Section}] {s.Entry?.Definition.Key}</color>");
            }
            else if (item?.AdvModule != null)
            {
                _infTitle.Set("ADVANCED");
                _infDesc.Set("Calibration values, logs and debug switches. The defaults are tuned; change these only if you know what they do.");
                _infStatus.Set($"<b>{item.AdvModule.Name}</b>\n{item.AdvModule.AdvancedCount} advanced settings");
            }
            else if (FavesTab)
            {
                _infTitle.Set("QUICK MENU");
                _infDesc.Set("The rows of the quick menu you can open while driving (LB+RB on a controller, ` on the keyboard). In any tab, X on a setting adds it here or removes it.");
                _infStatus.Set("The last row of the quick menu always opens this hub.");
            }
            else if (_query.Length > 0)
            {
                _infTitle.Set("SEARCH");
                _infDesc.Set("Keep typing to narrow it down. Backspace deletes a letter, Esc clears the search.");
                _infStatus.Set("");
            }
            else if (m != null)
            {
                _infTitle.Set(m.Name);
                _infDesc.Set($"{CountSettings(m.Settings, basicOnly: false)} settings{(HasAction(m.Settings) ? " and buttons" : "")}. " +
                             (In.PadUsed ? "A or right opens them; X switches the mod on or off." : "Enter or right opens them; click a card to jump in."));
                _infStatus.Set($"<b>{m.Name}</b>  v{m.Version}\n{ModuleStatus(m)}");
            }
            else { _infTitle.Set(""); _infDesc.Set(""); _infStatus.Set(""); }
        }

        private void Kv(int i, string k, string v) { _kvKey[i].Set(k); _kvVal[i].Set(v); }

        // plain loops in the draw path (no LINQ delegates on every redraw)
        private static int CountSettings(List<Setting> list, bool basicOnly)
        {
            int n = 0;
            foreach (var x in list) if (x.Entry != null && (!basicOnly || !x.Advanced)) n++;
            return n;
        }

        private static bool HasAction(List<Setting> list)
        {
            foreach (var x in list) if (x.Kind == Kind.Action) return true;
            return false;
        }

        private static string ModuleStatus(Module m)
        {
            if (m.Status == null) return m.Enabled != null ? ((bool)m.Enabled.Entry.BoxedValue ? "on" : "off") : "";
            try { return m.Status() ?? ""; } catch (Exception e) { return "status unavailable: " + e.Message; }
        }

        private void DrawPad()
        {
            bool on = _edit == Edit.NumPad;
            Ui.Show(_pad, on);
            if (!on) return;
            var s = CurSetting;
            _padTitle.Set($"TYPE A VALUE  ·  {(s != null ? Catalog.Range(s).ToUpperInvariant() : "")}");
            _padDisplay.Set((_buf.Length > 0 ? _buf : "") + "<color=#ff9b21>|</color>" + (s != null && s.Unit.Length > 0 ? $" <size=18><color=#8ea2c9>{s.Unit}</color></size>" : ""));
            for (int i = 0; i < _padKeyBg.Count; i++)
                _padKeyBg[i].color = i == _padSel ? new Color(0.11f, 0.4f, 0.66f, 1f) : i == 13 ? new Color(0.6f, 0.36f, 0.06f, 1f) : new Color(0.094f, 0.133f, 0.31f, 1f);
        }

        private void DrawPrompts()
        {
            var list = new List<(string g, string t)>(8);
            bool pad = In.PadUsed;
            if (_edit == Edit.NumPad) { list.Add(("+", "Pick")); list.Add(("A", "Press")); list.Add(("Y", "Delete")); list.Add(("X", "OK")); list.Add(("B", "Cancel")); }
            else if (_edit == Edit.Typing) { list.Add(("ENTER", "OK")); list.Add(("ESC", "Cancel")); list.Add(("BKSP", "Delete")); }
            else if (_edit == Edit.KeyCapture) { list.Add(("KEY", "Bind it")); list.Add(("DEL", "No key")); list.Add(("ESC", "Cancel")); }
            else if (_focus == Focus.Modules)
            {
                list.Add(pad ? ("A", "Open") : ("ENTER", "Open"));
                if (CurModule?.Enabled != null && pad) list.Add(("X", "On / off"));
                list.Add(pad ? ("LB RB", "Tabs") : ("TAB", "Tabs"));
                if (!pad) list.Add(("A-Z", "Search"));
                list.Add(pad ? ("B", "Close") : ("ESC", "Close"));
            }
            else
            {
                var s = CurSetting;
                if (s != null && (s.Kind == Kind.Number || s.Kind == Kind.Choice || s.Kind == Kind.Bool)) list.Add((pad ? "<>" : "<- ->", "Adjust"));
                if (s != null)
                {
                    string a = s.Kind == Kind.Number ? (pad ? "Number pad" : "Type a value") : s.Kind == Kind.KeyBind ? "Bind a key"
                             : s.Kind == Kind.Action ? "Run" : s.Kind == Kind.Bool ? "Switch" : s.Kind == Kind.Choice ? "Next" : s.Kind == Kind.Text ? "Edit" : "";
                    if (a.Length > 0) list.Add((pad ? "A" : "ENTER", a));
                    if (s.Entry != null) list.Add((pad ? "Y" : "DEL", "Reset"));
                    list.Add((pad ? "X" : "R-CLICK", Favs.Contains(s.Id) ? "Remove from Quick" : "Add to Quick"));
                }
                list.Add(pad ? ("B", "Back") : ("ESC", "Back"));
            }
            float x = 70f;
            for (int i = 0; i < _glyph.Count; i++)
            {
                bool show = i < list.Count;
                Ui.Show(_glyphBg[i], show);
                _prompt[i].Show(show);
                if (!show) continue;
                var (g, t) = list[i];
                float gw = Math.Max(40f, 14f + g.Length * 13f);
                Ui.Place(_glyphBg[i].rectTransform, x, 1012, gw, 30);
                _glyph[i].T.rectTransform.sizeDelta = new Vector2(gw, 30);
                Color gc = g == "A" ? Pal.Green : g == "B" ? new Color(1f, 0.3f, 0.42f) : g == "X" ? new Color(0.23f, 0.63f, 1f) : g == "Y" ? Pal.Gold : new Color(0.91f, 0.93f, 0.99f);
                _glyphBg[i].color = gc;
                _glyph[i].Set(g);
                Ui.Place(_prompt[i].T.rectTransform, x + gw + 10, 1006, 300, 42);
                _prompt[i].Set(t);
                x += gw + 10 + Math.Max(60f, t.Length * 11f) + 34f;
            }
        }
    }

    /// <summary>Quick-menu rows (ids), kept in the hub's own config.</summary>
    internal static class Favs
    {
        private static string _raw;
        private static readonly List<string> Cache = new List<string>();

        internal static List<string> List()
        {
            string raw = Plugin.QuickItems.Value ?? "";
            if (raw != _raw)
            {
                _raw = raw;
                Cache.Clear();
                foreach (var p in raw.Split(';')) { var t = p.Trim(); if (t.Length > 0 && !Cache.Contains(t)) Cache.Add(t); }
            }
            return Cache;
        }

        internal static bool Contains(string id) => List().Contains(id);

        /// <summary>Adds or removes; returns true if it is in the list now.</summary>
        internal static bool Toggle(string id)
        {
            var l = new List<string>(List());
            bool added;
            if (l.Remove(id)) added = false;
            else { l.Add(id); added = true; }
            Plugin.QuickItems.Value = string.Join(";", l);
            return added;
        }
    }
}
