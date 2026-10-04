using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Game.Runtime.Manager;
using Game.Runtime.UI;

namespace Sandbox
{
    /// <summary>
    /// The sandbox flag ([State] RunIsSandbox) and every run entry point that sets or clears it (GameAssembly.dll):
    ///   - MainMenuManager.StartGameWithVehicle (0x90A180) -> GameCoordinatorManager.StartNewSingleplayerGame (0x8D6670):
    ///     a new run from the main menu. Sandbox only when the SANDBOX button opened this car choice (Pending).
    ///   - StartNewSingleplayerGame from inside a run (DefeatScreenPanel / ProgressionScreenPanel retry, LoadingManager
    ///     restart, GameCoordinatorManager.Awake in the game scene): the same run's retry, the flag is kept. From a special
    ///     scene (RequestNextLevel after the tutorial): a normal run, the flag is cleared.
    ///   - GameCoordinatorManager.TryRestoreSingleplayerGame (0x8D6A30): Continue (SessionBackupManager.TryRestoreLastSession)
    ///     and checkpoint restore (TryRestoreSessionCheckpoint). The flag comes from the snapshot's own timestamp, which
    ///     SessionBackup.CaptureSnapshot (0x77CBB0) recorded together with the flag when the snapshot was taken.
    ///   - StartFullTutorial / StartQuickTutorial / StartNPCTestingArea / StartMultiplayerFromHost and the main menu's
    ///     Tutorial / Multiplayer / Singleplayer buttons: cleared (the tutorial scenes set themselves up in
    ///     GameCoordinatorManager.Awake without these, so the menu buttons clear it too).
    ///   - GameCoordinatorManager.NextSingleplayerGame / RetrySingleplayerGame (next race, restart race): kept.
    /// </summary>
    internal static class State
    {
        /// <summary>The SANDBOX button opened the car choice; consumed by the next run started from the menu.</summary>
        internal static bool Pending;
        /// <summary>True while MainMenuPanel.OnSingleplayerButton runs because the SANDBOX button called it.</summary>
        internal static bool FromSandboxButton;
        private static bool _menuStart;

        private const int MaxMarks = 16;

        internal static int Install(Harmony h)
        {
            int n = 0;
            // harmony-target: MainMenuPanel.OnSingleplayerButton, MainMenuPanel.OnTutorialButton, MainMenuPanel.OnMultiplayerButton
            n += P(h, typeof(MainMenuPanel), "OnSingleplayerButton", nameof(BeforeSingleplayerButton), null);
            n += P(h, typeof(MainMenuPanel), "OnTutorialButton", nameof(BeforeOtherModeButton), null);
            n += P(h, typeof(MainMenuPanel), "OnMultiplayerButton", nameof(BeforeOtherModeButton), null);
            // harmony-target: MainMenuManager.OpenVehicleSelection (0x9098B0, own code; callers: OnSingleplayerButton, two panel
            // ClosePanel / OnBackButton paths, UIMiniMenu.SinglePlayer): any car choice the SANDBOX button didn't open drops Pending
            n += P(h, typeof(MainMenuManager), "OpenVehicleSelection", nameof(BeforeOpenVehicleSelection), null);
            // harmony-target: MainMenuManager.ReturnToMainMenuFromVehicleSelection, MainMenuManager.StartGameWithVehicle
            n += P(h, typeof(MainMenuManager), "ReturnToMainMenuFromVehicleSelection", null, nameof(AfterVehicleSelectionBack));
            n += P(h, typeof(MainMenuManager), "StartGameWithVehicle", nameof(BeforeStartGameWithVehicle), nameof(AfterStartGameWithVehicle));
            // harmony-target: GameCoordinatorManager.StartNewSingleplayerGame, GameCoordinatorManager.TryRestoreSingleplayerGame
            n += P(h, typeof(GameCoordinatorManager), "StartNewSingleplayerGame", nameof(BeforeStartNew), null);
            n += P(h, typeof(GameCoordinatorManager), "TryRestoreSingleplayerGame", nameof(BeforeRestore), null);
            // harmony-target: GameCoordinatorManager.StartFullTutorial, GameCoordinatorManager.StartQuickTutorial, GameCoordinatorManager.StartNPCTestingArea, GameCoordinatorManager.StartMultiplayerFromHost
            n += P(h, typeof(GameCoordinatorManager), "StartFullTutorial", nameof(BeforeOtherModeStart), null);
            n += P(h, typeof(GameCoordinatorManager), "StartQuickTutorial", nameof(BeforeOtherModeStart), null);
            n += P(h, typeof(GameCoordinatorManager), "StartNPCTestingArea", nameof(BeforeOtherModeStart), null);
            n += P(h, typeof(GameCoordinatorManager), "StartMultiplayerFromHost", nameof(BeforeOtherModeStart), null);
            // harmony-target: SessionBackup.CaptureSnapshot
            n += P(h, typeof(Game.Runtime.Systems.SaveSystem.SessionBackup), "CaptureSnapshot", null, nameof(AfterCaptureSnapshot));
            return n;
        }

