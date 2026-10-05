using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RogueShared;
using Game.Runtime.Manager;
using Game.Runtime.Systems.LevelGeneration;

namespace Sandbox
{
    /// <summary>The sandbox settings that must be the same for every player of a multiplayer sandbox run.</summary>
    internal struct SbSettings
    {
        public float Length;   // [Run] LengthMultiplier, 1-5 (only the host generates the road; sent for the HUD / logs)
        public int Slots;      // [Mods] ExtraSlots, 0-15
        public bool Picker;    // [Mods] AllCardsPicker
        public bool Wide;      // [Maps] WideRoads (only when wide roads are installed on the host)
        public float Width;    // [Maps] Width, 20-40 m
        public int Lanes;      // [Maps] Lanes, 4-8
        public bool Strip;     // [Scenery] StripBuildings (hidden objects' colliders go too: must match)
        public bool Lights;    // [Scenery] HideLights (cosmetic)
        public bool Poles;     // [Maps] StreetLights (cosmetic: the poles are visual-only, no collider)

        internal static SbSettings Local()
        {
            var s = new SbSettings();
            float len = Plugin.LengthMultiplier.Value;
            s.Length = float.IsNaN(len) ? 1f : Math.Max(1f, Math.Min(5f, len));
            s.Slots = Math.Max(0, Math.Min(15, Plugin.ExtraSlots.Value));
            s.Picker = Plugin.AllCardsPicker.Value;
            s.Wide = WideRoads.Installed && WideRoads.Value != null && WideRoads.Value.Value;
            float w = WideRoads.Width == null ? 30f : WideRoads.Width.Value;
            s.Width = float.IsNaN(w) ? 30f : Math.Max(20f, Math.Min(40f, w));
            s.Lanes = WideRoads.Lanes == null ? 6 : Math.Max(4, Math.Min(8, WideRoads.Lanes.Value));
            s.Strip = SceneryHost.Enabled != null && SceneryHost.Enabled.Value;
            s.Lights = SceneryHost.HideLights != null && SceneryHost.HideLights.Value;
            s.Poles = WideRoads.StreetLights != null && WideRoads.StreetLights.Value;
            return s;
        }

        internal int Flags => (Picker ? 1 : 0) | (Wide ? 2 : 0) | (Strip ? 4 : 0) | (Lights ? 8 : 0) | (Poles ? 16 : 0);

        internal void Write(SbWire w) { w.F32(Length); w.U8(Slots); w.U8(Flags); w.F32(Width); w.U8(Lanes); }

        internal static SbSettings Read(ref SbReader r)
        {
            var s = new SbSettings { Length = r.F32(), Slots = r.U8() };
            int f = r.U8();
            s.Width = r.F32(); s.Lanes = r.U8();
            s.Picker = (f & 1) != 0; s.Wide = (f & 2) != 0; s.Strip = (f & 4) != 0; s.Lights = (f & 8) != 0; s.Poles = (f & 16) != 0;
            // clamp whatever came in to the plugin's own ranges
            s.Length = Math.Max(1f, Math.Min(5f, s.Length));
            s.Slots = Math.Max(0, Math.Min(15, s.Slots));
            s.Width = Math.Max(20f, Math.Min(40f, s.Width <= 0f ? 30f : s.Width));
            s.Lanes = Math.Max(4, Math.Min(8, s.Lanes));
            return s;
        }

        internal bool Same(in SbSettings o) =>
            Math.Abs(Length - o.Length) < 0.001f && Slots == o.Slots && Flags == o.Flags && Math.Abs(Width - o.Width) < 0.001f && Lanes == o.Lanes;

        public override string ToString() =>
            $"road x{Length.ToString("0.#", CultureInfo.InvariantCulture)}, {10 + Slots} mod slots, all-cards picker {(Picker ? "on" : "off")}, " +
            (Wide ? $"sandbox map {Width:0} m / {Lanes} lanes{(Poles ? ", street lights" : "")}" : "the game's road") +
            $", scenery {(Strip ? "stripped" : "kept")}{(Strip && Lights ? " (lights off)" : "")}";
    }

