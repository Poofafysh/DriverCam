using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RogueShared;

namespace Sandbox
{
    /// <summary>
    /// Sandbox: a separate run mode started from a SANDBOX button on the main menu (a copy of the Singleplayer button).
    /// While a sandbox run is active (<see cref="Active"/>): every card is free (rerolls too, selling pays 0), locked mods
    /// are offered (nothing is saved as unlocked), the game's own hidden all-cards picker is shown in the shop (its
    /// buy-all button hidden), there are 20 mod slots (10 + ExtraSlots) with the Current Mods row in two rows, roads
    /// are longer (LengthMultiplier, x2 by default) and wide (Maps*.cs: 6 lanes / 30 m by default, buildings and props
    /// hidden, a new road with barriers and street lights built along each tile's own path), and a SANDBOX tag is on the HUD.
    ///
    /// Records guard (the player's decision, 2026-10-04): a sandbox run never reaches the Steam leaderboards,
    /// achievements, missions, XP, credits, unlocks or statistics. Each is blocked at its source by a Harmony prefix on a
    /// game method with its own code, or (boss progress) whose shared code is the same record write (Guards.cs). Not
    /// blocked: the per-card "times acquired" count (its code is ACardSO.OnCardAcquired, which card effects need).
    /// Checkpoint restores are free (TrySpendCredits reports success without spending): a sandbox perk. If ANY guard or run-state patch fails to install,
    /// the SANDBOX button is not added, so a sandbox run can't be started (and a stored sandbox run gets no perks).
    ///
    /// The sandbox flag: set when the SANDBOX button starts a run, kept for Continue / Retry / Next of that run (saved in
    /// the config, and matched to the game's session snapshots by their timestamp), cleared by every other run start
    /// (Singleplayer, tutorials, multiplayer, NPC test area, restoring a non-sandbox snapshot). In multiplayer
    /// only during an agreed multiplayer sandbox run (Multiplayer.cs: Recompute / ActiveNow call into it). Scenery for sandbox maps lives in Scenery.cs and only acts while Active is true.
    /// </summary>
    [BepInPlugin(Guid, "Sandbox", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.sandbox";
        public const string Version = "0.3.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, AllCardsPicker;
        internal static ConfigEntry<int> ExtraSlots;
        internal static ConfigEntry<float> LengthMultiplier;
        internal static ConfigEntry<bool> RunIsSandbox;
        internal static ConfigEntry<string> SnapshotMarks;
        internal static ConfigEntry<int> DefaultsVersion;

        /// <summary>True when every guard and run-state patch is installed: only then can a sandbox run start or get perks.</summary>
        internal static bool AllOk;
        internal static readonly List<string> Failed = new List<string>();

        /// <summary>True while the current run is a sandbox run (read by Scenery and other plugins). Main thread only.</summary>
        public static bool Active { get; internal set; }

        /// <summary>A sandbox run is loaded (any scene but multiplayer): mod slot count (menu snapshots keep their 20 mods).</summary>
        internal static bool InSandboxRun { get; private set; }

        private static bool _mpCached;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription(
                "Adds the SANDBOX button to the main menu. Off = no button (a sandbox run already in progress keeps its guards).",
                null, HubLink.Meta("SANDBOX button on the main menu", applies: "main menu")));
            ExtraSlots = Config.Bind("Mods", "ExtraSlots", 10, new ConfigDescription(
                "Mod slots added in sandbox runs (the game has 10; 10 here = 20). The game itself never allows more than 25 free slots.",
                new AcceptableValueRange<int>(0, 15), HubLink.Meta("Extra mod slots", 0, 15, 1, applies: "now")));
            AllCardsPicker = Config.Bind("Mods", "AllCardsPicker", true, new ConfigDescription(
                "Show the game's own all-cards picker (search + category filter) in the shop during sandbox runs. Its buy-all button stays hidden.",
                null, HubLink.Meta("All-cards picker in the shop", applies: "next shop")));
            LengthMultiplier = Config.Bind("Run", "LengthMultiplier", 2f, new ConfigDescription(
                "Road length of each sandbox race (x1 = the game's own length, x2 = the default). Longer roads load more road tiles (slower loading, more memory). In multiplayer the host's road is used.",
                new AcceptableValueRange<float>(1f, 5f), HubLink.Meta("Road length", 1, 5, 0.5, "x", applies: "next race")));
            RunIsSandbox = Config.Bind("State", "RunIsSandbox", false, new ConfigDescription(
                "Written by the plugin: true while the current run is a sandbox run (kept for Continue). Not a setting.",
                null, HubLink.Meta(hidden: true)));
            SnapshotMarks = Config.Bind("State", "SnapshotMarks", "", new ConfigDescription(
                "Written by the plugin: timestamps of the game's recent run snapshots and whether each was a sandbox run. Not a setting.",
                null, HubLink.Meta(hidden: true)));
            DefaultsVersion = Config.Bind("State", "DefaultsVersion", 0, new ConfigDescription(
                "Written by the plugin: which default changes were applied to this config. Not a setting.",
                null, HubLink.Meta(hidden: true)));
            WideRoads.Bind(Config);
            RoadShape.Bind(Config);
            MigrateDefaults();

            var harmony = new Harmony(Guid);
            int guards = 0, state = 0, perks = 0;
            try { guards = Guards.Install(harmony); }
            catch (Exception e) { Fail("guards", e); }
            try { state = State.Install(harmony); }
            catch (Exception e) { Fail("run state", e); }
            AllOk = Failed.Count == 0;
            try { perks = Shop.Install(harmony) + RoadLength.Install(harmony); }
            catch (Exception e) { Log.LogWarning($"[Sandbox] perks not installed: {e.Message}"); }
            try { perks += WideRoads.Install(harmony); }   // a failure = the game's road in sandbox races; the SANDBOX button stays
            catch (Exception e) { WideRoads.Installed = false; Log.LogWarning($"[Sandbox] maps not installed: {e.Message}"); }
            try { if (AllOk) MenuButton.Install(harmony); }
            catch (Exception e) { Log.LogWarning($"[Sandbox] SANDBOX button hook not installed: {e.Message}"); MenuButton.Off("hook failed"); }

            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            try { ClassInjector.RegisterTypeInIl2Cpp<MapsRunner>(); AddComponent<MapsRunner>(); }
            catch (Exception e) { WideRoads.Installed = false; Log.LogWarning($"[Sandbox] maps runner not started (sandbox races keep the game's road): {e.Message}"); }
            try { SceneryHost.Init(Config, this); }
            catch (Exception e) { Log.LogWarning($"[Sandbox] scenery not started: {e.Message}"); }
            try { Multiplayer.Init(Config, harmony); }   // after the guards, maps and scenery: it reports what this game can play
            catch (Exception e) { Log.LogWarning($"[Sandbox] multiplayer sandbox not started (multiplayer runs stay normal): {e.Message}"); }

            HubLink.Status(Guid, HubStatus);
            if (AllOk)
                Log.LogInfo($"Sandbox {Version} loaded. {guards} record guards, {state} run-state hooks, {perks} sandbox perks installed; " +
                            (Enabled.Value ? "the SANDBOX button is on the main menu." : "the SANDBOX button is switched off ([General] Enabled).") +
                            (RunIsSandbox.Value ? " The stored run is a sandbox run (Continue keeps it in sandbox mode)." : "") +
                            WideRoads.LoadText());
            else
                Log.LogWarning($"Sandbox {Version} loaded WITHOUT the SANDBOX button: these guard / run-state patches failed, so a sandbox run could reach your records: {string.Join("; ", Failed)}");
        }

