using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Sandbox
{
    /// <summary>[Scenery] settings and the one call Plugin.Load makes (SceneryHost.Init).</summary>
    internal static class SceneryHost
    {
        internal static ConfigEntry<bool> Enabled, SimpleBlocks, HideLights;
        internal static ConfigEntry<float> BudgetMs;

        internal static void Init(ConfigFile config, BasePlugin plugin)
        {
            Enabled = config.Bind("Scenery", "StripBuildings", true,
                "In Sandbox mode, hide the buildings, trees, props and ads around the roads for frame rate. The road, sidewalks, curbs, walls, ground and traffic cones stay.");
            SimpleBlocks = config.Bind("Scenery", "SimpleBlocks", true,
                "Put a plain block where each building stood (one mesh and one draw call per tile, no shadows), so the city keeps its shape. Off = open ground.");
            HideLights = config.Bind("Scenery", "HideLights", true, "Also switch off the street and building lights of the hidden scenery (about 100 per tile).");
            BudgetMs = config.Bind("Scenery", "BudgetMs", 2f, new ConfigDescription("Milliseconds per frame spent stripping a newly loaded tile (spread over frames, no stutter).",
                new AcceptableValueRange<float>(0.5f, 10f)));
            ClassInjector.RegisterTypeInIl2Cpp<SceneryRunner>();
            plugin.AddComponent<SceneryRunner>();
        }
    }

    /// <summary>
    /// Sandbox scenery: strips each road tile down to what the car needs and what is cheap to draw.
    ///
    /// The game builds a race from additive tile scenes (LevelGenerator.SpawnTilesCoroutine, all loaded during the loading
    /// screen, unloaded at race end). Each tile has a "Road Network" (the road mesh PresetRoad, RoadGroundCollider and the
    /// invisible guardrail walls, layers Street 11 / Guardrail 15) and a "Biomes" group whose active biome holds the
    /// buildings, trees, props, ads, lamps and lights. Buildings and props carry no colliders (only the traffic cones do),
    /// so switching their renderers off changes nothing the car can hit. Static batching is off in this game, so every
    /// hidden renderer is one draw call (and often a shadow caster) saved: 500-1,300 per tile.
    ///
    /// Per tile (polled once a second while Sandbox is active, at most one new tile opened per poll, work spread over frames
    /// by BudgetMs):
    /// - kept: everything outside "Biomes"; inside it anything whose own name or a parent's matches a Keep word: road,
    ///   sidewalk / walkway / kerb / curb surfaces, the ground meshes (Ground_Easy/Normal/Hard/Pro, Moss_Plane), walls,
    ///   guardrails, railings, barriers, fences, barricades, bollards, cement blocks, bus stops, tunnels, bridges, cliffs,
    ///   bricks, gates, planters, floors, water and riversides (CurbFeel's surface and hard words plus the bridge pieces),
    ///   traffic cones (coneV*, the only props with colliders), and anything on the Street / Guardrail / WeatherBlocker layers;
    /// - hidden: every other renderer under Biomes with Renderer.forceRenderingOff = true. Its `enabled` flag is left alone,
    ///   so CurbFeel (which reads enabled renderers for its walls) sees the same tile whatever the order, and LODGroups keep
    ///   working; with HideLights the Lights under Biomes too, except tunnel and bridge lights;
    /// - SimpleBlocks: one combined mesh of plain boxes, one per hidden building (LOD0 or a single-LOD renderer named like a
    ///   building, its own name or its parent's), using its world bounds (read before hiding; boxes larger than 120 m across
    ///   are skipped), no colliders, no shadows, one shared material; a root named "fx_SandboxBlocks" in the tile scene
    ///   (fx_ is a CurbFeel ignore word, so it never becomes a wall) that goes when the tile unloads (its mesh is destroyed
    ///   by us). A tile that fails is left as it is and logged, without switching the feature off.
    /// Everything is given back (renderers and lights on, blocks destroyed) when Sandbox ends or StripBuildings is switched
    /// off, and on unload. Unity calls used: SceneManager.sceneCount / GetSceneAt / MoveGameObjectToScene, Scene.isLoaded /
    /// handle / GetRootGameObjects, GetComponentsInChildren, Renderer.forceRenderingOff / bounds, Light.enabled, Mesh, Material.
    /// </summary>
    public class SceneryRunner : MonoBehaviour
    {
        public SceneryRunner(IntPtr ptr) : base(ptr) { }

        private static readonly string[] Keep =
        {
            "road", "sidewalk", "walkway", "kerb", "curb", "sides_", "base_h", "ground_easy", "ground_normal", "ground_hard",
            "ground_pro", "moss_plane", "guardrail", "guard_rail", "railing", "railgenerator", "barrier", "wall", "retaining",
            "fence", "barricade", "bollard", "cementblock", "bus_stop", "tunnel", "bridge", "cliff", "i_brick", "grey_brick",
            "metal_gate", "side_pole", "treeplanter", "planter_p", "floor", "riverside_", "water", "ramp", "collider",
        };
        private static readonly string[] BuildingWords = { "residential", "c_bd", "c_store", "i_bd", "silo", "house", "building" };
        private const int LayerStreet = 11, LayerGuardrail = 15, LayerWeather = 28;

        private sealed class Tile
        {
            public int Handle;
            public string Name;
            public Scene Scene;
            public Renderer[] Renderers;
            public Light[] Lights;
            public int Next;
            public bool Done;
            public readonly List<Renderer> Hidden = new List<Renderer>();
            public readonly List<Light> Off = new List<Light>();
            public readonly List<Vector3> BoxMin = new List<Vector3>(), BoxMax = new List<Vector3>();
            public GameObject Blocks;
            public Mesh BlockMesh;
        }

        private readonly Dictionary<int, Tile> _tiles = new Dictionary<int, Tile>();
        private readonly HashSet<int> _notTile = new HashSet<int>(), _seen = new HashSet<int>();
        private readonly List<int> _gone = new List<int>();
        private readonly Stopwatch _sw = new Stopwatch();
        private Material _blockMat;
        private float _nextPoll;
        private bool _wasOn, _broken;
        private int _errors;

        private void Update()
        {
            if (_broken) return;
            try
            {
                bool on = Plugin.Active && SceneryHost.Enabled != null && SceneryHost.Enabled.Value;
                if (!on) { if (_wasOn) RestoreAll(Plugin.Active ? "StripBuildings off" : "Sandbox ended"); _wasOn = false; return; }
                _wasOn = true;
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
            if (_blockMat != null) { try { Destroy(_blockMat); } catch { /* shutting down */ } _blockMat = null; }
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
                var tile = Open(scene);   // at most one new tile per poll: its renderer list is a one-off hitch
                opened = true;
                if (tile != null) _tiles[h] = tile; else _notTile.Add(h);
            }
            // scenes that unloaded: a tile's renderers went with it; our block mesh is ours to destroy
            _gone.Clear();
            foreach (var kv in _tiles) if (!_seen.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (int h in _gone) { var t = _tiles[h]; DestroyBlocks(t); _tiles.Remove(h); }
            if (_isGone == null) _isGone = IsGone;
            _notTile.RemoveWhere(_isGone);
            if (opened) _nextPoll = 0f;   // more scenes may wait: open the next one next frame, not in a second
        }

        private Predicate<int> _isGone;

        private bool IsGone(int handle) => !_seen.Contains(handle);
        private void Awake() { _isGone = IsGone; }

        /// <summary>A road tile = a loaded scene with a root (or child) named "Biomes". Collects its renderers and lights once.</summary>
        private static Tile Open(Scene scene)
        {
            var roots = scene.GetRootGameObjects();
            Transform biomes = null;
            for (int i = 0; i < roots.Length && biomes == null; i++)
            {
                var r = roots[i];
                if (r == null) continue;
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
                Handle = scene.handle, Name = scene.name, Scene = scene,
                Renderers = biomes.GetComponentsInChildren<Renderer>(false),
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
                    t.Done = true;   // this tile stays as far as it got; the others carry on
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
                if (r != null && !r.forceRenderingOff && !Keeps(r.transform, r.gameObject.layer))
                {
                    if (SceneryHost.SimpleBlocks.Value && IsBuilding(r.transform))
                    {
                        var b = r.bounds;   // read before hiding
                        Vector3 lo = b.min, hi = b.max;
                        float w = hi.x - lo.x, d = hi.z - lo.z, h = hi.y - lo.y;
                        if (w < 120f && d < 120f && w >= 3f && d >= 3f && h >= 3f) { t.BoxMin.Add(lo); t.BoxMax.Add(hi); }   // buildings, not props
                    }
                    r.forceRenderingOff = true;   // `enabled` untouched: CurbFeel and LODGroups see the same renderer
                    t.Hidden.Add(r);
                }
                if ((t.Next & 31) == 0 && _sw.Elapsed.TotalMilliseconds > budget) return false;
            }
            if (SceneryHost.HideLights.Value)
                foreach (var l in t.Lights)
                    if (l != null && l.enabled && !Lit(l.transform)) { l.enabled = false; t.Off.Add(l); }
            int blocks = t.BoxMin.Count;
            if (SceneryHost.SimpleBlocks.Value) BuildBlocks(t);
            t.Done = true;
            t.Renderers = Array.Empty<Renderer>(); t.Lights = Array.Empty<Light>();   // let the arrays go
            Plugin.Log.LogInfo($"[Sandbox] scenery: tile {t.Name}: {t.Hidden.Count} renderers hidden, {t.Off.Count} lights off, {blocks} blocks");
            return true;
        }

        /// <summary>Tunnel and bridge lights stay on (the tunnels and bridges themselves are kept).</summary>
        private static bool Lit(Transform t)
        {
            for (int depth = 0; t != null && depth < 6; depth++, t = t.parent)
            {
                string n = t.gameObject.name;
                if (n == "Biomes") break;
                n = n.ToLowerInvariant();
                if (n.Contains("tunnel") || n.Contains("bridge")) return true;
            }
            return false;
        }

        /// <summary>Keep road, sidewalk, curb, ground, walls and other hard things (by own or parent name, up to the biome), and collision layers.</summary>
        private static bool Keeps(Transform t, int layer)
        {
            if (layer == LayerStreet || layer == LayerGuardrail || layer == LayerWeather) return true;
            for (int depth = 0; t != null && depth < 6; depth++, t = t.parent)
            {
                string n = t.gameObject.name;
                if (n == "Biomes") break;
                n = n.ToLowerInvariant();
                if (n.StartsWith("conev", StringComparison.Ordinal)) return true;   // traffic cones (colliders), not light cones
                for (int i = 0; i < Keep.Length; i++) if (n.Contains(Keep[i])) return true;
            }
            return false;
        }

        /// <summary>A building's mesh (its own name or its parent's, e.g. "geo1." children), top LOD only, so it gets one block.</summary>
        private static bool IsBuilding(Transform t)
        {
            if (IsBuildingName(t.gameObject.name)) return true;
            var p = t.parent;
            if (p == null || !IsBuildingName(p.gameObject.name)) return false;
            string own = t.gameObject.name.ToLowerInvariant();
            return own.IndexOf("_lod", StringComparison.Ordinal) < 0 || own.IndexOf("_lod0", StringComparison.Ordinal) >= 0;
        }

        private static bool IsBuildingName(string name)
        {
            string n = name.ToLowerInvariant();
            bool word = false;
            for (int i = 0; i < BuildingWords.Length && !word; i++) word = n.Contains(BuildingWords[i]);
            if (!word) return false;
            int lod = n.IndexOf("_lod", StringComparison.Ordinal);
            return lod < 0 || n.IndexOf("_lod0", StringComparison.Ordinal) >= 0;
        }

        // ------------------------------------------------------------------ simple blocks: one mesh per tile

        private void BuildBlocks(Tile t)
        {
            int n = t.BoxMin.Count;
            if (n == 0) return;
            if (!t.Scene.isLoaded) { t.BoxMin.Clear(); t.BoxMax.Clear(); return; }   // unloaded meanwhile
            var mat = BlockMaterial();   // first: a missing shader throws before anything is created
            var verts = new Vector3[n * 20];   // 5 faces (no bottom) x 4
            var norms = new Vector3[n * 20];
            var tris = new int[n * 30];
            for (int b = 0; b < n; b++)
            {
                Vector3 lo = t.BoxMin[b], hi = t.BoxMax[b];
                int v = b * 20, i = b * 30;
                Face(verts, norms, tris, ref v, ref i, new Vector3(lo.x, hi.y, lo.z), new Vector3(lo.x, hi.y, hi.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(hi.x, hi.y, lo.z), Vector3.up);
                Face(verts, norms, tris, ref v, ref i, new Vector3(lo.x, lo.y, hi.z), new Vector3(hi.x, lo.y, hi.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(lo.x, hi.y, hi.z), Vector3.forward);
                Face(verts, norms, tris, ref v, ref i, new Vector3(hi.x, lo.y, lo.z), new Vector3(lo.x, lo.y, lo.z), new Vector3(lo.x, hi.y, lo.z), new Vector3(hi.x, hi.y, lo.z), Vector3.back);
                Face(verts, norms, tris, ref v, ref i, new Vector3(hi.x, lo.y, hi.z), new Vector3(hi.x, lo.y, lo.z), new Vector3(hi.x, hi.y, lo.z), new Vector3(hi.x, hi.y, hi.z), Vector3.right);
                Face(verts, norms, tris, ref v, ref i, new Vector3(lo.x, lo.y, lo.z), new Vector3(lo.x, lo.y, hi.z), new Vector3(lo.x, hi.y, hi.z), new Vector3(lo.x, hi.y, lo.z), Vector3.left);
            }
            var mesh = new Mesh { name = "Sandbox.Blocks." + t.Name };
            if (verts.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var go = new GameObject("fx_SandboxBlocks");   // "fx_": a CurbFeel ignore word, never read as a wall
            t.Blocks = go; t.BlockMesh = mesh;               // tracked at once: a throw below can't leak them
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            SceneManager.MoveGameObjectToScene(go, t.Scene);   // unloads with the tile
            t.BoxMin.Clear(); t.BoxMax.Clear();
        }

        // a quad a, b, c, d listed clockwise as seen from outside (Unity's front-face order): triangles a-b-c and a-c-d
        private static void Face(Vector3[] verts, Vector3[] norms, int[] tris, ref int v, ref int i, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            verts[v] = a; verts[v + 1] = b; verts[v + 2] = c; verts[v + 3] = d;
            norms[v] = n; norms[v + 1] = n; norms[v + 2] = n; norms[v + 3] = n;
            tris[i] = v; tris[i + 1] = v + 1; tris[i + 2] = v + 2;
            tris[i + 3] = v; tris[i + 4] = v + 2; tris[i + 5] = v + 3;
            v += 4; i += 6;
        }

        private Material BlockMaterial()
        {
            if (_blockMat != null) return _blockMat;
            var sh = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) throw new InvalidOperationException("no URP lit shader for the blocks");
            _blockMat = new Material(sh) { name = "Sandbox.Blocks", hideFlags = HideFlags.DontUnloadUnusedAsset };
            _blockMat.SetColor("_BaseColor", new Color(0.62f, 0.64f, 0.68f, 1f));
            return _blockMat;
        }

        private void DestroyBlocks(Tile t)
        {
            if (t.Blocks != null) { try { Destroy(t.Blocks); } catch { /* went with the scene */ } }
            if (t.BlockMesh != null) { try { Destroy(t.BlockMesh); } catch { /* shutting down */ } }
            t.Blocks = null; t.BlockMesh = null;
        }

        // ------------------------------------------------------------------ give everything back

        private void RestoreAll(string why)
        {
            int r = 0, l = 0;
            foreach (var t in _tiles.Values)
            {
                foreach (var x in t.Hidden) { try { if (x != null) { x.forceRenderingOff = false; r++; } } catch { /* gone */ } }
                foreach (var x in t.Off) { try { if (x != null) { x.enabled = true; l++; } } catch { /* gone */ } }
                DestroyBlocks(t);
            }
            if (_tiles.Count > 0) Plugin.Log.LogInfo($"[Sandbox] scenery restored ({why}): {r} renderers and {l} lights back on in {_tiles.Count} tiles");
            _tiles.Clear();
        }
    }
}