    /// <summary>
    /// Sandbox in multiplayer (session fd5438 for the 0.2.x fork). The host chooses SANDBOX for the lobby ([Multiplayer]
    /// Sandbox, or the SANDBOX switch on the lobby panel, MultiplayerLobby.cs); every player needs Sandbox, the same
    /// version, all record guards installed and the multiplayer hooks; then every player's game runs the same sandbox
    /// run: the same perks, the same guards, the host's settings.
    ///
    /// How a multiplayer race is built (GameAssembly.dll, IDA, 2026-10-04):
    ///   - Host: MultiplayerPanel.StartGame / updatePlayerList (main menu lobby) -> GameCoordinatorManager.
    ///     StartMultiplayerFromHost (0x8D6140) -> SetupGameInfo(Multiplayer) -> LevelGeneratorTileSelector.Setup +
    ///     GenerateLevel -> GenerateRegularLevel -> GetRandomTiles (RoadLength's prefix: length, width) and
    ///     ShouldSpawnGasStation / ShouldSpawnCarWash; then GetCurrentLevelIdList -> MultiplayerLobbyManager.SetupGameInfo
    ///     -> RPC_SetLevelTiles etc. to every client -> MoveToMultiplayerScene. NextMultiplayerFromHost (0x8D44E0, from
    ///     MultiplayerLobbyManager._NextMultiplayerFromHost) does the same for the next race of the run.
    ///   - So ONLY THE HOST picks tiles; the road length and the gas station / car wash choice reach the clients inside
    ///     the game's own tile list. In the scene "01_GameScene_Multiplayer" (GameState.IsMultiplayerMode; IsGameScene is
    ///     only "01_GameScene"), LevelGenerator.Start (0x796C80) spawns the host's own list when Mirror.NetworkServer.active,
    ///     and on a pure client WaitAndStartLevelGeneration -> GenerateMultiplayerLevelAsClient (0x795CF0) builds the
    ///     list from MultiplayerLobbyManager.levelTiles via FindTileById. Both then run SpawnTilesCoroutine ->
    ///     RoadPathGenerator.GeneratePath (WideRoads' build).
    ///   - Card shop, card slots, the all-cards picker, run currency, XP / credits / unlocks and records are per player
    ///     (no Command / RPC touches cards or credits). Traffic, AI racers and obstacles are host-run and synced as lane
    ///     offsets / distances, so the road width and lane count must match on every machine.
    ///
    /// So: the host's StartMultiplayerFromHost prefix decides the run (sandbox only if every lobby member agreed to the
    /// current offer), sends it, and marks the host's tile pick (<see cref="GuardNow"/> true while it runs, so RoadLength
    /// and WideRoads act on the host although its scene is the menu). Each client turns the run on from the host's
    /// message and applies the host's width / lanes in a GenerateMultiplayerLevelAsClient prefix (WideRoads.Apply), before
    /// any tile exists. Every client reports what it applied; a mismatch is logged and toasted.
    ///
    /// The run stays sandbox until the session ends (Mirror stops), the host starts another run, or the plugin unloads;
    /// guards hold for the whole run on every player (Guards.cs calls <see cref="GuardNow"/> / <see cref="GuardFast"/>,
    /// leaderboards <see cref="LeaderboardBlocked"/>). Nothing here writes Mirror-synced state; the channel is
    /// SteamLink (7742, MultiplayerNet.cs).
    ///
    /// Message bodies (after the "RSBX" / protocol / type header, SbWire):
    ///   STATE  host -> every lobby member, every second and at once on a change / run start:
    ///          hostVersion str, hostCaps u32, offerRev u32, want u8, offer settings, agreed u8, runId u32, raceNo u32,
    ///          runFlags u8 (1 run active, 2 sandbox), run settings, why str
    ///   HELLO  client -> host, every second: version str, caps u32, ackRev u32, ackOk u8, reason str,
    ///          appliedRun u32, appliedRace u32, appliedFlags u8 (1 sandbox on, 2 wide road), appliedWidth f32, appliedLanes u8
    ///   BYE    either way: reason str
    ///   settings = length f32, extraSlots u8, flags u8 (1 picker, 2 wide, 4 strip, 8 hide lights, 16 street lights), width f32, lanes u8
    /// </summary>
    internal static class Multiplayer
    {
        internal const uint CapGuards = 1, CapWide = 2, CapScenery = 4, CapClientHook = 8, CapHostHooks = 16,
                            CapPluginHooks = 32, CapWideHooks = 64, CapShopHooks = 128, CapSceneryHooks = 256, CapNet = 512;

