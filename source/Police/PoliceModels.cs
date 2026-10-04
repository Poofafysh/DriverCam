using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// The plugin's own police car models (Assets/models/*.pcm, built in Blender by Assets/models/police_models.py; no
    /// game or third-party meshes). Each file is parsed once and turned into shared Unity meshes (the body as one mesh
    /// with a submesh per material tag, each wheel as its own mesh around its pivot) and one shared material per tag; every
    /// patrol that uses the model shares them, so a patrol costs a few renderers, not a mesh build. Destroyed by DestroyAll.
    ///
    /// .pcm (text, Unity axes, metres, origin on the ground at the car's centre): `name`, `roof x y z`, `tex tag file`,
    /// `mat tag r g b smoothness metallic emission`, then `o part`, `p x y z` (wheel pivot, spins about local x),
    /// `m tag`, `f`/`u` triangles (3 x position + normal [+ uv]). Unity APIs used are all in dump.cs: new Mesh,
    /// vertices/normals/uv, subMeshCount, SetTriangles(int[], int), RecalculateBounds, Material(Shader), SetColor/SetFloat,
    /// EnableKeyword, Texture2D + ImageConversion.LoadImage.
    /// </summary>
    internal sealed class PoliceModel
    {
        public string Name;
        public Vector3 Roof;
        public Mesh Body;
        public Material[] BodyMats;
        public readonly List<(Vector3 pivot, Mesh mesh, Material[] mats)> Wheels = new List<(Vector3, Mesh, Material[])>();
        public Bounds Bounds;
        public float WheelRadius = 0.36f;
        // the two livery tags' materials, so a livery can swap them per patrol
        public Material PaintA, PaintB;
        public int PaintBSlot = -1;
    }

    internal static class PoliceModels
    {
        private static readonly List<PoliceModel> s_models = new List<PoliceModel>();
        private static readonly List<UnityEngine.Object> s_owned = new List<UnityEngine.Object>();
        private static bool s_loaded;
        private static Material s_interceptorPaint;

        public static int Count => s_models.Count;

        private static string Folder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "Police");

        /// <summary>Loads every .pcm once (on the main thread, first time a patrol needs one). Returns how many loaded.</summary>
        public static int EnsureLoaded()
        {
            if (s_loaded) return s_models.Count;
            s_loaded = true;
            if (!Directory.Exists(Folder)) { Plugin.Log.LogWarning($"[Police] no police car models folder ({Folder}): boss-car looks instead"); return 0; }
            foreach (var path in Directory.GetFiles(Folder, "*.pcm"))
            {
                try
                {
                    var m = Load(path);
                    if (m != null) s_models.Add(m);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Police] police car model {Path.GetFileName(path)} failed to load: {e.Message}"); }
            }
            Plugin.Log.LogInfo($"[Police] {s_models.Count} police car models loaded ({string.Join(", ", s_models.ConvertAll(x => x.Name))}) from {Folder}");
            return s_models.Count;
        }

        public static PoliceModel Get(int i) => s_models.Count == 0 ? null : s_models[((i % s_models.Count) + s_models.Count) % s_models.Count];

        /// <summary>The paint material for the 'Interceptor' livery (all black), shared.</summary>
        public static Material InterceptorPaint(PoliceModel m)
        {
            if (s_interceptorPaint == null && m.PaintA != null)
            {
                s_interceptorPaint = new Material(m.PaintA) { name = "Police.Paint.Interceptor", hideFlags = HideFlags.DontUnloadUnusedAsset };
                s_owned.Add(s_interceptorPaint);
            }
            return s_interceptorPaint;
        }

        public static void DestroyAll()
        {
            foreach (var o in s_owned) RogueShared.Fx.Kill(o);
            s_owned.Clear(); s_models.Clear(); s_loaded = false; s_interceptorPaint = null;
        }

        private sealed class PartData
        {
            public string Name;
            public bool HasPivot;
            public Vector3 Pivot;
            public readonly Dictionary<string, (List<Vector3> v, List<Vector3> n, List<Vector2> uv)> ByTag = new Dictionary<string, (List<Vector3>, List<Vector3>, List<Vector2>)>();
            public readonly List<string> Order = new List<string>();
        }

        private static PoliceModel Load(string path)
        {
            var inv = CultureInfo.InvariantCulture;
            var model = new PoliceModel { Name = Path.GetFileNameWithoutExtension(path) };
            var mats = new Dictionary<string, (Color c, float s, float m, float e)>();
            var tex = new Dictionary<string, string>();
            var parts = new List<PartData>();
            PartData part = null; string tag = "paint_a";
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length < 2 || raw[0] == '#') continue;
                var t = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                float F(int i) => float.Parse(t[i], inv);
                switch (t[0])
                {
                    case "name": model.Name = t[1]; break;
                    case "roof": model.Roof = new Vector3(F(1), F(2), F(3)); break;
                    case "tex": tex[t[1]] = t[2]; break;
                    case "mat": mats[t[1]] = (new Color(F(2), F(3), F(4)), F(5), F(6), F(7)); break;
                    case "o": part = new PartData { Name = t[1] }; parts.Add(part); break;
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
                mm = MakeMaterial(model.Name, tg, mats.TryGetValue(tg, out var d) ? d : (Color.gray, 0.3f, 0f, 0f), tex.TryGetValue(tg, out var file) ? file : null);
                matCache[tg] = mm;
                return mm;
            }

            bool haveBounds = false;
            foreach (var p in parts)
            {
                var (mesh, tags) = BuildMesh(model.Name + "." + p.Name, p);
                if (mesh == null) continue;
                var ms = new Material[tags.Count];
                for (int i = 0; i < tags.Count; i++) ms[i] = Mat(tags[i]);
                // bounds in model space
                var b = mesh.bounds; if (p.HasPivot) b.center += p.Pivot;
                if (!haveBounds) { model.Bounds = b; haveBounds = true; } else model.Bounds.Encapsulate(b);
                if (p.HasPivot)
                {
                    model.Wheels.Add((p.Pivot, mesh, ms));
                    model.WheelRadius = Mathf.Max(0.2f, mesh.bounds.extents.y);
                }
                else if (model.Body == null)
                {
                    model.Body = mesh; model.BodyMats = ms;
                    model.PaintBSlot = tags.IndexOf("paint_b");
                }
            }
            if (model.Body == null) throw new InvalidDataException("no body part");
            matCache.TryGetValue("paint_a", out model.PaintA);
            matCache.TryGetValue("paint_b", out model.PaintB);
            return model;
        }

        /// <summary>One mesh for a part: a submesh per tag, positions relative to the pivot (wheels) or the model origin.</summary>
        private static (Mesh, List<string>) BuildMesh(string name, PartData p)
        {
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
            var subs = new List<int[]>(); var tags = new List<string>();
            foreach (var tg in p.Order)
            {
                var l = p.ByTag[tg];
                var idx = new int[l.v.Count];
                // .pcm wheel triangles are already written around their pivot, body triangles around the model origin
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
            mesh.UploadMeshData(true);   // GPU only from here: no CPU copy kept
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;   // shared by patrols across scenes; freed by DestroyAll
            s_owned.Add(mesh);
            return (mesh, tags);
        }

        private static Shader s_lit;

        private static Material MakeMaterial(string model, string tag, (Color c, float s, float m, float e) d, string texFile)
        {
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Lit");
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Simple Lit");   // Unity null: == only
            if (s_lit == null) throw new InvalidOperationException("no URP lit shader");
            var mat = new Material(s_lit) { name = $"Police.{model}.{tag}" };
            mat.SetColor("_BaseColor", d.c);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", d.s);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", d.m);
            if (d.e > 0f && mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", d.c * d.e);
            }
            if (texFile != null)
            {
                var path = Path.Combine(Folder, texFile);
                if (File.Exists(path))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = texFile };
                    if (ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
                    {
                        tex.wrapMode = TextureWrapMode.Clamp; tex.anisoLevel = 4;
                        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        s_owned.Add(tex);
                        mat.SetTexture("_BaseMap", tex);
                        // decal: transparent pixels are cut out (same alpha-clip setup DriverCam uses for its cockpit shell)
                        if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                        if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
                        mat.EnableKeyword("_ALPHATEST_ON");
                        mat.renderQueue = 2450;
                    }
                    else RogueShared.Fx.Kill(tex);
                }
            }
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_owned.Add(mat);
            return mat;
        }
    }
}
