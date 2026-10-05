using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using RogueShared;
using Il2CppInterop.Runtime;
using Game.Runtime.Data;
using Game.Runtime.Manager;
using Game.Runtime.Systems.LevelGeneration;
using UnityEngine;

namespace Sandbox
{
    /// <summary>
    /// Sandbox maps ([Maps], Maps*.cs; the class keeps its 0.2.0-dev name WideRoads because Multiplayer.cs uses it): every
    /// sandbox race is on a wide road (default 30 m with 6 lanes; the game's roads are 20 m with 4), built at runtime
    /// along each tile's own path (<see cref="WideBuild"/>), with the tile's buildings, props, stock road and lights
    /// hidden. The race spline, traffic, AI racers, obstacles, the timer, RacingLine and Police need no path change - only
    /// the width changes. Length (x2 by default) is RoadLength.cs.
    ///
    /// Width (verified, GameAssembly.dll): RoadTileContainerSO.roadWidth (0x58) / roadLaneCount (0x5C) are global. AI
    /// racer behaviours (AvoidancePathRacerBehaviour.OnInitialize 0x1809C9D90, SteerRacerBehaviour.OnInitialize
    /// 0x1809D7DC0) and VehicleAutoInputHandler read the container itself when they start, so the container stays wide
    /// for the whole race. RunWorldManager.UpdateLaneInfo (0x1809280F0, called by the game only at run start) rebuilds the
    /// shared laneOffsetList (traffic, obstacles, path followers) and currentRoadWidth; we call it after each change.
    ///
    /// When the width is set (<see cref="Apply"/>, idempotent), before any tile, racer, traffic car or obstacle exists:
    /// - host / single-player: LevelGeneratorTileSelector.GetRandomTiles prefix (RoadLength.BeforeTiles; also needed
    ///   before RunWorldManager.ShouldSpawnGasStation, which GenerateRegularLevel calls after it);
    /// - multiplayer client: Multiplayer.cs's LevelGenerator.GenerateMultiplayerLevelAsClient prefix (0x180795CF0;
    ///   clients never pick tiles, they load the host's tile ids, so length and the gas-station choice come with that
    ///   list; the width comes from the host's run settings through Multiplayer.Wide / Width / Lanes);
    /// - every other level load (local level, the multiplayer host's own spawn, NPC test area):
    ///   LevelGenerator.SpawnTilesCoroutine prefix (0x180796BF0), a safety net.
    /// Restore (idempotent) runs when the sandbox race ends (Runner / MapsRunner, also a multiplayer race end or
    /// disconnect, since Plugin.Active covers multiplayer sandbox races), on SetRun(false), on the next race that isn't a
    /// sandbox race, on the error breaker and on unload. The build starts in RoadPathGenerator.GeneratePath's postfix
    /// (RoadLength.AfterPath), inside the loading coroutine.
    /// </summary>
    internal static class WideRoads
    {
        internal static ConfigEntry<bool> Value, StreetLights;
        internal static ConfigEntry<float> Width, BudgetMs;
        internal static ConfigEntry<int> Lanes;

        /// <summary>All hooks are in place (GetRandomTiles + GeneratePath via RoadLength, both service-tile postfixes).</summary>
        internal static bool Installed;
        /// <summary>This race was set up wide (container changed). Main thread only.</summary>
        internal static bool RaceWide;
        internal static float W = 30f;
        internal static int N = 6;

        private static RoadTileContainerSO _container;
        private static bool _triedFind, _changed, _serviceLogged, _broken, _noContainerLogged;
        private static float _stockWidth;
        private static int _stockLanes;

