using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CurbFeel
{
    /// <summary>
    /// B + D. Every road tile (an additive scene) has two invisible wall meshes per road object, Guardrail_Regular
    /// (inner) and Guardrail_Wider (outer): 2 m thick vertical slabs sharing topology, so vertex i of one matches vertex i
    /// of the other; W[i]-R[i] is the outward direction at that point. Where the visible curb really is differs by street
    /// type (city sidewalks: ~1.7 m before the outer wall, in front of the inner one; park / industrial RoadSide: ~0.1 m
    /// behind it), so it is measured on the tile's own sidewalk mesh (SidewalkMap.Curb). B moves both walls to
    /// (curb face + AllowedOverCurb), stopped short of the first visible railing, fence, rock wall, planter, bollard, tree
    /// trunk or building (SidewalkMap.Obstacle). D adds a bevelled curb collider on the Street layer.
    /// </summary>
    internal class WallShifter
    {
        private const int GuardrailLayer = 15;
        private const int StreetLayer = 11;

        private class Pair
        {
            public MeshCollider Regular, Wider;
            public Mesh RegularOriginal, WiderOriginal;
            public GameObject Ramp;
            public readonly List<Mesh> Owned = new();   // meshes we made for this pair (shifted walls, ramp, wall view)
            public string Label;
            public float AvgShift, AvgOver, CappedShare;
        }

        private int _unpaired;

        private void UpdateStats()
        {
            int ramps = 0; float shift = 0f, over = 0f, capped = 0f;
            foreach (var p in _pairs) { if (p.Ramp != null) ramps++; shift += p.AvgShift; over += p.AvgOver; capped += p.CappedShare; }
            Stats.WallPairs = _pairs.Count;
            Stats.Ramps = ramps;
            Stats.AvgWallShift = _pairs.Count > 0 ? shift / _pairs.Count : 0f;
            Stats.AvgOverCurb = _pairs.Count > 0 ? over / _pairs.Count : 0f;
            Stats.CappedShare = _pairs.Count > 0 ? capped / _pairs.Count : 0f;
            Stats.UnpairedWalls = _unpaired;
        }

        private readonly List<Pair> _pairs = new();
        private readonly HashSet<int> _seen = new();          // Regular collider instance IDs already handled
        private readonly Queue<(MeshCollider r, MeshCollider w)> _queue = new();

        // Scratch buffers reused by every wall pair (Process / ComputeOverCurb / BuildRamp run on the main thread, one at a time).
        private readonly Dictionary<long, (float obstacle, float h)> _probeCache = new();
        private readonly Dictionary<(int, int), List<int>> _grid = new();
        private readonly Stack<List<int>> _cellPool = new();
        private readonly Dictionary<int, float> _groundY = new();
        private readonly List<Vector3> _rampVerts = new();
        private readonly List<int> _rampIdx = new();
        private readonly HashSet<long> _rampDone = new();
        private Material _debugMat;
        private bool _debugMatTried;

        public int PairCount => _pairs.Count;

        public void Tick()
        {
            // drop pairs whose tile was unloaded
            for (int i = _pairs.Count - 1; i >= 0; i--)
                if (_pairs[i].Regular == null) { DestroyOwned(_pairs[i]); _pairs.RemoveAt(i); }

            Discover();
            // spread work over frames: at most one wall pair per tick (dead entries from unloaded tiles don't count)
            while (_queue.Count > 0)
            {
                var (r, w) = _queue.Peek();
                if (r == null || w == null) { _queue.Dequeue(); continue; }
                // the tile's sidewalk map is read over several frames (SidewalkMap.Pump): wait for it instead of hitching
                if (!SidewalkMap.IsReady(r.gameObject.scene)) break;
                _queue.Dequeue();
                try { Process(r, w); }
                catch (Exception e) { Plugin.Log.LogError($"[Walls] {r.name}: {e}"); }
                break;
            }
            UpdateStats();
        }

        private static void DestroyOwned(Pair p)
        {
            foreach (var m in p.Owned) { try { if (m != null) UnityEngine.Object.Destroy(m); } catch { /* gone with the tile */ } }
            p.Owned.Clear();
        }

        public void Revert()
        {
            foreach (var p in _pairs)
            {
                if (p.Regular != null && p.RegularOriginal != null) p.Regular.sharedMesh = p.RegularOriginal;
                if (p.Wider != null && p.WiderOriginal != null) p.Wider.sharedMesh = p.WiderOriginal;
                if (p.Ramp != null) UnityEngine.Object.Destroy(p.Ramp);
                DestroyOwned(p);
            }
            _pairs.Clear();
            _seen.Clear();
            _queue.Clear();
            _unpaired = 0;
            _scenes.Clear();                                  // every loaded tile is scanned again by the next ticks
            _meshFirst.Clear();
            _nextSafety = 0f;
            SidewalkMap.ClearCache();
            UpdateStats();
        }

        // ---------------------------------------------------------------- discovery
        // Walls only appear when a road tile (additive scene) loads, so instead of searching every MeshCollider in the
        // game every tick, each loaded scene is walked from its own roots: once when it first shows up loaded, plus
        // FollowUpScans later (in case the tile creates walls after loading). While no wall pair exists at all, every
        // loaded scene is rescanned every SafetyRescan seconds. At most ScenesPerTick scene walks run per tick.

        private static readonly float[] FollowUpScans = { 1f, 4f };   // seconds after a scene's first scan
        private const float SafetyRescan = 5f;
        private const int ScenesPerTick = 2;
        private const int MaxMeshCache = 8192;

        private class SceneScan
        {
            public string Name;
            public float FirstScanTime = -1f;
            public int ScansDone;
            public bool Force;
            public int Regulars, Widers;                         // from the last walk, for the DIAG census
            public readonly HashSet<int> NotWall = new();        // guardrail-layer collider IDs whose name rules them out
        }

        private struct Candidate
        {
            public MeshCollider Col;
            public int Id, VertexCount;
            public string Name;
            public bool Batch, Ok;
            public Vector3 First;                                // world-space first vertex
        }

        private readonly Dictionary<int, SceneScan> _scenes = new();                       // by scene handle
        private readonly Dictionary<int, (int count, Vector3 first, bool ok)> _meshFirst = new();   // by mesh instance ID
        private readonly List<(UnityEngine.SceneManagement.Scene scene, int handle)> _loadedScratch = new();
        private readonly List<int> _goneScratch = new();
        private readonly List<Candidate> _regScratch = new(), _widScratch = new();
        private float _nextSafety;

        private void Discover()
        {
            float now = Time.unscaledTime;
            _loadedScratch.Clear();
            int count = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;                   // still streaming in: picked up once it is loaded
                int handle = scene.handle;
                _loadedScratch.Add((scene, handle));
                if (!_scenes.ContainsKey(handle)) _scenes[handle] = new SceneScan { Name = scene.name };
            }

            // forget tiles that were unloaded
            if (_scenes.Count > _loadedScratch.Count)
            {
                _goneScratch.Clear();
                foreach (var h in _scenes.Keys)
                {
                    bool loaded = false;
                    foreach (var l in _loadedScratch) if (l.handle == h) { loaded = true; break; }
                    if (!loaded) _goneScratch.Add(h);
                }
                foreach (var h in _goneScratch) { _scenes.Remove(h); SidewalkMap.Forget(h); }
            }

            if (_pairs.Count == 0 && _queue.Count == 0 && now >= _nextSafety)
            {
                _nextSafety = now + SafetyRescan;
                foreach (var st in _scenes.Values) st.Force = true;
            }

            int budget = ScenesPerTick;
            foreach (var (scene, handle) in _loadedScratch)
            {
                if (budget <= 0) break;
                var st = _scenes[handle];
                bool due = st.Force
                           || st.ScansDone == 0
                           || (st.ScansDone <= FollowUpScans.Length && now >= st.FirstScanTime + FollowUpScans[st.ScansDone - 1]);
                if (!due) continue;
                budget--;
                st.Force = false;
                if (st.ScansDone == 0) st.FirstScanTime = now;
                st.ScansDone++;
                ScanScene(scene, st);
            }

            Census();
        }

        /// <summary>Walks one loaded scene's MeshColliders (inactive included) and queues new Regular/Wider wall pairs.</summary>
        private void ScanScene(UnityEngine.SceneManagement.Scene scene, SceneScan st)
        {
            _regScratch.Clear();
            _widScratch.Clear();
            var roots = scene.GetRootGameObjects();
            for (int ri = 0; ri < roots.Length; ri++)
            {
                var root = roots[ri];
                if (root == null) continue;
                var cols = root.transform.GetComponentsInChildren<MeshCollider>(true);   // same Component overload HullTrimmer uses
                for (int i = 0; i < cols.Length; i++)
                {
                    var c = cols[i];
                    if (c == null) continue;
                    // cheap filters first: layer, already-handled ID, known non-wall ID, then the name, then the mesh
                    var go = c.gameObject;
                    int layer = go.layer;
                    if (layer != GuardrailLayer && layer != 16) continue;
                    int id = c.GetInstanceID();
                    if (_seen.Contains(id) || st.NotWall.Contains(id)) continue;
                    string n = go.name;
                    bool regular = false, wider = false;
                    if (n.IndexOf("Guardrail", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        regular = n.IndexOf("Regular", StringComparison.OrdinalIgnoreCase) >= 0;
                        wider = !regular && n.IndexOf("Wider", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    if (!regular && !wider) { st.NotWall.Add(id); continue; }
                    var mesh = c.sharedMesh;
                    if (mesh == null) continue;                  // not cached: a mesh may still be assigned later

                    var cand = new Candidate { Col = c, Id = id, Name = n, Batch = n.Contains("Batch") };
                    var (vc, first, ok) = FirstVertex(mesh);
                    cand.VertexCount = vc;
                    cand.Ok = ok;
                    if (ok) cand.First = c.transform.TransformPoint(first);
                    (regular ? _regScratch : _widScratch).Add(cand);
                }
            }
            st.Regulars = _regScratch.Count;
            st.Widers = _widScratch.Count;

            // All candidates come from one scene, so the old "same scene" test always holds here.
            foreach (var r in _regScratch)
            {
                _seen.Add(r.Id);
                if (!r.Ok)
                {
                    _unpaired++;
                    Plugin.Verbose($"[Walls] {st.Name}/{r.Name}: mesh not readable or empty - left stock");
                    continue;
                }

                MeshCollider best = null; float bestD = 5f;
                foreach (var w in _widScratch)
                {
                    if (!w.Ok || w.VertexCount != r.VertexCount) continue;
                    if (w.Batch != r.Batch) continue;
                    float d = Vector3.Distance(r.First, w.First);
                    if (d < bestD) { bestD = d; best = w.Col; }
                }
                if (best == null)
                {
                    _unpaired++;
                    Plugin.Verbose($"[Walls] no matching outer wall for {st.Name}/{r.Name} ({r.VertexCount} verts) - left stock");
                    continue;
                }
                _queue.Enqueue((r.Col, best));
            }
            _regScratch.Clear();
            _widScratch.Clear();
        }

        /// <summary>Vertex count and first vertex (local space) of a mesh, read once per mesh instead of once per comparison.</summary>
        private (int count, Vector3 first, bool ok) FirstVertex(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (_meshFirst.TryGetValue(id, out var info)) return info;
            int vc = mesh.vertexCount;
            info = (vc, Vector3.zero, false);
            if (vc > 0 && mesh.isReadable)
            {
                var v = mesh.vertices;
                if (v.Length > 0) info = (vc, v[0], true);
            }
            if (_meshFirst.Count >= MaxMeshCache) _meshFirst.Clear();
            _meshFirst[id] = info;
            return info;
        }

        private void Process(MeshCollider rCol, MeshCollider wCol)
        {
            Mesh rMesh = rCol.sharedMesh, wMesh = wCol.sharedMesh;
            if (!rMesh.isReadable || !wMesh.isReadable)
            {
                Plugin.Log.LogWarning($"[Walls] {rCol.name}: mesh not readable, skipped");
                return;
            }

            Transform rT = rCol.transform, wT = wCol.transform;
            var rv = rMesh.vertices; var wv = wMesh.vertices; var tris = rMesh.triangles;
            int n = rv.Length;

            // world-space wall points
            var R = new Vector3[n]; var W = new Vector3[n];
            float minY = float.MaxValue, maxY = float.MinValue, worldMinY = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                R[i] = rT.TransformPoint(rv[i]);
                W[i] = wT.TransformPoint(wv[i]);
                minY = Mathf.Min(minY, rv[i].y); maxY = Mathf.Max(maxY, rv[i].y);
                worldMinY = Mathf.Min(worldMinY, R[i].y);
            }
            float midY = (minY + maxY) * 0.5f;

            // per-vertex outward direction and wall separation
            var dir = new Vector3[n]; var sep = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 d = W[i] - R[i]; d.y = 0f;
                sep[i] = d.magnitude;
                dir[i] = sep[i] > 1e-3f ? d / sep[i] : Vector3.zero;
            }

            // The walls are 2 m thick slabs: every cross-section has a road-facing and a back face. All probes run from
            // the road-facing vertex of each cross-section ("root"), and the whole cross-section moves by the same amount.
            var root = FaceRoots(R, dir);

            var curb = new float[n]; var over = new float[n]; var rampH = new float[n]; var roadY = new float[n];
            int capped = ComputePlacement(rCol, R, dir, sep, root, worldMinY + 1f, rv, midY, curb, over, rampH, roadY, out int detected, out int roots);
            var shift = new float[n];
            for (int i = 0; i < n; i++)
                shift[i] = (Settings.WallsEnabled.Value && dir[i] != Vector3.zero) ? curb[i] + over[i] : 0f;

            var pair = new Pair { Regular = rCol, Wider = wCol, RegularOriginal = rMesh, WiderOriginal = wMesh, Label = $"{rCol.gameObject.scene.name}/{rCol.name}" };
            _owned = pair.Owned;
            _pairs.Add(pair);   // recorded before anything is changed: Revert can always undo it

            // B. shifted walls
            if (Settings.WallsEnabled.Value)
            {
                var newR = new Vector3[n]; var newW = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    newR[i] = rT.InverseTransformPoint(R[i] + dir[i] * shift[i]);
                    float wPush = Mathf.Max(0f, shift[i] + Settings.BackupGap.Value - sep[i]);
                    newW[i] = wT.InverseTransformPoint(W[i] + dir[i] * wPush);
                }
                var nr = CloneWithVertices(rMesh, newR, "CurbFeel_" + rMesh.name, tris);
                var nw = CloneWithVertices(wMesh, newW, "CurbFeel_" + wMesh.name);
                pair.Owned.Add(nr); pair.Owned.Add(nw);
                rCol.sharedMesh = nr;
                wCol.sharedMesh = nw;
            }

            // D. curb ramp (+ the ShowWalls debug strip)
            int rampTris = 0;
            if (Settings.RampEnabled.Value || Settings.ShowWalls.Value)
                pair.Ramp = BuildRamp(rCol, R, root, tris, rv, midY, dir, curb, shift, rampH, roadY, out rampTris);

            float avgCurb = 0f, avgShift = 0f, avgOver = 0f, avgH = 0f; int cnt = 0;
            for (int i = 0; i < n; i++) if (rv[i].y < midY && root[i] == i) { avgCurb += curb[i]; avgShift += shift[i]; avgOver += over[i]; avgH += rampH[i]; cnt++; }
            if (cnt > 0) { avgCurb /= cnt; avgShift /= cnt; avgOver /= cnt; avgH /= cnt; }
            pair.AvgShift = avgShift;
            pair.AvgOver = avgOver;
            pair.CappedShare = cnt > 0 ? capped / (float)cnt : 0f;
            Plugin.Verbose($"[Walls] {pair.Label}: curb {avgCurb:+0.00;-0.00} m from the stock wall ({detected}/{roots} points measured on the sidewalk mesh), " +
                           $"wall {avgOver:F2} m past the curb ({pair.CappedShare:P0} stopped by railings / walls / buildings), ramp {avgH:F2} m high, {rampTris} tris");
        }

        /// <summary>
        /// For each vertex, the road-facing vertex of its cross-section: the vertex within 3 m, less than 0.4 m to the side
        /// (across dir), that lies furthest towards the road. Vertices on the slab's back face map to their front vertex.
        /// </summary>
        private List<Mesh> _owned;   // the pair being built (Process -> BuildRamp)
        private readonly HashSet<int> _noSidewalkLogged = new();

        /// <summary>Farthest a wall may sit on the road side of the stock inner wall, and only where a railing / wall was measured right there.</summary>
        private const float MaxInward = 1f;

        private int[] FaceRoots(Vector3[] R, Vector3[] dir)
        {
            int n = R.Length;
            var root = new int[n];
            const float cell = 3f;
            var grid = _grid;
            foreach (var c in grid.Values) { c.Clear(); _cellPool.Push(c); }
            grid.Clear();
            for (int i = 0; i < n; i++)
            {
                var key = (Mathf.FloorToInt(R[i].x / cell), Mathf.FloorToInt(R[i].z / cell));
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = _cellPool.Count > 0 ? _cellPool.Pop() : new List<int>();
                l.Add(i);
            }
            for (int i = 0; i < n; i++)
            {
                root[i] = i;
                if (dir[i] == Vector3.zero) continue;
                float best = 0f;
                int gx = Mathf.FloorToInt(R[i].x / cell), gz = Mathf.FloorToInt(R[i].z / cell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                float rx = R[j].x - R[i].x, rz = R[j].z - R[i].z;
                                float along = rx * dir[i].x + rz * dir[i].z;
                                float side = Mathf.Abs(-rx * dir[i].z + rz * dir[i].x);
                                if (side < 0.4f && along < best - 0.05f && along > -3f) { best = along; root[i] = j; }
                            }
            }
            // a root's own root (chains on very thick walls): follow to the end
            for (int i = 0; i < n; i++) { int r = root[i], guard = 0; while (root[r] != r && guard++ < 8) r = root[r]; root[i] = r; }
            return root;
        }

        /// <summary>
        /// Per cross-section (root vertex): road height, curb face (measured on the sidewalk mesh, else filled from measured
        /// neighbours, else the street-style constant), how far past the curb the wall may go (stopped short of the first
        /// railing, fence, rock wall, planter, bollard, tree trunk or building; never into one), and the sidewalk height.
        /// Then a conservative smoothing along the road. Copies every root's values to its back-face vertices.
        /// Returns how many bottom roots were limited by an obstacle.
        /// </summary>
        private int ComputePlacement(MeshCollider rCol, Vector3[] R, Vector3[] dir, float[] sep, int[] root, float roadEstimate,
                                     Il2CppStructArray<Vector3> rv, float midY, float[] curb, float[] over, float[] rampH, float[] roadY,
                                     out int detected, out int roots)
        {
            int n = R.Length;
            detected = 0; roots = 0;
            float allowed = Settings.AllowedOverCurb.Value;
            float margin = Mathf.Max(0f, Settings.SidewalkMargin.Value);
            float fixedH = Settings.RampHeight.Value;
            string mode = Settings.CurbReference.Value ?? "Auto";
            bool auto = string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase);
            bool wider = string.Equals(mode, "Wider", StringComparison.OrdinalIgnoreCase);
            SidewalkMap map = SidewalkMap.ForScene(rCol.gameObject.scene);
            // a tile with no sidewalk / curb mesh at all (river and bridge tiles: the edge drops to an embankment) keeps
            // its stock walls: there is nothing to ride up onto, and a wall moved out would let the car off the edge
            if (map.SurfaceTris == 0 && Settings.SidewalkCap.Value)
            {
                for (int i = 0; i < n; i++) { curb[i] = 0f; over[i] = 0f; rampH[i] = 0f; roadY[i] = roadEstimate; }
                detected = 0; roots = 0;
                if (_noSidewalkLogged.Add(rCol.gameObject.scene.handle))   // once per tile, always
                    Plugin.Log.LogInfo($"[Walls] {rCol.gameObject.scene.name}: no sidewalk on this tile (bridge / river edge): stock walls kept");
                return 0;
            }

            var measured = new bool[n];
            var hitAt = new bool[n];      // an obstacle was measured at this root
            var hitNear = new bool[n];    // ... or within the erosion radius (its wall is pulled in by that obstacle)
            var probeCache = _probeCache; probeCache.Clear();
            for (int i = 0; i < n; i++)
            {
                curb[i] = float.NaN; over[i] = allowed; rampH[i] = fixedH > 0f ? fixedH : 0.32f; roadY[i] = roadEstimate;
                if (root[i] != i || dir[i] == Vector3.zero) continue;
                roots++;
                // the road under this point (the tile's flat ground collider), 1.5 m in front of the wall
                Vector3 front = R[i] - dir[i] * 1.5f;
                if (Physics.Raycast(new Vector3(front.x, roadEstimate + 3f, front.z), Vector3.down, out RaycastHit hit, 8f, 1 << StreetLayer, QueryTriggerInteraction.Ignore))
                    roadY[i] = hit.point.y;
                var p = new Vector3(R[i].x, roadY[i], R[i].z);
                if (auto)
                {
                    float c = map.Curb(p, dir[i], roadY[i]);
                    if (!float.IsNaN(c) && c > -3f && c < 4.5f) { curb[i] = c; measured[i] = true; detected++; }
                }
                else curb[i] = wider && sep[i] > 0.6f ? sep[i] - Settings.WiderBehindCurb.Value : Settings.CurbFromRegular.Value;
            }

            // fill unmeasured roots: median of measured roots within 8 m, else the street-style constant
            if (auto)
            {
                var near = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    if (root[i] != i || dir[i] == Vector3.zero || measured[i]) continue;
                    near.Clear();
                    for (int j = 0; j < n; j++)
                        if (measured[j] && (R[j] - R[i]).sqrMagnitude < 64f) near.Add(curb[j]);
                    if (near.Count >= 2) { near.Sort(); curb[i] = near[near.Count / 2]; }
                    else curb[i] = sep[i] > 0.6f
                        ? sep[i] + (map.CityStyle ? Settings.CurbFromWiderCity.Value : Settings.CurbFromWiderPark.Value)
                        : Settings.CurbFromRegular.Value;
                }
            }

            // obstacles and sidewalk height, from the curb face outward
            for (int i = 0; i < n; i++)
            {
                if (root[i] != i || dir[i] == Vector3.zero) continue;
                curb[i] = Mathf.Clamp(curb[i], -3f, 4.5f);
                long k = Key(R[i]);
                if (!probeCache.TryGetValue(k, out var pr))
                {
                    var c = new Vector3(R[i].x, roadY[i], R[i].z) + dir[i] * curb[i];
                    float obstacle = Settings.SidewalkCap.Value ? map.Obstacle(c - dir[i] * 0.05f, dir[i], roadY[i], allowed + margin + 1f) - 0.05f : float.MaxValue;
                    if (obstacle > allowed + margin + 0.5f) obstacle = float.MaxValue;
                    float h = fixedH > 0f ? fixedH : map.Height(c + dir[i] * 0.4f, roadY[i]);
                    pr = (obstacle, h);
                    probeCache[k] = pr;
                }
                if (pr.obstacle < float.MaxValue)
                {
                    hitAt[i] = true;
                    // stop short of it by the margin, and never let the wall reach into it
                    float stop = pr.obstacle - margin;
                    float floor = Mathf.Max(-1.5f, Mathf.Min(Settings.MinOverCurb.Value, pr.obstacle - 0.05f));
                    over[i] = Mathf.Clamp(stop, floor, allowed);
                }
                if (fixedH <= 0f && !float.IsNaN(pr.h) && pr.h > 0f) rampH[i] = Mathf.Clamp(pr.h, 0.1f, 0.5f);
            }

            // conservative smoothing over neighbouring roots (local minimum, then average, never above a point's own cap)
            float r = Mathf.Max(0.5f, Settings.CapSmoothing.Value);
            var grid = _grid;
            foreach (var cell in grid.Values) { cell.Clear(); _cellPool.Push(cell); }
            grid.Clear();
            for (int i = 0; i < n; i++)
            {
                if (root[i] != i) continue;
                var key = (Mathf.FloorToInt(R[i].x / r), Mathf.FloorToInt(R[i].z / r));
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = _cellPool.Count > 0 ? _cellPool.Pop() : new List<int>();
                l.Add(i);
            }
            // work in "wall end" terms (curb + over, metres from the stock wall) so a curb that steps doesn't move the
            // end past an obstacle
            var end = new float[n]; var eroded = new float[n]; var smoothH = new float[n]; var smoothC = new float[n];
            for (int i = 0; i < n; i++) end[i] = curb[i] + over[i];
            float erodeR2 = r * 0.5f * r * 0.5f, r2 = r * r;
            for (int i = 0; i < n; i++)
            {
                if (root[i] != i) continue;
                float m = end[i];
                int gx = Mathf.FloorToInt(R[i].x / r), gz = Mathf.FloorToInt(R[i].z / r);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                float ex = R[j].x - R[i].x, ez = R[j].z - R[i].z;
                                if (ex * ex + ez * ez <= erodeR2) { m = Mathf.Min(m, end[j]); if (hitAt[j]) hitNear[i] = true; }
                            }
                eroded[i] = m;
            }
            for (int i = 0; i < n; i++)
            {
                if (root[i] != i) continue;
                float s = 0f, sh = 0f, sc = 0f; int c = 0;
                int gx = Mathf.FloorToInt(R[i].x / r), gz = Mathf.FloorToInt(R[i].z / r);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                float ex = R[j].x - R[i].x, ez = R[j].z - R[i].z;
                                if (ex * ex + ez * ez <= r2) { s += eroded[j]; sh += rampH[j]; sc += curb[j]; c++; }
                            }
                smoothH[i] = c > 0 ? sh / c : rampH[i];
                smoothC[i] = c > 0 ? sc / c : curb[i];
            }
            for (int i = 0; i < n; i++)
            {
                if (root[i] != i) continue;
                float s = 0f; int c = 0;
                int gx = Mathf.FloorToInt(R[i].x / r), gz = Mathf.FloorToInt(R[i].z / r);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                float ex = R[j].x - R[i].x, ez = R[j].z - R[i].z;
                                if (ex * ex + ez * ez <= r2) { s += eroded[j]; c++; }
                            }
                float e = c > 0 ? Mathf.Min(s / c, end[i]) : eroded[i];   // never past this point's own cap
                curb[i] = smoothC[i];
                over[i] = e - curb[i];
                rampH[i] = smoothH[i];
            }

            // safety floor: a wall never moves into the road past its stock place, except up to MaxInward where a railing /
            // wall stands at the curb (measured here or right next to here). A misread face can't put a wall in a lane.
            for (int i = 0; i < n; i++)
            {
                if (root[i] != i || dir[i] == Vector3.zero) continue;
                float floor = hitNear[i] || hitAt[i] ? -MaxInward : 0f;
                if (curb[i] + over[i] < floor) over[i] = floor - curb[i];
            }

            // back-face vertices take their road face's values
            for (int i = 0; i < n; i++)
            {
                int j = root[i];
                if (j == i) continue;
                curb[i] = curb[j]; over[i] = over[j]; rampH[i] = rampH[j]; roadY[i] = roadY[j];
            }

            int capped = 0;
            for (int i = 0; i < n; i++) if (root[i] == i && rv[i].y < midY && over[i] < allowed - 0.01f) capped++;
            return capped;
        }

        private float _nextCensus;

        /// <summary>
        /// Diagnostic: while driving, if no wall pairs have been found, log what wall-like colliders actually exist
        /// (names, layers, scenes, mesh state) every 20 s so the matching rules can be fixed.
        /// </summary>
        private void Census()
        {
            if (_pairs.Count > 0 || Game.Runtime.Vehicle.VehicleManager.Instance == null) { _nextCensus = Time.unscaledTime + 20f; return; }
            if (Time.unscaledTime < _nextCensus) return;
            _nextCensus = Time.unscaledTime + 20f;

            // diagnostic only (no pairs while driving, every 20 s): the whole-scene search is fine here
            var cols = UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int regulars = 0, widers = 0;
            foreach (var st in _scenes.Values) { regulars += st.Regulars; widers += st.Widers; }

            var byLayer = new SortedDictionary<int, int>();
            var wallish = new Dictionary<string, int>();
            int total = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null) continue;
                total++;
                int layer = c.gameObject.layer;
                byLayer[layer] = byLayer.TryGetValue(layer, out int v) ? v + 1 : 1;
                string n = c.gameObject.name;
                bool interesting = layer == GuardrailLayer || layer == 16 ||
                                   n.IndexOf("Guard", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   n.IndexOf("Road", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!interesting) continue;
                var m = c.sharedMesh;
                string key = $"'{n}' layer {layer} scene '{c.gameObject.scene.name}' active {c.gameObject.activeInHierarchy} enabled {c.enabled} " +
                             (m == null ? "mesh=null" : $"mesh '{m.name}' {m.vertexCount}v readable {m.isReadable}");
                wallish[key] = wallish.TryGetValue(key, out int w) ? w + 1 : 1;
            }
            var scenes = new List<string>();
            for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
                scenes.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(s).name);

            Plugin.Log.LogWarning($"[Walls] DIAG: driving but no wall pairs. MeshColliders {total} (regular {regulars}, wider {widers}); " +
                                  $"by layer: {string.Join(", ", byLayer.Select(kv => $"{kv.Key}:{kv.Value}"))}; scenes: {string.Join(", ", scenes)}");
            int shown = 0;
            foreach (var kv in wallish.OrderByDescending(kv => kv.Value))
            {
                Plugin.Log.LogWarning($"[Walls] DIAG   {kv.Value}x {kv.Key}");
                if (++shown >= 25) break;
            }
        }

        /// <summary>
        /// D. The bevelled curb collider along the wall's road face (and, with ShowWalls, a visible 1 m strip where the
        /// moved wall now stands). Built from road-facing (root) vertices only, so the slab's back face adds nothing.
        /// Returns the holder object (parented to the wall: unloads with the tile), or null.
        /// </summary>
        private GameObject BuildRamp(MeshCollider rCol, Vector3[] R, int[] root, Il2CppStructArray<int> tris, Il2CppStructArray<Vector3> rv, float midY,
                                     Vector3[] dir, float[] curb, float[] shift, float[] rampH, float[] roadY, out int triCount)
        {
            triCount = 0;
            Transform rT = rCol.transform;
            float before = Settings.RampStartBeforeCurb.Value, after = Settings.RampFullHeightAfterCurb.Value, extend = Settings.RampTopExtend.Value;
            bool ramp = Settings.RampEnabled.Value, show = Settings.ShowWalls.Value;

            var verts = _rampVerts; verts.Clear();               // reused buffers (copied out by ToArray below)
            var idx = _rampIdx; idx.Clear();
            var done = _rampDone; done.Clear();
            var wallVerts = new List<Vector3>(); var wallIdx = new List<int>();

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                // wall base segment = the two bottom vertices of a vertical triangle, taken at their road face
                int i0 = -1, i1 = -1, lowCount = 0;
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[t + k];
                    if (rv[v].y < midY) { lowCount++; if (i0 < 0) i0 = v; else i1 = v; }
                }
                if (lowCount != 2) continue;
                i0 = root[i0]; i1 = root[i1];
                if (i0 == i1 || dir[i0] == Vector3.zero || dir[i1] == Vector3.zero) continue;
                if ((R[i0] - R[i1]).sqrMagnitude > 400f) continue;   // not neighbours along the road

                // dedupe (both faces, and both triangles of a quad, map to the same road-face segment)
                long key = Key(R[i0]) ^ (Key(R[i1]) * 31);
                long key2 = Key(R[i1]) ^ (Key(R[i0]) * 31);
                if (done.Contains(key) || done.Contains(key2)) continue;
                done.Add(key);

                if (ramp)
                {
                    int baseIdx = verts.Count;
                    for (int k = 0; k < 2; k++)
                    {
                        int i = k == 0 ? i0 : i1;
                        float h = rampH[i];
                        Vector3 o = R[i]; o.y = roadY[i];
                        float topDist = Mathf.Max(shift[i] + extend, curb[i] + after + 0.1f);
                        verts.Add(rT.InverseTransformPoint(o + dir[i] * (curb[i] - before) + Vector3.down * 0.03f)); // bevel start (just under the road)
                        verts.Add(rT.InverseTransformPoint(o + dir[i] * (curb[i] + after) + Vector3.up * h));        // full height
                        verts.Add(rT.InverseTransformPoint(o + dir[i] * topDist + Vector3.up * h));                  // flat top end
                    }
                    // columns: i0 -> baseIdx+0..2, i1 -> baseIdx+3..5
                    AddQuadBothSides(idx, baseIdx + 0, baseIdx + 3, baseIdx + 4, baseIdx + 1);
                    AddQuadBothSides(idx, baseIdx + 1, baseIdx + 4, baseIdx + 5, baseIdx + 2);
                }
                if (show)
                {
                    int b = wallVerts.Count;
                    for (int k = 0; k < 2; k++)
                    {
                        int i = k == 0 ? i0 : i1;
                        Vector3 o = R[i] + dir[i] * shift[i]; o.y = roadY[i];
                        wallVerts.Add(rT.InverseTransformPoint(o));
                        wallVerts.Add(rT.InverseTransformPoint(o + Vector3.up * 1f));
                    }
                    AddQuadBothSides(wallIdx, b + 0, b + 2, b + 3, b + 1);
                }
            }

            if (verts.Count == 0 && wallVerts.Count == 0) return null;
            var go = new GameObject("CurbFeel_Ramp");
            go.layer = StreetLayer;
            go.transform.SetParent(rT, false);   // unloads with the tile

            if (verts.Count > 0)
            {
                var mesh = new Mesh { name = "CurbFeel_Ramp_" + rCol.sharedMesh.name };
                _owned?.Add(mesh);
                if (verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
                mesh.vertices = verts.ToArray();
                mesh.triangles = idx.ToArray();
                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
                triCount = idx.Count / 3;
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                if (Settings.ShowRamps.Value)
                {
                    var mat = DebugMaterial();
                    if (mat != null)
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    }
                }
            }
            if (wallVerts.Count > 0)
            {
                var mat = WallMaterial();
                if (mat != null)
                {
                    var wm = new Mesh { name = "CurbFeel_WallView" };
                    _owned?.Add(wm);
                    if (wallVerts.Count > 65000) wm.indexFormat = IndexFormat.UInt32;
                    wm.vertices = wallVerts.ToArray();
                    wm.triangles = wallIdx.ToArray();
                    wm.RecalculateBounds();
                    var view = new GameObject("CurbFeel_WallView");   // child of the ramp object: no collider, render only
                    view.transform.SetParent(go.transform, false);
                    view.AddComponent<MeshFilter>().sharedMesh = wm;
                    var mr = view.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                }
            }
            return go;
        }

        private Material _wallMat;
        private bool _wallMatTried;

        /// <summary>Cyan, unlit: where the moved walls stand (ShowWalls debug).</summary>
        private Material WallMaterial()
        {
            if (_wallMatTried) return _wallMat;
            _wallMatTried = true;
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) { Plugin.Log.LogWarning("[Walls] ShowWalls: no URP unlit shader, walls stay invisible"); return null; }
            _wallMat = new Material(sh);
            _wallMat.SetColor("_BaseColor", new Color(0.1f, 0.95f, 1f, 1f));
            return _wallMat;
        }

        private static long Key(Vector3 p) =>
            ((long)Mathf.RoundToInt(p.x * 50f) * 73856093L) ^ ((long)Mathf.RoundToInt(p.z * 50f) * 19349663L);

        private static void AddQuadBothSides(List<int> idx, int a, int b, int c, int d)
        {
            idx.Add(a); idx.Add(b); idx.Add(c);
            idx.Add(a); idx.Add(c); idx.Add(d);
            idx.Add(a); idx.Add(c); idx.Add(b);
            idx.Add(a); idx.Add(d); idx.Add(c);
        }

        /// <summary>Copy of src with new vertices. Pass src's triangles if they were already read (saves another copy).</summary>
        private static Mesh CloneWithVertices(Mesh src, Vector3[] verts, string name, Il2CppStructArray<int> srcTriangles = null)
        {
            var m = new Mesh { name = name, indexFormat = src.indexFormat };
            m.vertices = verts;
            m.triangles = srcTriangles ?? src.triangles;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Hot-module unload only (after Revert destroyed the ramps that used it).</summary>
        public void DestroyDebugMaterial()
        {
            if (_debugMat != null) UnityEngine.Object.Destroy(_debugMat);
            _debugMat = null;
            _debugMatTried = false;
            if (_wallMat != null) UnityEngine.Object.Destroy(_wallMat);
            _wallMat = null;
            _wallMatTried = false;
        }

        private Material DebugMaterial()
        {
            if (_debugMatTried) return _debugMat;
            _debugMatTried = true;
            foreach (var s in new[] { "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default", "Universal Render Pipeline/Lit" })
            {
                var sh = Shader.Find(s);
                if (sh == null) continue;
                _debugMat = new Material(sh) { color = new Color(1f, 0.2f, 0.8f, 1f) };
                Plugin.Log.LogInfo($"[Walls] ShowRamps using shader '{s}'");
                return _debugMat;
            }
            Plugin.Log.LogWarning("[Walls] ShowRamps: no usable shader found, ramps stay invisible");
            return null;
        }
    }
}