        internal static ConfigEntry<bool> HostSandbox;

        /// <summary>The multiplayer side is set up (hooks installed, Mirror readable). False = multiplayer stays normal.</summary>
        internal static bool Available { get; private set; }
        internal static string Unavailable { get; private set; } = "not started";
        internal static uint MyCaps { get; private set; }

        /// <summary>This machine runs an agreed multiplayer sandbox run (lobby or race). Main thread only.</summary>
        internal static bool SessionOn { get; private set; }
        /// <summary>The settings of that run (the host's).</summary>
        internal static SbSettings Run;

        private static bool _generating;          // host: inside StartMultiplayerFromHost / NextMultiplayerFromHost of a sandbox run
        private static int _generatingFrame = -1;
        private static bool _lastRunSandbox;       // the latest multiplayer run was sandbox (leaderboards), until the next run starts
        private static bool _pluginHookSeen;
        private static int _sceneFrame = -1;
        private static bool _sceneMp, _cachedMp;

        // ------------------------------------------------------------------ hooks for the other Sandbox files (cheap; main thread)

        /// <summary>
        /// Plugin.Recompute: true when the current run is an agreed multiplayer sandbox run and this is the multiplayer race
        /// scene. Lifts Plugin.Active's !IsMultiplayerMode gate for that run only.
        /// </summary>
        internal static bool SandboxAgreed(bool mpScene)
        {
            _pluginHookSeen = true;
            _cachedMp = mpScene;
            return SessionOn && mpScene;
        }

        /// <summary>Record guards and Plugin.ActiveNow in multiplayer: an agreed sandbox run, in the multiplayer scene or during the host's tile pick.</summary>
        internal static bool GuardNow()
        {
            if (!SessionOn) return false;
            if (_generating) return true;
            int frame = -1;
            try { frame = UnityEngine.Time.frameCount; } catch { }
            if (frame >= 0 && frame == _sceneFrame) return _sceneMp;
            bool mp;
            try { mp = Game.Runtime.GameState.IsMultiplayerMode; }
            catch { return true; }   // can't tell: guard
            if (frame >= 0) { _sceneFrame = frame; _sceneMp = mp; }
            return mp;
        }

        /// <summary>Per-frame guard path (AObjective.SetCompleted): no game call while the cached scene already says multiplayer.</summary>
        internal static bool GuardFast()
        {
            if (!SessionOn) return false;
            if (_cachedMp || _generating) return true;
            return GuardNow();
        }

        /// <summary>Leaderboard prefixes: block while a multiplayer sandbox run is on, and after it until the next run starts (any scene).</summary>
        internal static bool LeaderboardBlocked() => SessionOn || _lastRunSandbox;

        /// <summary>The latest multiplayer run was sandbox (kept until the next run starts).</summary>
        internal static bool LastRunSandbox => _lastRunSandbox;

        internal static bool Wide(bool local) => SessionOn ? Run.Wide : local;
        internal static float Width(float local) => SessionOn ? Run.Width : local;
        internal static int Lanes(int local) => SessionOn ? Run.Lanes : local;
        internal static int Slots(int local) => SessionOn ? Run.Slots : local;
        internal static bool Picker(bool local) => SessionOn ? Run.Picker : local;
        internal static bool Strip(bool local) => SessionOn ? Run.Strip : local;
        internal static bool HideLights(bool local) => SessionOn ? Run.Lights : local;
        internal static float Length(float local) => SessionOn ? Run.Length : local;
        internal static bool StreetLights(bool local) => SessionOn ? Run.Poles : local;