        /// <summary>
        /// One-time default changes for configs written by an older version (a changed default alone never reaches an
        /// existing config). 1 (0.2.0): road length x1 -> x2, only when it was still the old default x1.
        /// </summary>
        private static void MigrateDefaults()
        {
            try
            {
                if (DefaultsVersion.Value >= 1) return;
                if (Math.Abs(LengthMultiplier.Value - 1f) < 0.001f)
                {
                    LengthMultiplier.Value = 2f;
                    Log.LogInfo("[Sandbox] settings updated to the 0.2.0 defaults: road length x1 -> x2 ([Run] LengthMultiplier; set it back to 1 for the game's length)");
                }
                DefaultsVersion.Value = 1;   // saved to rogue.sandbox.cfg
            }
            catch (Exception e) { Log.LogWarning($"[Sandbox] settings update skipped: {e.Message}"); }
        }

        internal static void Fail(string what, Exception e)
        {
            Failed.Add($"{what}: {e.GetType().Name}: {e.Message}");
        }

        /// <summary>
        /// A sandbox run in a single-player game scene. Used by the guards and one-off hooks. The scene part
        /// (GameState.IsGameScene reads the active scene's name, a managed string per call) is read at most once per frame
        /// (cached against Time.frameCount, also refreshed by <see cref="Recompute"/>); scene loads finish between frames, so
        /// the cache can't be stale within a frame. The stored flag is read live, and a failed read is never cached.
        /// </summary>
        internal static bool GuardNow()
        {
            if (!RunIsSandbox.Value) return false;
            int frame = -1;
            try { frame = UnityEngine.Time.frameCount; } catch { }
            if (frame >= 0 && frame == _sceneFrame) return _sceneGuard;
            bool g;
            try { g = Game.Runtime.GameState.IsGameScene && !Game.Runtime.GameState.IsMultiplayerMode; }
            catch { return true; }   // can't tell: guard (never let a sandbox run through)
            if (frame >= 0) { _sceneFrame = frame; _sceneGuard = g; }
            return g;
        }

