using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using Game.Runtime.Systems.LevelGeneration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Sandbox
{
    /// <summary>
    /// The wide road of a sandbox race (Maps.cs sets the width). Started from RoadPathGenerator.GeneratePath's postfix,
    /// after every tile scene loaded and was placed, inside the loading coroutine:
    /// - Centre line: each tile's WaypointParentReference children (sibling order = path order) joined into one line and
    ///   resampled every 2.5 m (Catmull-Rom; tangents and right vectors computed globally, so tile seams share a sample).
    ///   If that line is more than 0.5 m off the race spline (GeneratePath drops some waypoints), RoadPathGenerator.RegularPath
    ///   is sampled instead. Both ends run on straight for max(40 m, 1.5 W).
    /// - Hairpins: where the reach of two road sections meets, the edge is "pinched": no wall, barrier, sidewalk or pole.
    /// - Per tile, under "fx_WideRoad" parented to the tile's "Road Network" (unloads with the tile), all built at once
    ///   during the loading screen: one visual mesh (asphalt, lines, curbs, sidewalks, ground, guardrail with posts,
    ///   street-light poles and lamp heads; 9 submeshes), a Street-layer (11) MeshCollider "fx_WideStreet" and a
    ///   Guardrail-layer (15) wall MeshCollider "fx_WideWall" (not a "Guardrail" name: CurbFeel's WallShifter leaves it
    ///   alone). Every non-trigger collider and every Light under Biomes and Road Network is switched off at once (so no
    ///   car meets a stock wall or building on a tile not hidden yet); triggers (finish, reverb zones) stay.
    /// - Every renderer under Biomes and Road Network is collected during the loading screen too; then, a slice per frame
    ///   (WideRoads.BudgetMs), they are hidden (forceRenderingOff only, no scan; the first two tiles at once). Colliders added
    ///   later (CurbFeel's CurbFeel_Ramp) are caught on the camera's tile and the next one within 0.25 s (NearCheck; about
    ///   1 s once the race has run 10 s), and on every tile by a round-robin re-check (one tile per 0.15 s, so each tile
    ///   about every N x 0.15 s; after 15 s each tile every 5 s at most, then also renderers and lights). Our own
    ///   objects are told apart by pointer, not by name.
    /// - The finish line (spawned by GeneratePath for a 20 m road) is widened to W + 4 by its root's localScale.x.
    /// - Street lights (WideRoads.StreetLights): a pool of 8 point lights moved to the poles nearest the camera every 0.5 s.
    /// Everything is recorded and put back by <see cref="RestoreAll"/> (idempotent).
    /// </summary>
    internal static class WideBuild
    {
        private const float Step = 2.5f;
        private const int LayerStreet = 11, LayerGuardrail = 15;
        private const float WallOut = 4.5f, StreetOut = 6f, Sidewalk = 4f, GroundOut = 80f, CurbH = 0.15f, LineY = 0.012f;
        private const float RailLow = 0.45f, RailHigh = 0.8f, PostEvery = 5f, PoleEvery = 40f, PoleH = 8f, ArmLen = 2.5f;
        private const int SubAsphalt = 0, SubWhite = 1, SubYellow = 2, SubCurb = 3, SubSidewalk = 4, SubGround = 5,
                          SubRail = 6, SubPole = 7, SubLamp = 8, SubCount = 9;
        private const int PoolSize = 8;
        private const float FastScan = 0.25f, FastScanFor = 15f, SlowScan = 5f, ScanGap = 0.15f;
        private const float NearFast = 0.25f, NearSlow = 0.5f, NearFastAfterReady = 10f;

        private sealed class Tile
        {
            public int Handle, Index;
            public string Name;
            public Scene Scene;
            public Transform RoadNet, Biomes;
            public int S0, S1, Waypoints, Pinches, Poles;
            public bool HideDone;
            public GameObject Root;
            public IntPtr RootPtr, StreetPtr, WallPtr;   // our own objects, compared by pointer (no name strings)
            public Mesh Visual, Street, Wall;
            public PhysicsMaterial StreetMat, WallMat;
            public string StreetMatName = "none", WallMatName = "none";
            public Renderer[] Rends;
            public int RNext;
            public readonly List<Renderer> Hidden = new List<Renderer>();
            public readonly List<Collider> Disabled = new List<Collider>();
            public readonly List<Light> Lights = new List<Light>();
            public int VisTris, StreetTris, WallTris, LateCols, LateRends, LateLights;
            public float NextScan, FastUntil;
            public double Ms;
        }

        private static readonly List<Tile> Tiles = new List<Tile>();
        private static readonly HashSet<int> Handles = new HashSet<int>();
        private static readonly Stopwatch Sw = new Stopwatch(), Race = new Stopwatch();
        private static Material[] _mats;
        private static int _scanCursor;
        private static float _nextRescan, _nextNear, _nearFastUntil;
        private static int _nearPhase;
        private static bool _raceLogged;
        private static float Extend = 40f;

        // the global centre line (world space)
        private static float[] X, Y, Z, RX, RZ, NX, NY, NZ, S;
        private static bool[] P;
        private static int Count;

        // street-light heads (world space, path order) and the light pool
        private static readonly List<float> PoleX = new List<float>(), PoleY = new List<float>(), PoleZ = new List<float>();
        private static readonly List<GameObject> Pool = new List<GameObject>();
        private static readonly List<Light> PoolLights = new List<Light>();
        private static int _poolAt = -1;
        private static float _poolNext;

        // the finish line we widened
        private static readonly List<Transform> Finishes = new List<Transform>();
        private static readonly List<Vector3> FinishScales = new List<Vector3>();

        internal static bool Busy => Tiles.Count > 0 || Pool.Count > 0;
        internal static bool Owns(int handle) => Handles.Contains(handle);

        // ------------------------------------------------------------------ start of a race

        internal static void Begin(RoadPathGenerator gen)
        {
            RestoreAll("new race");
            Race.Restart();
            _raceLogged = false;
            float W = WideRoads.W;
            Extend = Math.Max(40f, W * 1.5f);
            var list = LevelGenerator.spawnedSceneTileList;
            if (list == null || list.Count == 0) throw new InvalidOperationException("no spawned tiles");

            var raw = new List<Vector3>();
            var tileFirst = new List<int>();
            var tiles = new List<Tile>();
            for (int i = 0; i < list.Count; i++)
            {
                var pair = list[i];
                if (pair == null) throw new InvalidOperationException($"tile {i} missing");
                var scene = pair.Scene;
                if (!scene.isLoaded) throw new InvalidOperationException($"tile {i} not loaded");
                string id = "?";
                try { var so = pair.Tile; if (so != null) id = $"{so.GetId()} ({so.TileSize})"; } catch { }
                var t = new Tile { Handle = scene.handle, Index = i, Name = id, Scene = scene };
                var roots = scene.GetRootGameObjects();
                WaypointParentReference wpr = null;
                for (int r = 0; r < roots.Length; r++)
                {
                    var go = roots[r];
                    if (go == null) continue;
                    if (go.name == "Road Network") t.RoadNet = go.transform;
                    if (go.name == "Biomes") t.Biomes = go.transform;
                    else if (t.Biomes == null) { var b = go.transform.Find("Biomes"); if (b != null) t.Biomes = b; }
                }
                // the path lives under Road Network: search it first, the big Biomes tree only if it isn't there
                if (t.RoadNet != null) wpr = t.RoadNet.GetComponentInChildren<WaypointParentReference>(true);
                for (int r = 0; r < roots.Length && wpr == null; r++)
                    if (roots[r] != null) wpr = roots[r].GetComponentInChildren<WaypointParentReference>(true);
                if (wpr == null) throw new InvalidOperationException($"tile {id}: no WaypointParentReference");
                if (t.RoadNet == null) t.RoadNet = wpr.transform.root;
                var parent = wpr.transform;
                int kids = parent.childCount, named = 0;
                tileFirst.Add(raw.Count);
                for (int k = 0; k < kids; k++)
                {
                    var c = parent.GetChild(k);
                    if (c == null) continue;
                    if (c.gameObject.name.StartsWith("EasyRoad_PathWaypoint", StringComparison.Ordinal)) named++;
                    raw.Add(c.position);
                }
                t.Waypoints = raw.Count - tileFirst[tileFirst.Count - 1];
                if (t.Waypoints < 2) throw new InvalidOperationException($"tile {id}: {t.Waypoints} waypoints");
                if (named != t.Waypoints) Plugin.Log.LogInfo($"[Sandbox] maps: tile {id}: {t.Waypoints} waypoints, {named} named EasyRoad_PathWaypoint*");
                tiles.Add(t);
            }

            BuildCentreLine(raw, tileFirst, out int[] firstSample);
            string mode = "waypoints (Catmull-Rom)";
            float gap = PathGap(gen);
            if (gap > 0.5f)
            {
                if (SampleRegularPath(gen, raw, tileFirst, out firstSample)) mode = $"the race spline (waypoint line was {gap:0.0} m off)";
                else Plugin.Log.LogWarning($"[Sandbox] maps: waypoint line is {gap:0.0} m off the race spline and the spline could not be sampled; using the waypoint line");
            }
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].S0 = i == 0 ? 0 : firstSample[i];
                tiles[i].S1 = i + 1 < tiles.Count ? firstSample[i + 1] : Count - 1;
                if (tiles[i].S1 <= tiles[i].S0) tiles[i].S1 = Math.Min(Count - 1, tiles[i].S0 + 1);
            }
            int pinches = Pinch(W);
            foreach (var t in tiles)
            {
                for (int i = t.S0; i <= t.S1; i++) if (P[i]) t.Pinches++;
                Tiles.Add(t);
                Handles.Add(t.Handle);
            }
            Plugin.Log.LogInfo($"[Sandbox] maps: {tiles.Count} tiles, {raw.Count} waypoints, {Count} samples ({S[Count - 1]:0} m) from {mode}, racer-to-spline gap {gap:0.00} m, {pinches} pinched samples; building the road now");

            WidenFinish(W);
            // every tile's road, walls and collider switch-off now (loading screen): no car meets a stock wall later
            Sw.Restart();
            float now = Time.unscaledTime;
            foreach (var t in Tiles)
            {
                double t0 = Sw.Elapsed.TotalMilliseconds;
                Setup(t);
                BuildVisual(t);
                BuildStreet(t);
                BuildWalls(t);
                DisableColliders(t);
                DisableLights(t.RoadNet, t, false);
                if (t.Biomes != null) DisableLights(t.Biomes, t, false);
                CollectRenderers(t);   // here, not in the race: the hiding in Tick only flips forceRenderingOff
                t.NextScan = now + FastScan;
                t.FastUntil = now + FastScanFor;
                t.Ms += Sw.Elapsed.TotalMilliseconds - t0;
            }
            double buildMs = Sw.Elapsed.TotalMilliseconds;
            _nearFastUntil = now + FastScanFor;
            _nextNear = 0f;
            _nearPhase = 0;
            if (Multiplayer.StreetLights(WideRoads.StreetLights.Value)) MakePool();
            // the start area hidden at once (the car stands on it), the rest a slice per frame
            Sw.Restart();
            for (int i = 0; i < Tiles.Count && i < 2; i++) FinishHide(Tiles[i], float.MaxValue);
            Plugin.Log.LogInfo($"[Sandbox] maps: road built in {buildMs:0} ms ({PoleX.Count} street lights){(Tiles.Count > 2 ? $"; hiding the scenery of {Tiles.Count - 2} more tiles over the next frames" : "")}");
        }

        // ------------------------------------------------------------------ centre line

        /// <summary>Catmull-Rom through the waypoints every 2.5 m, plus Extend straight on at both ends. firstSample[k] = tile k's first sample.</summary>
        private static void BuildCentreLine(List<Vector3> raw, List<int> tileFirst, out int[] firstSample)
        {
            // drop a joint point that repeats the previous one; map each raw point to its kept point
            var kx = new List<float>(); var ky = new List<float>(); var kz = new List<float>();
            var map = new int[raw.Count];
            for (int i = 0; i < raw.Count; i++)
            {
                Vector3 p = raw[i];
                int n = kx.Count;
                if (n > 0)
                {
                    float dx = p.x - kx[n - 1], dy = p.y - ky[n - 1], dz = p.z - kz[n - 1];
                    if (dx * dx + dy * dy + dz * dz < 0.25f) { map[i] = n - 1; continue; }
                }
                kx.Add(p.x); ky.Add(p.y); kz.Add(p.z);
                map[i] = kx.Count - 1;
            }
            int m = kx.Count;
            if (m < 2) throw new InvalidOperationException($"only {m} distinct waypoints");
            var sx = new List<float>(m * 5); var sy = new List<float>(m * 5); var sz = new List<float>(m * 5);
            var sampleOf = new int[m];
            int ext = (int)(Extend / Step);
            // lead-in: straight back from the first point
            {
                float dx = kx[0] - kx[1], dz = kz[0] - kz[1];
                float l = (float)Math.Sqrt(dx * dx + dz * dz); if (l < 1e-3f) { dx = 0f; dz = -1f; l = 1f; }
                dx /= l; dz /= l;
                for (int k = ext; k >= 1; k--) { sx.Add(kx[0] + dx * k * Step); sy.Add(ky[0]); sz.Add(kz[0] + dz * k * Step); }
            }
            for (int j = 0; j + 1 < m; j++)
            {
                int j0 = Math.Max(0, j - 1), j3 = Math.Min(m - 1, j + 2);
                float x0 = kx[j0], y0 = ky[j0], z0 = kz[j0], x1 = kx[j], y1 = ky[j], z1 = kz[j];
                float x2 = kx[j + 1], y2 = ky[j + 1], z2 = kz[j + 1], x3 = kx[j3], y3 = ky[j3], z3 = kz[j3];
                float dx = x2 - x1, dy = y2 - y1, dz = z2 - z1;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) / Step));
                sampleOf[j] = sx.Count;
                for (int k = 0; k < steps; k++)
                {
                    float t = (float)k / steps, t2 = t * t, t3 = t2 * t;
                    sx.Add(Cr(x0, x1, x2, x3, t, t2, t3)); sy.Add(Cr(y0, y1, y2, y3, t, t2, t3)); sz.Add(Cr(z0, z1, z2, z3, t, t2, t3));
                }
            }
            sampleOf[m - 1] = sx.Count;
            sx.Add(kx[m - 1]); sy.Add(ky[m - 1]); sz.Add(kz[m - 1]);
            {
                float dx = kx[m - 1] - kx[m - 2], dz = kz[m - 1] - kz[m - 2];
                float l = (float)Math.Sqrt(dx * dx + dz * dz); if (l < 1e-3f) { dx = 0f; dz = 1f; l = 1f; }
                dx /= l; dz /= l;
                for (int k = 1; k <= ext; k++) { sx.Add(kx[m - 1] + dx * k * Step); sy.Add(ky[m - 1]); sz.Add(kz[m - 1] + dz * k * Step); }
            }
            Finish(sx, sy, sz);
            firstSample = new int[tileFirst.Count];
            for (int k = 0; k < tileFirst.Count; k++) firstSample[k] = sampleOf[map[tileFirst[k]]];
        }

        private static float Cr(float p0, float p1, float p2, float p3, float t, float t2, float t3) =>
            0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);

        /// <summary>Stores the samples and works out distance, right vector (cross(up, dir), flat) and surface normal for each, globally.</summary>
        private static void Finish(List<float> sx, List<float> sy, List<float> sz)
        {
            Count = sx.Count;
            X = sx.ToArray(); Y = sy.ToArray(); Z = sz.ToArray();
            RX = new float[Count]; RZ = new float[Count]; NX = new float[Count]; NY = new float[Count]; NZ = new float[Count];
            S = new float[Count]; P = new bool[Count];
            for (int i = 1; i < Count; i++)
            {
                float dx = X[i] - X[i - 1], dy = Y[i] - Y[i - 1], dz = Z[i] - Z[i - 1];
                S[i] = S[i - 1] + (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            float lrx = 1f, lrz = 0f;
            for (int i = 0; i < Count; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(Count - 1, i + 1);
                float tx = X[b] - X[a], ty = Y[b] - Y[a], tz = Z[b] - Z[a];
                float tl = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
                float rx = tz, rz = -tx, rl = (float)Math.Sqrt(rx * rx + rz * rz);
                if (rl < 1e-4f || tl < 1e-4f) { rx = lrx; rz = lrz; tx = -lrz; ty = 0f; tz = lrx; tl = 1f; }
                else { rx /= rl; rz /= rl; tx /= tl; ty /= tl; tz /= tl; }
                RX[i] = rx; RZ[i] = rz; lrx = rx; lrz = rz;
                // normal = cross(tangent, right)
                float nx = ty * rz, ny = tz * rx - tx * rz, nz = -ty * rx;
                float nl = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl < 1e-4f) { nx = 0f; ny = 1f; nz = 0f; nl = 1f; }
                NX[i] = nx / nl; NY[i] = ny / nl; NZ[i] = nz / nl;
            }
        }

        /// <summary>Largest gap between 20 centre-line points and the race spline (closest point), in metres; 0 when it can't be read.</summary>
        private static float PathGap(RoadPathGenerator gen)
        {
            try
            {
                var path = gen == null ? null : gen.RegularPath;
                if (path == null) return 0f;
                float worst = 0f;
                int from = (int)(Extend / Step), to = Count - 1 - (int)(Extend / Step);
                if (to <= from) return 0f;
                var p = new Vector3();
                for (int k = 0; k < 20; k++)
                {
                    int i = from + (int)((long)(to - from) * k / 19);
                    p.x = X[i]; p.y = Y[i]; p.z = Z[i];
                    float d = path.GetClosestPointDistanceOnPath(p);
                    Vector3 q = path.GetPositionFromDistance(d);
                    float dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z;
                    float g = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (!float.IsNaN(g) && g > worst) worst = g;
                }
                return worst;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: race spline check failed: {e.Message}"); return 0f; }
        }

        /// <summary>Fallback: the centre line from RoadPathGenerator.RegularPath every 2.5 m; tile k starts at the sample nearest its first waypoint.</summary>
        private static bool SampleRegularPath(RoadPathGenerator gen, List<Vector3> raw, List<int> tileFirst, out int[] firstSample)
        {
            firstSample = null;
            try
            {
                var path = gen.RegularPath;
                float len = path.TotalLength;
                if (!(len > 10f && len < 200000f)) return false;
                int n = (int)(len / Step) + 1;
                int ext = (int)(Extend / Step);
                var sx = new List<float>(n + 2 * ext); var sy = new List<float>(n + 2 * ext); var sz = new List<float>(n + 2 * ext);
                Vector3 d0 = path.GetDirectionFromDistance(0f), d1 = path.GetDirectionFromDistance(len);
                Vector3 p0 = path.GetPositionFromDistance(0f), p1 = path.GetPositionFromDistance(len);
                float l0 = (float)Math.Sqrt(d0.x * d0.x + d0.z * d0.z), l1 = (float)Math.Sqrt(d1.x * d1.x + d1.z * d1.z);
                if (l0 < 1e-3f || l1 < 1e-3f) return false;
                for (int k = ext; k >= 1; k--) { sx.Add(p0.x - d0.x / l0 * k * Step); sy.Add(p0.y); sz.Add(p0.z - d0.z / l0 * k * Step); }
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = path.GetPositionFromDistance(Math.Min(len, i * Step));
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) return false;
                    sx.Add(p.x); sy.Add(p.y); sz.Add(p.z);
                }
                if (len - (n - 1) * Step > 0.5f) { sx.Add(p1.x); sy.Add(p1.y); sz.Add(p1.z); }
                for (int k = 1; k <= ext; k++) { sx.Add(p1.x + d1.x / l1 * k * Step); sy.Add(p1.y); sz.Add(p1.z + d1.z / l1 * k * Step); }
                Finish(sx, sy, sz);
                firstSample = new int[tileFirst.Count];
                int from = 0;
                for (int k = 0; k < tileFirst.Count; k++)
                {
                    Vector3 w = raw[tileFirst[k]];
                    int best = from; float bd = float.MaxValue;
                    for (int i = from; i < Count; i++)
                    {
                        float dx = X[i] - w.x, dy = Y[i] - w.y, dz = Z[i] - w.z;
                        float dd = dx * dx + dy * dy + dz * dz;
                        if (dd < bd) { bd = dd; best = i; }
                    }
                    firstSample[k] = best;
                    from = best;
                }
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: race spline sampling failed: {e.Message}"); return false; }
        }

        /// <summary>
        /// Hairpins: a sample whose reach (W/2 + 6 m each side, the street collider's edge) meets the road of a sample more
        /// than 60 m (or 2 W) further along the path (horizontal distance under W + 12, height within 6 m) is pinched:
        /// no wall, barrier, raised sidewalk or street light there.
        /// </summary>
        private static int Pinch(float w)
        {
            const float Cell = 25f;
            float reach = w + 2f * StreetOut, reach2 = reach * reach, apart = Math.Max(60f, 2f * w);
            int span = (int)Math.Ceiling(reach / Cell);
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < Count; i++)
            {
                long key = Key((int)Math.Floor(X[i] / Cell), (int)Math.Floor(Z[i] / Cell));
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
                l.Add(i);
            }
            int count = 0;
            for (int i = 0; i < Count; i++)
            {
                int cx = (int)Math.Floor(X[i] / Cell), cz = (int)Math.Floor(Z[i] / Cell);
                bool hit = false;
                for (int ax = -span; ax <= span && !hit; ax++)
                    for (int az = -span; az <= span && !hit; az++)
                    {
                        if (!grid.TryGetValue(Key(cx + ax, cz + az), out var l)) continue;
                        for (int k = 0; k < l.Count; k++)
                        {
                            int j = l[k];
                            if (Math.Abs(S[j] - S[i]) <= apart) continue;
                            float dx = X[j] - X[i], dz = Z[j] - Z[i], dy = Y[j] - Y[i];
                            if (dx * dx + dz * dz < reach2 && Math.Abs(dy) < 6f) { hit = true; break; }
                        }
                    }
                if (hit) { P[i] = true; count++; }
            }
            return count;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        // ------------------------------------------------------------------ per frame

        internal static void Tick()
        {
            if (Tiles.Count == 0)
            {
                // every tile unloaded (Drop) while the light pool is still up: nothing left to light
                RestoreAll("tiles unloaded");
                return;
            }
            float now = Time.unscaledTime;
            NearCheck(now);   // before the hiding: CurbFeel's ramps on the tiles around the car go at once
            float budget = WideRoads.BudgetMs.Value;
            if (!(budget >= 0.5f)) budget = 0.5f; else if (budget > 10f) budget = 10f;
            Sw.Restart();
            bool hiding = false;
            for (int i = 0; i < Tiles.Count; i++)
            {
                var t = Tiles[i];
                if (t.HideDone) continue;
                hiding = true;
                if (!t.Scene.isLoaded) { Drop(i); return; }
                if (!FinishHide(t, budget)) return;   // out of time: carry on next frame (tiles in path order)
                if (Sw.Elapsed.TotalMilliseconds > budget) return;
            }
            if (!hiding && !_raceLogged && Tiles.Count > 0)
            {
                _raceLogged = true;
                Race.Stop();
                if (_nearFastUntil < now + NearFastAfterReady) _nearFastUntil = now + NearFastAfterReady;
                double ms = 0; int vis = 0, hid = 0, dis = 0, li = 0;
                foreach (var t in Tiles) { ms += t.Ms; vis += t.VisTris; hid += t.Hidden.Count; dis += t.Disabled.Count; li += t.Lights.Count; }
                Plugin.Log.LogInfo($"[Sandbox] maps: race ready, {Tiles.Count} tiles in {Race.Elapsed.TotalSeconds:0.0} s ({ms:0} ms of work), {vis} visual triangles, {hid} renderers hidden, {dis} colliders and {li} lights off");
            }
            if (hiding || Tiles.Count == 0) return;
            MovePool(now);
            // round-robin re-check of every tile: at most one tile every 0.15 s (so with N tiles each one comes round about
            // every N x 0.15 s); colliders only for 15 s after the build, then colliders, renderers and lights every 5 s.
            // The tiles around the car are covered sooner by NearCheck.
            if (now < _nextRescan) return;
            for (int k = 0; k < Tiles.Count; k++)
            {
                _scanCursor = (_scanCursor + 1) % Tiles.Count;
                var t = Tiles[_scanCursor];
                if (now < t.NextScan) continue;
                bool fast = now < t.FastUntil;
                t.NextScan = now + (fast ? FastScan : SlowScan);
                _nextRescan = now + ScanGap;
                if (!t.Scene.isLoaded) { Drop(_scanCursor); return; }
                Rescan(t, !fast);
                return;
            }
        }

        /// <summary>
        /// CurbFeel's WallShifter builds CurbFeel_Ramp colliders (+-10..14 m, inside our outer lanes) under the disabled stock
        /// guardrails when a tile is first scanned, +1 s and +4 s later, and again after its F9 / F10 reset. So the tile the
        /// camera is on and the next one get their Road Network colliders re-checked: both every 0.25 s until 10 s after the
        /// race is ready (at least 15 s after the build), then alternately every 0.5 s (each about every 1 s). Runs during
        /// the hiding phase too. The camera tile comes from the nearest centre-line sample (plain maths over the samples).
        /// </summary>
        private static void NearCheck(float now)
        {
            if (now < _nextNear || Count == 0) return;
            bool fast = now < _nearFastUntil;
            _nextNear = now + (fast ? NearFast : NearSlow);
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 c = cam.transform.position;
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < Count; i++)
            {
                float dx = X[i] - c.x, dz = Z[i] - c.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            int at = -1;
            for (int k = 0; k < Tiles.Count; k++) if (best >= Tiles[k].S0 && best <= Tiles[k].S1) { at = k; break; }
            if (at < 0) return;
            if (fast) { NearOne(Tiles[at]); if (at + 1 < Tiles.Count) NearOne(Tiles[at + 1]); }
            else { _nearPhase ^= 1; NearOne(Tiles[_nearPhase == 0 || at + 1 >= Tiles.Count ? at : at + 1]); }
        }

        private static void NearOne(Tile t)
        {
            if (!t.Scene.isLoaded || t.RoadNet == null) return;   // the hiding loop / round-robin drop it
            int n = DisableIn(t, t.RoadNet, true);
            if (n > 0) Plugin.Log.LogInfo($"[Sandbox] maps: tile {t.Index} {t.Name}: near-car check caught {n} colliders added later");
        }

        /// <summary>The tile's scene went: its objects went with it; destroy our meshes and forget it.</summary>
        private static void Drop(int i)
        {
            var t = Tiles[i];
            DestroyOwn(t);
            Handles.Remove(t.Handle);
            Tiles.RemoveAt(i);
        }

        /// <summary>Hides the tile's renderers within the budget; when done, logs the tile. False = out of time.</summary>
        private static bool FinishHide(Tile t, float budget)
        {
            double t0 = Sw.Elapsed.TotalMilliseconds;   // the frame's own stopwatch: no allocation per frame
            bool done = HideSlice(t, budget);
            t.Ms += Sw.Elapsed.TotalMilliseconds - t0;
            if (!done) return false;
            t.HideDone = true;
            Plugin.Log.LogInfo($"[Sandbox] maps: tile {t.Index} {t.Name}: {t.Waypoints} waypoints, samples {t.S0}-{t.S1}, {t.Pinches} pinched, " +
                               $"{t.VisTris} visual / {t.StreetTris} street / {t.WallTris} wall triangles, {t.Poles} street lights, {t.Hidden.Count} renderers hidden, " +
                               $"{t.Disabled.Count} colliders and {t.Lights.Count} lights off, materials street '{t.StreetMatName}' wall '{t.WallMatName}', {t.Ms:0.0} ms");
            return true;
        }

        private static void Setup(Tile t)
        {
            SceneryRunner.ReleaseTile(t.Handle);   // Scenery's hidden renderers / lights / blocks back before we take the tile
            // the stock physics materials, read before their colliders are disabled
            var cols = t.RoadNet.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null || c.isTrigger) continue;
                string n = c.gameObject.name;
                if (t.StreetMat == null && (n.Contains("RoadGroundCollider") || n.Contains("PresetRoad")))
                {
                    t.StreetMat = c.sharedMaterial;
                    t.StreetMatName = t.StreetMat == null ? "null" : t.StreetMat.name;
                }
                if (t.WallMat == null && n.Contains("Guardrail_Regular"))
                {
                    t.WallMat = c.sharedMaterial;
                    t.WallMatName = t.WallMat == null ? "null" : t.WallMat.name;
                }
            }
            var root = new GameObject("fx_WideRoad");
            t.Root = root;
            t.RootPtr = root.Pointer;
            root.layer = LayerStreet;
            root.transform.SetParent(t.RoadNet, false);
        }

        // ------------------------------------------------------------------ meshes

        private sealed class MB
        {
            public readonly List<float> V = new List<float>(4096), Nr = new List<float>(4096);
            public readonly List<int>[] T;
            public MB(int subs) { T = new List<int>[subs]; for (int i = 0; i < subs; i++) T[i] = new List<int>(2048); }
            public int Count => V.Count / 3;

            public int Add(int i, float off, float y, float nx, float ny, float nz)
            {
                int k = Count;
                V.Add(X[i] + RX[i] * off); V.Add(Y[i] + y); V.Add(Z[i] + RZ[i] * off);
                Nr.Add(nx); Nr.Add(ny); Nr.Add(nz);
                return k;
            }

            public int AddP(float x, float y, float z, float nx, float ny, float nz)
            {
                int k = Count;
                V.Add(x); V.Add(y); V.Add(z);
                Nr.Add(nx); Nr.Add(ny); Nr.Add(nz);
                return k;
            }

            public void Tri(int sub, int a, int b, int c) { var l = T[sub]; l.Add(a); l.Add(b); l.Add(c); }
        }

        /// <summary>which: 0 every sample, 1 not at pinched samples, 2 only at (and next to) pinched samples.</summary>
        private static bool Use(int i, int which)
        {
            if (which == 0) return true;
            if (which == 1) return !P[i];
            // the pinched-only pieces reach one sample further each way, so they meet the normal pieces without a gap
            return P[i] || (i > 0 && P[i - 1]) || (i + 1 < Count && P[i + 1]);
        }

        /// <summary>An up-facing band from offset a (left) to b (right), heights ya / yb.</summary>
        private static void Band(MB m, int sub, Tile t, float a, float ya, float b, float yb, int which)
        {
            int pa = -1, pb = -1;
            for (int i = t.S0; i <= t.S1; i++)
            {
                if (!Use(i, which)) { pa = -1; continue; }
                int va = m.Add(i, a, ya, NX[i], NY[i], NZ[i]), vb = m.Add(i, b, yb, NX[i], NY[i], NZ[i]);
                if (pa >= 0) { m.Tri(sub, pa, va, vb); m.Tri(sub, pa, vb, pb); }
                pa = va; pb = vb;
            }
        }

        /// <summary>A vertical band at offset o from y0 to y1, facing the right side (or the left), optionally both.</summary>
        private static void Face(MB m, int sub, Tile t, float o, float y0, float y1, bool facesRight, bool both, int which)
        {
            int pb = -1, pt = -1;
            for (int i = t.S0; i <= t.S1; i++)
            {
                if (!Use(i, which)) { pb = -1; continue; }
                float s = facesRight ? 1f : -1f;
                int vb = m.Add(i, o, y0, RX[i] * s, 0f, RZ[i] * s), vt = m.Add(i, o, y1, RX[i] * s, 0f, RZ[i] * s);
                if (pb >= 0)
                {
                    if (facesRight || both) { m.Tri(sub, pb, pt, vt); m.Tri(sub, pb, vt, vb); }
                    if (!facesRight || both) { m.Tri(sub, pb, vt, pt); m.Tri(sub, pb, vb, vt); }
                }
                pb = vb; pt = vt;
            }
        }

        /// <summary>Lane dashes at offset o: 3 m on, 9 m off along the whole road's distance.</summary>
        private static void Dashes(MB m, int sub, Tile t, float o, float half)
        {
            for (int i = t.S0; i < t.S1; i++)
            {
                if (S[i] % 12f >= 3f) continue;
                int a = m.Add(i, o - half, LineY, NX[i], NY[i], NZ[i]), b = m.Add(i, o + half, LineY, NX[i], NY[i], NZ[i]);
                int c = m.Add(i + 1, o - half, LineY, NX[i + 1], NY[i + 1], NZ[i + 1]), d = m.Add(i + 1, o + half, LineY, NX[i + 1], NY[i + 1], NZ[i + 1]);
                m.Tri(sub, a, c, d); m.Tri(sub, a, d, b);
            }
        }

        /// <summary>A cross wall at sample i (the two ends of the whole road), both faces.</summary>
        private static void Cap(MB m, int i, float half, float y0, float y1)
        {
            int a = m.Add(i, -half, y0, 0f, 0f, 0f), b = m.Add(i, half, y0, 0f, 0f, 0f), c = m.Add(i, half, y1, 0f, 0f, 0f), d = m.Add(i, -half, y1, 0f, 0f, 0f);
            m.Tri(0, a, b, c); m.Tri(0, a, c, d); m.Tri(0, a, c, b); m.Tri(0, a, d, c);
        }

        /// <summary>
        /// One quad of a box: centre (cx, cy, cz) + n * d, spanned by the half vectors a and b, visible from outside
        /// (clockwise as seen along -n; (a, n, b) has the handedness of (right, up, forward)).
        /// </summary>
        private static void Quad(MB m, int sub, float cx, float cy, float cz, float nx, float ny, float nz, float d,
                                 float ax, float ay, float az, float bx, float by, float bz)
        {
            float ox = cx + nx * d, oy = cy + ny * d, oz = cz + nz * d;
            int v0 = m.AddP(ox - ax - bx, oy - ay - by, oz - az - bz, nx, ny, nz);
            int v1 = m.AddP(ox - ax + bx, oy - ay + by, oz - az + bz, nx, ny, nz);
            int v2 = m.AddP(ox + ax + bx, oy + ay + by, oz + az + bz, nx, ny, nz);
            int v3 = m.AddP(ox + ax - bx, oy + ay - by, oz + az - bz, nx, ny, nz);
            m.Tri(sub, v0, v1, v2); m.Tri(sub, v0, v2, v3);
        }

        /// <summary>
        /// A box (no bottom) at sample i: centred at offset `off` across and `along` metres forward, from y0 to y1 above the
        /// centre line's height, half sizes hr across and hf along. Axes are the sample's flat right / forward and world up.
        /// </summary>
        private static void Box(MB m, int sub, int i, float off, float along, float y0, float y1, float hr, float hf)
        {
            float rx = RX[i], rz = RZ[i], fx = -rz, fz = rx;   // forward = cross(right, up) for right = cross(up, dir)
            float hu = (y1 - y0) * 0.5f;
            float cx = X[i] + rx * off + fx * along, cy = Y[i] + (y0 + y1) * 0.5f, cz = Z[i] + rz * off + fz * along;
            Quad(m, sub, cx, cy, cz, 0f, 1f, 0f, hu, rx * hr, 0f, rz * hr, fx * hf, 0f, fz * hf);        // top
            Quad(m, sub, cx, cy, cz, rx, 0f, rz, hr, fx * hf, 0f, fz * hf, 0f, hu, 0f);                 // right
            Quad(m, sub, cx, cy, cz, -rx, 0f, -rz, hr, -fx * hf, 0f, -fz * hf, 0f, hu, 0f);             // left
            Quad(m, sub, cx, cy, cz, fx, 0f, fz, hf, 0f, hu, 0f, rx * hr, 0f, rz * hr);                 // front
            Quad(m, sub, cx, cy, cz, -fx, 0f, -fz, hf, 0f, -hu, 0f, rx * hr, 0f, rz * hr);              // back
        }

        /// <summary>True at the first sample of each `every`-metre stretch, owned by one tile only (seams shared).</summary>
        private static bool Every(Tile t, int i, float every)
        {
            if (i == t.S1 && t.S1 != Count - 1) return false;   // the next tile's S0
            if (i == 0) return true;
            return (int)(S[i] / every) != (int)(S[i - 1] / every);
        }

        private static void BuildVisual(Tile t)
        {
            float w = WideRoads.W, h = w * 0.5f;
            int n = WideRoads.N;
            var m = new MB(SubCount);
            Band(m, SubAsphalt, t, -h, 0f, h, 0f, 0);
            foreach (float s in new[] { -1f, 1f })
            {
                float e = s * (h - 0.4f);
                Band(m, SubWhite, t, e - 0.075f, LineY, e + 0.075f, LineY, 0);
            }
            for (int k = 1; k < n; k++)
            {
                float o = -h + k * w / n;
                if ((n & 1) == 0 && k == n / 2)
                {
                    Band(m, SubYellow, t, -0.24f, LineY, -0.12f, LineY, 0);
                    Band(m, SubYellow, t, 0.12f, LineY, 0.24f, LineY, 0);
                }
                else Dashes(m, SubWhite, t, o, 0.075f);
            }
            // curb faces, curb tops, sidewalks, sidewalk outer faces (not at pinched samples)
            Face(m, SubCurb, t, -h, 0f, CurbH, true, false, 1);
            Face(m, SubCurb, t, h, 0f, CurbH, false, false, 1);
            Band(m, SubCurb, t, -h - 0.3f, CurbH, -h, CurbH, 1);
            Band(m, SubCurb, t, h, CurbH, h + 0.3f, CurbH, 1);
            Band(m, SubSidewalk, t, -h - Sidewalk, CurbH, -h - 0.3f, CurbH, 1);
            Band(m, SubSidewalk, t, h + 0.3f, CurbH, h + Sidewalk, CurbH, 1);
            Face(m, SubSidewalk, t, -h - Sidewalk, -0.05f, CurbH, false, false, 1);
            Face(m, SubSidewalk, t, h + Sidewalk, -0.05f, CurbH, true, false, 1);
            // ground: just below the road, sloping gently down outwards (so it stays under a neighbouring road)
            Band(m, SubGround, t, -h - GroundOut, -2f, -h - Sidewalk, -0.05f, 0);
            Band(m, SubGround, t, h + Sidewalk, -0.05f, h + GroundOut, -2f, 0);
            Band(m, SubGround, t, -h - Sidewalk, -0.05f, -h, -0.05f, 2);
            Band(m, SubGround, t, h, -0.05f, h + Sidewalk, -0.05f, 2);
            // guardrail on the wall line (both faces), posts every 5 m (not at pinched samples)
            float rail = h + WallOut;
            Face(m, SubRail, t, -rail, RailLow, RailHigh, true, true, 1);
            Face(m, SubRail, t, rail, RailLow, RailHigh, false, true, 1);
            for (int i = t.S0; i <= t.S1; i++)
            {
                if (P[i] || !Every(t, i, PostEvery)) continue;
                Box(m, SubPole, i, -rail - 0.08f, 0f, -0.05f, RailHigh + 0.05f, 0.06f, 0.06f);
                Box(m, SubPole, i, rail + 0.08f, 0f, -0.05f, RailHigh + 0.05f, 0.06f, 0.06f);
            }
            // street lights: a pole every 40 m, alternating sides, between the sidewalk and the barrier, arm over the road
            if (Multiplayer.StreetLights(WideRoads.StreetLights.Value))
            {
                float pole = h + Sidewalk + 0.25f;
                for (int i = t.S0; i <= t.S1; i++)
                {
                    if (P[i] || !Every(t, i, PoleEvery)) continue;
                    float side = ((int)(S[i] / PoleEvery) & 1) == 0 ? 1f : -1f;
                    float o = side * pole, inward = -side;
                    Box(m, SubPole, i, o, 0f, -0.05f, PoleH, 0.1f, 0.1f);
                    Box(m, SubPole, i, o + inward * ArmLen * 0.5f, 0f, PoleH - 0.25f, PoleH - 0.1f, ArmLen * 0.5f, 0.06f);
                    float head = o + inward * (ArmLen - 0.3f);
                    Box(m, SubLamp, i, head, 0f, PoleH - 0.4f, PoleH - 0.22f, 0.35f, 0.18f);
                    PoleX.Add(X[i] + RX[i] * head); PoleY.Add(Y[i] + PoleH - 0.6f); PoleZ.Add(Z[i] + RZ[i] * head);
                    t.Poles++;
                }
            }

            var mesh = ToMesh(m, t, "Sandbox.WideRoad." + t.Index, true);
            t.Visual = mesh;
            for (int s = 0; s < SubCount; s++) t.VisTris += m.T[s].Count / 3;
            t.Root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = t.Root.AddComponent<MeshRenderer>();
            mr.sharedMaterials = Materials();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
        }

        private static void BuildStreet(Tile t)
        {
            float h = WideRoads.W * 0.5f;
            var m = new MB(1);
            // flat out to +-(W/2 + 6) with a bevelled curb (0 at W/2 - 0.2 up to 0.15 at W/2 + 0.2); flat at pinched samples
            Band(m, 0, t, -h + 0.2f, 0f, h - 0.2f, 0f, 0);
            Band(m, 0, t, -h - 0.2f, CurbH, -h + 0.2f, 0f, 1);
            Band(m, 0, t, h - 0.2f, 0f, h + 0.2f, CurbH, 1);
            Band(m, 0, t, -h - StreetOut, CurbH, -h - 0.2f, CurbH, 1);
            Band(m, 0, t, h + 0.2f, CurbH, h + StreetOut, CurbH, 1);
            Band(m, 0, t, -h - StreetOut, 0f, -h + 0.2f, 0f, 2);
            Band(m, 0, t, h - 0.2f, 0f, h + StreetOut, 0f, 2);
            t.StreetTris = m.T[0].Count / 3;
            t.Street = ToMesh(m, t, "Sandbox.WideStreet." + t.Index, false);
            var go = new GameObject("fx_WideStreet");
            t.StreetPtr = go.Pointer;
            go.layer = LayerStreet;
            go.transform.SetParent(t.Root.transform, false);
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = t.Street;
            if (t.StreetMat != null) mc.sharedMaterial = t.StreetMat;
        }

        private static void BuildWalls(Tile t)
        {
            float o = WideRoads.W * 0.5f + WallOut;
            var m = new MB(1);
            Face(m, 0, t, -o, -0.5f, 3f, true, true, 1);
            Face(m, 0, t, o, -0.5f, 3f, false, true, 1);
            if (t.S0 == 0) Cap(m, 0, o, -0.5f, 3f);
            if (t.S1 == Count - 1) Cap(m, Count - 1, o, -0.5f, 3f);
            t.WallTris = m.T[0].Count / 3;
            if (t.WallTris == 0) return;   // every sample pinched
            t.Wall = ToMesh(m, t, "Sandbox.WideWall." + t.Index, false);
            var go = new GameObject("fx_WideWall");   // not a "Guardrail" name: CurbFeel's WallShifter leaves it alone
            t.WallPtr = go.Pointer;
            go.layer = LayerGuardrail;
            go.transform.SetParent(t.Root.transform, false);
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = t.Wall;
            if (t.WallMat != null) mc.sharedMaterial = t.WallMat;
        }

        /// <summary>World samples into the Road Network's local frame (one matrix read, plain maths), submeshes, upload.</summary>
        private static Mesh ToMesh(MB m, Tile t, string name, bool render)
        {
            Matrix4x4 w2l = t.Root.transform.worldToLocalMatrix;
            float m00 = w2l.m00, m01 = w2l.m01, m02 = w2l.m02, m03 = w2l.m03;
            float m10 = w2l.m10, m11 = w2l.m11, m12 = w2l.m12, m13 = w2l.m13;
            float m20 = w2l.m20, m21 = w2l.m21, m22 = w2l.m22, m23 = w2l.m23;
            int n = m.Count;
            var verts = new Vector3[n];
            var norms = render ? new Vector3[n] : null;
            for (int i = 0; i < n; i++)
            {
                float x = m.V[i * 3], y = m.V[i * 3 + 1], z = m.V[i * 3 + 2];
                verts[i].x = m00 * x + m01 * y + m02 * z + m03;
                verts[i].y = m10 * x + m11 * y + m12 * z + m13;
                verts[i].z = m20 * x + m21 * y + m22 * z + m23;
                if (render)
                {
                    float a = m.Nr[i * 3], b = m.Nr[i * 3 + 1], c = m.Nr[i * 3 + 2];
                    float nx = m00 * a + m01 * b + m02 * c, ny = m10 * a + m11 * b + m12 * c, nz = m20 * a + m21 * b + m22 * c;
                    float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (l > 1e-6f) { nx /= l; ny /= l; nz /= l; } else { nx = 0f; ny = 1f; nz = 0f; }
                    norms[i].x = nx; norms[i].y = ny; norms[i].z = nz;
                }
            }
            var mesh = new Mesh { name = name };
            if (n > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            if (render) mesh.normals = norms;
            mesh.subMeshCount = m.T.Length;
            for (int s = 0; s < m.T.Length; s++) mesh.SetTriangles(m.T[s].ToArray(), s);
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (render) mesh.UploadMeshData(true);   // collider meshes stay readable for cooking
            return mesh;
        }

        private static Material[] Materials()
        {
            if (_mats != null)
            {
                bool ok = true;
                for (int i = 0; i < _mats.Length; i++) if (_mats[i] == null) ok = false;
                if (ok) return _mats;
                DestroyMaterials();
            }
            var sh = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) throw new InvalidOperationException("no URP lit shader for the wide road");
            var mats = new Material[SubCount];
            mats[SubAsphalt] = Mat(sh, "asphalt", 0.16f, 0.16f, 0.16f, 0f);
            mats[SubWhite] = Mat(sh, "white line", 0.9f, 0.9f, 0.9f, 0.12f);
            mats[SubYellow] = Mat(sh, "yellow line", 0.95f, 0.75f, 0.15f, 0f);
            mats[SubCurb] = Mat(sh, "curb", 0.55f, 0.55f, 0.55f, 0f);
            mats[SubSidewalk] = Mat(sh, "sidewalk", 0.42f, 0.42f, 0.42f, 0f);
            mats[SubGround] = Mat(sh, "ground", 0.20f, 0.22f, 0.18f, 0f);
            mats[SubRail] = Mat(sh, "guardrail", 0.62f, 0.64f, 0.66f, 0.05f);
            mats[SubPole] = Mat(sh, "pole", 0.30f, 0.31f, 0.33f, 0f);
            mats[SubLamp] = Mat(sh, "lamp", 1f, 0.92f, 0.75f, 1.6f);
            _mats = mats;
            return _mats;
        }

        /// <summary>A URP Simple Lit material; glow &gt; 0 = emission of that strength (lane lines readable at night, lamp heads lit).</summary>
        private static Material Mat(Shader sh, string name, float r, float g, float b, float glow)
        {
            var m = new Material(sh) { name = "Sandbox.WideRoad." + name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.SetColor("_BaseColor", new Color(r, g, b, 1f));
            if (glow > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(r * glow, g * glow, b * glow, 1f));
            }
            return m;
        }

        internal static void DestroyMaterials()
        {
            if (_mats == null) return;
            foreach (var m in _mats) { try { if (m != null) UnityEngine.Object.Destroy(m); } catch { /* shutting down */ } }
            _mats = null;
        }

        // ------------------------------------------------------------------ street-light pool

        private static void MakePool()
        {
            if (PoleX.Count == 0) return;
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("fx_WideLight");
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = 22f;
                l.intensity = 1.6f;
                l.color = new Color(1f, 0.86f, 0.62f, 1f);
                l.shadows = LightShadows.None;
                Pool.Add(go);
                PoolLights.Add(l);
            }
            _poolAt = -1;
            _poolNext = 0f;
            MovePool(0f);
        }

        /// <summary>Every 0.5 s: the 8 lights onto the poles nearest the camera (a window of poles in path order).</summary>
        private static void MovePool(float now)
        {
            if (Pool.Count == 0 || PoleX.Count == 0 || now < _poolNext) return;
            _poolNext = now + 0.5f;
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 c = cam.transform.position;
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < PoleX.Count; i++)
            {
                float dx = PoleX[i] - c.x, dz = PoleZ[i] - c.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            int first = Math.Max(0, Math.Min(PoleX.Count - Pool.Count, best - Pool.Count / 2 + 1));
            if (first == _poolAt) return;
            _poolAt = first;
            for (int k = 0; k < Pool.Count; k++)
            {
                var go = Pool[k];
                if (go == null) continue;
                int p = first + k;
                bool on = p < PoleX.Count;
                if (on) go.transform.position = new Vector3(PoleX[p], PoleY[p], PoleZ[p]);
                if (go.activeSelf != on) go.SetActive(on);
            }
        }

        // ------------------------------------------------------------------ the stock tile

        /// <summary>One of this tile's own objects (fx_WideRoad / fx_WideStreet / fx_WideWall): a pointer compare, no name read.</summary>
        private static bool Ours(Tile t, Component c)
        {
            var p = c.gameObject.Pointer;
            return p != IntPtr.Zero && (p == t.RootPtr || p == t.StreetPtr || p == t.WallPtr);
        }

        /// <summary>Every renderer under Biomes and Road Network (inactive ones too) into t.Rends; during the loading screen (Begin).</summary>
        private static void CollectRenderers(Tile t)
        {
            var a = t.Biomes == null ? null : t.Biomes.GetComponentsInChildren<Renderer>(true);
            var b = t.RoadNet.GetComponentsInChildren<Renderer>(true);
            int na = a == null ? 0 : a.Length, nb = b == null ? 0 : b.Length;
            var all = new Renderer[na + nb];
            for (int i = 0; i < na; i++) all[i] = a[i];
            for (int i = 0; i < nb; i++) all[na + i] = b[i];
            t.Rends = all;
            t.RNext = 0;
        }

        /// <summary>Hides the renderers collected in Begin a slice at a time (no scan here); false = out of time.</summary>
        private static bool HideSlice(Tile t, float budget)
        {
            if (t.Rends == null) return true;
            while (t.RNext < t.Rends.Length)
            {
                var r = t.Rends[t.RNext++];
                if (r != null && !r.forceRenderingOff && !Ours(t, r)) { r.forceRenderingOff = true; t.Hidden.Add(r); }
                if ((t.RNext & 63) == 0 && Sw.Elapsed.TotalMilliseconds > budget) return false;
            }
            t.Rends = null;
            return true;
        }

        /// <summary>Disables every non-trigger collider under Road Network and Biomes (road, guardrails, buildings, cones); triggers stay.</summary>
        private static void DisableColliders(Tile t)
        {
            DisableIn(t, t.RoadNet, false);
            if (t.Biomes != null) DisableIn(t, t.Biomes, false);
        }

        private static int DisableIn(Tile t, Transform root, bool late)
        {
            var cols = root.GetComponentsInChildren<Collider>(true);
            int n = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null || !c.enabled || c.isTrigger || Ours(t, c)) continue;
                c.enabled = false;
                t.Disabled.Add(c);
                n++;
            }
            if (late) t.LateCols += n;
            return n;
        }

        /// <summary>Switches off the stock lamps' Lights (their poles are hidden with the scenery); recorded and put back.</summary>
        private static int DisableLights(Transform root, Tile t, bool late)
        {
            var lights = root.GetComponentsInChildren<Light>(true);
            int n = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                var l = lights[i];
                if (l == null || !l.enabled || Ours(t, l)) continue;
                l.enabled = false;
                t.Lights.Add(l);
                n++;
            }
            if (late) t.LateLights += n;
            return n;
        }

        /// <summary>
        /// New colliders under Road Network (e.g. CurbFeel's CurbFeel_Ramp); full = also new renderers (its debug
        /// renderers) and lights. Rate-limited by Tick (one tile per 0.15 s at most).
        /// </summary>
        private static void Rescan(Tile t, bool full)
        {
            int c = DisableIn(t, t.RoadNet, true), r = 0, l = 0;
            if (full)
            {
                l = DisableLights(t.RoadNet, t, true);
                var rends = t.RoadNet.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rends.Length; i++)
                {
                    var x = rends[i];
                    if (x == null || x.forceRenderingOff || Ours(t, x)) continue;
                    x.forceRenderingOff = true; t.Hidden.Add(x); r++;
                }
            }
            t.LateRends += r;
            if (c > 0 || r > 0 || l > 0) Plugin.Log.LogInfo($"[Sandbox] maps: tile {t.Index} {t.Name}: re-check caught {c} colliders, {r} renderers and {l} lights added later");
        }

        // ------------------------------------------------------------------ finish line

        private static void WidenFinish(float w)
        {
            try
            {
                var found = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<FinishLineTrigger>(), FindObjectsSortMode.None);
                if (found == null || found.Length == 0) { Plugin.Log.LogInfo("[Sandbox] maps: no finish line found to widen"); return; }
                for (int f = 0; f < found.Length; f++)
                {
                    var trig = found[f] == null ? null : found[f].TryCast<FinishLineTrigger>();
                    if (trig != null) WidenOne(trig.transform.root, w);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: finish line not widened: {e.Message}"); }
        }

        private static void WidenOne(Transform root, float w)
        {
            try
            {
                for (int i = 0; i < Finishes.Count; i++) if (Finishes[i] == root) return;   // two triggers under one root
                Vector3 right = root.right;
                float width = 0f;
                var cols = root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++)
                {
                    var c = cols[i];
                    if (c == null || !c.isTrigger) continue;
                    float cw;
                    var box = c.TryCast<BoxCollider>();
                    if (box != null) cw = Math.Abs(box.size.x * box.transform.lossyScale.x);
                    else
                    {
                        Vector3 e = c.bounds.extents;
                        cw = 2f * (Math.Abs(e.x * right.x) + Math.Abs(e.z * right.z));
                    }
                    if (cw > width) width = cw;
                }
                float want = w + 4f;
                if (!(width > 1f)) { Plugin.Log.LogInfo($"[Sandbox] maps: finish trigger width unreadable ({width:0.0} m), left as it is"); return; }
                if (width >= want) { Plugin.Log.LogInfo($"[Sandbox] maps: finish trigger {width:0.0} m wide, already covers {want:0} m"); return; }
                Vector3 old = root.localScale, s = old;
                Finishes.Add(root); FinishScales.Add(old);
                s.x *= want / width;
                root.localScale = s;
                Plugin.Log.LogInfo($"[Sandbox] maps: finish line '{root.gameObject.name}' {width:0.0} m -> {want:0.0} m wide (scale x {old.x:0.##} -> {s.x:0.##})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] maps: finish line not widened: {e.Message}"); }
        }

        // ------------------------------------------------------------------ restore

        private static void DestroyOwn(Tile t)
        {
            if (t.Root != null) { try { UnityEngine.Object.Destroy(t.Root); } catch { /* went with the scene */ } }
            foreach (var m in new[] { t.Visual, t.Street, t.Wall })
                if (m != null) { try { UnityEngine.Object.Destroy(m); } catch { /* shutting down */ } }
            t.Root = null; t.Visual = null; t.Street = null; t.Wall = null; t.Rends = null;
            t.RootPtr = IntPtr.Zero; t.StreetPtr = IntPtr.Zero; t.WallPtr = IntPtr.Zero;
        }

        /// <summary>Renderers, colliders and lights back, our objects, meshes and light pool destroyed, the finish line's scale back. Idempotent.</summary>
        internal static void RestoreAll(string why)
        {
            int r = 0, c = 0, l = 0, n = Tiles.Count;
            foreach (var t in Tiles)
            {
                foreach (var x in t.Hidden) { try { if (x != null) { x.forceRenderingOff = false; r++; } } catch { /* gone */ } }
                foreach (var x in t.Disabled) { try { if (x != null) { x.enabled = true; c++; } } catch { /* gone */ } }
                foreach (var x in t.Lights) { try { if (x != null) { x.enabled = true; l++; } } catch { /* gone */ } }
                t.Hidden.Clear(); t.Disabled.Clear(); t.Lights.Clear();
                DestroyOwn(t);
            }
            Tiles.Clear();
            Handles.Clear();
            _nextRescan = 0f;
            _nextNear = 0f;
            int pooled = Pool.Count;
            foreach (var go in Pool) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { /* went with the scene */ } }
            Pool.Clear(); PoolLights.Clear();
            PoleX.Clear(); PoleY.Clear(); PoleZ.Clear();
            _poolAt = -1;
            for (int i = 0; i < Finishes.Count; i++) { try { if (Finishes[i] != null) Finishes[i].localScale = FinishScales[i]; } catch { /* gone */ } }
            Finishes.Clear(); FinishScales.Clear();
            if (n > 0 || pooled > 0) Plugin.Log.LogInfo($"[Sandbox] maps restored ({why}): {n} tiles, {r} renderers, {c} colliders and {l} lights back on, {pooled} pooled street lights removed");
        }
    }
}
