using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sandbox
{
    /// <summary>[Scenery] settings and the one call Plugin.Load makes (SceneryHost.Init).</summary>
    internal static class SceneryHost
    {
        internal static ConfigEntry<bool> Enabled, HideLights;
        internal static ConfigEntry<float> BudgetMs;

        internal static void Init(ConfigFile config, BasePlugin plugin)
        {
            Enabled = config.Bind("Scenery", "StripBuildings", true,
                "In Sandbox mode, remove everything around the roads that isn't part of the road itself: buildings, trees, props, ads, walls, fences, cones, water. " +
                "Only the road, sidewalks, curbs, the ground, guardrails, railings, barriers, bridges, tunnels and street lights stay. Their colliders go too (given back when the sandbox race ends).");
            HideLights = config.Bind("Scenery", "HideLights", true,
                "Also switch off the lights of the removed scenery (building and prop lights, about 100 per tile). Street, tunnel and bridge lights stay on.");
            BudgetMs = config.Bind("Scenery", "BudgetMs", 2f, new ConfigDescription("Milliseconds per frame spent stripping a newly loaded tile (spread over frames, no stutter).",
                new AcceptableValueRange<float>(0.5f, 10f)));
            ClassInjector.RegisterTypeInIl2Cpp<SceneryRunner>();
            plugin.AddComponent<SceneryRunner>();
        }
    }

    /// <summary>
    /// Sandbox scenery: strips each road tile down to the road and what is built around it.
    ///
    /// The game builds a race from additive tile scenes (LevelGenerator.SpawnTilesCoroutine, all loaded during the loading
    /// screen, unloaded at race end). Each tile has a "Road Network" (the road mesh PresetRoad, RoadGroundCollider, the
    /// invisible guardrail walls on layers Street 11 / Guardrail 15, and WideRoads' fx_Wide* pieces) and a "Biomes" group
    /// whose active biome holds the buildings, trees, props, ads, lamps and lights. "Road Network" is never touched here.
    ///
    /// Per tile (polled once a second while Sandbox is active, at most one new tile opened per poll, work spread over frames
    /// by BudgetMs), everything under "Biomes":
    /// - kept: anything whose own name or a parent's (up to "Biomes") matches a road word: road, sidewalk / walkway / kerb /
    ///   curb surfaces, sides_, base_h, the ground meshes (Ground_Easy/Normal/Hard/Pro, Moss_Plane), guardrails, railings,
    ///   rail generators, barriers, bridges, tunnels, and street lights (light / lamp / pole), plus anything on the Street /
    ///   Guardrail / WeatherBlocker layers (11 / 15 / 28);
    /// - hidden: every other renderer, with Renderer.forceRenderingOff = true. Its `enabled` flag is left alone, so CurbFeel
    ///   (which reads enabled renderers) sees the same tile whatever the order, and LODGroups keep working;
    /// - colliders off: every enabled non-trigger Collider on a hidden object (Collider.enabled = false), except ground
    ///   (names with ground / moss / floor / terrain, layer 11, or a TerrainCollider), so the car can't hit invisible things;
    /// - HideLights: Light components under Biomes switched off, except street lights (a parent named light / lamp / pole, or
    ///   the light's own object named lamp / pole / streetlight) and tunnel / bridge lights.
    /// A leftover "fx_SandboxBlocks" root from older versions (stand-in building blocks) is destroyed when the tile is opened.
    /// Wide-road tiles (WideRoads.Owns) are never stripped here; one stripped before WideRoads took it over is given back
    /// first (ReleaseTile). A tile that fails is left as far as it got and logged, without switching the feature off.
    /// Everything is given back (renderers, colliders and lights on) when Sandbox ends or StripBuildings is switched off, and
    /// on unload. Unity calls used: SceneManager.sceneCount / GetSceneAt, Scene.isLoaded / handle / GetRootGameObjects,
    /// GetComponentsInChildren, Renderer.forceRenderingOff, Collider.enabled / isTrigger, Light.enabled, Object.Destroy.
    /// </summary>
    public class SceneryRunner : MonoBehaviour
    {
        public SceneryRunner(IntPtr ptr) : base(ptr) { }

        private static readonly string[] Keep =
        {
            "road", "sidewalk", "walkway", "kerb", "curb", "sides_", "base_h", "ground_easy", "ground_normal", "ground_hard",
            "ground_pro", "moss_plane", "guardrail", "guard_rail", "railing", "railgenerator", "barrier", "bridge", "tunnel",
            "light", "lamp", "pole",
        };
        private static readonly string[] Ground = { "ground", "moss", "floor", "terrain" };
        private const int LayerStreet = 11, LayerGuardrail = 15, LayerWeather = 28;

        private sealed class Tile
        {
            public int Handle;
            public string Name;
            public Renderer[] Renderers;
            public Collider[] Colliders;
            public Light[] Lights;
            public int Next, NextCol;
            public bool Done;
            public int Kept, StreetLights, OldBlocks;
            public readonly List<Renderer> Hidden = new List<Renderer>();
            public readonly List<Collider> ColOff = new List<Collider>();
            public readonly List<Light> Off = new List<Light>();
        }

        private readonly Dictionary<int, Tile> _tiles = new Dictionary<int, Tile>();
        private readonly HashSet<int> _notTile = new HashSet<int>(), _seen = new HashSet<int>();
        private readonly List<int> _gone = new List<int>();
        private readonly Stopwatch _sw = new Stopwatch();
        private float _nextPoll;
        private bool _wasOn, _broken;
        private int _errors;

        private void Update()
        {
            SsrGuard.Apply(Plugin.Active);   // SSR off for the whole run (independent of StripBuildings); has its own breaker
            if (_broken) return;
            try
            {
                bool on = Plugin.Active && SceneryHost.Enabled != null && Multiplayer.Strip(SceneryHost.Enabled.Value);   // the host's value in a sandbox MP run
                if (!on) { if (_wasOn) RestoreAll(Plugin.Active ? "StripBuildings off" : "Sandbox ended"); _wasOn = false; return; }
                _wasOn = true;
                bool paused = false;
                try { paused = Game.Runtime.GameState.IsGamePaused; } catch { }
                if (paused) return;   // pause menu open: no writes (a restore above still runs)
                float now = Time.unscaledTime;
                if (now >= _nextPoll) { _nextPoll = now + 1f; Poll(); }
                Work();
            }
            catch (Exception e)
            {
                if (++_errors >= 3)
                {
                    _broken = true;
                    try { RestoreAll("errors"); } catch { /* scene gone */ }
                    Plugin.Log.LogError($"[Sandbox] scenery switched off for this session after repeated errors (the tiles are restored): {e}");
                }
                else Plugin.Log.LogWarning($"[Sandbox] scenery error ({_errors}/3): {e.Message}");
            }
        }

        private void OnDestroy()
        {
            try { RestoreAll("plugin unloaded"); } catch { /* shutting down */ }
            try { SsrGuard.RestoreNow("plugin unloaded"); } catch { /* shutting down */ }
            if (_instance == this) _instance = null;
        }

        // ------------------------------------------------------------------ find tiles

        private void Poll()
        {
            _seen.Clear();
            bool opened = false;
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                int h = scene.handle;
                _seen.Add(h);
                if (_tiles.ContainsKey(h) || _notTile.Contains(h) || opened) continue;
                if (WideRoads.Owns(h)) continue;   // a wide-road tile: WideRoads hides it (re-checked each poll, never stripped here)
                var tile = Open(scene);   // at most one new tile per poll: its component lists are a one-off hitch
                opened = true;
                if (tile != null) _tiles[h] = tile; else _notTile.Add(h);
            }
            // scenes that unloaded: their renderers, colliders and lights went with them
            _gone.Clear();
            foreach (var kv in _tiles) if (!_seen.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (int h in _gone) _tiles.Remove(h);
            if (_isGone == null) _isGone = IsGone;
            _notTile.RemoveWhere(_isGone);
            if (opened) _nextPoll = 0f;   // more scenes may wait: open the next one next frame, not in a second
        }

        private Predicate<int> _isGone;

        private bool IsGone(int handle) => !_seen.Contains(handle);
        private void Awake() { _isGone = IsGone; _instance = this; }

        private static SceneryRunner _instance;

        /// <summary>
        /// WideRoads is about to take this tile over: give back what Scenery hid there (renderers, colliders, lights) and
        /// forget it, so the two never undo each other's changes. Poll skips the tile from then on (WideRoads.Owns). Main
        /// thread only.
        /// </summary>
        internal static void ReleaseTile(int handle)
        {
            var self = _instance;
            if (self == null || !self._tiles.TryGetValue(handle, out var t)) return;
            Give(t, out int r, out int c, out int l);
            self._tiles.Remove(handle);
            Plugin.Log.LogInfo($"[Sandbox] scenery: tile {t.Name} handed to wide roads ({r} renderers, {c} colliders and {l} lights given back first)");
        }

        /// <summary>A road tile = a loaded scene with a root (or child) named "Biomes". Collects its renderers, colliders and lights once.</summary>
        private static Tile Open(Scene scene)
        {
            var roots = scene.GetRootGameObjects();
            Transform biomes = null;
            int oldBlocks = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                var r = roots[i];
                if (r == null) continue;
                if (r.name == "fx_SandboxBlocks") { Destroy(r); oldBlocks++; continue; }   // stand-in blocks of Scenery 0.1.x (hot reload)
                if (biomes != null) continue;
                if (r.name == "Biomes") biomes = r.transform;
                else
                {
                    var t = r.transform.Find("Biomes");
                    if (t != null) biomes = t;
                }
            }
            if (biomes == null) return null;   // not a road tile (the game scene, menus, UI)
            return new Tile
            {
                Handle = scene.handle, Name = scene.name, OldBlocks = oldBlocks,
                Renderers = biomes.GetComponentsInChildren<Renderer>(false),
                Colliders = biomes.GetComponentsInChildren<Collider>(false),
                Lights = biomes.GetComponentsInChildren<Light>(false),
            };
        }

        // ------------------------------------------------------------------ strip, a slice per frame

        private void Work()
        {
            bool any = false;
            foreach (var t in _tiles.Values) if (!t.Done) { any = true; break; }
            if (!any) return;
            float budget = SceneryHost.BudgetMs.Value;
            if (budget < 0.5f) budget = 0.5f; else if (budget > 10f) budget = 10f;
            _sw.Restart();
            foreach (var t in _tiles.Values)
            {
                if (t.Done) continue;
                try
                {
                    if (!StripSlice(t, budget)) return;   // out of time: carry on next frame
                }
                catch (Exception e)
                {
                    t.Done = true;   // this tile stays as far as it got (and is still given back); the others carry on
                    Plugin.Log.LogWarning($"[Sandbox] scenery: tile {t.Name} left as it is after an error: {e.Message}");
                }
                if (_sw.Elapsed.TotalMilliseconds > budget) return;
            }
        }

        /// <summary>Hides a slice of one tile; false when the budget ran out before the tile was finished.</summary>
        private bool StripSlice(Tile t, float budget)
        {
            while (t.Next < t.Renderers.Length)
            {
                var r = t.Renderers[t.Next++];
                if (r != null && !r.forceRenderingOff)
                {
                    if (Keeps(r.transform, r.gameObject.layer)) t.Kept++;
                    else
                    {
                        r.forceRenderingOff = true;   // `enabled` untouched: CurbFeel and LODGroups see the same renderer
                        t.Hidden.Add(r);
                    }
                }
                if ((t.Next & 31) == 0 && _sw.Elapsed.TotalMilliseconds > budget) return false;
            }
            while (t.NextCol < t.Colliders.Length)
            {
                var c = t.Colliders[t.NextCol++];
                if (c != null && c.enabled && !c.isTrigger)
                {
                    var tr = c.transform;
                    int layer = c.gameObject.layer;
                    if (!IsGround(tr, layer) && !Keeps(tr, layer) && c.GetIl2CppType().Name != "TerrainCollider")
                    {
                        c.enabled = false;
                        t.ColOff.Add(c);
                    }
                }
                if ((t.NextCol & 31) == 0 && _sw.Elapsed.TotalMilliseconds > budget) return false;
            }
            if (Multiplayer.HideLights(SceneryHost.HideLights.Value))
                foreach (var l in t.Lights)
                {
                    if (l == null || !l.enabled) continue;
                    if (Lit(l.transform)) { t.StreetLights++; continue; }
                    l.enabled = false;
                    t.Off.Add(l);
                }
            t.Done = true;
            t.Renderers = Array.Empty<Renderer>(); t.Colliders = Array.Empty<Collider>(); t.Lights = Array.Empty<Light>();   // let the arrays go
            Plugin.Log.LogInfo($"[Sandbox] scenery: tile {t.Name}: {t.Hidden.Count} renderers hidden ({t.Kept} road renderers kept), {t.ColOff.Count} colliders off, " +
                               $"{t.Off.Count} lights off ({t.StreetLights} street / tunnel / bridge lights kept)" +
                               (t.OldBlocks > 0 ? $", {t.OldBlocks} old block set(s) removed" : ""));
            return true;
        }

        /// <summary>
        /// Lights that stay on: tunnel and bridge lights, and street lights: a parent named light / lamp / pole, or the light's
        /// own object named lamp / pole / streetlight (its own name alone is not enough when it is just "Point Light").
        /// </summary>
        private static bool Lit(Transform t)
        {
            for (int depth = 0; t != null && depth < 6; depth++, t = t.parent)
            {
                string n = t.gameObject.name;
                if (n == "Biomes") break;
                n = n.ToLowerInvariant();
                if (n.Contains("tunnel") || n.Contains("bridge") || n.Contains("lamp") || n.Contains("pole") || n.Contains("streetlight") || n.Contains("street_light")) return true;
                if (depth > 0 && n.Contains("light")) return true;
            }
            return false;
        }

        /// <summary>Keep the road and what is built around it (by own or parent name, up to the biome), and the collision layers.</summary>
        private static bool Keeps(Transform t, int layer)
        {
            if (layer == LayerStreet || layer == LayerGuardrail || layer == LayerWeather) return true;
            for (int depth = 0; t != null && depth < 6; depth++, t = t.parent)
            {
                string n = t.gameObject.name;
                if (n == "Biomes") break;
                n = n.ToLowerInvariant();
                for (int i = 0; i < Keep.Length; i++) if (n.Contains(Keep[i])) return true;
            }
            return false;
        }

        /// <summary>Ground colliders stay on whatever their renderer does: the car may drive or land on them.</summary>
        private static bool IsGround(Transform t, int layer)
        {
            if (layer == LayerStreet) return true;
            string n = t.gameObject.name.ToLowerInvariant();
            for (int i = 0; i < Ground.Length; i++) if (n.Contains(Ground[i])) return true;
            var p = t.parent;
            if (p != null && p.gameObject.name != "Biomes")
            {
                n = p.gameObject.name.ToLowerInvariant();
                for (int i = 0; i < Ground.Length; i++) if (n.Contains(Ground[i])) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ give everything back

        private static void Give(Tile t, out int r, out int c, out int l)
        {
            r = 0; c = 0; l = 0;
            foreach (var x in t.Hidden) { try { if (x != null) { x.forceRenderingOff = false; r++; } } catch { /* gone */ } }
            foreach (var x in t.ColOff) { try { if (x != null) { x.enabled = true; c++; } } catch { /* gone */ } }
            foreach (var x in t.Off) { try { if (x != null) { x.enabled = true; l++; } } catch { /* gone */ } }
            t.Hidden.Clear(); t.ColOff.Clear(); t.Off.Clear();
        }

        private void RestoreAll(string why)
        {
            int r = 0, c = 0, l = 0;
            foreach (var t in _tiles.Values)
            {
                Give(t, out int tr, out int tc, out int tl);
                r += tr; c += tc; l += tl;
            }
            if (_tiles.Count > 0) Plugin.Log.LogInfo($"[Sandbox] scenery restored ({why}): {r} renderers, {c} colliders and {l} lights back on in {_tiles.Count} tiles");
            _tiles.Clear();
        }
    }
}
