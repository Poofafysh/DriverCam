using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RogueShared;
using UnityEngine.InputSystem;

namespace RogueHub
{
    /// <summary>
    /// Rogue Hub: one menu for every mod. Design: "Rogue Hub: one menu for every mod" (claude.ai doc
    /// 99d98c4b-7b01-4f2a-bf9f-12982034b3a3), version 1 (Neon Garage) with a controller number pad and type-to-search.
    ///   - Hub menu (game paused): every loaded plugin's settings, read from BepInEx (no plugin has to know about the
    ///     hub), drawn as toggles, sliders with a number box, lists and key binds, grouped in tabs by area.
    ///   - Quick menu (while driving): a short GTA-style list of favourites; never pauses.
    ///   - Notifications: one stack for every mod (HubLink.Toast).
    /// Opens with the MODS button in the pause menu, LB+RB, or the backtick key (Shift + backtick = hub).
    /// Only reads and writes the plugins' own config entries and the game's input / pause plumbing; nothing it changes
    /// in the game outlives a close (menu lock, binding overrides, cursor, pause it started).
    /// </summary>
    [BepInPlugin(Guid, "RogueHub", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.hub";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, Chord, PauseButton, Toasts;
        internal static ConfigEntry<Key> QuickKey;
        internal static ConfigEntry<string> QuickItems;
        internal static ConfigEntry<float> QuickClose, ToastSeconds;

        internal const string DefaultQuick =
            "rogue.racingline|Preview|ShowLine;rogue.police|General|Mode;rogue.trafficdensity|General|Multiplier;act:rogue.pitstop|refill;rogue.engineaudio|General|Enabled";

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription("Master switch.", null, HubLink.Meta("Rogue Hub on", applies: "next game start")));
            QuickKey = Config.Bind("Open", "QuickKey", Key.Backquote, new ConfigDescription(
                "Keyboard key that opens the quick menu while driving (Shift + it opens the hub). Input System key name.", null,
                HubLink.Meta("Quick menu key (Shift = hub)")));
            Chord = Config.Bind("Open", "ControllerLBRB", true, new ConfigDescription(
                "Pressing LB and RB together opens the quick menu while driving (both bumpers are free in the game's driving controls).", null,
                HubLink.Meta("LB + RB opens the quick menu")));
            PauseButton = Config.Bind("Open", "PauseMenuButton", true, new ConfigDescription(
                "Add a MODS button to the game's pause menu (applies the next time the pause menu is created, e.g. the next race).", null,
                HubLink.Meta("MODS button in the pause menu", applies: "next race")));
            QuickItems = Config.Bind("Quick", "Items", DefaultQuick, new ConfigDescription(
                "Quick-menu rows, in order: guid|Section|Key for a setting, act:guid|id for a button. Add or remove rows with X in the hub.", null,
                HubLink.Meta("Quick-menu rows", advanced: true)));
            QuickClose = Config.Bind("Quick", "AutoCloseSeconds", 6f, new ConfigDescription(
                "The quick menu closes by itself after this many seconds without input.", new AcceptableValueRange<float>(2f, 30f),
                HubLink.Meta("Quick menu closes after", 2, 30, 1, "s")));
            Toasts = Config.Bind("Notifications", "Enabled", true, new ConfigDescription(
                "Show notifications from the mods (top right). Off = the mods show their own messages where they have them.", null,
                HubLink.Meta("Notifications")));
            ToastSeconds = Config.Bind("Notifications", "Seconds", 3.5f, new ConfigDescription(
                "How long a notification stays.", new AcceptableValueRange<float>(1f, 10f), HubLink.Meta("Notification time", 1, 10, 0.5, "s")));

            if (!Enabled.Value) { Log.LogInfo($"RogueHub {Version} loaded but switched off in its config."); return; }
            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[RogueHub] game check crashed, the hub stays off: {e}"); return; }

            // the runner first: if it can't be registered, nothing is patched either
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            try { GameApi.InstallPauseButton(new Harmony(Guid), Runner.OpenFromPauseMenu); }
            catch (Exception e) { GameApi.ButtonOk = false; Log.LogWarning($"[RogueHub] MODS button not installed (Shift + {QuickKey.Value} still opens the hub): {e.Message}"); }
            Log.LogInfo($"RogueHub {Version} loaded. {QuickKey.Value} = quick menu, Shift+{QuickKey.Value} or MODS in the pause menu = hub" +
                        (Chord.Value ? ", LB+RB = quick menu on a controller." : "."));
        }
    }
}
