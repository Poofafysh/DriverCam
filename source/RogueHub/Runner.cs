using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.Attributes;
using RogueShared;
using UnityEngine;

namespace RogueHub
{
    /// <summary>
    /// The one injected MonoBehaviour: opens and closes the quick menu and the hub, drives them, and pumps the
    /// notification stack. Owns everything the hub changes in the game and undoes it on every way out (close, plugin
    /// destroyed, error breaker): the menu lock, the driving-button overrides, the cursor, a pause it started itself,
    /// the "hub open" flag (other mods hide their own panels while it's set) and DriverCam's on-screen button, hidden
    /// while a menu is up. Views are built the first time they're needed (the game's fonts are loaded by then).
    /// While everything is closed, a frame costs a few cached-control reads (no allocation).
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        internal static Runner Instance;

        private enum State { Closed, Quick, Hub }

        private State _state;
        private HubView _hub;
        private QuickView _quick;
        private ToastView _toasts;
        private bool _pausedByUs, _broken, _cursorSaved, _cursorVisible;
        private CursorLockMode _cursorLock;
        private int _errors;
        private float _quickIdleAt, _nextQuickRefresh, _nextPresent;
        private readonly List<Setting> _quickItems = new List<Setting>();
        private Action<string, string, string, string> _toastSink;

        private void Awake()
        {
            Instance = this;
            _toastSink = PushToast;
            SetFlag("present", true);
        }

        private static void SetFlag(string key, bool on)
        {
            try { HubLink.Registry()[key] = on; } catch { /* registry is optional */ }
        }

        /// <summary>The MODS button in the game's pause menu.</summary>
        internal static void OpenFromPauseMenu()
        {
            var r = Instance;
            if (r == null || r._broken || r._state == State.Hub) return;
            try { r.OpenHubNow(); }
            catch (Exception e) { r.Fault(e); }
        }

        private void Update()
        {
            if (_broken) return;
            try
            {
                float now = Time.unscaledTime;
                if (now >= _nextPresent) { _nextPresent = now + 1f; SetFlag("present", Plugin.Toasts.Value); }
                Saver.Tick();
                TickToasts();
                switch (_state)
                {
                    case State.Closed: TickClosed(); break;
                    case State.Quick: TickQuick(); break;
                    case State.Hub: TickHub(); break;
                }
            }
            catch (Exception e) { Fault(e); }
        }

        private void TickToasts()
        {
            if (_toasts == null)
            {
                bool queued = false;
                try { queued = ((Queue<object[]>)HubLink.Registry()["toasts"]).Count > 0; } catch { /* none */ }
                if (!queued) return;
                _toasts = new ToastView();
            }
            _toasts.Tick(Plugin.Toasts.Value);
        }

        [HideFromIl2Cpp]
        private void PushToast(string guid, string title, string detail, string kind)
        {
            if (_toasts == null) _toasts = new ToastView();
            _toasts.Push(guid, title, detail, kind);
        }

        // ------------------------------------------------------------------ closed: watch the open inputs

        private void TickClosed()
        {
            bool key = In.OpenKey(Plugin.QuickKey.Value, out bool shift);
            bool chord = Plugin.Chord.Value && In.Chord();
            if (!key && !chord) return;
            if (key && shift) { RequestHub(); return; }
            // driving: the quick menu; paused or in the game's menus: the hub
            if (!GameApi.Paused() && GameApi.InRace()) OpenQuick();
            else RequestHub();
        }

        // ------------------------------------------------------------------ quick menu

        private void OpenQuick()
        {
            Catalog.Refresh();
            _quickItems.Clear();
            foreach (var id in Favs.List())
                if (Catalog.ById.TryGetValue(id, out var s) && (s.Kind == Kind.Bool || s.Kind == Kind.Number || s.Kind == Kind.Choice || s.Kind == Kind.Action))
                    _quickItems.Add(s);
            if (_quick == null) _quick = new QuickView();
            int blocked = GameApi.BlockDriving();
            In.Reset();
            _state = State.Quick;
            SetFlag("open", true);   // CurbFeel hides its panel; DriverCam's button stays (the quick menu sits clear of it)
            _quick.Open(_quickItems);
            _quickIdleAt = Time.unscaledTime + Mathf.Clamp(Plugin.QuickClose.Value, 2f, 30f);
            Plugin.Log.LogInfo($"[RogueHub] quick menu open ({_quickItems.Count} rows, {blocked} game bindings paused)");
        }