        // ------------------------------------------------------------------ setup

        /// <summary>Plugin.Load: binds [Multiplayer], installs the run hooks, starts the runner. Never throws out.</summary>
        internal static void Init(ConfigFile config, Harmony h)
        {
            HostSandbox = config.Bind("Multiplayer", "Sandbox", false, new ConfigDescription(
                "When you HOST a multiplayer lobby: make its runs sandbox runs (free cards, more slots, your road / wide road / scenery settings, " +
                "off every record for every player). Every player needs Sandbox, the same version; otherwise the run is a normal run. Also on the lobby panel.",
                null, HubLink.Meta("Sandbox in multiplayer (host)", applies: "next multiplayer run")));
            try
            {
                MirrorApi.Init();
                if (!MirrorApi.Ok) { Off($"Mirror / multiplayer members not readable: {MirrorApi.Missing}"); return; }
                uint caps = 0;
                int hostHooks = 0;
                // harmony-target: GameCoordinatorManager.StartMultiplayerFromHost (cooperates with Sandbox State.cs's own prefix: both only record state)
                hostHooks += P(h, typeof(GameCoordinatorManager), "StartMultiplayerFromHost", nameof(BeforeHostStart), nameof(AfterHostGenerate));
                // harmony-target: GameCoordinatorManager.NextMultiplayerFromHost
                hostHooks += P(h, typeof(GameCoordinatorManager), "NextMultiplayerFromHost", nameof(BeforeHostNext), nameof(AfterHostGenerate));
                if (hostHooks == 2) caps |= CapHostHooks;
                // harmony-target: LevelGenerator.GenerateMultiplayerLevelAsClient
                if (P(h, typeof(LevelGenerator), "GenerateMultiplayerLevelAsClient", nameof(BeforeClientTiles), null) == 1) caps |= CapClientHook;
                // harmony-target: GameCoordinatorManager.StartNewSingleplayerGame (cooperates with Sandbox State.cs), GameCoordinatorManager.TryRestoreSingleplayerGame (cooperates with Sandbox State.cs)
                P(h, typeof(GameCoordinatorManager), "StartNewSingleplayerGame", nameof(BeforeSingleRun), null);
                P(h, typeof(GameCoordinatorManager), "TryRestoreSingleplayerGame", nameof(BeforeSingleRun), null);
                if (Plugin.AllOk) caps |= CapGuards;
                if (WideRoads.Installed) caps |= CapWide;
                if (SceneryHost.Enabled != null) caps |= CapScenery;
                caps |= HookCaps();
                MyCaps = caps;
                ClassInjector.RegisterTypeInIl2Cpp<MultiplayerRunner>();
                IL2CPPChainloader.AddUnityComponent(typeof(MultiplayerRunner));
                Available = (caps & (CapHostHooks | CapClientHook)) == (CapHostHooks | CapClientHook);
                Unavailable = Available ? null : "a multiplayer run hook failed to install";
                Plugin.Log.LogInfo($"[Sandbox] multiplayer: {(Available ? "ready" : "NOT available (" + Unavailable + ")")}; {DescribeCaps(caps)}; " +
                                   $"hosting sandbox is {(HostSandbox.Value ? "ON" : "off")} ([Multiplayer] Sandbox / the lobby panel)");
            }
            catch (Exception e) { Off(e.Message); }
        }

        private static void Off(string why)
        {
            Available = false;
            Unavailable = why;
            Plugin.Log.LogWarning($"[Sandbox] multiplayer sandbox not available (multiplayer runs stay normal): {why}");
        }

