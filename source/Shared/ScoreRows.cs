using System;
using System.Runtime.CompilerServices;
using Game.Runtime.UI;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RogueShared
{
    /// <summary>
    /// A mod score category's rows on the game's two score screens (linked as source into RacingLine and Police; a change
    /// here needs a bump of both). One instance per category. Rows are UI only: they never add points (the game's TOTAL
    /// already includes a native category's points).
    ///
    /// - Per-race results screen (RankingStatsController): when the screen becomes active, the Top Speed row's 'Stat Area'
    ///   is cloned (inside the screen's 0.85 s intro delay), placed after Near Miss and after any mod row of a lower rank,
    ///   added to rankingStatList after the built-in rows and after any mod row of a lower rank (index 5 or later, never
    ///   index 4: UpdateUI writes the Time row there; the list order is the count-up order) and filled with
    ///   RankingStatItem.Setup. The screen's own animation then counts it like the others. Removed (list + object) only
    ///   once the screen has closed: its animation coroutine waits on every row's tween callbacks, so removing a row
    ///   mid-animation would leave the player without a Continue button. Accepted exception: DestroyAll (plugin unload,
    ///   i.e. game quit or a HotReload unload) removes it at once, even mid-animation.
    /// - End-of-run Victory screen (VictoryScreenPanel): one fixed VictoryScreenScoreItem per built-in category, so the
    ///   Near Miss item is cloned, relabelled, given the mod icon and filled with SetupItem(value, isNewRecord).
    ///   "Showing" = the panel's Results Window is active: VictoryScreenPanel.Show and ResetScreens switch it off,
    ///   FadeToResults switches it on after the scores are written, and the whole Window is off while the panel is hidden.
    ///   (The score controller itself sits outside the toggled Window and stays active, which is why watching it never
    ///   saw the screen open.) Only a real opening counts: when the panel is first found, or VictoryTick was not called
    ///   on the previous frame, the window's current state is the baseline, so a window that is already up then gets no
    ///   row and data() (which counts the race and may write the record) is not called.
    ///
    /// The screens' GameObjects are fetched once when the screen is found (no Il2Cpp wrappers made every frame).
    ///
    /// Order: rows are named "Stat Area Mod &lt;rank&gt; &lt;id&gt;" / "Victory Mod &lt;rank&gt; &lt;id&gt;" and each one goes after the
    /// rows of a lower rank, so two plugins (separate copies of this class) give the same order whichever Update runs
    /// first. No Harmony: activeInHierarchy is polled; the results screen is searched at most every 3 s, the Victory
    /// screen at once when a race's results screen opens, then every 3 s for 90 s after it was last up, else every 10 s.
    ///
    /// Every method that touches a game type is NoInlining and only called once the plugin's game check (Check) passed.
    /// </summary>
    internal sealed class ModScoreRows
    {
        internal struct ResultsData
        {
            public bool Skip;          // no row this race (the plugin had no category)
            public string Amount;      // the amount column (time, count)
            public double Score;
            public int Coins;
            public bool Counts;        // false = display only (no score / coins shown)
            public Sprite Icon;
        }

        internal struct VictoryData
        {
            public bool Show;
            public string Why;         // when !Show: why (logged once per victory)
            public string Value;
            public bool NewRecord;
            public Sprite Icon;
        }

        private const string ResultsPrefix = "Stat Area Mod ";
        private const string VictoryPrefix = "Victory Mod ";

        private readonly string _id, _label;
        private readonly int _rank;
        private readonly Action<string> _log;

        // untyped (MonoBehaviour), so this class loads even if a game update removed a type
        private MonoBehaviour _ranking;     // RankingStatsController (inactive in the UI until a race ends)
        private GameObject _rankingGo;      // its GameObject, fetched once when found
        private MonoBehaviour _ourRow;      // our RankingStatItem
        private GameObject _ourRowRoot;     // what we cloned and must destroy
        private bool _rankingWasActive;
        private float _nextRankingSearch;

        private MonoBehaviour _victoryPanel;   // VictoryScreenPanel
        private GameObject _victoryWindow;     // its Results Window (resultsWindowCanvasGroup's GameObject), fetched once when found
        private GameObject _victoryRow;        // our clone
        private MonoBehaviour _victoryItem;    // its VictoryScreenScoreItem
        private bool _victoryWasShowing, _panelLogged;
        private float _nextVictorySearch;
        private float _resultsSeenAt = -1000f;  // Time.unscaledTime the results screen was last active (a race just ended)
        private int _victoryFrame = -10;       // Time.frameCount of the last VictoryTick

        internal ModScoreRows(string id, string label, int rank, Action<string> log)
        {
            _id = id; _label = label; _rank = rank;
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// The game check for both screens. has(typeName, members) looks the members up in the interop assembly and
        /// records anything missing.
        /// </summary>
        internal static void Check(Func<string, string[], bool> has, out bool resultsOk, out bool victoryOk)
        {
            resultsOk = has("Game.Runtime.UI.RankingStatsController", new[] { "rankingStatList" })
                     && has("Game.Runtime.UI.RankingStatItem", new[] { "Setup" });
            victoryOk = has("Game.Runtime.UI.VictoryScreenPanel", new[] { "resultsWindowCanvasGroup", "scoreController" })
                     && has("Game.Runtime.UI.VictoryScreenScoreController", new[] { "nearMissScoreItem" })
                     && has("Game.Runtime.UI.VictoryScreenScoreItem", new[] { "SetupItem", "scoreValueText", "newRecordObject" });
        }

        // ------------------------------------------------------------------ per-race results screen

        /// <summary>Call every frame. Adds the row when the results screen opens, removes it when it closes. Only when resultsOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void ResultsTick(Func<ResultsData> data)
        {
            if (_ranking == null)
            {
                _rankingWasActive = false; _ourRow = null; _ourRowRoot = null; _rankingGo = null;   // went with the old screen
                if (Time.unscaledTime < _nextRankingSearch) return;
                _nextRankingSearch = Time.unscaledTime + 3f;   // it exists from scene load, inactive; never search every frame
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<RankingStatsController>()))
                {
                    var c = o == null ? null : o.TryCast<RankingStatsController>();
                    if (c == null) continue;
                    var go = c.gameObject;
                    if (go != null && go.scene.IsValid()) { _ranking = c; _rankingGo = go; break; }   // skip prefab assets
                }
                if (_ranking == null) return;
            }

            bool active = _rankingGo != null && _rankingGo.activeInHierarchy;
            if (active)
            {
                if (!_rankingWasActive) _nextVictorySearch = 0f;   // a race ended: look for the Victory screen now
                _resultsSeenAt = Time.unscaledTime;
            }
            if (active && !_rankingWasActive) AddRow((RankingStatsController)_ranking, data());
            else if (!active && _rankingWasActive) RemoveRow((RankingStatsController)_ranking);
            _rankingWasActive = active;
        }

        private void AddRow(RankingStatsController rc, ResultsData d)
        {
            if (d.Skip) return;
            if (_ourRowRoot != null || _ourRow != null) return;   // never two rows; a leftover is removed once the screen closes
            var list = rc.rankingStatList;
            if (list == null || list.Count < 5) { _log($"results screen has fewer rows than expected: no {_label} row"); return; }
            var template = list[0];        // Top Speed: a provider row with amount, score and coins
            var nearMiss = list[3];
            if (template == null || nearMiss == null) return;

            // the item may sit inside a "Stat Area" container; clone the container so the layout matches
            Transform src = template.transform;
            if (src.parent != null && src.parent.name.StartsWith("Stat Area", StringComparison.OrdinalIgnoreCase)) src = src.parent;
            Transform anchor = nearMiss.transform;
            if (anchor.parent != null && anchor.parent.name.StartsWith("Stat Area", StringComparison.OrdinalIgnoreCase)) anchor = anchor.parent;

            var cloneObj = UnityEngine.Object.Instantiate((UnityEngine.Object)src.gameObject, src.parent);
            var clone = cloneObj == null ? null : cloneObj.TryCast<GameObject>();
            if (clone == null) { if (cloneObj != null) UnityEngine.Object.Destroy(cloneObj); return; }
            _ourRowRoot = clone;            // owned from this moment: any failure below destroys it
            try
            {
                clone.name = $"{ResultsPrefix}{_rank} {_id}";
                Place(clone.transform, anchor, ResultsPrefix);   // on screen: after Near Miss (and lower-ranked mod rows), before Time

                var item = clone.GetComponentInChildren<RankingStatItem>(true);
                if (item == null) throw new InvalidOperationException("the cloned row has no RankingStatItem");

                var score = d.Counts ? new Il2CppSystem.Nullable<double>(d.Score) : new Il2CppSystem.Nullable<double>();
                var coins = d.Counts ? new Il2CppSystem.Nullable<int>(d.Coins) : new Il2CppSystem.Nullable<int>();
                item.Setup(_label, d.Icon, d.Amount, score, coins, true);
                int at = ListSlot(list);    // index 5+: the screen's animation counts it like the others, in rank order
                if (at >= list.Count) list.Add(item);
                else list.Insert(at, item);
                _ourRow = item;
            }
            catch
            {
                // nothing of ours was added to rankingStatList yet (Add is the last step), so the half-built clone can go now
                UnityEngine.Object.Destroy(clone);
                _ourRowRoot = null; _ourRow = null;
                throw;
            }
            _log($"results row added: {d.Amount}, {(d.Counts ? $"{d.Score:0} pts, {d.Coins} coins" : "display only")}");
        }

        /// <summary>
        /// Where our item goes in rankingStatList: before the first mod row of a HIGHER rank, else at the end. Never
        /// before index 5 (the built-in rows, Time at index 4), so the count-up order matches the on-screen order
        /// whichever plugin adds its row first.
        /// </summary>
        private int ListSlot(Il2CppSystem.Collections.Generic.List<RankingStatItem> list)
        {
            int n = list.Count;
            for (int i = 5; i < n; i++)
            {
                var it = list[i];
                if (it == null) continue;
                var t = it.transform;
                int? r = RankOf(t.name, ResultsPrefix);
                if (r == null && t.parent != null) r = RankOf(t.parent.name, ResultsPrefix);   // the item inside our "Stat Area Mod" clone
                if (r is int rank && rank > _rank) return i;
            }
            return n;
        }

        private void RemoveRow(RankingStatsController rc)
        {
            if (_ourRow != null)
            {
                var list = rc == null ? null : rc.rankingStatList;
                if (list != null) list.Remove((RankingStatItem)_ourRow);
            }
            if (_ourRowRoot != null) UnityEngine.Object.Destroy(_ourRowRoot);
            _ourRow = null; _ourRowRoot = null;
        }

        /// <summary>Takes our results row out, but ONLY while the results screen is closed. Only when resultsOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CleanupResults(bool force)
        {
            if (_ourRowRoot == null && _ourRow == null) return;
            if (!force && _ranking != null && _rankingGo != null && _rankingGo.activeInHierarchy) return;   // still showing: wait
            RemoveRow(_ranking == null ? null : (RankingStatsController)_ranking);
            _rankingWasActive = false;
        }

        // ------------------------------------------------------------------ end-of-run Victory screen

        /// <summary>Call every frame. Adds (or refreshes) the row when the Results Window opens, removes it when it closes. Only when victoryOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void VictoryTick(Func<VictoryData> data)
        {
            int frame = Time.frameCount;
            bool continuous = _victoryFrame == frame - 1;   // called on the previous frame too: an edge seen now is real
            _victoryFrame = frame;
            if (_victoryPanel == null)
            {
                _victoryWasShowing = false; _victoryWindow = null;
                if (_victoryRow != null) UnityEngine.Object.Destroy(_victoryRow);   // normally gone with the panel already
                _victoryRow = null; _victoryItem = null;
                if (Time.unscaledTime < _nextVictorySearch) return;
                // the walk over every loaded object is cheap enough near a race end, not every 3 s through a whole race
                _nextVictorySearch = Time.unscaledTime + (Time.unscaledTime - _resultsSeenAt < 90f ? 3f : 10f);
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<VictoryScreenPanel>()))
                {
                    var p = o == null ? null : o.TryCast<VictoryScreenPanel>();
                    if (p != null && p.gameObject.scene.IsValid()) { _victoryPanel = p; break; }   // skip prefab assets
                }
                if (_victoryPanel == null) return;
                var cg = ((VictoryScreenPanel)_victoryPanel).resultsWindowCanvasGroup;
                _victoryWindow = cg == null ? null : cg.gameObject;
                if (!_panelLogged) { _panelLogged = true; LogPanel((VictoryScreenPanel)_victoryPanel); }
                continuous = false;   // just found: its current state is the baseline
            }

            bool showing = ResultsWindowShowing();
            if (!continuous)
            {
                // no edge from a stale state: a window already up when we (re)start watching gets no row, and data()
                // (which counts the race and may write the record) is not called for it
                if (showing && !_victoryWasShowing) _log("victory screen already open when first watched: no row this time");
                _victoryWasShowing = showing;
                return;
            }
            if (showing && !_victoryWasShowing) AddOrRefreshVictoryRow((VictoryScreenPanel)_victoryPanel, data());
            else if (!showing && _victoryWasShowing) RemoveVictoryRow();
            _victoryWasShowing = showing;
        }

        private bool ResultsWindowShowing() => _victoryWindow != null && _victoryWindow.activeInHierarchy;

        /// <summary>Once: which object holds the score controller and whether the results window was already up.</summary>
        private void LogPanel(VictoryScreenPanel panel)
        {
            var vc = panel.scoreController;
            string where = vc == null ? "no score controller" : $"score controller on '{PathOf(vc.transform)}' (active {vc.gameObject.activeInHierarchy})";
            _log($"victory screen found: {where}, results window active {ResultsWindowShowing()}");
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            int guard = 0;
            for (var p = t.parent; p != null && guard < 12; p = p.parent, guard++) path = p.name + "/" + path;
            return path;
        }

        private void AddOrRefreshVictoryRow(VictoryScreenPanel panel, VictoryData d)
        {
            if (!d.Show)
            {
                if (_victoryRow != null) RemoveVictoryRow();   // never leave a stale number up
                _log($"victory row skipped: {d.Why ?? "nothing to show"}");
                return;
            }
            if (_victoryRow != null && _victoryItem != null)
            {
                ((VictoryScreenScoreItem)_victoryItem).SetupItem(d.Value, d.NewRecord);   // still there from an earlier opening
                _log($"victory row refreshed: {d.Value}{(d.NewRecord ? " (new record)" : "")}");
                return;
            }
            if (_victoryRow != null) RemoveVictoryRow();   // half there (item lost): rebuild

            var vc = panel.scoreController;
            var nearMiss = vc == null ? null : vc.nearMissScoreItem;
            if (nearMiss == null) { _log($"victory row skipped: {(vc == null ? "no score controller" : "no Near Miss row to copy")}"); return; }
            var srcItem = nearMiss.gameObject;
            var cloneObj = UnityEngine.Object.Instantiate((UnityEngine.Object)srcItem, srcItem.transform.parent);
            var clone = cloneObj == null ? null : cloneObj.TryCast<GameObject>();
            if (clone == null) { if (cloneObj != null) UnityEngine.Object.Destroy(cloneObj); _log("victory row skipped: the Near Miss row could not be copied"); return; }
            _victoryRow = clone;   // owned from now on: any failure below destroys it
            try
            {
                clone.name = $"{VictoryPrefix}{_rank} {_id}";
                Place(clone.transform, srcItem.transform, VictoryPrefix);   // after Near Miss (and lower-ranked mod rows)
                var item = clone.GetComponent<VictoryScreenScoreItem>();
                if (item == null) throw new InvalidOperationException("cloned row has no VictoryScreenScoreItem");
                Relabel(clone, item);
                if (d.Icon != null)
                    foreach (var img in clone.GetComponentsInChildren<Image>(true))
                    {
                        var sp = img == null ? null : img.sprite;
                        if (sp != null && sp.name != null && sp.name.ToLowerInvariant().Contains("nearmiss")) { img.sprite = d.Icon; img.preserveAspect = true; }
                    }
                item.SetupItem(d.Value, d.NewRecord);
                _victoryItem = item;
            }
            catch
            {
                UnityEngine.Object.Destroy(clone);
                _victoryRow = null; _victoryItem = null;
                throw;
            }
            _log($"victory row added: {d.Value}{(d.NewRecord ? " (new record)" : "")}");
        }

        /// <summary>
        /// The label: the clone's text named "Title" if there is one; otherwise every text that is neither the value nor
        /// inside a NEW RECORD badge (works in any language). activeSelf, not activeInHierarchy: the row may be built
        /// while its window is still off.
        /// </summary>
        private void Relabel(GameObject clone, VictoryScreenScoreItem item)
        {
            var texts = clone.GetComponentsInChildren<TMP_Text>(true);
            foreach (var t in texts)
                if (t != null && t.gameObject.name == "Title") { t.text = _label; return; }

            var valueText = item.scoreValueText;
            var badge = item.newRecordObject;
            foreach (var t in texts)
            {
                if (t == null || string.IsNullOrEmpty(t.text) || !t.gameObject.activeSelf) continue;
                if (valueText != null && t.Pointer == valueText.Pointer) continue;
                if (badge != null && t.transform.IsChildOf(badge.transform)) continue;
                if (UnderNewRecord(t.transform, clone.transform)) continue;
                t.text = _label;
            }
        }

        private static bool UnderNewRecord(Transform t, Transform root)
        {
            for (var p = t; p != null; p = p.parent)
            {
                if (p.name != null && p.name.StartsWith("New Record", StringComparison.OrdinalIgnoreCase)) return true;
                if (p.Pointer == root.Pointer) break;
            }
            return false;
        }

        private void RemoveVictoryRow()
        {
            if (_victoryRow != null) UnityEngine.Object.Destroy(_victoryRow);
            _victoryRow = null; _victoryItem = null;
        }

        /// <summary>Removes our Victory row once the Results Window is closed (or the panel is gone). Only when victoryOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CleanupVictory(bool force)
        {
            if (_victoryRow == null) { _victoryItem = null; return; }
            if (!force && _victoryPanel != null && ResultsWindowShowing()) return;   // still showing: wait
            RemoveVictoryRow();
            _victoryWasShowing = false;
        }

        // ------------------------------------------------------------------ shared

        /// <summary>
        /// Puts row right after anchor, and after every sibling mod row (same prefix) of a lower rank, so the order is
        /// the same whichever plugin adds its row first.
        /// </summary>
        private void Place(Transform row, Transform anchor, string prefix)
        {
            var parent = row.parent;
            int after = anchor.GetSiblingIndex();
            if (parent != null)
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (c == null || c.Pointer == row.Pointer) continue;
                    if (RankOf(c.name, prefix) is int r && r < _rank) after = Math.Max(after, c.GetSiblingIndex());
                }
            // the clone was appended last, so every index up to 'after' is unchanged by taking it out
            row.SetSiblingIndex(after + 1);
        }

        private static int? RankOf(string name, string prefix)
        {
            if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)) return null;
            int end = name.IndexOf(' ', prefix.Length);
            string num = end < 0 ? name.Substring(prefix.Length) : name.Substring(prefix.Length, end - prefix.Length);
            return int.TryParse(num, out int r) ? r : (int?)null;
        }

        /// <summary>
        /// Removes rows whose screen has closed. Safe every frame, also while the plugin is off or broken; never throws.
        /// Pass the plugin's game-check results.
        /// </summary>
        internal void CleanupWhenClosed(bool resultsOk, bool victoryOk)
        {
            try { if (resultsOk) CleanupResults(false); } catch { /* the row goes with the scene at worst */ }
            try { if (victoryOk) CleanupVictory(false); } catch { /* the row goes with the scene at worst */ }
        }

        /// <summary>
        /// Plugin unload (game quit, HotReload unload): removes both rows now, whatever is showing. Accepted: if the
        /// results screen is mid-animation at that moment, its Continue button may not appear (only possible on an
        /// unload during that screen; at game quit nothing is left to continue). Never throws.
        /// </summary>
        internal void DestroyAll(bool resultsOk, bool victoryOk)
        {
            try { if (resultsOk) CleanupResults(true); } catch { /* shutting down */ }
            try { if (victoryOk) CleanupVictory(true); } catch { /* shutting down */ }
        }
    }
}