        private void CloseQuick()
        {
            if (_state != State.Quick) return;
            _state = State.Closed;
            try { _quick?.Close(); } catch { /* view gone */ }
            GameApi.UnblockDriving();
            SetFlag("open", false);
        }

        private void TickQuick()
        {
            float now = Time.unscaledTime;
            bool key = In.OpenKey(Plugin.QuickKey.Value, out bool shift);
            if (key && shift) { CloseQuick(); RequestHub(); return; }
            if (key || (Plugin.Chord.Value && In.Chord())) { CloseQuick(); return; }
            if (GameApi.Paused() || !GameApi.InRace()) { CloseQuick(); return; }
            In.Read(false, quick: true);   // d-pad, face buttons, arrows: the stick and triggers keep driving
            bool any = In.Up || In.Down || In.Left || In.Right || In.A || In.B || In.Back;
            if (any) _quickIdleAt = now + Mathf.Clamp(Plugin.QuickClose.Value, 2f, 30f);
            if (now >= _quickIdleAt || In.B || In.Back) { CloseQuick(); return; }
            int n = _quick.Items.Count;
            bool changed = false;
            if (In.Up) { _quick.Sel = (_quick.Sel - 1 + n) % n; changed = true; }
            if (In.Down) { _quick.Sel = (_quick.Sel + 1) % n; changed = true; }
            var s = _quick.Sel < n ? _quick.Items[_quick.Sel] : null;
            if ((In.Left || In.Right) && s != null && s.Kind != Kind.Action) { Catalog.Nudge(s, In.Right ? 1 : -1, In.FastMult); changed = true; }
            if (In.A)
            {
                if (s == null) { CloseQuick(); RequestHub(); return; }
                if (s.Kind == Kind.Bool || s.Kind == Kind.Choice) Catalog.Nudge(s, 1, 1);
                else if (s.Kind == Kind.Action) RunAction(s);
                changed = true;
            }
            if (changed || now >= _nextQuickRefresh) { _nextQuickRefresh = now + 0.25f; _quick.Refresh(); }
        }

        [HideFromIl2Cpp]
        private void RunAction(Setting s)
        {
            string result;
            try { result = s.Run?.Invoke(); }
            catch (Exception e) { result = "failed: " + e.Message; }
            if (!string.IsNullOrEmpty(result)) PushToast(s.Module.Guid, s.Label, result, "info");
        }

        // ------------------------------------------------------------------ hub

        /// <summary>Open the hub: over the pause menu (pausing the race first if we're driving), or over the game's menus.</summary>
        private void RequestHub()
        {
            if (GameApi.Paused() || !GameApi.InRace()) { OpenHubNow(); return; }
            if (GameApi.CanPause() && GameApi.SetPaused(true))
            {
                OpenHubNow();
                _pausedByUs = true;   // closing the hub resumes the race
                return;
            }
            PushToast(Plugin.Guid, "The hub can't open right now", "Open it from the pause menu (MODS) instead", "warn");
        }

        private void OpenHubNow()
        {
            if (_state == State.Quick) CloseQuick();
            if (_state == State.Hub) return;
            if (_hub == null) _hub = new HubView { Toast = _toastSink };
            GameApi.LockMenus();
            if (!_cursorSaved) { _cursorSaved = true; _cursorVisible = Cursor.visible; _cursorLock = Cursor.lockState; }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            _pausedByUs = false;
            In.Reset();
            _state = State.Hub;
            SetFlag("open", true);
            Overlays.Hide();
            _hub.Open();
        }

