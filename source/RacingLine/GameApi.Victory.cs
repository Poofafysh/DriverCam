using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Manager;
using Game.Runtime.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RacingLine
{
    /// <summary>
    /// The RACING LINE row on the end-of-run Victory screen (run totals per category).
    ///
    /// VictoryScreenScoreController has a fixed row per built-in category (each a VictoryScreenScoreItem: value text +
    /// "NEW RECORD" badge; the label and icon are part of the layout), so the game never makes a row for us. When the
    /// screen becomes active we clone the Near Miss row, put the clone right after it, relabel it, swap the icon, and fill it
    /// with SetupItem(value, isNewRecord). It reads no list and no coroutine waits on it, but like the per-race row it's
    /// only removed once the screen has closed. No Harmony: we watch activeInHierarchy.
    /// </summary>
    internal static partial class GameApi
    {
        internal static bool VictoryOk { get; private set; }

        private static MonoBehaviour _victory;        // VictoryScreenScoreController (inactive until a run ends)
        private static GameObject _victoryRow;        // our clone
        private static bool _victoryWasActive;
        private static float _nextVictorySearch;

        internal struct VictoryData { public bool Show; public string Value; public bool NewRecord; public Sprite Icon; }

        internal static void CheckVictory(System.Reflection.Assembly asm, System.Collections.Generic.List<string> missing)
        {
            VictoryOk = Has(asm, "Game.Runtime.UI.VictoryScreenScoreController", missing, "nearMissScoreItem")
                     && Has(asm, "Game.Runtime.UI.VictoryScreenScoreItem", missing, "SetupItem", "scoreValueText", "newRecordObject");
        }

        /// <summary>Call every frame. Adds the row when the Victory screen opens, removes it once it has closed. Only when VictoryOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void VictoryTick(Func<VictoryData> data, Action<string> log)
        {
            if (_victory == null)
            {
                _victoryWasActive = false; _victoryRow = null;
                if (Time.unscaledTime < _nextVictorySearch) return;
                _nextVictorySearch = Time.unscaledTime + 3f;
                foreach (var c in Resources.FindObjectsOfTypeAll<VictoryScreenScoreController>())
                    if (c != null && c.gameObject.scene.IsValid()) { _victory = c; break; }   // skip prefab assets
                if (_victory == null) return;
            }
            bool active = _victory.gameObject.activeInHierarchy;
            if (active && !_victoryWasActive) AddVictoryRow((VictoryScreenScoreController)_victory, data(), log);
            else if (!active && _victoryWasActive) RemoveVictoryRow();
            _victoryWasActive = active;
        }

        /// <summary>Removes a leftover row once the screen is closed (safe to call every frame, also when the plugin is off).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CleanupVictoryRowWhenClosed()
        {
            if (_victoryRow == null) return;
            if (_victory != null && _victory.gameObject.activeInHierarchy) return;
            RemoveVictoryRow();
            _victoryWasActive = false;
        }

        private static void AddVictoryRow(VictoryScreenScoreController vc, VictoryData d, Action<string> log)
        {
            if (_victoryRow != null || !d.Show) return;
            var nearMiss = vc.nearMissScoreItem;
            if (nearMiss == null) return;
            var srcItem = nearMiss.gameObject;
            var cloneObj = UnityEngine.Object.Instantiate((UnityEngine.Object)srcItem, srcItem.transform.parent);
            var clone = cloneObj == null ? null : cloneObj.TryCast<GameObject>();
            if (clone == null) { if (cloneObj != null) UnityEngine.Object.Destroy(cloneObj); return; }
            _victoryRow = clone;   // owned from now on: any failure below destroys it
            try
            {
                clone.name = "RacingLine Victory Item";
                clone.transform.SetSiblingIndex(srcItem.transform.GetSiblingIndex() + 1);   // right after Near Miss
                var item = clone.GetComponent<VictoryScreenScoreItem>();
                if (item == null) throw new InvalidOperationException("cloned row has no VictoryScreenScoreItem");
                var valueText = item.scoreValueText;

                // the label is the row's text that is neither the value nor inside the NEW RECORD badge (works in any
                // language); the icon is the image showing the near-miss sprite
                var badge = item.newRecordObject;
                foreach (var t in clone.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (t == null || valueText == null || t.Pointer == valueText.Pointer || string.IsNullOrEmpty(t.text) || !t.gameObject.activeInHierarchy) continue;
                    if (badge != null && t.transform.IsChildOf(badge.transform)) continue;
                    t.text = NativeName;
                }
                if (d.Icon != null)
                    foreach (var img in clone.GetComponentsInChildren<Image>(true))
                    {
                        var sp = img == null ? null : img.sprite;
                        if (sp != null && sp.name != null && sp.name.ToLowerInvariant().Contains("nearmiss")) { img.sprite = d.Icon; img.preserveAspect = true; }
                    }
                item.SetupItem(d.Value, d.NewRecord);
            }
            catch
            {
                UnityEngine.Object.Destroy(clone);
                _victoryRow = null;
                throw;
            }
            log($"[RacingLine] victory row added: {d.Value}{(d.NewRecord ? " (new record)" : "")}");
        }

        private static void RemoveVictoryRow()
        {
            if (_victoryRow != null) UnityEngine.Object.Destroy(_victoryRow);
            _victoryRow = null;
        }

        /// <summary>The run's position: stage and race index (both 0 = the first race of a new run). Only when WidthOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool RunPosition(out int stage, out int race)
        {
            stage = -1; race = -1;
            if (_world == null) RoadWidth();   // finds the world manager (throttled)
            if (_world == null) return false;
            var w = (RunWorldManager)_world;
            stage = w.currentStageIndex; race = w.currentRaceIndex;
            return true;
        }
    }
}
