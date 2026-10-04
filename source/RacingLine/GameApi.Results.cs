using System;
using System.Runtime.CompilerServices;
using Game.Runtime.UI;
using UnityEngine;

namespace RacingLine
{
    /// <summary>
    /// The Racing Line row on the end-of-race results screen (design doc: "UI and new icons", step 4).
    ///
    /// The screen's five rows are fixed in the layout and read the built-in providers directly, so the game never makes
    /// a row for us. When the screen becomes active, we clone the Top Speed row (inside the screen's 0.85 s intro delay),
    /// put it after Near Miss, append it to the END of rankingStatList (never index 4: UpdateUI writes the Time row there)
    /// and fill it with RankingStatItem.Setup. The screen's own animation then counts it into TOTAL like the others.
    /// When the screen closes, the row is taken out of the list and destroyed. No Harmony: we watch activeInHierarchy.
    /// </summary>
    internal static partial class GameApi
    {
        // untyped: see _world
        private static MonoBehaviour _ranking;     // RankingStatsController (lives inactive in the UI until a race ends)
        private static MonoBehaviour _ourRow;      // our RankingStatItem
        private static GameObject _ourRowRoot;     // what we cloned and must destroy
        private static bool _rankingWasActive;
        private static float _nextRankingSearch;

        internal struct ResultsData { public string Amount; public double Score; public int Coins; public bool Counts; public Sprite Icon; }

        /// <summary>Call every frame. Adds the row when the results screen opens, removes it when it closes. Only when ResultsOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ResultsTick(Func<ResultsData> data, Action<string> log)
        {
            if (_ranking == null)
            {
                _rankingWasActive = false; _ourRow = null; _ourRowRoot = null;
                if (Time.unscaledTime < _nextRankingSearch) return;
                _nextRankingSearch = Time.unscaledTime + 3f;   // it exists from scene load, inactive; never search every frame
                foreach (var c in Resources.FindObjectsOfTypeAll<RankingStatsController>())
                    if (c != null && c.gameObject.scene.IsValid()) { _ranking = c; break; }   // skip prefab assets
                if (_ranking == null) return;
            }

            bool active = _ranking.gameObject.activeInHierarchy;
            if (active && !_rankingWasActive) AddRow((RankingStatsController)_ranking, data(), log);
            else if (!active && _rankingWasActive) RemoveRow((RankingStatsController)_ranking);
            _rankingWasActive = active;
        }

        /// <summary>
        /// Takes our row out, but ONLY while the results screen is closed: its animation coroutine waits on every row's
        /// tween callback (once to fade in, once to count up), so removing a row mid-animation would leave the player
        /// stuck without a Continue button. Safe to call every frame, even when the plugin is off or broken.
        /// Only when ResultsOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CleanupRowWhenClosed()
        {
            if (_ourRowRoot == null && _ourRow == null) return;
            if (_ranking != null && _ranking.gameObject.activeInHierarchy) return;   // still showing: wait
            RemoveRow(_ranking == null ? null : (RankingStatsController)_ranking);
            _rankingWasActive = false;
        }

        private static void AddRow(RankingStatsController rc, ResultsData d, Action<string> log)
        {
            if (_ourRowRoot != null || _ourRow != null) return;   // never two rows; a leftover is removed once the screen closes
            var list = rc.rankingStatList;
            if (list == null || list.Count < 5) { log("[RacingLine] results screen has fewer rows than expected: no Racing Line row"); return; }
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
                clone.name = "Stat Area RacingLine";
                clone.transform.SetSiblingIndex(anchor.GetSiblingIndex() + 1);   // on screen: after Near Miss, before Time

                var item = clone.GetComponentInChildren<RankingStatItem>(true);
                if (item == null) throw new InvalidOperationException("the cloned row has no RankingStatItem");

                var score = d.Counts ? new Il2CppSystem.Nullable<double>(d.Score) : new Il2CppSystem.Nullable<double>();
                var coins = d.Counts ? new Il2CppSystem.Nullable<int>(d.Coins) : new Il2CppSystem.Nullable<int>();
                item.Setup(NativeName, d.Icon, d.Amount, score, coins, true);
                list.Add(item);             // appended last (index 5+): the screen's animation sums it into TOTAL and earnings
                _ourRow = item;
            }
            catch
            {
                // nothing of ours was added to rankingStatList yet (Add is the last step), so the half-built clone can go now
                UnityEngine.Object.Destroy(clone);
                _ourRowRoot = null; _ourRow = null;
                throw;
            }
            log($"[RacingLine] results row added: {d.Amount}, {(d.Counts ? $"{d.Score:0} pts, {d.Coins} coins" : "display only")}");
        }

        private static void RemoveRow(RankingStatsController rc)
        {
            if (_ourRow != null)
            {
                var list = rc == null ? null : rc.rankingStatList;
                if (list != null) list.Remove((RankingStatItem)_ourRow);
            }
            if (_ourRowRoot != null) UnityEngine.Object.Destroy(_ourRowRoot);
            _ourRow = null; _ourRowRoot = null;
        }

    }
}
