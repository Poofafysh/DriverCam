using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CurbFeel
{
    /// <summary>
    /// B + D. Every road tile (an additive scene) has two invisible wall meshes per road object:
    /// Guardrail_Regular (inner, ~1.1-1.6 m before the visible curb) and Guardrail_Wider (outer, ~0.17 m behind the curb).
    /// They share topology, so vertex i of one matches vertex i of the other; W[i]-R[i] is the outward direction at that point.
    /// B moves both walls outward to (curb face + AllowedOverCurb). D adds a bevelled curb collider on the Street layer.
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
            _pairs.RemoveAll(p => p.Regular == null);

            Discover();
            // spread work over frames: at most one wall pair per tick (dead entries from unloaded tiles don't count)
            while (_queue.Count > 0)
            {
                var (r, w) = _queue.Dequeue();
                if (r == null || w == null) continue;
                try { Process(r, w); }
                catch (Exception e) { Plugin.Log.LogError($"[Walls] {r.name}: {e}"); }
                break;
            }
            UpdateStats();
        }

        public void Revert()
        {
            foreach (var p in _pairs)
            {
                if (p.Regular != null && p.RegularOriginal != null) p.Regular.sharedMesh = p.RegularOriginal;
                if (p.Wider != null && p.WiderOriginal != null) p.Wider.sharedMesh = p.WiderOriginal;
                if (p.Ramp != null) UnityEngine.Object.Destroy(p.Ramp);
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
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                R[i] = rT.TransformPoint(rv[i]);
                W[i] = wT.TransformPoint(wv[i]);
                minY = Mathf.Min(minY, rv[i].y); maxY = Mathf.Max(maxY, rv[i].y);
            }
            float midY = (minY + maxY) * 0.5f;

            // per-vertex outward direction, separation, curb distance and wall shift
            var dir = new Vector3[n]; var curb = new float[n]; var shift = new float[n]; var sep = new float[n];
            bool useWider = string.Equals(Settings.CurbReference.Value, "Wider", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = W[i] - R[i]; d.y = 0f;
                sep[i] = d.magnitude;
                dir[i] = sep[i] > 1e-3f ? d / sep[i] : Vector3.zero;
                float c = (useWider && sep[i] > 0.6f) ? sep[i] - Settings.WiderBehindCurb.Value : Settings.CurbFromRegular.Value;
                curb[i] = Mathf.Clamp(c, 0.3f, 3.5f);
            }

            // how far past the curb the wall goes at each vertex (capped by the real sidewalk), and the ramp height there
            var over = new float[n]; var rampH = new float[n];
            int capped = ComputeOverCurb(rCol, R, dir, curb, midY, rv, over, rampH);
            for (int i = 0; i < n; i++)
                shift[i] = (Settings.WallsEnabled.Value && dir[i] != Vector3.zero) ? curb[i] + over[i] : 0f;

            var pair = new Pair { Regular = rCol, Wider = wCol, RegularOriginal = rMesh, WiderOriginal = wMesh, Label = $"{rCol.gameObject.scene.name}/{rCol.name}" };

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
                rCol.sharedMesh = CloneWithVertices(rMesh, newR, "CurbFeel_" + rMesh.name, tris);
                wCol.sharedMesh = CloneWithVertices(wMesh, newW, "CurbFeel_" + wMesh.name);
            }

            // D. curb ramp
            int rampTris = 0;
            if (Settings.RampEnabled.Value)
            {
                pair.Ramp = BuildRamp(rCol, R, tris, rv, midY, dir, curb, shift, rampH, out rampTris);
            }

            _pairs.Add(pair);
            float avgCurb = 0f, avgShift = 0f, avgOver = 0f, avgH = 0f; int cnt = 0;
            for (int i = 0; i < n; i++) if (rv[i].y < midY) { avgCurb += curb[i]; avgShift += shift[i]; avgOver += over[i]; avgH += rampH[i]; cnt++; }
            if (cnt > 0) { avgCurb /= cnt; avgShift /= cnt; avgOver /= cnt; avgH /= cnt; }
            pair.AvgShift = avgShift;
            pair.AvgOver = avgOver;
            pair.CappedShare = cnt > 0 ? capped / (float)cnt : 0f;
            Plugin.Verbose($"[Walls] {pair.Label}: curb ~{avgCurb:F2} m past stock wall, wall {avgOver:F2} m past curb " +
                           $"({pair.CappedShare:P0} limited by buildings), ramp {avgH:F2} m high, {rampTris} tris");
        }

        /// <summary>
        /// Per vertex: over-curb distance = min(AllowedOverCurb, sidewalk depth - margin), then a conservative smoothing
        /// (local minimum, then average) so the wall never juts into a building and doesn't zig-zag. Returns how many
        /// bottom vertices were limited by the sidewalk.
        /// </summary>
        private int ComputeOverCurb(MeshCollider rCol, Vector3[] R, Vector3[] dir, float[] curb, float midY, Il2CppStructArray<Vector3> rv,
                                    float[] over, float[] rampH)
        {
            int n = R.Length;
            float allowed = Settings.AllowedOverCurb.Value;
            float fixedH = Settings.RampHeight.Value;
            SidewalkMap map = Settings.SidewalkCap.Value || fixedH <= 0f ? SidewalkMap.ForScene(rCol.gameObject.scene) : null;

            var probeCache = _probeCache; probeCache.Clear();
            for (int i = 0; i < n; i++)
            {
                over[i] = allowed;
                rampH[i] = fixedH > 0f ? fixedH : 0.32f;
                if (map == null || dir[i] == Vector3.zero) continue;

                long k = Key(R[i]);
                if (!probeCache.TryGetValue(k, out var pr))
                {
                    Vector3 c = R[i] + dir[i] * curb[i];
                    map.Probe(c, dir[i], R[i].y + 1f, out float obstacle, out float h);   // walls extend 1 m below the road
                    pr = (obstacle, h);
                    probeCache[k] = pr;
                }
                if (Settings.SidewalkCap.Value && pr.obstacle < float.MaxValue)
                    over[i] = Mathf.Clamp(pr.obstacle - Settings.SidewalkMargin.Value, Settings.MinOverCurb.Value, allowed);
                if (fixedH <= 0f && pr.h > 0f) rampH[i] = Mathf.Clamp(pr.h, 0.1f, 0.5f);
            }

            // conservative smoothing over neighbouring wall points (by position, so top/bottom vertices stay paired)
            float r = Mathf.Max(0.5f, Settings.CapSmoothing.Value);
            var grid = _grid;
            foreach (var cell in grid.Values) { cell.Clear(); _cellPool.Push(cell); }   // reuse the cell lists
            grid.Clear();
            for (int i = 0; i < n; i++)
            {
                var key = (Mathf.FloorToInt(R[i].x / r), Mathf.FloorToInt(R[i].z / r));
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = _cellPool.Count > 0 ? _cellPool.Pop() : new List<int>();
                l.Add(i);
            }
            // Neighbour loops are written out (same cell order as before) instead of an iterator, which allocated per call.
            var eroded = new float[n];
            float erodeR = r * 0.5f, erodeR2 = erodeR * erodeR;
            for (int i = 0; i < n; i++)
            {
                float m = over[i];
                int gx = Mathf.FloorToInt(R[i].x / r), gz = Mathf.FloorToInt(R[i].z / r);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                float ex = R[j].x - R[i].x, ez = R[j].z - R[i].z;
                                if (ex * ex + ez * ez <= erodeR2) m = Mathf.Min(m, over[j]);
                            }
                eroded[i] = m;
            }
            var smoothH = new float[n];
            float r2 = r * r;
            for (int i = 0; i < n; i++)
            {
                float s = 0f, sh = 0f; int c = 0;
                int gx = Mathf.FloorToInt(R[i].x / r), gz = Mathf.FloorToInt(R[i].z / r);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                float ex = R[j].x - R[i].x, ez = R[j].z - R[i].z;
                                if (ex * ex + ez * ez <= r2) { s += eroded[j]; sh += rampH[j]; c++; }
                            }
                over[i] = c > 0 ? Mathf.Min(s / c, over[i]) : eroded[i];   // never past this point's own sidewalk cap
                smoothH[i] = c > 0 ? sh / c : rampH[i];
            }
            Array.Copy(smoothH, rampH, n);

            int capped = 0;
            for (int i = 0; i < n; i++) if (rv[i].y < midY && over[i] < allowed - 0.01f) capped++;
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

        private GameObject BuildRamp(MeshCollider rCol, Vector3[] R, Il2CppStructArray<int> tris, Il2CppStructArray<Vector3> rv, float midY,
                                     Vector3[] dir, float[] curb, float[] shift, float[] rampH, out int triCount)
        {
            triCount = 0;
            Transform rT = rCol.transform;
            float before = Settings.RampStartBeforeCurb.Value, after = Settings.RampFullHeightAfterCurb.Value, extend = Settings.RampTopExtend.Value;

            // ground height under each curb point (cached per vertex)
            var groundY = _groundY; groundY.Clear();
            float Ground(int i)
            {
                if (groundY.TryGetValue(i, out float g)) return g;
                Vector3 c = R[i] + dir[i] * curb[i];
                if (Physics.Raycast(new Vector3(c.x, c.y + 4f, c.z), Vector3.down, out RaycastHit hit, 8f, 1 << StreetLayer, QueryTriggerInteraction.Ignore))
                    g = hit.point.y;
                else
                    g = R[i].y + 1f;   // walls extend 1 m below the road surface
                groundY[i] = g;
                return g;
            }

            var verts = _rampVerts; verts.Clear();               // reused buffers (copied out by ToArray below)
            var idx = _rampIdx; idx.Clear();
            var done = _rampDone; done.Clear();

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                // wall base segment = the two bottom vertices of a vertical triangle
                int i0 = -1, i1 = -1, lowCount = 0;
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[t + k];
                    if (rv[v].y < midY) { lowCount++; if (i0 < 0) i0 = v; else i1 = v; }
                }
                if (lowCount != 2 || dir[i0] == Vector3.zero || dir[i1] == Vector3.zero) continue;

                // dedupe (thick walls have two faces over the same base line)
                long key = Key(R[i0]) ^ (Key(R[i1]) * 31);
                long key2 = Key(R[i1]) ^ (Key(R[i0]) * 31);
                if (done.Contains(key) || done.Contains(key2)) continue;
                done.Add(key);

                int baseIdx = verts.Count;
                for (int k = 0; k < 2; k++)
                {
                    int i = k == 0 ? i0 : i1;
                    float g = Ground(i);
                    float h = rampH[i];
                    Vector3 o = R[i]; o.y = g;
                    float topDist = Mathf.Max(shift[i] + extend, curb[i] + after + 0.1f);
                    verts.Add(rT.InverseTransformPoint(o + dir[i] * (curb[i] - before) + Vector3.down * 0.03f)); // bevel start (just under the road)
                    verts.Add(rT.InverseTransformPoint(o + dir[i] * (curb[i] + after) + Vector3.up * h));        // full height
                    verts.Add(rT.InverseTransformPoint(o + dir[i] * topDist + Vector3.up * h));                  // flat top end
                }
                // columns: i0 -> baseIdx+0..2, i1 -> baseIdx+3..5
                AddQuadBothSides(idx, baseIdx + 0, baseIdx + 3, baseIdx + 4, baseIdx + 1);
                AddQuadBothSides(idx, baseIdx + 1, baseIdx + 4, baseIdx + 5, baseIdx + 2);
            }

            if (verts.Count == 0) return null;

            var mesh = new Mesh { name = "CurbFeel_Ramp_" + rCol.sharedMesh.name };
            if (verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts.ToArray();
            mesh.triangles = idx.ToArray();
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            triCount = idx.Count / 3;

            var go = new GameObject("CurbFeel_Ramp");
            go.layer = StreetLayer;
            go.transform.SetParent(rT, false);   // unloads with the tile
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
            return go;
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
