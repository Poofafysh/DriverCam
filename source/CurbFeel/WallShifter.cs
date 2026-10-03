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
        private Material _debugMat;
        private bool _debugMatTried;

        public int PairCount => _pairs.Count;

        public void Tick()
        {
            // drop pairs whose tile was unloaded
            _pairs.RemoveAll(p => p.Regular == null);

            Discover();
            // spread work over frames: at most two wall pairs per tick
            for (int n = 0; n < 2 && _queue.Count > 0; n++)
            {
                var (r, w) = _queue.Dequeue();
                if (r == null || w == null) continue;
                try { Process(r, w); }
                catch (Exception e) { Plugin.Log.LogError($"[Walls] {r.name}: {e}"); }
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
            SidewalkMap.ClearCache();
            UpdateStats();
        }

        private void Discover()
        {
            var cols = UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var regulars = new List<MeshCollider>();
            var widers = new List<MeshCollider>();
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null || c.sharedMesh == null) continue;
                string n = c.gameObject.name;
                if (n.IndexOf("Guardrail", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.gameObject.layer != GuardrailLayer && c.gameObject.layer != 16) continue;
                if (n.IndexOf("Regular", StringComparison.OrdinalIgnoreCase) >= 0) regulars.Add(c);
                else if (n.IndexOf("Wider", StringComparison.OrdinalIgnoreCase) >= 0) widers.Add(c);
            }
            Census(cols, regulars.Count, widers.Count);

            foreach (var r in regulars)
            {
                int id = r.GetInstanceID();
                if (_seen.Contains(id)) continue;
                _seen.Add(id);

                bool batch = r.gameObject.name.Contains("Batch");
                int scene = r.gameObject.scene.handle;
                int vc = r.sharedMesh.vertexCount;
                Vector3 r0 = r.transform.TransformPoint(r.sharedMesh.vertices[0]);

                MeshCollider best = null; float bestD = 5f;
                foreach (var w in widers)
                {
                    if (w.gameObject.scene.handle != scene || w.sharedMesh.vertexCount != vc) continue;
                    if (w.gameObject.name.Contains("Batch") != batch) continue;
                    float d = Vector3.Distance(r0, w.transform.TransformPoint(w.sharedMesh.vertices[0]));
                    if (d < bestD) { bestD = d; best = w; }
                }
                if (best == null)
                {
                    _unpaired++;
                    Plugin.Verbose($"[Walls] no matching outer wall for {r.gameObject.scene.name}/{r.name} ({vc} verts) - left stock");
                    continue;
                }
                _queue.Enqueue((r, best));
            }
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
                rCol.sharedMesh = CloneWithVertices(rMesh, newR, "CurbFeel_" + rMesh.name);
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

            var probeCache = new Dictionary<long, (float obstacle, float h)>();
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
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < n; i++)
            {
                var key = (Mathf.FloorToInt(R[i].x / r), Mathf.FloorToInt(R[i].z / r));
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
                l.Add(i);
            }
            IEnumerable<int> Near(int i, float radius)
            {
                int gx = Mathf.FloorToInt(R[i].x / r), gz = Mathf.FloorToInt(R[i].z / r);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((gx + dx, gz + dz), out var l))
                            foreach (int j in l)
                            {
                                float ex = R[j].x - R[i].x, ez = R[j].z - R[i].z;
                                if (ex * ex + ez * ez <= radius * radius) yield return j;
                            }
            }
            var eroded = new float[n];
            for (int i = 0; i < n; i++) { float m = over[i]; foreach (int j in Near(i, r * 0.5f)) m = Mathf.Min(m, over[j]); eroded[i] = m; }
            var smoothH = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = 0f, sh = 0f; int c = 0;
                foreach (int j in Near(i, r)) { s += eroded[j]; sh += rampH[j]; c++; }
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
        private void Census(Il2CppArrayBase<MeshCollider> cols, int regulars, int widers)
        {
            if (_pairs.Count > 0 || Game.Runtime.Vehicle.VehicleManager.Instance == null) { _nextCensus = Time.unscaledTime + 20f; return; }
            if (Time.unscaledTime < _nextCensus) return;
            _nextCensus = Time.unscaledTime + 20f;

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
            var groundY = new Dictionary<int, float>();
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

            var verts = new List<Vector3>();
            var idx = new List<int>();
            var done = new HashSet<long>();

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                // wall base segment = the two bottom vertices of a vertical triangle
                int i0 = -1, i1 = -1, lowCount = 0;
                foreach (int v in new[] { a, b, c })
                {
                    if (rv[v].y < midY) { lowCount++; if (i0 < 0) i0 = v; else i1 = v; }
                }
                if (lowCount != 2 || dir[i0] == Vector3.zero || dir[i1] == Vector3.zero) continue;

                // dedupe (thick walls have two faces over the same base line)
                long key = Key(R[i0]) ^ (Key(R[i1]) * 31);
                long key2 = Key(R[i1]) ^ (Key(R[i0]) * 31);
                if (done.Contains(key) || done.Contains(key2)) continue;
                done.Add(key);

                int baseIdx = verts.Count;
                foreach (int i in new[] { i0, i1 })
                {
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

        private static Mesh CloneWithVertices(Mesh src, Vector3[] verts, string name)
        {
            var m = new Mesh { name = name, indexFormat = src.indexFormat };
            m.vertices = verts;
            m.triangles = src.triangles;
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