        private static int P(Harmony h, Type type, string method, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) throw new MissingMethodException(type.Name, method);
                h.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(Multiplayer), prefix),
                                postfix: postfix == null ? null : new HarmonyMethod(typeof(Multiplayer), postfix));
                return 1;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] multiplayer hook {type.Name}.{method} not installed: {e.Message}"); return 0; }
        }

        /// <summary>
        /// Which of the one-line hooks the other Sandbox files must call are in this build (their IL is read once):
        /// without them a client would use its own settings, so the host refuses a sandbox run that needs them.
        /// </summary>
        private static uint HookCaps()
        {
            uint caps = 0;
            if (Calls(typeof(Plugin), "Recompute", nameof(SandboxAgreed)) && Calls(typeof(Plugin), "ActiveNow", nameof(GuardNow))) caps |= CapPluginHooks;
            var wr = typeof(WideRoads);
            if (Calls(wr, "Apply", nameof(Wide)) && Calls(wr, "ClampW", nameof(Width)) && Calls(wr, "ClampN", nameof(Lanes))) caps |= CapWideHooks;
            if (Calls(typeof(Shop), "MoreSlots", nameof(Slots)) && Calls(typeof(Shop), "AfterPickerAwake", nameof(Picker)) && Calls(typeof(Runner), "Update", nameof(Picker))) caps |= CapShopHooks;
            if (Calls(typeof(SceneryRunner), "Update", nameof(Strip)) && Calls(typeof(SceneryRunner), "StripSlice", nameof(HideLights))) caps |= CapSceneryHooks;
            return caps;
        }

        /// <summary>True if <paramref name="method"/>'s IL has a call to Multiplayer.<paramref name="target"/>.</summary>
        private static bool Calls(Type type, string method, string target)
        {
            try
            {
                var m = AccessTools.Method(type, method);
                var il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return false;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28) continue;   // call <token>
                    int token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                    if ((token >> 24) != 0x06 && (token >> 24) != 0x0A) continue;   // MethodDef / MemberRef
                    MethodBase called;
                    try { called = m.Module.ResolveMethod(token); } catch { continue; }
                    if (called != null && called.DeclaringType == typeof(Multiplayer) && called.Name == target) return true;
                }
            }
            catch { /* treated as missing */ }
            return false;
        }

        internal static string DescribeCaps(uint c)
        {
            var parts = new List<string>();
            if ((c & CapGuards) == 0) parts.Add("record guards NOT all installed");
            if ((c & CapPluginHooks) == 0) parts.Add("Plugin.cs hook missing (no perks in multiplayer)");
            if ((c & CapWideHooks) == 0) parts.Add("WideRoads.cs hooks missing (no wide roads in multiplayer)");
            if ((c & CapShopHooks) == 0) parts.Add("Shop.cs / Runner.cs hooks missing (slots / picker from each player's own settings)");
            if ((c & CapSceneryHooks) == 0) parts.Add("Scenery.cs hooks missing (no scenery strip in multiplayer)");
            if ((c & CapWide) == 0) parts.Add("wide roads not installed");
            if ((c & CapScenery) == 0) parts.Add("scenery not started");
            return parts.Count == 0 ? "every hook in place" : string.Join("; ", parts);
        }

        /// <summary>What a player with <paramref name="caps"/> is missing to play a sandbox run with <paramref name="s"/>; null = nothing.</summary>
        internal static string CantPlay(uint caps, in SbSettings s)
        {
            if ((caps & CapGuards) == 0) return "its record guards are not all installed";
            if ((caps & (CapHostHooks | CapClientHook)) != (CapHostHooks | CapClientHook)) return "its multiplayer run hooks failed";
            if ((caps & CapPluginHooks) == 0) return "its Sandbox build lacks the Plugin.cs multiplayer hook";
            if ((caps & CapShopHooks) == 0) return "its Sandbox build lacks the Shop.cs / Runner.cs multiplayer hooks";
            if (s.Wide && (caps & CapWide) == 0) return "wide roads are not available in its game";
            if (s.Wide && (caps & CapWideHooks) == 0) return "its Sandbox build lacks the WideRoads.cs multiplayer hooks";
            if (s.Strip && (caps & CapScenery) == 0) return "scenery stripping is not available in its game";
            if (s.Strip && (caps & CapSceneryHooks) == 0) return "its Sandbox build lacks the Scenery.cs multiplayer hooks";
            return null;
        }

        // ------------------------------------------------------------------ the run on this machine

        /// <summary>Turns this machine's multiplayer sandbox run on / off (perks, guards, the host's settings).</summary>
        internal static void SetSession(bool on, in SbSettings s, string why)
        {
            bool changed = on != SessionOn || (on && !s.Same(Run));
            if (!changed) return;
            SessionOn = on;
            Run = on ? s : default;
            if (on) { _lastRunSandbox = true; Guards.NewRun(); }
            try { Plugin.Recompute(); } catch { /* the runner refreshes it */ }
            if (on)
            {
                Plugin.Log.LogInfo($"[Sandbox] multiplayer SANDBOX run on ({why}): {s}; records guarded for every player" +
                                   (_pluginHookSeen ? "" : " (perks need the Plugin.cs hook: only the guards act in this build)"));
            }
            else Plugin.Log.LogInfo($"[Sandbox] multiplayer sandbox run off ({why})");
        }

        /// <summary>A normal multiplayer run starts: leaderboards are this run's again.</summary>
        internal static void NormalRun(string why)
        {
            SetSession(false, default, why);
            if (_lastRunSandbox) { _lastRunSandbox = false; Plugin.Log.LogInfo($"[Sandbox] multiplayer: normal run ({why})"); }
        }

        // ------------------------------------------------------------------ Harmony (never skip the game's method)

        private static void BeforeHostStart()
        {
            try { _generating = false; MpLobby.HostRunStart(next: false); _generating = SessionOn; _generatingFrame = UnityEngine.Time.frameCount; }
            catch (Exception e) { _generating = SessionOn; Plugin.Log.LogWarning($"[Sandbox] multiplayer run start: {e.Message}"); }
        }

        private static void BeforeHostNext()
        {
            try { MpLobby.HostRunStart(next: true); _generating = SessionOn; _generatingFrame = UnityEngine.Time.frameCount; }
            catch (Exception e) { _generating = SessionOn; Plugin.Log.LogWarning($"[Sandbox] multiplayer next race: {e.Message}"); }
        }

        private static void AfterHostGenerate() { _generating = false; }

        /// <summary>The runner clears a tile-pick mark whose postfix never ran (an exception in the game's method).</summary>
        internal static void ClearStaleGenerating()
        {
            if (!_generating) return;
            int f = -1;
            try { f = UnityEngine.Time.frameCount; } catch { }
            if (f != _generatingFrame) _generating = false;
        }

        /// <summary>Pure client, before its tiles are built from the host's list: the run's width / lanes (or the game's).</summary>
        private static void BeforeClientTiles()
        {
            try
            {
                // a client whose multiplayer runner stopped can't follow the host's run: never build a sandbox race then
                if (SessionOn && MpLobby.Current != MpLobby.Role.Client) Shutdown("multiplayer runner stopped: this race is normal");
                MpLobby.ClientBeforeTiles();
                WideRoads.Apply(SessionOn && Plugin.ActiveNow());
                MpLobby.ClientApplied(WideRoads.RaceWide, WideRoads.W, WideRoads.N);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] multiplayer client road setup: {e.Message}"); }
        }

        private static void BeforeSingleRun()
        {
            try
            {
                bool mp = false;
                try { mp = Game.Runtime.GameState.IsMultiplayerMode; } catch { }
                if (mp) return;
                if (SessionOn) SetSession(false, default, "single-player run started");
                _lastRunSandbox = false;
            }
            catch { /* state only */ }
        }

        /// <summary>Plugin unload / runner gone: everything off.</summary>
        internal static void Shutdown(string why)
        {
            _generating = false;
            if (SessionOn) SetSession(false, default, why);
        }
    }
}
