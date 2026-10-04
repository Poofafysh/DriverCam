using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RogueShared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TrafficDensity
{
    /// <summary>
    /// Multiplies how many NPC traffic cars are on the road.
    ///
    /// How the game sizes traffic (DefaultAISpawner.Start @ 0x1806A6EE0):
    ///   aiSpawnCount = RoundToInt(race NPC count x spawnCountMultiplier)   // race: 6 (stage 1) .. 14 (stage 9) cars;
    ///                                                                      // spawnCountMultiplier = traffic hazard card
    ///   (spawnCountMultiplierDebug is only applied in debug builds, so it does nothing in the release game)
    ///   pool max size 100
    /// The spawner tops traffic up to aiSpawnCount all race long (TryRespawnVehicle), so changing it live works:
    /// raising it spawns more cars ahead/behind; lowering it stops spawning until cars leave.
    /// </summary>
    [BepInPlugin(Guid, "TrafficDensity", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.trafficdensity";
        public const string Version = "0.4.0";
        public const int PoolMax = 100;

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Multiplier;
        internal static ConfigEntry<int> MaxCars;
        internal static ConfigEntry<bool> AllowInMultiplayer, KeepOffLeaderboards, UsedThisRun;
        internal static ConfigEntry<string> Steps;
        internal static ConfigEntry<bool> ShowToast;
        internal static ConfigEntry<bool> FixesEnabled;
        internal static ConfigEntry<float> WreckClearDistance;
        internal static ConfigEntry<float> LaneChangeCheckBehind;
        internal static ConfigEntry<float> MinBrake;
        internal static ConfigEntry<float> ObstructionCheckInterval;
        internal static ConfigEntry<float> SpawnGap;
        internal static ConfigEntry<float> RubberBandSpeedFactor;
        internal static ConfigEntry<bool> PerfEnabled;
        // [SafeLanes] (SafeLanes.cs)
        internal static ConfigEntry<bool> SafeLanesEnabled, OneLaneAtATime, SafeLanesLogEach;
        internal static ConfigEntry<float> PlayerGapSeconds, PlayerGapMetres, CarGapSeconds, CarGapMetres, LaneCooldown;
        // [SpeedLimits] (SpeedLimits.cs)
        internal static ConfigEntry<bool> SpeedEnabled, UseSignHints, MatchSpeedometer, SpeedLogZones;
        internal static ConfigEntry<float> HighwayMph, OpenRoadMph, CityMph, CurvyMph, DriverVariation, SlowDriverFactor, CurvyDegrees, HighwayMaxDegrees;
        internal static ConfigEntry<string> CityBiomes;
        internal static bool SafeLanesHooked, SpeedHooked;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Master switch for the traffic multiplier and the AI fixes ([Fixes]).");
            // ranges + hub tags (HubLink.Meta): Rogue Hub draws these as sliders with a number box; BepInEx clamps values typed into the .cfg
            Multiplier = Config.Bind("General", "Multiplier", 1.5f, new ConfigDescription(
                "NPC traffic multiplier on top of the game's own amount (which already includes traffic hazard cards). 1 = stock, 2 = twice the cars, 0.5 = half.",
                new AcceptableValueRange<float>(0.25f, 4f), HubLink.Meta("Traffic multiplier", step: 0.05, unit: "x", applies: "within a second")));
            MaxCars = Config.Bind("General", "MaxCars", 60, new ConfigDescription(
                $"Upper limit on NPC cars at once (the game's traffic pool holds at most {PoolMax}). Very high counts cost frame rate.",
                new AcceptableValueRange<int>(10, PoolMax), HubLink.Meta("Max cars at once", step: 1, unit: "cars", applies: "within a second")));
            AllowInMultiplayer = Config.Bind("General", "AllowInMultiplayer", false, new ConfigDescription(
                "Also apply the multiplier and the AI fixes in multiplayer when you are the host. The host runs everyone's traffic, so all players get the extra cars and the changed NPC behaviour. Off = stock traffic in multiplayer. As a client nothing is ever changed: the host controls traffic.",
                null, HubLink.Meta("Also when I host multiplayer")));
            // user 2026-10-04: "Add a toggle, default: uploads count" (Leaderboard.cs)
            KeepOffLeaderboards = Config.Bind("General", "KeepOffLeaderboards", false, new ConfigDescription(
                "Don't upload a run to the Steam leaderboards if safe lane changes or speed limits changed the traffic in it. Off (default) = runs upload as usual. On without the leaderboard guard (it failed to install) = those two features stay off.",
                null, HubLink.Meta("Keep changed-traffic runs off the leaderboards", applies: "at once")));
            UsedThisRun = Config.Bind("General", "UsedThisRun", false, new ConfigDescription(
                "Written by the plugin: true once safe lane changes or speed limits changed the traffic, until the next run starts (kept across a quit and continue). Don't edit.",
                null, HubLink.Meta(hidden: true)));
            Steps = Config.Bind("Keys", "Steps", "0.5,0.75,1,1.25,1.5,2,2.5,3,4", new ConfigDescription(
                "Multiplier values Ctrl+PageUp / Ctrl+PageDown step through. Ctrl+Home resets to 1.", null, HubLink.Meta("Ctrl+PageUp / PageDown steps", advanced: true)));
            ShowToast = Config.Bind("Keys", "ShowToast", true, new ConfigDescription(
                "Show a short on-screen message when the multiplier changes or a race starts.", null, HubLink.Meta("Message when it changes")));

            // the [Fixes] values keep -1 = "the game's own value", so no BepInEx range (it would clamp -1 away): the hub tag
            // gives the slider range and shows -1 as a GAME switch
            const string game = " -1 = leave the game's value.";
            FixesEnabled = Config.Bind("Fixes", "Enabled", true, new ConfigDescription(
                "Stop NPC traffic crashing into each other and jamming (matters most with extra traffic). Off = the game's own AI values.",
                null, HubLink.Meta("Smarter traffic")));
            WreckClearDistance = Config.Bind("Fixes", "WreckClearDistance", 40f, new ConfigDescription(
                "NPC cars that crash into each other further than this many metres ahead of you are removed at once instead of blocking the lane until you pass (game: 150)." + game,
                null, HubLink.Meta("Clear crashed cars past", 10, 150, 5, "m", game: -1, gameShows: 150)));
            LaneChangeCheckBehind = Config.Bind("Fixes", "LaneChangeCheckBehind", 25f, new ConfigDescription(
                "How far behind an NPC checks the target lane before changing lanes, in metres (game: 6, so they cut in on cars beside them)." + game,
                null, HubLink.Meta("Look behind before a lane change", 5, 50, 1, "m", game: -1, gameShows: 6)));
            MinBrake = Config.Bind("Fixes", "MinBrake", 0f, new ConfigDescription(
                "Lowest throttle an NPC keeps when a car is close ahead, 0-1. 0 lets it slow right down behind a slow or stopped car (game: 0.2, Daredevil 0.6)." + game,
                null, HubLink.Meta("Lowest throttle behind a slow car", 0, 1, 0.05, game: -1, gameShows: 0.2)));
            ObstructionCheckInterval = Config.Bind("Fixes", "ObstructionCheckInterval", 0.2f, new ConfigDescription(
                "Seconds between an NPC's checks for a car ahead (game: 0.5). Must be above 0." + game,
                null, HubLink.Meta("Check for a car ahead every", 0.05, 1, 0.05, "s", game: -1, gameShows: 0.5)));
            SpawnGap = Config.Bind("Fixes", "SpawnGap", 30f, new ConfigDescription(
                "Clear road the spawner wants around a new NPC in its lane, in metres (game: 20)." + game,
                null, HubLink.Meta("Space around a new car", 10, 60, 1, "m", game: -1, gameShows: 20)));
            RubberBandSpeedFactor = Config.Bind("Fixes", "RubberBandSpeedFactor", -1f, new ConfigDescription(
                "Speed of NPCs far ahead of you, as a fraction of normal (game: 0.6). Higher = less bunching, but changes the game's pacing. -1 = leave the game's value.",
                null, HubLink.Meta("Speed of cars far ahead", 0.3, 1, 0.05, game: -1, gameShows: 0.6)));

            // [SafeLanes]: lane changes that never cut into you or another car (SafeLanes.cs)
            SafeLanesEnabled = Config.Bind("SafeLanes", "Enabled", true, new ConfigDescription(
                "NPC cars only change lanes when the target lane is really clear: they check you (and every player in multiplayer) and other cars ahead and behind, allowing for how fast each one is closing in. Off = the game's own check (6 m behind, closing speed ignored).",
                null, HubLink.Meta("Safe lane changes", applies: "at once")));
            PlayerGapSeconds = Config.Bind("SafeLanes", "YourGapSeconds", 3f, new ConfigDescription(
                "Seconds of closing speed an NPC keeps clear around you before it pulls into your lane (or one you are moving into). 3 s at 140 mph against a 60 mph car = about 110 m.",
                new AcceptableValueRange<float>(0f, 6f), HubLink.Meta("Room left for you: closing time", step: 0.25, unit: "s", applies: "at once")));
            PlayerGapMetres = Config.Bind("SafeLanes", "YourGapMetres", 15f, new ConfigDescription(
                "Clear road (bumper to bumper) an NPC always leaves in front of and behind you when it changes lanes, on top of the closing time.",
                new AcceptableValueRange<float>(0f, 60f), HubLink.Meta("Room left for you: always", step: 1, unit: "m", applies: "at once")));
            CarGapSeconds = Config.Bind("SafeLanes", "CarGapSeconds", 1.5f, new ConfigDescription(
                "The same closing-time margin around other NPC cars.",
                new AcceptableValueRange<float>(0f, 4f), HubLink.Meta("Room left for other cars: closing time", step: 0.25, unit: "s", applies: "at once")));
            CarGapMetres = Config.Bind("SafeLanes", "CarGapMetres", 8f, new ConfigDescription(
                "Clear road an NPC always leaves around other NPC cars when it changes lanes.",
                new AcceptableValueRange<float>(0f, 30f), HubLink.Meta("Room left for other cars: always", step: 1, unit: "m", applies: "at once")));
            LaneCooldown = Config.Bind("SafeLanes", "CooldownSeconds", 4f, new ConfigDescription(
                "Seconds an NPC waits after a lane change before the next one (no weaving).",
                new AcceptableValueRange<float>(0f, 15f), HubLink.Meta("Wait between lane changes", step: 0.5, unit: "s", applies: "at once")));
            OneLaneAtATime = Config.Bind("SafeLanes", "OneLaneAtATime", true, new ConfigDescription(
                "Never jump two lanes in one move (the game allows it when a car is in the way).",
                null, HubLink.Meta("One lane at a time", applies: "at once")));
            SafeLanesLogEach = Config.Bind("SafeLanes", "LogEach", false, new ConfigDescription(
                "Log every stopped lane change (lots of lines; for tuning). A summary is logged every 60 s either way.",
                null, HubLink.Meta("Log every stopped lane change", advanced: true)));

            // [SpeedLimits]: NPC speed by kind of road, in mph (SpeedLimits.cs)
            SpeedEnabled = Config.Bind("SpeedLimits", "Enabled", true, new ConfigDescription(
                "NPC traffic drives the speed limit of the road it is on (highway, open road, city, curvy) instead of the game's 68 mph everywhere. Daredevils keep their speed. Off = the game's speeds.",
                null, HubLink.Meta("Speed limits for traffic", applies: "within a second")));
            HighwayMph = Config.Bind("SpeedLimits", "HighwayMph", 70f, new ConfigDescription(
                "Limit on highway stretches (long straight stretches outside the city, and tiles with freeway signs), in mph.",
                new AcceptableValueRange<float>(30f, 110f), HubLink.Meta("Highway", step: 1, unit: "mph", applies: "within a second")));
            OpenRoadMph = Config.Bind("SpeedLimits", "OpenRoadMph", 55f, new ConfigDescription(
                "Limit on the open road outside the city (park and industrial areas), in mph.",
                new AcceptableValueRange<float>(25f, 100f), HubLink.Meta("Open road", step: 1, unit: "mph", applies: "within a second")));
            CityMph = Config.Bind("SpeedLimits", "CityMph", 35f, new ConfigDescription(
                "Limit in the city (residential and commercial areas, and tiles with slow-down 40 signs), in mph.",
                new AcceptableValueRange<float>(15f, 80f), HubLink.Meta("City / residential", step: 1, unit: "mph", applies: "within a second")));
            CurvyMph = Config.Bind("SpeedLimits", "CurvyMph", 40f, new ConfigDescription(
                "Limit on curvy stretches, in mph (only where it is lower than the area's limit). The game still slows cars a little more in the sharpest bends.",
                new AcceptableValueRange<float>(15f, 80f), HubLink.Meta("Curvy roads", step: 1, unit: "mph", applies: "within a second")));
            DriverVariation = Config.Bind("SpeedLimits", "DriverVariation", 0.08f, new ConfigDescription(
                "Each NPC driver picks a speed up to this fraction over or under the limit (0.08 = +/-8%: 55 mph -> 51-59).",
                new AcceptableValueRange<float>(0f, 0.25f), HubLink.Meta("Drivers over / under the limit", step: 0.01, unit: "%", scale: 100, applies: "next spawn")));
            SlowDriverFactor = Config.Bind("SpeedLimits", "SlowDriverFactor", 0.85f, new ConfigDescription(
                "The game's slow cars (its 'grandma' drivers) drive this fraction of the limit.",
                new AcceptableValueRange<float>(0.5f, 1f), HubLink.Meta("Slow drivers", step: 0.05, unit: "x", applies: "next spawn")));
            CurvyDegrees = Config.Bind("SpeedLimits", "CurvyDegrees", 45f, new ConfigDescription(
                "A stretch is curvy when the road turns at least this many degrees within 200 m (45 = a bend of about 250 m radius).",
                new AcceptableValueRange<float>(15f, 120f), HubLink.Meta("Curvy from", step: 1, unit: "deg / 200 m", advanced: true, applies: "within a second")));
            HighwayMaxDegrees = Config.Bind("SpeedLimits", "HighwayMaxDegrees", 12f, new ConfigDescription(
                "Outside the city, a stretch is a highway when the road turns at most this many degrees within 400 m.",
                new AcceptableValueRange<float>(0f, 45f), HubLink.Meta("Highway when straighter than", step: 1, unit: "deg / 400 m", advanced: true, applies: "within a second")));
            CityBiomes = Config.Bind("SpeedLimits", "CityBiomes", "Residential,Commercial", new ConfigDescription(
                "The game's area types (Residential, Commercial, Park, Industrial; one per race) that count as city.",
                null, HubLink.Meta("Areas that count as city", advanced: true, applies: "within a second")));
            UseSignHints = Config.Bind("SpeedLimits", "UseSignHints", true, new ConfigDescription(
                "Use the map's own road signs: tiles with freeway signs are highway, tiles with slow-down 40 signs are city, a slow-down 80 sign is never highway.",
                null, HubLink.Meta("Follow the map's road signs", advanced: true, applies: "within a second")));
            MatchSpeedometer = Config.Bind("SpeedLimits", "MatchSpeedometer", true, new ConfigDescription(
                "On = the limits are what the game's speedometer shows (it reads about 10% high), so 70 here looks like 70 next to your own speed. Off = real mph.",
                null, HubLink.Meta("Limits as the speedometer shows them", advanced: true, applies: "within a second")));
            SpeedLogZones = Config.Bind("SpeedLimits", "LogZones", true, new ConfigDescription(
                "Log the zones picked for each race (one line per tile) and the speeds in use once a minute.",
                null, HubLink.Meta("Log the speed zones", advanced: true)));

            PerfEnabled = Config.Bind("Perf", "Enabled", false, new ConfigDescription(
                "Shared timing overlay for all Rogue mods (each mod times its own work into it). F4 toggles the overlay while this is on. Logs one summary line every 10 s. Off = no timing at all.",
                null, HubLink.Meta("Timing overlay for all mods", advanced: true)));

            // Rogue Hub (optional): the live line on TrafficDensity's card and a back-to-stock button
            HubLink.Status(Guid, TrafficRunner.HubStatus);
            HubLink.Action(Guid, "stock", "Back to stock traffic (x1)", "Sets the traffic multiplier to 1, the game's own amount.", TrafficRunner.HubStock);

            ClassInjector.RegisterTypeInIl2Cpp<TrafficRunner>();
            AddComponent<TrafficRunner>();
            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(SpawnerPatch));
            // each feature's hook on its own: a failed patch switches off only that feature
            try { harmony.PatchAll(typeof(SafeLanesPatch)); SafeLanesHooked = true; }
            catch (Exception e) { Log.LogError($"[Traffic] safe lane changes unavailable (AIVehicleLaneHandler.MoveToLaneIndex hook failed): {e.Message}"); }
            Leaderboard.Install(harmony);   // optional General.KeepOffLeaderboards (only ever skips an upload when that is on)
            try { harmony.PatchAll(typeof(SpeedLimitsPatch)); SpeedHooked = true; }
            catch (Exception e) { Log.LogError($"[Traffic] speed limits at spawn unavailable (AIPathFollower.SetVehicle hook failed; cars still get their limit within a second of spawning, pooled cars told apart by their jump in road distance): {e.Message}"); }
            Log.LogInfo($"TrafficDensity {Version} loaded: x{Multiplier.Value:0.##} (Ctrl+PageUp/PageDown to change, Ctrl+Home = stock).");
        }

        internal static float[] StepValues()
        {
            var list = new List<float>();
            foreach (var s in (Steps.Value ?? "").Split(','))
                if (float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= 0f) list.Add(v);
            if (list.Count == 0) list.AddRange(new[] { 0.5f, 1f, 1.5f, 2f, 3f });
            return list.Distinct().OrderBy(v => v).ToArray();
        }
    }

    /// <summary>Remembers the game's own count for the current race, then applies the multiplier.</summary>
    [HarmonyPatch]
    internal static class SpawnerPatch
    {
        internal static DefaultAISpawner Spawner;
        internal static int BaseCount = -1;

        [HarmonyPatch(typeof(DefaultAISpawner), nameof(DefaultAISpawner.Start))]
        [HarmonyPostfix]
        private static void StartPostfix(DefaultAISpawner __instance)
        {
            Spawner = __instance;
            BaseCount = __instance.aiSpawnCount;   // game value incl. traffic hazard multiplier
            TrafficRunner.InvalidateRole();        // new race: read the multiplayer role fresh
            int target = TrafficRunner.Apply();
            Plugin.Log.LogInfo($"[Traffic] race start: game wants {BaseCount} NPC cars -> {target} (x{TrafficRunner.EffectiveMultiplier():0.##})");
            TrafficRunner.Toast($"Traffic x{TrafficRunner.EffectiveMultiplier():0.##}: {target} cars (stock {BaseCount})");
        }
    }

    public class TrafficRunner : MonoBehaviour
    {
        public TrafficRunner(IntPtr ptr) : base(ptr) { }

        // OnGUI only uses GUI.* (no GUILayout / GUI.Window), so skip Unity's extra Layout pass of OnGUI every frame
        private void Awake() => useGUILayout = false;

        private static string _toast = "";
        private static float _toastUntil;
        private float _nextCheck;
        private float _nextFix;
        private float _nextSpeed;

        // shared Perf helper (source/Shared/Perf.cs): TrafficDensity owns the overlay + log; [Perf] Enabled switches timing for every mod
        private static bool _perfClaimed, _perfOwner, _perfOverlay = true;
        private static readonly Action<string> _perfLog = s => Plugin.Log.LogInfo(s);

        /// <summary>
        /// Connected to someone else's game: Mirror client running without a local server. The host simulates all
        /// traffic and syncs it to clients (NetworkAIVehicle), so a client must never try to change it.
        /// </summary>
        internal static bool IsRemoteClient() { RefreshRole(); return _remoteClient; }

        internal static bool InMultiplayer() { RefreshRole(); return _inMultiplayer; }

        // Mirror reads + GameState.IsMultiplayerMode + the scene-name scan, cached for 1 s (asked several times a second). Main thread only.
        private static float _roleUntil = -1f;
        private static bool _remoteClient, _inMultiplayer;

        /// <summary>Forget the cached multiplayer role (race start), so the next check reads it fresh.</summary>
        internal static void InvalidateRole() => _roleUntil = -1f;

        private static void RefreshRole()
        {
            float now = Time.unscaledTime;
            if (now < _roleUntil) return;
            _roleUntil = now + 1f;
            bool remote;
            try { remote = Mirror.NetworkClient.active && !Mirror.NetworkServer.active; }
            catch { remote = false; }
            bool mp = remote || GameSaysMultiplayer();
            for (int i = 0; !mp && i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name.IndexOf("Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0) mp = true;
            _remoteClient = remote;
            _inMultiplayer = mp;
        }

        /// <summary>The game's own multiplayer flag; false if it can't be read (the scene-name and Mirror checks still apply).</summary>
        private static bool GameSaysMultiplayer()
        {
            try { return ReadGameMultiplayerFlag(); }   // separate method: a missing GameState fails here, not in RefreshRole
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadGameMultiplayerFlag() => Game.Runtime.GameState.IsMultiplayerMode;

        /// <summary>We own the traffic simulation and the person allowed changes: offline, or hosting with AllowInMultiplayer.</summary>
        internal static bool MayChangeTraffic() => !IsRemoteClient() && (Plugin.AllowInMultiplayer.Value || !InMultiplayer());

        internal static float EffectiveMultiplier()
        {
            if (!Plugin.Enabled.Value || !MayChangeTraffic()) return 1f;
            return Mathf.Max(0f, Plugin.Multiplier.Value);
        }

        /// <summary>Sets the live spawn cap from the game's count; returns the new cap.</summary>
        internal static int Apply()
        {
            var sp = SpawnerPatch.Spawner;
            if (sp == null || SpawnerPatch.BaseCount < 0) return -1;
            int cap = Mathf.Clamp(Plugin.MaxCars.Value, 0, Plugin.PoolMax);
            int target = Mathf.Clamp(Mathf.RoundToInt(SpawnerPatch.BaseCount * EffectiveMultiplier()), 0, Mathf.Max(cap, SpawnerPatch.BaseCount));
            if (sp.aiSpawnCount != target) sp.aiSpawnCount = target;
            return target;
        }

        internal static void Toast(string text)
        {
            if (!Plugin.ShowToast.Value) return;
            if (HubLink.HubPresent) { HubLink.Toast(Plugin.Guid, text); return; }   // Rogue Hub shows it in its notification stack
            _toast = text;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        /// <summary>Rogue Hub: the live line on TrafficDensity's card.</summary>
        internal static string HubStatus()
        {
            if (!Plugin.Enabled.Value) return "off";
            if (IsRemoteClient()) return "multiplayer client: the host controls traffic";
            if (!MayChangeTraffic()) return "multiplayer: stock traffic";
            var sp = SpawnerPatch.Spawner;
            string m = $"x{EffectiveMultiplier():0.##}";
            string line = sp == null || SpawnerPatch.BaseCount < 0 ? m + ", applies at the next race" : $"{m}, {sp.aiSpawnCount} cars (stock {SpawnerPatch.BaseCount})";
            string speed = SpeedLimits.Status(), lanes = SafeLanes.Status();
            if (speed != null) line += ", " + speed;
            if (lanes != null) line += ", " + lanes;
            return line;
        }

        /// <summary>Rogue Hub button: back to the game's own amount.</summary>
        internal static string HubStock()
        {
            Set(1f);   // Set already shows "Traffic x1 ..." (in the hub's notifications when ShowToast is on)
            return Plugin.ShowToast.Value ? null : "Traffic back to stock (x1)";
        }

        private void Update()
        {
            PerfUpdate();
            using (Perf.Scope("Traffic.Update"))
            {
                try
                {
                    var kb = Keyboard.current;
                    if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
                    {
                        if (kb.pageUpKey.wasPressedThisFrame) Step(+1);
                        else if (kb.pageDownKey.wasPressedThisFrame) Step(-1);
                        else if (kb.homeKey.wasPressedThisFrame) Set(1f);
                    }
                    // AI fixes on active cars (new cars get them within a quarter second; they spawn 90+ m away)
                    if (Time.unscaledTime >= _nextFix)
                    {
                        _nextFix = Time.unscaledTime + 0.25f;
                        try
                        {
                            using (Perf.Scope("Traffic.Fixes"))
                                Fixes.Tick(SpawnerPatch.Spawner, Plugin.Enabled.Value && Plugin.FixesEnabled.Value && MayChangeTraffic());
                        }
                        catch (Exception e) { Plugin.Log.LogError(e); _nextFix = Time.unscaledTime + 5f; }
                        try { Leaderboard.Tick(); }
                        catch (Exception e) { Plugin.Log.LogError(e); }
                        try { SafeLanes.Tick(); }
                        catch (Exception e) { Plugin.Log.LogError(e); }
                    }
                    // speed limits: road profile once per race, every car's limit twice a second (restores when off)
                    if (Time.unscaledTime >= _nextSpeed)
                    {
                        _nextSpeed = Time.unscaledTime + 0.5f;
                        try
                        {
                            using (Perf.Scope("Traffic.Speed"))
                                SpeedLimits.Tick(SpeedLimits.Want && MayChangeTraffic());
                        }
                        catch (Exception e) { Plugin.Log.LogError(e); _nextSpeed = Time.unscaledTime + 5f; }
                    }
                    // keep the cap right if the config file was edited / reloaded or the spawner was replaced
                    if (Time.unscaledTime >= _nextCheck)
                    {
                        _nextCheck = Time.unscaledTime + 1f;
                        if (SpawnerPatch.Spawner == null) { SpawnerPatch.BaseCount = -1; return; }
                        using (Perf.Scope("Traffic.Apply")) Apply();
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(e);
                    _nextCheck = _nextFix = Time.unscaledTime + 5f;
                }
            }
        }

        /// <summary>Owner side of the shared Perf helper: follow [Perf] Enabled, close the frame, F4 overlay, 10 s log line.</summary>
        private static void PerfUpdate()
        {
            try
            {
                if (!_perfClaimed)
                {
                    _perfClaimed = true;
                    _perfOwner = Perf.ClaimOwner("TrafficDensity");
                    if (!_perfOwner) Plugin.Log.LogInfo($"[Perf] overlay owned by {Perf.Owner}; [Perf] settings here are ignored");
                }
                if (!_perfOwner) return;
                bool want = Plugin.PerfEnabled.Value;
                if (want != Perf.Enabled)
                {
                    Perf.SetEnabled(want);
                    Plugin.Log.LogInfo(want ? "[Perf] timing on for all Rogue mods (F4 toggles the overlay)" : "[Perf] timing off");
                }
                if (!want) return;
                Perf.Tick();
                var kb = Keyboard.current;
                if (kb != null && kb.f4Key.wasPressedThisFrame) _perfOverlay = !_perfOverlay;
                Perf.LogEvery(_perfLog, 10);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Perf] switched off after an error: {e}");
                _perfOwner = false;
                Perf.SetEnabled(false);
            }
        }

        private static void Step(int dir)
        {
            var steps = Plugin.StepValues();
            float cur = Plugin.Multiplier.Value;
            float next = dir > 0 ? steps.FirstOrDefault(v => v > cur + 1e-4f) : steps.LastOrDefault(v => v < cur - 1e-4f);
            if (dir > 0 && next <= cur) next = steps.Last();
            if (dir < 0 && next >= cur) next = steps.First();
            Set(next);
        }

        private static void Set(float value)
        {
            Plugin.Multiplier.Value = value;   // saved to rogue.trafficdensity.cfg
            int target = Apply();
            string mp = IsRemoteClient() ? " (multiplayer client: the host controls traffic)"
                      : !Plugin.AllowInMultiplayer.Value && InMultiplayer() ? " (multiplayer: stock)" : "";
            string cars = target >= 0 ? $": {target} cars (stock {SpawnerPatch.BaseCount})" : " (applies at the next race)";
            Toast($"Traffic x{value:0.##}{cars}{mp}");
            Plugin.Log.LogInfo($"[Traffic] multiplier set to x{value:0.##}{cars}{mp}");
        }

        private void OnGUI()
        {
            if (_perfOwner && _perfOverlay && Perf.Enabled)
            {
                // top-left corner, 12 px in (scaled by screen height): at most about 520 x 290 px at 1080p (12 rows),
                // above DriverCam's button (x 20, y 420) and clear of CurbFeel (top-right) and the toast (top-centre)
                try { Perf.DrawOverlay(); }
                catch (Exception e) { Plugin.Log.LogError($"[Perf] overlay hidden after an error: {e}"); _perfOverlay = false; }
            }
            if (Time.unscaledTime > _toastUntil || string.IsNullOrEmpty(_toast)) return;
            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            GUI.skin.box.fontSize = Mathf.RoundToInt(18 * s);
            float w = 460 * s, h = 36 * s;
            GUI.Box(new Rect((Screen.width - w) / 2f, 70 * s, w, h), _toast);   // top-centre: clear of DriverCam (left) and CurbFeel (top-right)
        }
    }
}
