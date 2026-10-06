using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Declutter
{
    /// <summary>
    /// Finds road tiles (loaded scenes with a "Biomes" group, polled once a second, one new tile per poll), then sorts
    /// each tile's renderers a slice per frame within BudgetMs. Whatever is too far from the road gets
    /// forceRenderingOff = true and is remembered, so it can all be shown again (switched off, Sandbox race, unload).
    /// Tiles that unload take their renderers with them. Nothing runs while paused.
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private static readonly string[] Keep = { "road", "sidewalk", "walkway", "kerb", "curb", "guardrail", "guard_rail", "railing", "barrier", "wall", "tunnel", "bridge", "ramp", "collider" };
        private const int LayerStreet = 11, LayerGuardrail = 15, LayerWeather = 28;

        private sealed class Tile
        {
            public string Name;
            public Scene Scene;
            public Renderer[] Renderers;
            public Transform[] Waypoints;
            public Transform Root;                 // the Biomes root: watched until the game has placed the tile
            public Vector3 LastPos; public Quaternion LastRot;
            public int Stable;                     // polls the root has stood still
            public List<Vector3> Path;
            public int Next, Far, Small;
            public bool Done;
            public readonly List<Renderer> Hidden = new List<Renderer>();
        }

        private static int s_hidden, s_tiles;
        private readonly Dictionary<int, Tile> _tiles = new Dictionary<int, Tile>();
        private readonly HashSet<int> _notTile = new HashSet<int>(), _seen = new HashSet<int>();
        private readonly List<int> _gone = new List<int>();
        private readonly Stopwatch _sw = new Stopwatch();
        private float _nextPoll;
        private bool _wasOn, _broken;
        private int _errors;
        private Func<bool> _sandboxActive, _sandboxRun;
        private bool _sandboxLooked, _sandbox;
        private Predicate<int> _isGone;
        private const int StablePolls = 2;   // a tile is sorted only after its root stood still for 2 polls (~2 s): the game places tiles after loading them

        private void Update()
        {
            if (_broken) return;
            try
            {
                float now = Time.unscaledTime;
                DamageGhost.Tick(now);   // its own switch: every race, Sandbox included
                if (now >= _nextPoll) _sandbox = SandboxRace();   // read once a second (and before every poll)
                bool on = Plugin.Enabled.Value && !_sandbox;
                // switching off restores at once, even from the pause menu
                if (!on) { if (_wasOn) RestoreAll(Plugin.Enabled.Value ? "Sandbox race" : "switched off"); _wasOn = false; return; }
                _wasOn = true;
                if (Time.timeScale <= 0f) return;   // paused: no polling or sorting
                if (now >= _nextPoll) { _nextPoll = now + 1f; Poll(); }
                Work();
            }
            catch (Exception e)
            {
                if (++_errors >= 3)
                {
                    _broken = true;
                    try { RestoreAll("errors"); } catch { /* scene gone */ }
                    try { DamageGhost.Restore("errors"); } catch { /* car gone */ }
                    Plugin.Log.LogError($"[Declutter] switched off for this session after repeated errors (everything is shown again): {e}");
                }
                else Plugin.Log.LogWarning($"[Declutter] error ({_errors}/3): {e.Message}");
            }
        }

        private void OnDestroy()
        {
            try { RestoreAll("plugin unloaded"); } catch { /* shutting down */ }
            try { DamageGhost.Restore("plugin unloaded"); } catch { /* shutting down */ }
        }

        /// <summary>
        /// A Sandbox run: Sandbox.Plugin.Active (public) or Sandbox.Plugin.InSandboxRun (internal, true from the menu on, so
        /// tiles are never sorted in the moment before Active catches up). Read through delegates made once by reflection;
        /// false without Sandbox.
        /// </summary>
        private bool SandboxRace()
        {
            if (!_sandboxLooked)
            {
                _sandboxLooked = true;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Sandbox") continue;
                    var t = a.GetType("Sandbox.Plugin");
                    _sandboxActive = Getter(t, "Active");
                    _sandboxRun = Getter(t, "InSandboxRun");
                    break;
                }
            }
            try { return (_sandboxActive != null && _sandboxActive()) || (_sandboxRun != null && _sandboxRun()); }
            catch { return false; }
        }

        private static Func<bool> Getter(Type t, string name)
        {
            var m = t?.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetGetMethod(true);
            if (m == null || m.ReturnType != typeof(bool)) return null;
            try { return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), m); } catch { return null; }
        }

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
                if (_sandbox) continue;
                var t = Open(scene);
                opened = true;
                if (t != null) _tiles[h] = t; else _notTile.Add(h);
            }
            _gone.Clear();
            foreach (var kv in _tiles) if (!_seen.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (int h in _gone) { s_hidden -= _tiles[h].Hidden.Count; _tiles.Remove(h); }
            if (_isGone == null) _isGone = IsGone;
            _notTile.RemoveWhere(_isGone);
            // settle check: a tile's root must stand still before it's sorted; one that moves shows its hidden renderers again
            foreach (var t in _tiles.Values)
            {
                if (t.Root == null) continue;
                Vector3 p = t.Root.position; Quaternion q = t.Root.rotation;
                if (p != t.LastPos || q != t.LastRot)
                {
                    if (t.Next > 0 || t.Done) Reset(t);
                    t.LastPos = p; t.LastRot = q; t.Stable = 0;
                }
                else if (t.Stable < StablePolls) t.Stable++;
            }
            s_tiles = _tiles.Count;
            if (opened) _nextPoll = 0f;   // more scenes may wait: the next one next frame
        }

        private bool IsGone(int h) => !_seen.Contains(h);

        private static Tile Open(Scene scene)
        {
            var roots = scene.GetRootGameObjects();
            Transform biomes = null, path = null;
            for (int i = 0; i < roots.Length; i++)
            {
                var r = roots[i];
                if (r == null) continue;
                if (biomes == null && r.name == "Biomes") biomes = r.transform;
                else if (path == null) path = FindDeep(r.transform, "PathWaypoints", 4);
            }
            if (biomes == null || path == null) return null;   // not a road tile, or one without a path (left alone)
            var wps = new List<Transform>(path.childCount);
            for (int i = 0; i < path.childCount; i++)
            {
                var c = path.GetChild(i);
                if (c != null && c.gameObject.name.StartsWith("EasyRoad_PathWaypoint", StringComparison.Ordinal)) wps.Add(c);
            }
            if (wps.Count < 2) return null;
            return new Tile { Name = scene.name, Scene = scene, Renderers = biomes.GetComponentsInChildren<Renderer>(false), Waypoints = wps.ToArray(),
                              Root = biomes, LastPos = biomes.position, LastRot = biomes.rotation };
        }

        private static Transform FindDeep(Transform t, string name, int depth)
        {
            if (t.gameObject.name == name) return t;
            if (depth <= 0) return null;
            for (int i = 0; i < t.childCount; i++)
            {
                var f = FindDeep(t.GetChild(i), name, depth - 1);
                if (f != null) return f;
            }
            return null;
        }

        private void Work()
        {
            float budget = Math.Max(0.5f, Math.Min(10f, Plugin.BudgetMs.Value));
            _sw.Restart();
            foreach (var t in _tiles.Values)
            {
                if (t.Done || t.Stable < StablePolls) continue;   // not placed yet (or moved): wait
                try { if (!Slice(t, budget)) return; }
                catch (Exception e) { t.Done = true; Plugin.Log.LogWarning($"[Declutter] tile {t.Name} left as it is after an error: {e.Message}"); }
                if (_sw.Elapsed.TotalMilliseconds > budget) return;
            }
        }

        /// <summary>Sorts a slice of one tile; false when the budget ran out first.</summary>
        private bool Slice(Tile t, float budget)
        {
            if (t.Path == null)
            {
                // the road path every 5 m or less, flat (x, z), read once the tile is placed
                t.Path = new List<Vector3>();
                Vector3 prev = default; bool have = false;
                foreach (var w in t.Waypoints)
                {
                    if (w == null) continue;
                    Vector3 p = w.position;
                    if (have)
                    {
                        float dx = p.x - prev.x, dz = p.z - prev.z;
                        int steps = (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 5.0);
                        for (int k = 1; k < steps; k++) { float f = (float)k / steps; t.Path.Add(new Vector3(prev.x + dx * f, 0f, prev.z + dz * f)); }
                    }
                    t.Path.Add(p); prev = p; have = true;
                }
            }
            float small = Plugin.SmallSize.Value, smallD = Plugin.SmallDistance.Value, far = Plugin.FarDistance.Value;
            while (t.Next < t.Renderers.Length)
            {
                var r = t.Renderers[t.Next++];
                if (r != null && !r.forceRenderingOff && !Keeps(r.transform, r.gameObject.layer))
                {
                    var b = r.bounds;
                    Vector3 c = b.center, e = b.extents;
                    float size = 2f * Math.Max(e.x, Math.Max(e.y, e.z));
                    float gap = Gap(t.Path, c.x, c.z) - Math.Max(e.x, e.z);   // bounds to road, roughly
                    bool isFar = gap > far, isSmall = size < small && gap > smallD;
                    if (isFar || isSmall)
                    {
                        r.forceRenderingOff = true;
                        t.Hidden.Add(r); s_hidden++;
                        if (isFar) t.Far++; else t.Small++;
                    }
                }
                if ((t.Next & 31) == 0 && _sw.Elapsed.TotalMilliseconds > budget) return false;
            }
            t.Done = true;
            t.Renderers = Array.Empty<Renderer>();
            Plugin.Log.LogInfo($"[Declutter] tile {t.Name}: {t.Hidden.Count} renderers hidden ({t.Far} far from the road, {t.Small} small props)");
            return true;
        }

        /// <summary>A tile that moved after sorting started: show what it hid and sort it again once it's still.</summary>
        private static void Reset(Tile t)
        {
            foreach (var r in t.Hidden) { try { if (r != null) r.forceRenderingOff = false; } catch { /* gone */ } }
            s_hidden -= t.Hidden.Count;
            t.Hidden.Clear(); t.Path = null; t.Next = 0; t.Far = 0; t.Small = 0; t.Done = false;
            if (t.Renderers.Length == 0 && t.Root != null) t.Renderers = t.Root.GetComponentsInChildren<Renderer>(false);
            Plugin.Log.LogInfo($"[Declutter] tile {t.Name} moved after sorting started: shown again, sorting again once it's still");
        }

        private static float Gap(List<Vector3> path, float x, float z)
        {
            float best = float.MaxValue;
            for (int i = 0; i < path.Count; i++)
            {
                float dx = path[i].x - x, dz = path[i].z - z, d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            return (float)Math.Sqrt(best);
        }

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

        private void RestoreAll(string why)
        {
            int n = 0;
            foreach (var t in _tiles.Values)
                foreach (var r in t.Hidden) { try { if (r != null) { r.forceRenderingOff = false; n++; } } catch { /* gone */ } }
            if (_tiles.Count > 0) Plugin.Log.LogInfo($"[Declutter] {n} renderers shown again in {_tiles.Count} tiles ({why})");
            _tiles.Clear(); _notTile.Clear();
            s_hidden = 0; s_tiles = 0;
        }

        internal static string HubStatus()
        {
            if (!Plugin.Enabled.Value) return "off";
            return s_tiles == 0 ? "waiting for a race" : $"{s_hidden} renderers hidden in {s_tiles} road tiles";
        }
    }
}