        internal static void Bind(ConfigFile config)
        {
            Value = config.Bind("Maps", "WideRoads", true, new ConfigDescription(
                "Sandbox races on the sandbox map: a wide road (Width / Lanes) built along each tile's own path, with guardrails and street lights; every building and prop is hidden. Off = the game's own road (Scenery strips the buildings instead). In multiplayer the host's setting is used.",
                null, HubLink.Meta("Sandbox map (wide road)", applies: "next race")));
            Width = config.Bind("Maps", "Width", 30f, new ConfigDescription(
                "Width of the sandbox road in metres (the game's roads are 20). In multiplayer the host's value is used.",
                new AcceptableValueRange<float>(20f, 40f), HubLink.Meta("Road width", 20, 40, 1, "m", applies: "next race")));
            Lanes = config.Bind("Maps", "Lanes", 6, new ConfigDescription(
                "Lanes on the sandbox road (the game's roads have 4). Lowered so each lane is at least 3.5 m wide. In multiplayer the host's value is used.",
                new AcceptableValueRange<int>(4, 8), HubLink.Meta("Lanes", 4, 8, 1, applies: "next race")));
            StreetLights = config.Bind("Maps", "StreetLights", true, new ConfigDescription(
                "Street lights along the sandbox road: a pole every 40 m (alternating sides) and 8 real lights that follow you along the poles. The game's own lamps are hidden with the scenery.",
                null, HubLink.Meta("Street lights", applies: "next race")));
            BudgetMs = config.Bind("Maps", "BudgetMs", 3f, new ConfigDescription(
                "Milliseconds per frame spent hiding the old scenery after the race loads (spread over frames, no stutter). The road itself is built during the loading screen.",
                new AcceptableValueRange<float>(0.5f, 10f), HubLink.Meta("Scenery hiding budget", 0.5, 10, 0.5, "ms", advanced: true)));
        }

