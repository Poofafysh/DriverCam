using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game.Runtime.UI;
using Game.Runtime.UI.Components;

namespace Sandbox
{
    /// <summary>
    /// The SANDBOX button: a copy of the main menu's Singleplayer MainMenuButton, placed right under it (same parent, so
    /// it shows and hides with the play section), with its own label and description and the d-pad navigation spliced in
    /// (RogueHub's MODS-button pattern). Clicking it marks the next run as a sandbox run (State.Pending) and then runs
    /// the panel's own OnSingleplayerButton, so the player picks a car exactly as for a normal run.
    /// Added in a MainMenuPanel.Awake postfix (0x82F780), only when every guard is installed (Plugin.AllOk).
    /// </summary>
    internal static class MenuButton
    {
        internal const string Description =
            "Sandbox run: every card free, locked mods offered, 20 mod slots, longer roads (Mod settings). " +
            "Never uploaded to the leaderboards; no XP, credits, missions, achievements, unlocks or statistics.";

        private static MainMenuPanel _panel;
        private static GameObject _button;
        private static Il2CppSystem.Action _onClick;   // held so the GC never collects the converted delegate
        private static string _off;

        internal static void Off(string why) { _off = why; }

        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(MainMenuPanel), "Awake");
            if (target == null) throw new MissingMethodException("MainMenuPanel", "Awake");
            // harmony-target: MainMenuPanel.Awake
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(MenuButton), nameof(AfterAwake)));
        }

        private static void AfterAwake(MainMenuPanel __instance)
        {
            try
            {
                if (!Plugin.AllOk) { Plugin.Log.LogWarning("[Sandbox] SANDBOX button not added: a guard failed to install (see the load line)"); return; }
                if (!Plugin.Enabled.Value) { Plugin.Log.LogInfo("[Sandbox] SANDBOX button not added ([General] Enabled is off)"); return; }
                if (_off != null) return;
                Add(__instance);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] SANDBOX button not added: {e.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Add(MainMenuPanel panel)
        {
            var single = panel.singleplayerButton;
            if (single == null) { Plugin.Log.LogWarning("[Sandbox] SANDBOX button not added: the main menu has no Singleplayer button"); return; }
            var parent = single.transform.parent;
            var clone = UnityEngine.Object.Instantiate(single.gameObject, parent);
            clone.name = "Sandbox_SANDBOX";
            // no hotkey on the copy (it would fire on Singleplayer's key) and no text copier that would put the old label back
            var comps = clone.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                var it = c.GetIl2CppType();
                string tn = it == null ? "" : it.Name;
                if (tn.StartsWith("Hotkey", StringComparison.Ordinal) || tn == "TextCopier") UnityEngine.Object.Destroy(c);
            }
            var texts = clone.GetComponentsInChildren<TMP_Text>(true);
            if (texts == null || texts.Length == 0)
            {
                UnityEngine.Object.Destroy(clone);
                Plugin.Log.LogWarning("[Sandbox] SANDBOX button not added: the Singleplayer button has no text label to change");
                return;
            }
            for (int i = 0; i < texts.Length; i++) if (texts[i] != null) texts[i].text = "SANDBOX";

            var mmb = clone.GetComponent<MainMenuButton>();
            if (mmb != null)
            {
                mmb.description = Description;
                if (panel.buttonDescriptionText != null) mmb.Initialize(panel.buttonDescriptionText);
                try { mmb.SetNotificationEnabled(false); } catch { /* cosmetic */ }
            }

            var anchor = single.transform;
            bool layout = parent.GetComponent<LayoutGroup>() != null;
            if (layout) clone.transform.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
            else
            {
                // no layout group: one button-spacing under the lowest button of this group
                var rts = new List<RectTransform>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var ch = parent.GetChild(i);
                    if (ch.gameObject == clone || !ch.gameObject.activeSelf || ch.GetComponent<CallbackButton>() == null) continue;
                    rts.Add(ch.GetComponent<RectTransform>());
                }
                rts.Sort((p, q) => q.anchoredPosition.y.CompareTo(p.anchoredPosition.y));
                float spacing = rts.Count >= 2 ? Mathf.Abs(rts[0].anchoredPosition.y - rts[1].anchoredPosition.y) : 80f;
                var lowest = rts.Count > 0 ? rts[rts.Count - 1] : single.GetComponent<RectTransform>();
                clone.GetComponent<RectTransform>().anchoredPosition = lowest.anchoredPosition - new Vector2(0f, spacing);
                anchor = lowest.transform;
            }

            var cb = clone.GetComponent<CallbackButton>();
            if (cb == null) { UnityEngine.Object.Destroy(clone); Plugin.Log.LogWarning("[Sandbox] SANDBOX button not added: no CallbackButton on the copy"); return; }
            if (_onClick == null) _onClick = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnClick));
            cb.SetCallback(_onClick);
            SpliceNavigation(anchor.GetComponent<Button>(), clone.GetComponent<Button>());
            _panel = panel;
            _button = clone;
            Plugin.Log.LogInfo($"[Sandbox] SANDBOX button added under Singleplayer ({(layout ? "layout group" : "placed under the last button")})");
        }

        private static void OnClick()
        {
            try
            {
                if (_panel == null || !Plugin.AllOk) return;
                State.Pending = true;
                State.FromSandboxButton = true;
                try { _panel.OnSingleplayerButton(); }
                finally { State.FromSandboxButton = false; }
                Plugin.Log.LogInfo("[Sandbox] SANDBOX chosen: the next car you start with begins a sandbox run");
            }
            catch (Exception e) { State.Pending = false; Plugin.Log.LogWarning($"[Sandbox] SANDBOX button: {e.Message}"); }
        }

        /// <summary>Explicit navigation (d-pad): put SANDBOX between the anchor button and the one below it.</summary>
        private static void SpliceNavigation(Button above, Button sandbox)
        {
            if (above == null || sandbox == null) return;
            var nav = above.navigation;
            if (nav.mode != Navigation.Mode.Explicit) return;   // automatic navigation finds the copy by itself
            var below = nav.selectOnDown;
            var snav = nav;
            snav.selectOnUp = above;
            snav.selectOnDown = below;
            sandbox.navigation = snav;
            nav.selectOnDown = sandbox;
            above.navigation = nav;
            SetOriginal(above, nav);
            SetOriginal(sandbox, snav);
            if (below != null)
            {
                var bnav = below.navigation;
                bnav.selectOnUp = sandbox;
                below.navigation = bnav;
                SetOriginal(below, bnav);
            }
        }

        /// <summary>CallbackButton caches its navigation and puts it back whenever the panel comes on top: update the cache too.</summary>
        private static void SetOriginal(Selectable sel, Navigation nav)
        {
            try
            {
                if (sel == null) return;
                var cb = sel.GetComponent<CallbackButton>();
                if (cb != null) cb.originalNavigation = nav;
            }
            catch { /* cosmetic */ }
        }
    }
}