        private static int P(Harmony h, Type type, string method, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) throw new MissingMethodException(type.Name, method);
                h.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(State), prefix),
                                postfix: postfix == null ? null : new HarmonyMethod(typeof(State), postfix));
                return 1;
            }
            catch (Exception e) { Plugin.Fail($"{type.Name}.{method}", e); return 0; }
        }

        /// <summary>Sets the stored flag (saved to the config at once), refreshes Active and starts a fresh skip log.</summary>
        internal static void SetRun(bool sandbox, string why)
        {
            bool was = Plugin.RunIsSandbox.Value;
            if (was != sandbox) Plugin.RunIsSandbox.Value = sandbox;   // saved to rogue.sandbox.cfg
            Guards.NewRun();
            Plugin.Recompute();
            if (sandbox || was) Plugin.Log.LogInfo($"[Sandbox] {(sandbox ? "SANDBOX run" : "normal run (sandbox off)")}: {why}");
        }

        // ---------------------------------------------------------------- main menu

        private static void BeforeSingleplayerButton()
        {
            try { if (!FromSandboxButton) Pending = false; } catch { }
        }

        /// <summary>
        /// The car choice opens synchronously inside OnSingleplayerButton (IDA 0x830F30 calls 0x9098B0 directly), so the
        /// SANDBOX button's call still has FromSandboxButton set here. Any other way in (MainMenuPanel.OnPlayButton's direct
        /// branch, UIMiniMenu.SinglePlayer, a panel's back / close) is a normal car choice and drops a stale Pending.
        /// </summary>
        private static void BeforeOpenVehicleSelection()
        {
            try { if (!FromSandboxButton) Pending = false; } catch { }
        }

        private static void BeforeOtherModeButton()
        {
            // Tutorial / Multiplayer: not a sandbox run. Safe even if the player backs out: Continue re-reads the
            // snapshot's own mark (BeforeRestore), and a retry inside a run never passes through the main menu.
            try { Pending = false; if (Plugin.RunIsSandbox.Value) SetRun(false, "Tutorial / Multiplayer chosen on the main menu"); }
            catch { }
        }

        private static void AfterVehicleSelectionBack()
        {
            try { Pending = false; } catch { }
        }

        private static void BeforeStartGameWithVehicle()
        {
            try { _menuStart = true; } catch { }
        }

        private static void AfterStartGameWithVehicle()
        {
            try { _menuStart = false; Pending = false; } catch { }
        }

        // ---------------------------------------------------------------- run starts

        private static void BeforeStartNew()
        {
            try
            {
                if (_menuStart)
                {
                    _menuStart = false;
                    bool sandbox = Pending && Plugin.AllOk;
                    Pending = false;
                    SetRun(sandbox, sandbox ? "started from the SANDBOX button" : "new run from the main menu");
                    return;
                }
                bool special = false;
                try { special = Game.Runtime.GameState.IsAnySpecialScene || Game.Runtime.GameState.IsMultiplayerMode; } catch { }
                if (special) { SetRun(false, "new run after the tutorial / test area"); return; }
                if (Plugin.RunIsSandbox.Value) SetRun(true, "retry / restart of the sandbox run");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] run start: {e.Message}"); }
        }

        private static void BeforeRestore(Game.Runtime.Systems.SaveSystem.SessionBackup.SessionSnapshot snapshot)
        {
            try
            {
                Pending = false;
                long ticks = snapshot == null ? 0 : snapshot.timestampTicks;
                bool known = TryGetMark(ticks, out bool sandbox, out bool lastSandbox);
                if (!known) sandbox = lastSandbox;   // a snapshot we didn't see taken: treat it like the latest one we did
                SetRun(sandbox, $"restored run snapshot ({(known ? "recorded" : "not recorded: same as the last snapshot")})");
            }
            catch (Exception e)
            {
                // can't tell: keep a sandbox flag rather than letting a possibly-sandbox run onto the records
                Plugin.Log.LogWarning($"[Sandbox] restore check failed, the flag stays {(Plugin.RunIsSandbox.Value ? "on" : "off")}: {e.Message}");
            }
        }

        private static void BeforeOtherModeStart()
        {
            try { Pending = false; SetRun(false, "tutorial / multiplayer / test area"); }
            catch { }
        }

        private static void AfterCaptureSnapshot(Game.Runtime.Systems.SaveSystem.SessionBackup.SessionSnapshot __result)
        {
            try
            {
                if (__result == null) return;
                bool mp = false;
                try { mp = Game.Runtime.GameState.IsMultiplayerMode; } catch { }
                AddMark(__result.timestampTicks, Plugin.RunIsSandbox.Value && !mp);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] snapshot mark: {e.Message}"); }
        }

        // ---------------------------------------------------------------- snapshot marks "ticks=1;ticks=0;..." (newest last)

        private static List<KeyValuePair<long, bool>> ReadMarks()
        {
            var list = new List<KeyValuePair<long, bool>>();
            foreach (var part in (Plugin.SnapshotMarks.Value ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (long.TryParse(part.Substring(0, eq), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
                    list.Add(new KeyValuePair<long, bool>(t, part.Substring(eq + 1) == "1"));
            }
            return list;
        }

        private static void AddMark(long ticks, bool sandbox)
        {
            var list = ReadMarks();
            list.RemoveAll(k => k.Key == ticks);
            list.Add(new KeyValuePair<long, bool>(ticks, sandbox));
            while (list.Count > MaxMarks) list.RemoveAt(0);
            var parts = new List<string>();
            foreach (var k in list) parts.Add(k.Key.ToString(CultureInfo.InvariantCulture) + "=" + (k.Value ? "1" : "0"));
            Plugin.SnapshotMarks.Value = string.Join(";", parts);   // saved to rogue.sandbox.cfg
        }

        private static bool TryGetMark(long ticks, out bool sandbox, out bool lastSandbox)
        {
            var list = ReadMarks();
            lastSandbox = list.Count > 0 && list[list.Count - 1].Value;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Key == ticks) { sandbox = list[i].Value; return true; }
            sandbox = false;
            return false;
        }
    }
}
