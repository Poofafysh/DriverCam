using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RogueHub
{
    /// <summary>
    /// Everything that touches the game. Each part is checked at startup by name; a part whose game members are
    /// missing is switched off (logged) and its callers fall back: the public methods here only test the flag and call
    /// a separate NoInlining method that holds the game types, inside a try, so a renamed member can't take the hub
    /// down. Game-typed fields live in <see cref="Held"/> for the same reason. The parts:
    ///   - pause / resume: GameManager.SetGamePaused (the game's own pause: menu, time scale, cursor);
    ///   - menu lock while the hub is open over a game menu: the game's own popup pattern (a BoolRef in
    ///     PlayerInputManager.LockNavigationModifier + IgnoreListenHotkeyInputModifier, and UIManager's lock-cancel and
    ///     overlay registrations), so B / Esc / Start / d-pad don't also drive the menu under the hub;
    ///   - driving buttons while the quick menu is open: the d-pad, face buttons and arrow / Enter / Esc keys of the
    ///     Player and Dev maps get an empty binding override (disabled) and are put back on close (triggers, sticks and
    ///     WASD keep driving);
    ///   - a MODS button cloned from the pause menu's Resume button (Harmony postfix on PauseMenuPanel.Awake).
    /// All of it is undone on close and on the hub's error breaker.
    /// </summary>
    internal static class GameApi
    {
        internal static bool PauseOk, LockOk, BlockOk, ButtonOk, RaceOk;

        internal static void Check()
        {
            var missing = new List<string>();
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            bool single = Has(asm, "Singleton", missing, "Instance", "GameManager", "PlayerInput", "UIManager");
            bool state = Has(asm, "Game.Runtime.GameState", missing, "IsGamePaused");
            PauseOk = single && state && Has(asm, "Game.Runtime.Manager.GameManager", missing, "SetGamePaused", "CanPause");
            RaceOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "LevelWasEnded");
            LockOk = single && Has(asm, "PlayerInputManager", missing, "LockNavigationModifier", "IgnoreListenHotkeyInputModifier")
                     && Has(asm, "Game.Runtime.Manager.UIManager", missing, "Instance", "RegisterLockCancelEvent", "UnregisterLockCancelEvent", "RegisterOverlayObject", "UnregisterOverlayObject")
                     && Has(asm, "Game.Runtime.Utility.BoolRef", missing);
            BlockOk = single && Has(asm, "PlayerInputManager", missing, "inputActions");
            ButtonOk = Has(asm, "Game.Runtime.UI.PauseMenuPanel", missing, "resumeButton", "settingsButton", "Awake")
                       && Has(asm, "Game.Runtime.UI.Components.CallbackButton", missing, "SetCallback", "originalNavigation");
            if (missing.Count > 0) Plugin.Log.LogWarning($"[RogueHub] game check: missing {string.Join(", ", missing)}");
            Plugin.Log.LogInfo($"[RogueHub] game check: pause {Ok(PauseOk)}, race state {Ok(RaceOk)}, menu lock {Ok(LockOk)}, " +
                               $"driving-button block {Ok(BlockOk)}, MODS button {Ok(ButtonOk)}");
        }

        private static string Ok(bool b) => b ? "OK" : "off";

        private static bool Has(Assembly asm, string typeName, List<string> missing, params string[] members)
        {
            var t = asm?.GetType(typeName);
            if (t == null) { missing.Add(typeName); return false; }
            bool ok = true;
            foreach (var m in members)
                if (t.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0)
                { missing.Add($"{t.Name}.{m}"); ok = false; }
            return ok;
        }

        /// <summary>Game-typed state, kept out of GameApi's own fields so GameApi loads even if these types are gone.</summary>
        private static class Held
        {
            internal static Game.Runtime.Utility.BoolRef LockRef;
            internal static Il2CppSystem.Object Token;
            internal static Il2CppSystem.Action OpenFromPause;
        }

        // ------------------------------------------------------------------ race / pause state

        internal static bool Paused()
        {
            if (!PauseOk) return Time.timeScale <= 0f;
            try { return PausedRaw(); } catch { return Time.timeScale <= 0f; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PausedRaw() => Game.Runtime.GameState.IsGamePaused;

        /// <summary>A player car is in a running race (the quick menu is only offered then).</summary>
        internal static bool InRace()
        {
            if (!RaceOk) return false;
            try { return InRaceRaw(); } catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool InRaceRaw()
        {
            var veh = Game.Runtime.Vehicle.VehicleManager.Instance;
            return veh != null && !veh.LevelWasEnded;
        }

        internal static bool CanPause()
        {
            if (!PauseOk) return false;
            try { return CanPauseRaw(); } catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CanPauseRaw()
        {
            var single = global::Singleton.Instance;
            if (single == null) return false;
            var gm = single.GameManager;
            return gm != null && gm.CanPause;
        }

        internal static bool SetPaused(bool paused)
        {
            if (!PauseOk) return false;
            try { return SetPausedRaw(paused); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] {(paused ? "pause" : "resume")} failed: {e.Message}"); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SetPausedRaw(bool paused)
        {
            var single = global::Singleton.Instance;
            if (single == null) return false;
            var gm = single.GameManager;
            if (gm == null) return false;
            if (paused && !gm.CanPause) return false;
            gm.SetGamePaused(paused);
            return true;
        }

        // ------------------------------------------------------------------ menu lock (hub open over a game menu)

        private static GameObject _savedSelection;
        private static bool _locked, _navAdded, _hotAdded, _cancelAdded, _overlayAdded;

        internal static void LockMenus()
        {
            if (!LockOk || _locked) return;
            _locked = true;   // set first: UnlockMenus undoes whichever steps below succeeded, even if a later one throws
            try { LockRaw(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] menu lock incomplete (the game's menu may also react): {e.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LockRaw()
        {
            if (Held.LockRef == null) Held.LockRef = new Game.Runtime.Utility.BoolRef(true);
            if (Held.Token == null) Held.Token = new Il2CppSystem.Object();
            var es = EventSystem.current;
            if (es != null)
            {
                _savedSelection = es.currentSelectedGameObject;
                es.SetSelectedGameObject(null);
            }
            _navAdded = true;   // recorded before each call: removing a modifier that isn't there is harmless, a leaked one isn't
            global::PlayerInputManager.LockNavigationModifier.AddModifier(Held.LockRef);
            _hotAdded = true;
            global::PlayerInputManager.IgnoreListenHotkeyInputModifier.AddModifier(Held.LockRef);
            var ui = Game.Runtime.Manager.UIManager.Instance;
            if (ui == null) return;
            _cancelAdded = true;
            ui.RegisterLockCancelEvent(Held.Token);
            _overlayAdded = true;
            ui.RegisterOverlayObject(Held.Token);
        }

        /// <summary>Gives the game its menus back. reselect = an object to select (the MODS button) or null for the one selected before.</summary>
        internal static void UnlockMenus(GameObject reselect)
        {
            if (!_locked) return;
            _locked = false;
            try { UnlockRaw(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] menu unlock: {e.Message}"); }
            try
            {
                var es = EventSystem.current;
                var target = reselect != null && reselect.activeInHierarchy ? reselect
                           : _savedSelection != null && _savedSelection.activeInHierarchy ? _savedSelection : null;
                if (es != null && target != null) es.SetSelectedGameObject(target);
            }
            catch { /* selection is cosmetic */ }
            _savedSelection = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UnlockRaw()
        {
            if (_navAdded)
            {
                _navAdded = false;
                try { global::PlayerInputManager.LockNavigationModifier.RemoveModifier(Held.LockRef); } catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] navigation unlock: {e.Message}"); }
            }
            if (_hotAdded)
            {
                _hotAdded = false;
                try { global::PlayerInputManager.IgnoreListenHotkeyInputModifier.RemoveModifier(Held.LockRef); } catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] hotkey unlock: {e.Message}"); }
            }
            var ui = Game.Runtime.Manager.UIManager.Instance;
            if (ui == null) { _overlayAdded = _cancelAdded = false; return; }
            // delayed: the same B / Esc press that closed the hub must not also close the pause menu
            if (_overlayAdded)
            {
                _overlayAdded = false;
                try { ui.UnregisterOverlayObject(Held.Token, true); } catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] overlay unlock: {e.Message}"); }
            }
            if (_cancelAdded)
            {
                _cancelAdded = false;
                try { ui.UnregisterLockCancelEvent(Held.Token, true); } catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] cancel unlock: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ driving buttons (quick menu)

        private struct Bind { public InputAction Action; public int Index; public string Path; }
        private struct Saved { public InputAction Action; public int Index; public string Override; }
        private static readonly List<Bind> Candidates = new List<Bind>();   // found once, re-checked at each open
        private static readonly List<Saved> Blocked = new List<Saved>();
        private static InputActionAsset _scanned;

        private static readonly string[] BlockedKeys = { "<keyboard>/uparrow", "<keyboard>/downarrow", "<keyboard>/leftarrow", "<keyboard>/rightarrow",
                                                         "<keyboard>/enter", "<keyboard>/numpadenter", "<keyboard>/escape", "<keyboard>/backspace" };
        private static readonly string[] Buttons = { "/buttonsouth", "/buttoneast", "/buttonwest", "/buttonnorth" };

        private static bool IsMenuButton(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            if (Array.IndexOf(BlockedKeys, p) >= 0) return true;
            bool pad = p.StartsWith("<gamepad>") || p.StartsWith("<xinputcontroller>") || p.StartsWith("<dualshockgamepad>") || p.StartsWith("<switchproc");
            if (!pad) return false;
            if (p.Contains("/dpad")) return true;
            foreach (var b in Buttons) if (p.EndsWith(b, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Disables the d-pad / face-button / arrow-key bindings of the game's Player and Dev maps. Returns how many.</summary>
        internal static int BlockDriving()
        {
            if (!BlockOk || Blocked.Count > 0) return Blocked.Count;
            try { return BlockRaw(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RogueHub] couldn't take the d-pad from driving (it steers too while the quick menu is open): {e.Message}");
                UnblockDriving();
                return 0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int BlockRaw()
        {
            var single = global::Singleton.Instance;
            var pim = single == null ? null : single.PlayerInput;
            var asset = pim == null ? null : pim.inputActions;
            if (asset == null) return 0;
            if (_scanned == null || asset.Pointer != _scanned.Pointer || !StillValid()) Scan(asset);
            foreach (var c in Candidates)
            {
                var b = c.Action.bindings[c.Index];
                string effective = b.effectivePath;   // the player's own rebinds count: a binding they moved off the d-pad is left alone
                if (!IsMenuButton(effective)) continue;
                Blocked.Add(new Saved { Action = c.Action, Index = c.Index, Override = b.overridePath });
                InputActionRebindingExtensions.ApplyBindingOverride(c.Action, c.Index, "");
            }
            return Blocked.Count;
        }

        /// <summary>The bindings found last time are still where they were (a rebuilt asset or new bindings = scan again).</summary>
        private static bool StillValid()
        {
            try
            {
                foreach (var c in Candidates)
                    if (c.Index >= c.Action.bindings.Count || c.Action.bindings[c.Index].path != c.Path) return false;
                return true;
            }
            catch { return false; }
        }

        private static void Scan(InputActionAsset asset)
        {
            Candidates.Clear();
            _scanned = asset;
            var names = new List<string>();
            var maps = asset.actionMaps;
            for (int m = 0; m < maps.Count; m++)
            {
                var map = maps[m];
                if (map == null) continue;
                string mn = map.name ?? "";
                names.Add(mn);
                if (!mn.Equals("Player", StringComparison.OrdinalIgnoreCase) && !mn.Equals("Dev", StringComparison.OrdinalIgnoreCase)) continue;
                var actions = map.actions;
                for (int a = 0; a < actions.Count; a++)
                {
                    var action = actions[a];
                    if (action == null) continue;
                    var bindings = action.bindings;
                    for (int i = 0; i < bindings.Count; i++)
                    {
                        var b = bindings[i];
                        // the original path or the player's rebind may be a menu button: keep both kinds, decide at each open
                        if (IsMenuButton(b.path) || IsMenuButton(b.effectivePath))
                            Candidates.Add(new Bind { Action = action, Index = i, Path = b.path });
                    }
                }
            }
            Plugin.Log.LogInfo(Candidates.Count > 0
                ? $"[RogueHub] quick menu: {Candidates.Count} game bindings to pause while it's open"
                : $"[RogueHub] quick menu: no d-pad / face-button bindings found in the game's maps ({string.Join(", ", names)}); the d-pad also drives while it's open");
        }

        /// <summary>Puts every binding BlockDriving disabled back as it was (a player's own override included).</summary>
        internal static void UnblockDriving()
        {
            foreach (var s in Blocked)
            {
                try
                {
                    if (s.Action == null) continue;
                    // null = there was no override; "" = the player had unbound it themselves (put that back too)
                    if (s.Override == null) InputActionRebindingExtensions.RemoveBindingOverride(s.Action, s.Index);
                    else InputActionRebindingExtensions.ApplyBindingOverride(s.Action, s.Index, s.Override);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] restoring a game binding failed: {e.Message}"); }
            }
            Blocked.Clear();
        }

        // ------------------------------------------------------------------ MODS button in the pause menu

        internal static GameObject ModsButton;
        private static Action _openFromPause;

        internal static void InstallPauseButton(Harmony harmony, Action openHub)
        {
            if (!ButtonOk) return;
            _openFromPause = openHub;
            InstallRaw(harmony);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InstallRaw(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(Game.Runtime.UI.PauseMenuPanel), "Awake");
            if (target == null) { ButtonOk = false; return; }
            // harmony-target: PauseMenuPanel.Awake
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(GameApi), nameof(AfterPauseAwake)));
            Plugin.Log.LogInfo("[RogueHub] MODS button hook installed (PauseMenuPanel.Awake)");
        }

        private static void AfterPauseAwake(Game.Runtime.UI.PauseMenuPanel __instance)
        {
            try { if (Plugin.PauseButton.Value) AddModsButton(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] MODS button not added: {e.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddModsButton(Game.Runtime.UI.PauseMenuPanel panel)
        {
            var resume = panel.resumeButton;
            var settings = panel.settingsButton;
            if (resume == null) return;
            var parent = resume.transform.parent;
            var clone = UnityEngine.Object.Instantiate(resume.gameObject, parent);
            clone.name = "RogueHub_MODS";
            // no hotkey on the copy (a cloned HotkeyButton would fire "MODS" on Resume's key) and no text copier
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
                // a label we can't change would read "Resume": no button then (Shift + backtick still opens the hub)
                UnityEngine.Object.Destroy(clone);
                Plugin.Log.LogWarning("[RogueHub] MODS button skipped: the pause menu's Resume button has no text label to change");
                return;
            }
            for (int i = 0; i < texts.Length; i++) if (texts[i] != null) texts[i].text = "MODS";

            var anchor = settings != null && settings.transform.parent == parent ? settings.transform : resume.transform;
            bool layout = parent.GetComponent<LayoutGroup>() != null;
            if (layout) clone.transform.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
            else
            {
                // no layout group: put it under the lowest button, one button-spacing further down
                var rts = new List<RectTransform>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var ch = parent.GetChild(i);
                    if (ch.gameObject == clone || !ch.gameObject.activeSelf || ch.GetComponent<Game.Runtime.UI.Components.CallbackButton>() == null) continue;
                    rts.Add(ch.GetComponent<RectTransform>());
                }
                rts.Sort((p, q) => q.anchoredPosition.y.CompareTo(p.anchoredPosition.y));
                float spacing = rts.Count >= 2 ? Mathf.Abs(rts[0].anchoredPosition.y - rts[1].anchoredPosition.y) : 80f;
                var lowest = rts.Count > 0 ? rts[rts.Count - 1] : resume.GetComponent<RectTransform>();
                var crt = clone.GetComponent<RectTransform>();
                crt.anchoredPosition = lowest.anchoredPosition - new Vector2(0f, spacing);
                anchor = lowest.transform;
            }

            var cb = clone.GetComponent<Game.Runtime.UI.Components.CallbackButton>();
            if (Held.OpenFromPause == null)
                Held.OpenFromPause = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OpenFromPause));
            cb.SetCallback(Held.OpenFromPause);
            SpliceNavigation(anchor.GetComponent<Button>(), clone.GetComponent<Button>());
            ModsButton = clone;
            Plugin.Log.LogInfo($"[RogueHub] MODS button added to the pause menu ({(layout ? "layout group" : "placed under the last button")})");
        }

        private static void OpenFromPause()
        {
            try { _openFromPause?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] MODS: {e.Message}"); }
        }

        /// <summary>Explicit navigation (d-pad between the buttons): put MODS between the anchor button and the one below it.</summary>
        private static void SpliceNavigation(Button above, Button mods)
        {
            if (above == null || mods == null) return;
            var nav = above.navigation;
            if (nav.mode != Navigation.Mode.Explicit) return;   // automatic navigation finds the clone by itself
            var below = nav.selectOnDown;
            var mnav = nav;
            mnav.selectOnUp = above;
            mnav.selectOnDown = below;
            mods.navigation = mnav;
            nav.selectOnDown = mods;
            above.navigation = nav;
            SetOriginal(above, nav);
            SetOriginal(mods, mnav);
            if (below != null)
            {
                var bnav = below.navigation;
                bnav.selectOnUp = mods;
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
                var cb = sel.GetComponent<Game.Runtime.UI.Components.CallbackButton>();
                if (cb != null) cb.originalNavigation = nav;
            }
            catch { /* cosmetic */ }
        }
    }
}
