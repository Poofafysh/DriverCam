using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace CarSkins
{
    /// <summary>
    /// A replacement car model (.csm, built in Blender by Assets/build_e46.py): text, Unity axes, metres, origin on the
    /// ground. `name`, `tex tag file` (opaque base-colour texture for that tag), `mat tag r g b smoothness metallic emission`,
    /// then parts: `o part`, `p x y z` (wheel pivot; its triangles are written around it), `m tag`, `f` / `u` triangles
    /// (3 x position + normal [+ uv]). Body and glass are one mesh with a submesh per tag; each wheel (WheelFL / FR / RL /
    /// RR) is its own mesh. Meshes and materials are built once, shared by every use, and freed on unload (DestroyAll).
    /// Unity calls (all in dump.cs): new Mesh, vertices/normals/uv, subMeshCount, SetTriangles(int[], int),
    /// RecalculateBounds, UploadMeshData, Material(Shader), SetColor/SetFloat/SetTexture, Texture2D +
    /// ImageConversion.LoadImage.
    /// </summary>
    internal sealed class CarModel
    {
        public string Name;
        public Mesh Body;
        public Material[] BodyMats;
        public readonly Dictionary<string, (Vector3 pivot, Mesh mesh, Material[] mats)> Wheels = new Dictionary<string, (Vector3, Mesh, Material[])>();
        public Bounds Bounds;   // body + wheels, model space

        private static readonly Dictionary<string, CarModel> s_cache = new Dictionary<string, CarModel>();
        private static readonly List<UnityEngine.Object> s_owned = new List<UnityEngine.Object>();
        private static Shader s_lit;

        private static string Folder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "CarSkins");

        /// <summary>The model file name (without .csm), loaded once. Null (logged) if it can't be read.</summary>
        public static CarModel Get(string name)
        {
            if (s_cache.TryGetValue(name, out var m)) return m;
            s_cache[name] = null;
            string path = Path.Combine(Folder, name + ".csm");
            try
            {
                if (!File.Exists(path)) { Plugin.Log.LogWarning($"[CarSkins] model not found: {path}"); return null; }
                m = Load(path);
                s_cache[name] = m;
                Plugin.Log.LogInfo($"[CarSkins] model {m.Name} loaded: {m.Body.vertexCount / 3 + Count(m)} triangles, {m.Wheels.Count} wheels, {m.Bounds.size.x:0.00} x {m.Bounds.size.y:0.00} x {m.Bounds.size.z:0.00} m");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CarSkins] model {name} failed to load: {e.Message}"); }
            return m;
        }

        private static int Count(CarModel m) { int n = 0; foreach (var w in m.Wheels.Values) n += w.mesh.vertexCount / 3; return n; }

        public static void DestroyAll()
        {
            foreach (var o in s_owned) { try { if (o != null) UnityEngine.Object.Destroy(o); } catch { /* shutting down */ } }
            s_owned.Clear(); s_cache.Clear();
        }

        private sealed class Part
        {
            public string Name;
            public bool HasPivot;
            public Vector3 Pivot;
            public readonly Dictionary<string, (List<Vector3> v, List<Vector3> n, List<Vector2> uv)> ByTag = new Dictionary<string, (List<Vector3>, List<Vector3>, List<Vector2>)>();
            public readonly List<string> Order = new List<string>();
        }

        private static CarModel Load(string path)
        {
            var inv = CultureInfo.InvariantCulture;
            var model = new CarModel { Name = Path.GetFileNameWithoutExtension(path) };
            var mats = new Dictionary<string, (Color c, float s, float m, float e)>();
            var tex = new Dictionary<string, string>();
            var parts = new List<Part>();
            Part part = null; string tag = "paint";
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length < 2 || raw[0] == '#') continue;
                var t = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                float F(int i) => float.Parse(t[i], inv);
                switch (t[0])
                {
                    case "name": model.Name = t[1]; break;
                    case "tex": tex[t[1]] = t[2]; break;
                    case "mat": mats[t[1]] = (new Color(F(2), F(3), F(4)), F(5), F(6), F(7)); break;
                    case "o": part = new Part { Name = t[1] }; parts.Add(part); break;
                    case "p": part.HasPivot = true; part.Pivot = new Vector3(F(1), F(2), F(3)); break;
                    case "m": tag = t[1]; break;
                    case "f":
                    case "u":
                        if (part == null) continue;
                        if (!part.ByTag.TryGetValue(tag, out var l)) { l = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>()); part.ByTag[tag] = l; part.Order.Add(tag); }
                        int stride = t[0] == "u" ? 8 : 6;
                        for (int k = 0; k < 3; k++)
                        {
                            int b = 1 + k * stride;
                            l.v.Add(new Vector3(F(b), F(b + 1), F(b + 2)));
                            l.n.Add(new Vector3(F(b + 3), F(b + 4), F(b + 5)));
                            l.uv.Add(stride == 8 ? new Vector2(F(b + 6), F(b + 7)) : Vector2.zero);
                        }
                        break;
                }
            }
            var matCache = new Dictionary<string, Material>();
            Material Mat(string tg)
            {
                if (matCache.TryGetValue(tg, out var mm)) return mm;
                mm = MakeMaterial(model.Name, tg, mats.TryGetValue(tg, out var d) ? d : (Color.gray, 0.4f, 0f, 0f), tex.TryGetValue(tg, out var f) ? f : null);
                matCache[tg] = mm;
                return mm;
            }
            // body and glass: one mesh, a submesh per tag
            var bodyParts = parts.FindAll(p => !p.HasPivot);
            var merged = new Part { Name = "Body" };
            foreach (var p in bodyParts)
                foreach (var tg in p.Order)
                {
                    if (!merged.ByTag.TryGetValue(tg, out var dst)) { dst = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>()); merged.ByTag[tg] = dst; merged.Order.Add(tg); }
                    var src = p.ByTag[tg];
                    dst.v.AddRange(src.v); dst.n.AddRange(src.n); dst.uv.AddRange(src.uv);
                }
            var (bodyMesh, bodyTags) = BuildMesh(model.Name + ".Body", merged);
            if (bodyMesh == null) throw new InvalidDataException("no body");
            model.Body = bodyMesh;
            model.BodyMats = bodyTags.ConvertAll(Mat).ToArray();
            model.Bounds = bodyMesh.bounds;
            foreach (var p in parts)
            {
                if (!p.HasPivot) continue;
                var (mesh, tags) = BuildMesh(model.Name + "." + p.Name, p);
                if (mesh == null) continue;
                string key = p.Name.StartsWith("Wheel", StringComparison.Ordinal) ? p.Name.Substring(5) : p.Name;   // FL / FR / RL / RR
                model.Wheels[key] = (p.Pivot, mesh, tags.ConvertAll(Mat).ToArray());
                var b = mesh.bounds; b.center += p.Pivot; model.Bounds.Encapsulate(b);
            }
            if (model.Wheels.Count != 4) throw new InvalidDataException($"expected 4 wheels (FL, FR, RL, RR), found {model.Wheels.Count}");
            return model;
        }

        private static (Mesh, List<string>) BuildMesh(string name, Part p)
        {
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
            var subs = new List<int[]>(); var tags = new List<string>();
            foreach (var tg in p.Order)
            {
                var l = p.ByTag[tg];
                var idx = new int[l.v.Count];
                for (int i = 0; i < l.v.Count; i++) { idx[i] = verts.Count; verts.Add(l.v[i]); norms.Add(l.n[i]); uvs.Add(l.uv[i]); }
                subs.Add(idx); tags.Add(tg);
            }
            if (verts.Count == 0) return (null, tags);
            var mesh = new Mesh { name = name };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts.ToArray();
            mesh.normals = norms.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) mesh.SetTriangles(subs[i], i);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_owned.Add(mesh);
            return (mesh, tags);
        }

        private static Material MakeMaterial(string model, string tag, (Color c, float s, float m, float e) d, string texFile)
        {
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Lit");
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (s_lit == null) throw new InvalidOperationException("no URP lit shader");
            var mat = new Material(s_lit) { name = $"CarSkins.{model}.{tag}", hideFlags = HideFlags.DontUnloadUnusedAsset };
            mat.SetColor("_BaseColor", d.c);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", d.s);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", d.m);
            if (texFile != null)
            {
                string path = Path.Combine(Folder, texFile);
                if (File.Exists(path))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = texFile, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    if (ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) { tex.anisoLevel = 4; s_owned.Add(tex); mat.SetTexture("_BaseMap", tex); }
                    else UnityEngine.Object.Destroy(tex);
                }
                else Plugin.Log.LogWarning($"[CarSkins] texture not found: {path} (plain colour instead)");
            }
            s_owned.Add(mat);
            return mat;
        }
    }
}