        private static int _sceneFrame = -1;
        private static bool _sceneGuard;

        /// <summary>
        /// For guards that can sit on a per-frame path (AObjective.SetCompleted): no game call when the stored run is not a
        /// sandbox run, or when the cached state (Recompute, 4x a second) already says sandbox run + single-player game
        /// scene. Only a "no" from the cache is re-checked (GuardNow, at most one scene read per frame), so a stale cache
        /// right after entering a race never lets a record write through; a stale "yes" for up to 0.25 s after leaving the race only leaves an
        /// objective not completed yet (the game re-checks it).
        /// </summary>
        internal static bool GuardFast()
        {
            if (!RunIsSandbox.Value) return false;
            if (_guardCached) return true;
            return GuardNow();
        }

        private static bool _guardCached;

        /// <summary>Fresh check for perks: guards installed and a sandbox run in a single-player game scene.</summary>
        internal static bool ActiveNow() => AllOk && (GuardNow() || Multiplayer.GuardNow());   // + an agreed multiplayer sandbox run (Multiplayer.cs reads this call)

        /// <summary>
        /// The road of the race being picked right now belongs to a sandbox run (LevelGeneratorTileSelector.GetRandomTiles
        /// and the service-tile checks inside it). Not tied to the game scene: GameCoordinatorManager.SetupGameInfo
        /// (0x1808D5E90) generates the level from StartNewSingleplayerGame / TryRestoreSingleplayerGame while the MAIN MENU
        /// is still loaded (the first race of a run), and State.cs sets the run flag in those methods' prefixes, before
        /// SetupGameInfo. Tutorials / the NPC test area clear the flag first (State.cs). Multiplayer: the host's tile pick of
        /// an agreed sandbox run (Multiplayer.GuardNow).
        /// </summary>
        internal static bool TilePickNow()
        {
            if (!AllOk) return false;
            bool mp;
            try { mp = Game.Runtime.GameState.IsMultiplayerMode; } catch { mp = _mpCached; }
            if (RunIsSandbox.Value && !mp) return true;
            return Multiplayer.GuardNow();
        }

        /// <summary>Re-reads the scene and the flag into the cached <see cref="Active"/> / <see cref="InSandboxRun"/> (Runner calls it 4x a second; state hooks after a change).</summary>
        internal static void Recompute()
        {
            bool game = false, mp = _mpCached, read = false;
            try { game = Game.Runtime.GameState.IsGameScene; mp = Game.Runtime.GameState.IsMultiplayerMode; read = true; } catch { }
            _mpCached = mp;
            _guardCached = read && RunIsSandbox.Value && game && !mp;   // a failed read leaves it false: GuardFast then checks fresh
            if (read) { try { _sceneFrame = UnityEngine.Time.frameCount; _sceneGuard = game && !mp; } catch { _sceneFrame = -1; } }
            bool run = AllOk && RunIsSandbox.Value && !mp;
            // an agreed multiplayer sandbox run (Multiplayer.cs; it reads this call) in the multiplayer race scene
            bool mpRace = AllOk && read && Multiplayer.SandboxAgreed(mp);
            InSandboxRun = run || (AllOk && Multiplayer.SessionOn);
            Active = (run && game) || mpRace;
        }

        private static string HubStatus()
        {
            if (!AllOk) return "unavailable: a guard failed to install (see the log)";
            if (Active) return $"SANDBOX run: free cards, {10 + ExtraSlots.Value} mod slots, road x{LengthMultiplier.Value:0.#}{WideRoads.Describe()}; off the leaderboards";
            if (RunIsSandbox.Value) return "the stored run is a sandbox run";
            return Enabled.Value ? "start one with SANDBOX on the main menu" : "SANDBOX button off";
        }
    }
}