        internal static int Install(Harmony h)
        {
            Installed = false;
            if (!RoadLength.TilesHooked || !RoadLength.PathHooked)
            {
                Plugin.Log.LogWarning("[Sandbox] maps not available: the road length hooks (GetRandomTiles / GeneratePath) are not installed; sandbox races keep the game's road");
                return 0;
            }
            int n = 0;
            try
            {
                var gas = AccessTools.Method(typeof(RunWorldManager), "ShouldSpawnGasStation");
                var wash = AccessTools.Method(typeof(RunWorldManager), "ShouldSpawnCarWash");
                if (gas == null) throw new MissingMethodException("RunWorldManager", "ShouldSpawnGasStation");
                if (wash == null) throw new MissingMethodException("RunWorldManager", "ShouldSpawnCarWash");
                // harmony-target: RunWorldManager.ShouldSpawnGasStation (0x928060, own code; only caller GenerateRegularLevel 0x1807A9030, after GetRandomTiles)
                h.Patch(gas, postfix: new HarmonyMethod(typeof(WideRoads), nameof(NoGasStation)));
                n++;
                // harmony-target: RunWorldManager.ShouldSpawnCarWash (0x927E40, own code; only caller GenerateRegularLevel 0x1807A9030, after GetRandomTiles)
                h.Patch(wash, postfix: new HarmonyMethod(typeof(WideRoads), nameof(NoCarWash)));
                n++;
                Installed = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps not available (a service-tile hook failed: walls would trap the car at a gas station): {e.Message}"); }
            if (!Installed) return n;
            try
            {
                var spawn = AccessTools.Method(typeof(LevelGenerator), "SpawnTilesCoroutine");
                if (spawn == null) throw new MissingMethodException("LevelGenerator", "SpawnTilesCoroutine");
                // harmony-target: LevelGenerator.SpawnTilesCoroutine (0x180796BF0, own code; called from GenerateLocalLevel / GenerateTestLevel / Start)
                h.Patch(spawn, prefix: new HarmonyMethod(typeof(WideRoads), nameof(BeforeSpawnTiles)));
                n++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: tile-spawn safety hook not installed: {e.Message}"); }
            return n;
        }

        /// <summary>This race's width: the multiplayer run's (the host's) when one is on, else the config; 20-40 m.</summary>
        internal static float ClampW()
        {
            float w = Multiplayer.Width(Width == null ? 30f : Width.Value);
            if (float.IsNaN(w)) w = 30f;
            return Math.Max(20f, Math.Min(40f, w));
        }

        /// <summary>This race's lanes (multiplayer run's or the config's), 4-8, lowered so each lane is at least 3.5 m.</summary>
        internal static int ClampN(float w)
        {
            int asked = Multiplayer.Lanes(Lanes == null ? 6 : Lanes.Value);
            asked = Math.Max(4, Math.Min(8, asked));
            return Math.Max(2, Math.Min(asked, (int)Math.Floor(w / 3.5f)));
        }

        /// <summary>The sandbox map is on for this race's settings (the multiplayer run's when one is on).</summary>
        internal static bool On() => Installed && !_broken && Multiplayer.Wide(Value != null && Value.Value);

        internal static string Describe()
        {
            if (!Installed || _broken) return "";
            if (RaceWide) return $", {N} lanes / {W:0.#} m";
            if (!On()) return ", the game's road";
            float w = ClampW();
            return $", {ClampN(w)} lanes / {w:0.#} m";
        }

        internal static string LoadText()
        {
            if (!Installed) return " Sandbox maps unavailable (a hook failed): sandbox races keep the game's road.";
            if (Value == null || !Value.Value) return " Sandbox maps off ([Maps] WideRoads): sandbox races on the game's road.";
            float w = ClampW();
            return $" Sandbox maps: {ClampN(w)} lanes, {w:0.#} m, road x{Plugin.LengthMultiplier.Value:0.#}, scenery hidden{(StreetLights.Value ? ", street lights" : "")}.";
        }

        // ------------------------------------------------------------------ width and lanes

        private static RoadTileContainerSO Container()
        {
            if (_container != null) return _container;
            try
            {
                var refs = GeneralReferencesData.Instance;
                if (refs != null) _container = refs.RoadTileContainer;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: GeneralReferencesData unreadable: {e.Message}"); }
            if (_container == null && !_triedFind)
            {
                _triedFind = true;   // the fallback scan runs at most once
                try
                {
                    var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<RoadTileContainerSO>());
                    if (all != null && all.Length > 0 && all[0] != null) _container = all[0].TryCast<RoadTileContainerSO>();
                    if (_container != null) Plugin.Log.LogInfo($"[Sandbox] maps: road container found by search ({all.Length})");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: road container search failed: {e.Message}"); }
            }
            return _container;
        }

        private static void BeforeSpawnTiles()
        {
            // the host / single-player already applied in GetRandomTiles and a client in GenerateMultiplayerLevelAsClient;
            // this catches every other level load (Apply is idempotent). Never stops the game's level load. Same predicate
            // as the tile pick (run flag) or the scene check, so an early spawn of a sandbox race never puts the stock width back.
            try { Apply(SandboxRace()); } catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps (tile spawn): {e.Message}"); }
        }

        /// <summary>Sets this race's width: wide = a sandbox race (single-player or an agreed multiplayer sandbox run) with the map on. Idempotent.</summary>
        internal static void Apply(bool wide)
        {
            try
            {
                wide = wide && Installed && !_broken && Multiplayer.Wide(Value != null && Value.Value);   // Multiplayer.cs reads this call (CapWideHooks)
                if (!wide) { RestoreAll("race not wide"); Restore(); return; }   // never a stale wide road or light pool
                var c = Container();
                if (c == null)
                {
                    if (!_noContainerLogged) { _noContainerLogged = true; Plugin.Log.LogWarning("[Sandbox] maps: no road container found, this race keeps the game's road"); }
                    Restore();
                    return;
                }
                float w = ClampW();
                int n = ClampN(w);
                if (_changed && RaceWide && Math.Abs(c.roadWidth - w) < 0.01f && c.roadLaneCount == n) return;   // already set for this race
                int asked = Math.Max(4, Math.Min(8, Multiplayer.Lanes(Lanes.Value)));
                if (n < asked) Plugin.Log.LogInfo($"[Sandbox] maps: {asked} lanes asked, {n} used (lanes at least 3.5 m on a {w:0} m road)");
                if (!_changed) { _stockWidth = c.roadWidth; _stockLanes = c.roadLaneCount; }
                float oldW = c.roadWidth; int oldN = c.roadLaneCount;
                c.roadWidth = w;
                c.roadLaneCount = n;
                _changed = true;
                W = w; N = n;
                RaceWide = true;
                _serviceLogged = false;
                string lanes = UpdateLanes();
                Plugin.Log.LogInfo($"[Sandbox] maps: road {oldW:0.#} m / {oldN} lanes -> {w:0.#} m / {n} lanes (stock {_stockWidth:0.#} m / {_stockLanes}); lane offsets {lanes}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Sandbox] maps: width not applied: {e.Message}");
                Restore();
            }
        }

        /// <summary>Puts the stock width and lane count back and rebuilds the lanes. Calling it twice does nothing.</summary>
        internal static void Restore()
        {
            RaceWide = false;
            if (!_changed) return;
            _changed = false;
            try
            {
                var c = Container();
                if (c != null) { c.roadWidth = _stockWidth; c.roadLaneCount = _stockLanes; }
                string lanes = UpdateLanes();
                Plugin.Log.LogInfo($"[Sandbox] maps: road width restored to {_stockWidth:0.#} m / {_stockLanes} lanes; lane offsets {lanes}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: width restore: {e.Message}"); }
        }

        /// <summary>RunWorldManager.UpdateLaneInfo when a RunWorldManager exists; returns the lane offsets for the log.</summary>
        private static string UpdateLanes()
        {
            try
            {
                var s = global::Singleton.Instance;
                if (s == null) return "(no run world yet)";
                var rw = s.RunWorldManager;
                if (rw == null) return "(no run world yet)";
                rw.UpdateLaneInfo();
                var list = rw.laneOffsetList;
                if (list == null) return "(none)";
                var parts = new List<string>();
                for (int i = 0; i < list.Count; i++) parts.Add(list[i].ToString("0.##"));
                return $"[{string.Join(", ", parts)}], road {rw.CurrentRoadWidth:0.#} m";
            }
            catch (Exception e) { return $"(lane update failed: {e.Message})"; }
        }

        // ------------------------------------------------------------------ service tiles

        private static void NoGasStation(ref bool __result) => NoService(ref __result, "gas station");
        private static void NoCarWash(ref bool __result) => NoService(ref __result, "car wash");

        private static void NoService(ref bool result, string what)
        {
            try
            {
                if (!result || !RaceWide || !Plugin.TilePickNow()) return;
                result = false;
                if (_serviceLogged) return;
                _serviceLogged = true;
                Plugin.Log.LogInfo($"[Sandbox] maps: no {what} this race (its side lane would be walled off; cards are free in sandbox and PitStop's F2 refills)");
            }
            catch { /* keep the game's answer */ }
        }

        // ------------------------------------------------------------------ hooks from RoadLength / Runner / State

        /// <summary>RoadPathGenerator.GeneratePath postfix (through RoadLength.AfterPath).</summary>
        internal static void AfterPath(RoadPathGenerator gen)
        {
            if (!RaceWide) { RestoreAll("race not wide"); return; }   // a previous wide race's road / lights (idempotent)
            try
            {
                if (_broken || !SandboxRace())
                {
                    Plugin.Log.LogInfo("[Sandbox] maps: road path built for a race that is not a sandbox race; width put back");
                    Restore();   // never a wide width without the wide road
                    return;
                }
                WideBuild.Begin(gen);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Sandbox] maps: build failed, this race keeps the game's road: {e}");
                try { WideBuild.RestoreAll("build failed"); } catch { }
                Restore();
            }
        }

        /// <summary>The race being set up is a sandbox race: the run flag (tile pick) or the scene check. One predicate for the width hooks.</summary>
        private static bool SandboxRace() => Plugin.TilePickNow() || Plugin.ActiveNow();

        internal static bool Owns(int handle) => WideBuild.Owns(handle);

        internal static void RestoreAll(string why)
        {
            try { WideBuild.RestoreAll(why); } catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps restore: {e.Message}"); }
        }

        /// <summary>Everything back: tiles, lights, finish line and the width.</summary>
        internal static void RestoreEverything(string why)
        {
            RestoreAll(why);
            Restore();
        }

        /// <summary>Error breaker: everything back, no sandbox map until the game restarts.</summary>
        internal static void Break(Exception e)
        {
            _broken = true;
            Plugin.Log.LogError($"[Sandbox] maps switched off for this session after repeated errors (tiles and width restored): {e}");
            RestoreEverything("errors");
        }

        internal static void Shutdown()
        {
            RestoreEverything("plugin unloaded");
            WideBuild.DestroyMaterials();
        }
    }
}
