using System;
using System.Collections.Generic;
using UnityEngine;

namespace CurbFeel
{
    /// <summary>
    /// Per-tile picture of what lies beyond the curb:
    ///  - obstacles: world bounds of solid, building-sized scenery (buildings, walls, cliffs, bus stops). Thin props
    ///    (lamp posts, signs, benches, bikes) are ignored. Nothing in the tiles has collision, so this is the only thing
    ///    stopping a moved wall from letting the car into a facade.
    ///  - surface points: the sidewalk meshes (*RoadSide*, *Sidewalk*, *Walkway*, Sides_*), read from the GPU, used for
    ///    the ramp height only.
    /// </summary>
    internal class SidewalkMap
    {
        private const float Cell = 4f;
        private readonly Dictionary<long, List<Bounds>> _obstacles = new();
        private readonly Dictionary<long, List<Vector3>> _surface = new();
        public int ObstacleCount { get; private set; }
        public int SurfacePoints { get; private set; }

        private static readonly Dictionary<int, SidewalkMap> Cache = new();

        public static SidewalkMap ForScene(UnityEngine.SceneManagement.Scene scene)
        {
            int handle = scene.handle;
            if (Cache.TryGetValue(handle, out var map)) return map;
            map = new SidewalkMap();
            map.Build(scene, handle);
            Cache[handle] = map;
            Plugin.Verbose($"[Sidewalk] {scene.name}: {map.ObstacleCount} building-sized obstacles, {map.SurfacePoints} sidewalk surface points");
            return map;
        }

        public static void ClearCache() => Cache.Clear();

        /// <summary>Drop the map of a tile that was unloaded (scene handles aren't reused, so it would never be read again).</summary>
        public static void Forget(int sceneHandle) => Cache.Remove(sceneHandle);

        private static readonly string[] IgnoreObstacle = { "Guardrail", "PresetRoad", "Road", "Sidewalk", "Walkway", "Ground", "Terrain", "Plane", "Decal", "CurbFeel", "Water", "Sky", "Cloud" };
        private static readonly string[] SurfaceNames = { "RoadSide", "Sidewalk", "Walkway", "Sides_" };

        /// <summary>
        /// Reads the tile's active MeshRenderers. Walks only this scene's root objects (GetComponentsInChildren without
        /// inactive = the same set FindObjectsByType returns for the scene); falls back to the whole-scene search if the
        /// scene can't be walked.
        /// </summary>
        private void Build(UnityEngine.SceneManagement.Scene scene, int sceneHandle)
        {
            GameObject[] roots = null;
            try
            {
                if (scene.isLoaded)
                {
                    var r = scene.GetRootGameObjects();
                    roots = new GameObject[r.Length];
                    for (int i = 0; i < r.Length; i++) roots[i] = r[i];
                }
            }
            catch (Exception e)
            {
                Plugin.Verbose($"[Sidewalk] {scene.name}: root walk failed ({e.Message}), searching the whole scene");
                roots = null;
            }

            if (roots == null)
            {
                var all = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i];
                    if (r == null || r.gameObject.scene.handle != sceneHandle) continue;
                    Add(r);
                }
                return;
            }

            foreach (var root in roots)
            {
                if (root == null) continue;
                var renderers = root.transform.GetComponentsInChildren<MeshRenderer>(false);
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r != null) Add(r);
                }
            }
        }

        private void Add(MeshRenderer r)
        {
            if (!r.enabled) return;
            string n = r.gameObject.name;

            // sidewalk surface (ramp height)
            if (Matches(n, SurfaceNames))
            {
                var mf = r.GetComponent<MeshFilter>();
                var pts = mf != null && mf.sharedMesh != null ? GpuMeshReader.TryReadPositions(mf.sharedMesh) : null;
                if (pts != null)
                {
                    var t = r.transform;
                    foreach (var p in pts)
                    {
                        var w = t.TransformPoint(p);
                        long k = Key(w.x, w.z);
                        if (!_surface.TryGetValue(k, out var l)) _surface[k] = l = new List<Vector3>();
                        l.Add(w);
                        SurfacePoints++;
                    }
                }
                return;
            }

            // building-sized obstacle
            if (Matches(n, IgnoreObstacle)) return;
            Bounds b = r.bounds;
            Vector3 s = b.size;
            float horiz = Mathf.Max(s.x, s.z);
            if (s.y < 0.9f || horiz < 1.5f || horiz > 60f) return;   // too low, too thin (posts, signs) or tile-sized
            AddObstacle(b);
        }

        private static bool Matches(string n, string[] list)
        {
            foreach (var s in list) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void AddObstacle(Bounds b)
        {
            int x0 = Mathf.FloorToInt(b.min.x / Cell), x1 = Mathf.FloorToInt(b.max.x / Cell);
            int z0 = Mathf.FloorToInt(b.min.z / Cell), z1 = Mathf.FloorToInt(b.max.z / Cell);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long k = ((long)x << 32) ^ (uint)z;
                    if (!_obstacles.TryGetValue(k, out var l)) _obstacles[k] = l = new List<Bounds>();
                    l.Add(b);
                }
            ObstacleCount++;
        }

        // Probe scratch buffers (main thread only, Probe is not re-entrant).
        private static readonly HashSet<long> ProbeSeen = new();
        private static readonly List<float> ProbeHeights = new();

        private static long Key(float x, float z) => ((long)Mathf.FloorToInt(x / Cell) << 32) ^ (uint)Mathf.FloorToInt(z / Cell);

        /// <summary>
        /// From the curb face, outward along dir: distance to the first building-sized obstacle at bumper height
        /// (float.MaxValue if none within 12 m), and the median sidewalk height near the curb (-1 if unknown).
        /// </summary>
        public void Probe(Vector3 curb, Vector3 dir, float roadY, out float obstacle, out float height)
        {
            obstacle = float.MaxValue; height = -1f;
            var origin = new Vector3(curb.x, roadY + 0.6f, curb.z);
            var seen = ProbeSeen; seen.Clear();             // reused: a wall pair runs hundreds of probes
            var heights = ProbeHeights; heights.Clear();
            for (float a = 0f; a <= 12f; a += Cell * 0.5f)
            {
                float cx = curb.x + dir.x * a, cz = curb.z + dir.z * a;
                for (int ix = -1; ix <= 1; ix++)
                    for (int iz = -1; iz <= 1; iz++)
                    {
                        long k = Key(cx + ix * Cell, cz + iz * Cell);
                        if (!seen.Add(k)) continue;
                        if (_obstacles.TryGetValue(k, out var obs))
                            foreach (var b in obs)
                            {
                                if (b.min.y > roadY + 1.5f || b.max.y < roadY + 0.4f) continue;     // not at bumper height
                                if (b.Contains(origin)) continue;                                    // encloses the road itself
                                if (b.IntersectRay(new Ray(origin, dir), out float d) && d < obstacle) obstacle = d;
                            }
                        if (_surface.TryGetValue(k, out var pts))
                            foreach (var p in pts)
                            {
                                float rx = p.x - curb.x, rz = p.z - curb.z;
                                float along = rx * dir.x + rz * dir.z;
                                if (along < 0.1f || along > 1.5f || Mathf.Abs(-rx * dir.z + rz * dir.x) > 0.5f) continue;
                                float h = p.y - roadY;
                                if (h > 0.1f && h < 0.6f) heights.Add(h);
                            }
                    }
            }
            if (heights.Count >= 3)
            {
                heights.Sort();
                height = heights[heights.Count / 2];
            }
        }
    }
}