        private void CloseHub()
        {
            if (_state != State.Hub) return;
            _state = State.Closed;
            try { _hub?.Close(); } catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] closing the hub: {e.Message}"); }
            GameApi.UnlockMenus(GameApi.ModsButton);
            if (_pausedByUs)
            {
                // resume before the cursor: the game's unpause only sets the cursor's visibility, not its lock
                _pausedByUs = false;
                GameApi.SetPaused(false);
            }
            RestoreCursor();
            Overlays.Restore();
            SetFlag("open", false);
            Saver.Flush();
        }

        private void RestoreCursor()
        {
            if (!_cursorSaved) return;
            _cursorSaved = false;
            bool paused = GameApi.Paused();
            Cursor.lockState = paused ? CursorLockMode.None : _cursorLock;
            Cursor.visible = paused || _cursorVisible;
        }

        private void TickHub()
        {
            // the open key closes the hub, but not while typing a search or a value, or capturing a key
            if (In.OpenKey(Plugin.QuickKey.Value, out _) && !_hub.Busy) { CloseHub(); return; }
            _hub.Tick();
            if (_hub.CloseRequested) CloseHub();
        }

        // ------------------------------------------------------------------ safety

        /// <summary>Close everything the hub opened and give the game back its input, menus, cursor, pause and the other mods' panels.</summary>
        private void CloseAll()
        {
            try { CloseQuick(); } catch { /* best effort */ }
            try { CloseHub(); } catch { /* best effort */ }
            try { GameApi.UnblockDriving(); } catch { /* best effort */ }
            try { GameApi.UnlockMenus(null); } catch { /* best effort */ }
            try { if (_pausedByUs) { _pausedByUs = false; GameApi.SetPaused(false); } } catch { /* best effort */ }
            try { RestoreCursor(); } catch { /* best effort */ }
            try { Overlays.Restore(); } catch { /* best effort */ }
            SetFlag("open", false);
            try { Saver.Flush(); } catch { /* best effort */ }
        }

        [HideFromIl2Cpp]
        private void Fault(Exception e)
        {
            _errors++;
            Plugin.Log.LogWarning($"[RogueHub] error ({_errors}/5): {e.Message}");
            if (_errors < 5) return;
            _broken = true;
            CloseAll();
            SetFlag("present", false);   // the mods show their own messages again
            try { _hub?.Destroy(); } catch { /* scene gone */ }
            try { _quick?.Destroy(); } catch { /* scene gone */ }
            try { _toasts?.Destroy(); } catch { /* scene gone */ }
            _hub = null; _quick = null; _toasts = null;
            Plugin.Log.LogError($"[RogueHub] switched off for this session; the game's menus, input and pause are back to normal. Last error: {e}");
        }

        private void OnDestroy()
        {
            CloseAll();
            SetFlag("present", false);
            if (Instance == this) Instance = null;
        }
    }

    /// <summary>
    /// Other mods' IMGUI drawn over every canvas while the hub is up. CurbFeel hides its own panel (HubLink.HubOpen);
    /// DriverCam (another developer's plugin, not changed) is asked through its own UI.ShowButton setting: switched off
    /// without saving when the hub opens, switched back on and its file saved when the hub closes. (Something else that
    /// saves DriverCam's file meanwhile writes "off"; the close puts "on" back. Only a quit or crash with the hub open
    /// leaves it off, and the setting is in the hub's Camera tab.) The quick menu doesn't need this: it sits clear of
    /// the button, and the cursor is locked while driving.
    /// </summary>
    internal static class Overlays
    {
        private const string DriverCamButton = "drivingrogue.drivercam|UI|ShowButton";
        private static Setting _hidden;

        internal static void Hide()
        {
            if (_hidden != null) return;
            try
            {
                Catalog.Refresh();
                if (!Catalog.ById.TryGetValue(DriverCamButton, out var s) || s.Kind != Kind.Bool || !(bool)s.Entry.BoxedValue) return;
                ConfigFile cfg = s.Module.Config;
                bool save = cfg.SaveOnConfigSet;
                cfg.SaveOnConfigSet = false;   // never written to DriverCam's file as off
                try { s.Entry.BoxedValue = false; }
                finally { cfg.SaveOnConfigSet = save; }
                _hidden = s;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] couldn't hide DriverCam's button: {e.Message}"); }
        }

        /// <summary>The player changed that setting in the hub themselves: it's theirs now, leave it as they set it.</summary>
        internal static void Touched(Setting s)
        {
            if (_hidden != null && ReferenceEquals(s, _hidden)) _hidden = null;
        }

        internal static void Restore()
        {
            var s = _hidden;
            if (s == null) return;
            _hidden = null;
            try
            {
                if (!(bool)s.Entry.BoxedValue) s.Entry.BoxedValue = true;
                s.Module.Config.Save();   // explicitly: DriverCam's Edit mode may have switched save-on-change off
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] couldn't show DriverCam's button again: {e.Message}"); }
        }
    }
}
