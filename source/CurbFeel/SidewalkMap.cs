using System;
using System.Collections.Generic;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace CurbFeel
{
    /// <summary>
    /// Per-tile geometry of everything beside the road, read from the tile's own visible meshes (nothing beside the road
    /// has collision in this game), so the moved walls follow the map instead of fixed offsets:
    ///  - surface triangles: the sidewalk / curb meshes (Sides_*, Base_*, Sidewalk_*, *_RoadSide, Curb*). A horizontal ray
    ///    just above the road finds the curb face exactly; a downward ray gives the sidewalk height (ramp).
    ///  - obstacles a car must not pass through: visible guardrails and rails, fences, rock walls, barricades, wood bars,
    ///    bollards, planters, bus stops, tree trunks, buildings, cliffs, tunnel walls. Compact ones are oriented boxes from
    ///    the mesh's own bounds (exact for rotated segments along curves); long hard ones (rails, fences, cliffs, tunnels:
    ///    one mesh of 100-200 m that follows the curve) use their real triangles.
    ///  - ignored: the invisible walls themselves, lamp cones, foliage, billboards, decals, terrain, signs, lamps.
    /// Rules from a census of all 54 tiles (RESEARCH.md, "Walls vs the visible map").
    /// Mesh data is read once per mesh (CPU copy if readable, else from the GPU buffers) and cached until Revert.
    ///
    /// Cost: a tile is read over several frames ([B.Walls] MapBudgetMs per frame, Pump from CurbFeelCore.Update) instead
    /// of in one frame (big tiles have 100k+ obstacle triangles: up to ~1 s in one go before 0.7); WallShifter waits for
    /// IsReady before it moves that tile's walls. All maths in here reads struct fields (Shared/FastMath.cs): Unity's
    /// Mathf / Vector3 / Matrix4x4 helpers are slow interop calls and this runs per vertex and per triangle.
    /// </summary>
    internal class SidewalkMap
    {
        private const float Cell = 4f;
        private const float LongMesh = 12f;        // longer than this (and hard): use triangles, not a box

        private struct Obb { public Vector3 C, X, Y, Z, H; }

        private readonly List<Obb> _boxes = new();
        private readonly List<Vector3> _obsTris = new();     // 3 vertices per triangle (world space)
        private readonly List<Vector3> _surfTris = new();
        private readonly Dictionary<long, List<int>> _boxGrid = new(), _obsGrid = new(), _surfGrid = new();
        private int[] _boxStamp = Array.Empty<int>(), _obsStamp = Array.Empty<int>(), _surfStamp = Array.Empty<int>();
        private int _stamp;

        public int Boxes => _boxes.Count;
        public int ObstacleTris => _obsTris.Count / 3;
        public int SurfaceTris => _surfTris.Count / 3;
        public int Skipped { get; private set; }
        /// <summary>City-style sidewalks (Sides_/Base_/Sidewalk_: curb ~1.7 m before the stock outer wall) vs park-style RoadSide meshes.</summary>
        public bool CityStyle { get; private set; }
        private int _cityMeshes, _parkMeshes;

        // incremental build: the tile's renderers, gathered once, then read a few per frame
        private readonly List<MeshRenderer> _pending = new();
        private int _next;
        private bool _ready;
        private string _sceneName;
        private double _buildMs;
        private int _buildFrames;

        private static readonly Dictionary<int, SidewalkMap> Cache = new();
        private static readonly Dictionary<int, (Vector3[] v, int[] t)> MeshCache = new();
        private const int MaxMeshCache = 4096;
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static int _building;   // maps started but not finished

        /// <summary>True while a tile's map is still being read (Pump has work).</summary>
        public static bool Building => _building > 0;

        /// <summary>
        /// The tile's map, finished now if it isn't yet (in one go: the old behaviour). WallShifter only calls this after
        /// IsReady returned true, so normally it just returns the cached map.
        /// </summary>
        public static SidewalkMap ForScene(UnityEngine.SceneManagement.Scene scene)
        {
            var map = Get(scene);
            if (!map._ready) map.Continue(double.MaxValue);
            return map;
        }

        /// <summary>
        /// True when the tile's map is complete. Starts it if needed; with [B.Walls] MapBudgetMs = 0 it is built right here
        /// in one go, otherwise Pump reads it over the next frames.
        /// </summary>
        public static bool IsReady(UnityEngine.SceneManagement.Scene scene)
        {
            var map = Get(scene);
            if (!map._ready && Settings.MapBudgetMs.Value <= 0f) map.Continue(double.MaxValue);
            return map._ready;
        }

        /// <summary>Once a frame: reads unfinished maps for at most budgetMs (at least one renderer per call).</summary>
        public static void Pump(float budgetMs)
        {
            if (_building <= 0) return;
            double until = Clock.Elapsed.TotalMilliseconds + Math.Max(0.25f, budgetMs);
            foreach (var map in Cache.Values)
            {
                if (map._ready) continue;
                map.Continue(until);
                if (Clock.Elapsed.TotalMilliseconds >= until) break;
            }
        }

        private static SidewalkMap Get(UnityEngine.SceneManagement.Scene scene)
        {
            int handle = scene.handle;
            if (Cache.TryGetValue(handle, out var map)) return map;
            map = new SidewalkMap();
            Cache[handle] = map;
            map.Start(scene);
            return map;
        }

        public static void ClearCache() { Cache.Clear(); MeshCache.Clear(); _building = 0; }

        /// <summary>Drop the map of a tile that was unloaded (scene handles aren't reused, so it would never be read again).</summary>
        public static void Forget(int sceneHandle)
        {
            if (Cache.TryGetValue(sceneHandle, out var map) && !map._ready) _building--;
            Cache.Remove(sceneHandle);
        }

        // ------------------------------------------------------------------ classification (lower-case substrings)

        private static readonly string[] Surface = { "roadside", "sidewalk", "walkway", "sides_", "base_", "curb", "kerb" };
        private static readonly string[] Ignore =
        {
            "guardrail_regular", "guardrail_wider", "curbfeel", "presetroad", "roadground",
            "light_cone", "lightcone", "bush", "grass", "ivy", "billboard", "flower", "leaves", "leaf", "canopy", "foliage",
            "decal", "shadow", "water", "sky", "cloud", "terrain", "ground_", "particle", "glow", "fx_",
            "lamp", "streetlight", "light", "sign", "cone", "flag", "banner", "poster", "wire", "cable",
        };
        private static readonly string[] Hard =
        {
            "guardrail", "guard_rail", "railgenerator", "fencegenerator", "fence", "railing", "rockwall", "rock_wall", "stonebarricade",
            "barricade", "woodbar", "wood_bar", "metal_gate", "i_brick", "grey_brick", "side_pole", "bollard", "barrierv", "barrier",
            "cementblock", "planter", "bus_stop", "busstop", "tunnel", "cliff", "retaining", "wall",
        };

        private static bool Has(string n, string[] list)
        {
            foreach (var s in list) if (n.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>Gathers the tile's active mesh renderers (one walk per root); the reading happens in Continue.</summary>
        private void Start(UnityEngine.SceneManagement.Scene scene)
        {
            _sceneName = scene.name;
            if (!scene.isLoaded) { Finish(); return; }
            double t0 = Clock.Elapsed.TotalMilliseconds;
            var roots = scene.GetRootGameObjects();
            for (int ri = 0; ri < roots.Length; ri++)
            {
                var root = roots[ri];
                if (root == null) continue;
                var renderers = root.transform.GetComponentsInChildren<MeshRenderer>(false);   // the active biome only
                for (int i = 0; i < renderers.Length; i++) _pending.Add(renderers[i]);
            }
            _buildMs += Clock.Elapsed.TotalMilliseconds - t0;
            _building++;
        }

        /// <summary>Reads pending renderers until the clock passes `until` (at least one per call); finishes the map at the end.</summary>
        private void Continue(double until)
        {
            if (_ready) return;
            double t0 = Clock.Elapsed.TotalMilliseconds;
            _buildFrames++;
            while (_next < _pending.Count)
            {
                var r = _pending[_next++];
                if (r != null && r.enabled)
                {
                    try { Add(r); }
                    catch (Exception e) { Skipped++; Plugin.Verbose($"[Sidewalk] {r.gameObject.name}: {e.Message}"); }
                }
                if (Clock.Elapsed.TotalMilliseconds >= until) break;
            }
            _buildMs += Clock.Elapsed.TotalMilliseconds - t0;
            if (_next >= _pending.Count)
            {
                _building--;
                Finish();
            }
        }

        private void Finish()
        {
            CityStyle = _cityMeshes >= _parkMeshes && _cityMeshes > 0;
            _boxStamp = new int[_boxes.Count];
            _obsStamp = new int[_obsTris.Count / 3];
            _surfStamp = new int[_surfTris.Count / 3];
            _pending.Clear();
            _ready = true;
            Plugin.Log.LogInfo($"[Sidewalk] {_sceneName}: {Boxes} obstacle boxes, {ObstacleTris} obstacle triangles, {SurfaceTris} sidewalk triangles " +
                               $"({(CityStyle ? "city" : "park")} style), {Skipped} meshes unreadable, {_buildMs:0} ms over {Math.Max(1, _buildFrames)} frame(s)");
        }

        private void Add(MeshRenderer r)
        {
            string n = (r.gameObject.name ?? "").ToLowerInvariant();
            if (n.StartsWith("guardrail_regular", StringComparison.Ordinal) || n.StartsWith("guardrail_wider", StringComparison.Ordinal)) return;   // the walls we move
            Bounds wb = r.bounds;
            Vector3 ws = wb.size;

            if (Has(n, Surface) && ws.y < 1.5f)
            {
                if (n.Contains("roadside")) _parkMeshes++; else _cityMeshes++;
                if (Tris(r, _surfTris, _surfGrid)) return;
                Skipped++;
                return;
            }
            bool hard = Has(n, Hard);
            if (!hard && Has(n, Ignore)) return;

            var t = r.transform;
            if (!hard && n.Contains("tree"))
            {
                // a tree's canopy is not an obstacle; its trunk is (0.6 m post at the pivot)
                var p = t.position;
                AddBox(new Obb { C = FM.V3(p.x, p.y + 2.5f, p.z), X = FM.V3(1f, 0f, 0f), Y = FM.V3(0f, 1f, 0f), Z = FM.V3(0f, 0f, 1f), H = FM.V3(0.3f, 2.5f, 0.3f) });
                return;
            }

            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return;
            Bounds lb = mesh.bounds;
            Vector3 sc = t.lossyScale;
            var ext = lb.extents;
            var half = FM.V3(Math.Abs(ext.x * sc.x), Math.Abs(ext.y * sc.y), Math.Abs(ext.z * sc.z));
            float lengthM = 2f * Math.Max(half.x, half.z);
            if (!hard)
            {
                // generic scenery: solid, building-sized things only (buildings, kiosks, shelters); not tile-sized ground
                if (ws.y < 0.9f || Math.Max(ws.x, ws.z) < 1.5f || lengthM > 120f) return;
            }
            else if (ws.y < 0.25f) return;   // a flat decal-like piece of a hard object

            if (hard && lengthM > LongMesh)
            {
                if (Tris(r, _obsTris, _obsGrid)) return;
                Skipped++;
                if (lengthM > 30f) return;   // a box around a long curved rail would block the road: skip it
            }
            AddBox(new Obb
            {
                C = t.TransformPoint(lb.center),
                X = t.right, Y = t.up, Z = t.forward,
                H = half,
            });
        }

        private void AddBox(Obb b)
        {
            int idx = _boxes.Count;
            _boxes.Add(b);
            // world AABB of the box for the grid
            float ex = Math.Abs(b.X.x) * b.H.x + Math.Abs(b.Y.x) * b.H.y + Math.Abs(b.Z.x) * b.H.z;
            float ez = Math.Abs(b.X.z) * b.H.x + Math.Abs(b.Y.z) * b.H.y + Math.Abs(b.Z.z) * b.H.z;
            Insert(_boxGrid, idx, b.C.x - ex, b.C.z - ez, b.C.x + ex, b.C.z + ez);
        }

        /// <summary>Adds the renderer's triangles (world space) to a soup. False if the mesh can't be read.</summary>
        private static bool Tris(MeshRenderer r, List<Vector3> soup, Dictionary<long, List<int>> grid)
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return false;
            int id = mesh.GetInstanceID();
            if (!MeshCache.TryGetValue(id, out var geo))
            {
                var v = GpuMeshReader.TryReadPositions(mesh);
                var tri = v != null ? GpuMeshReader.TryReadTriangles(mesh, v.Length) : null;
                geo = (v, tri);
                if (MeshCache.Count >= MaxMeshCache) MeshCache.Clear();
                MeshCache[id] = geo;
            }
            if (geo.v == null || geo.t == null) return false;
            var m = r.transform.localToWorldMatrix;   // one call; the per-vertex transform reads its fields
            var w = new Vector3[geo.v.Length];
            for (int i = 0; i < w.Length; i++) w[i] = FM.MulPoint(m, geo.v[i]);
            for (int i = 0; i + 2 < geo.t.Length; i += 3)
            {
                Vector3 a = w[geo.t[i]], b = w[geo.t[i + 1]], c = w[geo.t[i + 2]];
                int idx = soup.Count / 3;
                soup.Add(a); soup.Add(b); soup.Add(c);
                Insert(grid, idx, Math.Min(a.x, Math.Min(b.x, c.x)), Math.Min(a.z, Math.Min(b.z, c.z)),
                                  Math.Max(a.x, Math.Max(b.x, c.x)), Math.Max(a.z, Math.Max(b.z, c.z)));
            }
            return true;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static void Insert(Dictionary<long, List<int>> grid, int idx, float x0, float z0, float x1, float z1)
        {
            int cx0 = FM.FloorToInt(x0 / Cell), cx1 = FM.FloorToInt(x1 / Cell);
            int cz0 = FM.FloorToInt(z0 / Cell), cz1 = FM.FloorToInt(z1 / Cell);
            if ((long)(cx1 - cx0 + 1) * (cz1 - cz0 + 1) > 2500) return;   // absurdly large: not something beside a road
            for (int x = cx0; x <= cx1; x++)
                for (int z = cz0; z <= cz1; z++)
                {
                    long k = Key(x, z);
                    if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                    l.Add(idx);
                }
        }

        // ------------------------------------------------------------------ probes (main thread, not re-entrant)

        /// <summary>
        /// Distance from p along the flat direction dir (unit, y = 0) to the first obstacle, at bumper heights (road + 0.6 /
        /// + 1.1 m) and 0.5 m to either side; 0 if p is inside one. maxDist if nothing is found.
        /// </summary>
        public float Obstacle(Vector3 p, Vector3 dir, float roadY, float maxDist)
        {
            float best = maxDist;
            float sx = -dir.z, sz = dir.x;   // flat side direction
            for (int h = 0; h < 2; h++)
                for (int s = -1; s <= 1; s++)
                {
                    float k = 0.5f * s;
                    var o = FM.V3(p.x + sx * k, roadY + (h == 0 ? 0.6f : 1.1f), p.z + sz * k);
                    best = Math.Min(best, Ray(o, dir, best, true, false));
                }
            return best;
        }

        /// <summary>
        /// The curb face along dir from p (p = the stock inner wall's road face at road level): the first steep sidewalk
        /// triangle hit by a ray 0.1-0.16 m above the road, started 3.5 m on the road side of p. Distance from p (may be
        /// negative: the curb can sit in front of the stock wall), or NaN if this tile has no curb mesh there.
        /// </summary>
        public float Curb(Vector3 p, Vector3 dir, float roadY)
        {
            float best = float.MaxValue;
            for (int h = 0; h < 2; h++)
            {
                var o = FM.V3(p.x - dir.x * 3.5f, roadY + (h == 0 ? 0.1f : 0.16f) - dir.y * 3.5f, p.z - dir.z * 3.5f);
                best = Math.Min(best, Ray(o, dir, 9f, false, true));
            }
            return best < 9f ? best - 3.5f : float.NaN;
        }

        /// <summary>Sidewalk height above the road at p (downward ray on the sidewalk triangles), or NaN.</summary>
        public float Height(Vector3 p, float roadY)
        {
            var o = FM.V3(p.x, roadY + 1.5f, p.z);
            var down = FM.V3(0f, -1f, 0f);
            int cx = FM.FloorToInt(p.x / Cell), cz = FM.FloorToInt(p.z / Cell);
            if (!_surfGrid.TryGetValue(Key(cx, cz), out var list)) return float.NaN;
            float best = float.MaxValue;
            foreach (int i in list)
            {
                float t = RayTri(o, down, _surfTris[i * 3], _surfTris[i * 3 + 1], _surfTris[i * 3 + 2]);
                if (t >= 0f && t < best) best = t;
            }
            return best < 3f ? 1.5f - best : float.NaN;
        }

        /// <summary>
        /// Marches the cells along a flat ray. obstacles = boxes + obstacle triangles; else surface triangles, steep ones
        /// only when steepOnly (a curb face, not the flat gutter or sidewalk top).
        /// </summary>
        private float Ray(Vector3 o, Vector3 d, float maxDist, bool obstacles, bool steepOnly)
        {
            float best = maxDist;
            _stamp++;
            if (_stamp == int.MaxValue) { _stamp = 1; Array.Clear(_boxStamp, 0, _boxStamp.Length); Array.Clear(_obsStamp, 0, _obsStamp.Length); Array.Clear(_surfStamp, 0, _surfStamp.Length); }
            for (float a = -Cell * 0.5f; a <= maxDist + Cell * 0.5f; a += Cell * 0.5f)
            {
                int cx = FM.FloorToInt((o.x + d.x * a) / Cell), cz = FM.FloorToInt((o.z + d.z * a) / Cell);
                for (int ix = -1; ix <= 1; ix++)
                    for (int iz = -1; iz <= 1; iz++)
                    {
                        long k = Key(cx + ix, cz + iz);
                        if (obstacles)
                        {
                            if (_boxGrid.TryGetValue(k, out var bl))
                                foreach (int i in bl)
                                {
                                    if (_boxStamp[i] == _stamp) continue;
                                    _boxStamp[i] = _stamp;
                                    float t = RayBox(o, d, _boxes[i]);
                                    if (t >= 0f && t < best) best = t;
                                }
                            if (_obsGrid.TryGetValue(k, out var tl))
                                foreach (int i in tl)
                                {
                                    if (_obsStamp[i] == _stamp) continue;
                                    _obsStamp[i] = _stamp;
                                    float t = RayTri(o, d, _obsTris[i * 3], _obsTris[i * 3 + 1], _obsTris[i * 3 + 2]);
                                    if (t >= 0f && t < best) best = t;
                                }
                        }
                        else if (_surfGrid.TryGetValue(k, out var sl))
                            foreach (int i in sl)
                            {
                                if (_surfStamp[i] == _stamp) continue;
                                _surfStamp[i] = _stamp;
                                Vector3 a0 = _surfTris[i * 3], b0 = _surfTris[i * 3 + 1], c0 = _surfTris[i * 3 + 2];
                                if (steepOnly)
                                {
                                    var nrm = FM.Cross(FM.Sub(b0, a0), FM.Sub(c0, a0));
                                    float len = FM.Length(nrm);
                                    if (len < 1e-6f || Math.Abs(nrm.y / len) > 0.7f) continue;   // flat: gutter / sidewalk top
                                }
                                float t = RayTri(o, d, a0, b0, c0);
                                if (t >= 0f && t < best) best = t;
                            }
                    }
                if (a > best + Cell) break;   // nothing nearer can come from cells further along
            }
            return best;
        }

        /// <summary>Ray vs oriented box: entry distance (0 if the origin is inside), or -1.</summary>
        private static float RayBox(Vector3 o, Vector3 d, Obb b)
        {
            Vector3 rel = FM.Sub(o, b.C);
            float tmin = 0f, tmax = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 ax = axis == 0 ? b.X : axis == 1 ? b.Y : b.Z;
                float h = axis == 0 ? b.H.x : axis == 1 ? b.H.y : b.H.z;
                float lo = FM.Dot(rel, ax), ld = FM.Dot(d, ax);
                if (Math.Abs(ld) < 1e-6f) { if (lo < -h || lo > h) return -1f; continue; }
                float t1 = (-h - lo) / ld, t2 = (h - lo) / ld;
                if (t1 > t2) (t1, t2) = (t2, t1);
                if (t1 > tmin) tmin = t1;
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) return -1f;
            }
            return tmin;
        }

        /// <summary>Two-sided Moller-Trumbore: distance along d, or -1.</summary>
        private static float RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 e1 = FM.Sub(b, a), e2 = FM.Sub(c, a);
            Vector3 p = FM.Cross(d, e2);
            float det = FM.Dot(e1, p);
            if (Math.Abs(det) < 1e-8f) return -1f;
            float inv = 1f / det;
            Vector3 s = FM.Sub(o, a);
            float u = FM.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return -1f;
            Vector3 q = FM.Cross(s, e1);
            float v = FM.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return -1f;
            float t = FM.Dot(e2, q) * inv;
            return t >= 0f ? t : -1f;
        }
    }
}
