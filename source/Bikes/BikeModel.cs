using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Bikes
{
    /// <summary>
    /// A vehicle model: a bike (.csm with `kind bike`, Assets/build_bike.py; wheels WheelF / WheelR) or a car (`kind car`,
    /// Assets/build_car.py; wheels WheelFL / FR / RL / RR). Text, Unity axes, metres, real size, origin on the ground
    /// midway between the axles, +z forward. Lines:
    /// - `name`, `tex tag file` (base-colour texture for that tag), `mat tag r g b smoothness metallic emission [alpha]`
    ///   (alpha under 1 = see-through glass: an alpha-blended URP Particles/Unlit material, a shader variant the game ships;
    ///   plain opaque Lit if that shader isn't loaded);
    /// - parts: `o part`, `p x y z` (a wheel's pivot, its triangles written around it), `m tag`, `f` / `u` triangles
    ///   (3 x position + normal [+ uv]).
    /// Body = one mesh with a submesh per tag; each wheel is its own mesh. Meshes, materials and textures are
    /// built once, shared by every bike body that uses them, and freed on unload (DestroyAll).
    /// </summary>
    internal sealed class BikeModel
    {
        public string Name;
        public Mesh Body;
        public Material[] BodyMats;
        public Vector3 PivotF, PivotR;
        public Mesh WheelF, WheelR;
        public Material[] WheelFMats, WheelRMats;
        public bool Car;
        /// <summary>Car models: FL / FR / RL / RR -> (pivot, mesh, materials).</summary>
        public readonly Dictionary<string, (Vector3 pivot, Mesh mesh, Material[] mats)> Wheels = new Dictionary<string, (Vector3, Mesh, Material[])>();

        private static readonly Dictionary<string, BikeModel> s_cache = new Dictionary<string, BikeModel>();
        private static readonly List<UnityEngine.Object> s_owned = new List<UnityEngine.Object>();
        private static Shader s_lit, s_glass;

        internal static string Folder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "Bikes");

        /// <summary>The model file (without .csm), loaded once. Null (logged) if it can't be read.</summary>
        public static BikeModel Get(string name)
        {
            if (s_cache.TryGetValue(name, out var m)) return m;
            s_cache[name] = null;
            string path = Path.Combine(Folder, name + ".csm");
            try
            {
                if (!File.Exists(path)) { Plugin.Log.LogWarning($"[Bikes] model not found: {path}"); return null; }
                m = Load(path);
                s_cache[name] = m;
                int tris = m.Body.vertexCount;
                if (m.Car) foreach (var w in m.Wheels.Values) tris += w.mesh.vertexCount; else tris += m.WheelF.vertexCount + m.WheelR.vertexCount;
                Plugin.Log.LogInfo($"[Bikes] model {m.Name} loaded ({(m.Car ? "car" : "bike")}): {tris / 3} triangles, wheelbase {m.PivotF.z - m.PivotR.z:0.00} m");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] model {name} failed to load: {e.Message}"); }
            return m;
        }

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

        private static BikeModel Load(string path)
        {
            var inv = CultureInfo.InvariantCulture;
            var model = new BikeModel { Name = Path.GetFileNameWithoutExtension(path) };
            var mats = new Dictionary<string, (Color c, float s, float m, float e)>();   // c.a = alpha
            var tex = new Dictionary<string, string>();
            var parts = new List<Part>();
            Part part = null; string tag = "paint"; bool bike = false, car = false;
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length < 2 || raw[0] == '#') continue;
                var t = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                    case "kind": bike = t.Length > 1 && t[1] == "bike"; car = t.Length > 1 && t[1] == "car"; break;
                    case "name": model.Name = t[1]; break;
                    case "tex": tex[t[1]] = t[2]; break;
                    case "mat": mats[t[1]] = (new Color(F(t, 2), F(t, 3), F(t, 4), t.Length > 8 ? Mathf.Clamp01(F(t, 8)) : 1f), F(t, 5), F(t, 6), F(t, 7)); break;
                    case "o": part = new Part { Name = t[1] }; parts.Add(part); break;
                    case "p": if (part != null) { part.HasPivot = true; part.Pivot = new Vector3(F(t, 1), F(t, 2), F(t, 3)); } break;
                    case "m": tag = t[1]; break;
                    case "f":
                    case "u":
                        if (part == null) continue;
                        if (!part.ByTag.TryGetValue(tag, out var l)) { l = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>()); part.ByTag[tag] = l; part.Order.Add(tag); }
                        int stride = t[0] == "u" ? 8 : 6;
                        for (int k = 0; k < 3; k++)
                        {
                            int b = 1 + k * stride;
                            l.v.Add(new Vector3(F(t, b), F(t, b + 1), F(t, b + 2)));
                            l.n.Add(new Vector3(F(t, b + 3), F(t, b + 4), F(t, b + 5)));
                            l.uv.Add(stride == 8 ? new Vector2(F(t, b + 6), F(t, b + 7)) : Vector2.zero);
                        }
                        break;
                }
            }
            if (!bike && !car) throw new InvalidDataException("no `kind bike` or `kind car` line");
            model.Car = car;
            var matCache = new Dictionary<string, Material>();
            foreach (var p in parts)
            {
                var (mesh, tags) = BuildMesh(model.Name + "." + p.Name, p);
                if (mesh == null) continue;
                var arr = new Material[tags.Count];
                for (int i = 0; i < tags.Count; i++)
                {
                    if (!matCache.TryGetValue(tags[i], out var mm))
                    {
                        mm = MakeMaterial(model.Name, tags[i], mats.TryGetValue(tags[i], out var d) ? d : (Color.gray, 0.4f, 0f, 0f), tex.TryGetValue(tags[i], out var f) ? f : null);
                        matCache[tags[i]] = mm;
                    }
                    arr[i] = mm;
                }
                if (p.Name == "WheelF") { model.WheelF = mesh; model.WheelFMats = arr; model.PivotF = p.Pivot; }
                else if (p.Name == "WheelR") { model.WheelR = mesh; model.WheelRMats = arr; model.PivotR = p.Pivot; }
                else if (p.HasPivot && p.Name.StartsWith("Wheel", StringComparison.Ordinal) && p.Name.Length == 7) model.Wheels[p.Name.Substring(5)] = (p.Pivot, mesh, arr);
                else if (!p.HasPivot) { model.Body = mesh; model.BodyMats = arr; }
            }
            if (car)
            {
                if (model.Body == null || model.Wheels.Count != 4) throw new InvalidDataException("expected Body and WheelFL / FR / RL / RR");
                model.PivotF = (model.Wheels["FL"].pivot + model.Wheels["FR"].pivot) * 0.5f;
                model.PivotR = (model.Wheels["RL"].pivot + model.Wheels["RR"].pivot) * 0.5f;
            }
            else if (model.Body == null || model.WheelF == null || model.WheelR == null) throw new InvalidDataException("expected Body, WheelF and WheelR");
            if (!(model.PivotF.z - model.PivotR.z > 0.5f)) throw new InvalidDataException("odd wheelbase");
            return model;
        }

        /// <summary>
        /// See-through glass: URP "Particles/Unlit" with exactly the keyword set the game's own ~90 particle materials use
        /// ({_SURFACE_TYPE_TRANSPARENT}, SrcAlpha / OneMinusSrcAlpha), so the variant is compiled in (as RogueShared.Fx).
        /// Back faces culled, so a window's outer and inner layers each tint once. Null if the shader isn't loaded.
        /// </summary>
        private static Material MakeGlass(string model, string tag, Color c)
        {
            if (s_glass == null) s_glass = Shader.Find("Universal Render Pipeline/Particles/Unlit");   // looked up again until found
            if (s_glass == null) { Plugin.Log.LogWarning($"[Bikes] no transparent shader: {model} {tag} glass drawn opaque"); return null; }
            var m = new Material(s_glass) { name = $"Bikes.{model}.{tag}", hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", 5f);    // SrcAlpha
            m.SetFloat("_DstBlend", 10f);   // OneMinusSrcAlpha
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 2f);        // back
            m.SetFloat("_ColorMode", 0f);   // tint x vertex colour (white)
            m.SetColor("_BaseColor", c);
            m.renderQueue = 3000;
            s_owned.Add(m);
            return m;
        }

        private static float F(string[] t, int i) => float.Parse(t[i], CultureInfo.InvariantCulture);

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
            var white = new Color32[verts.Count];   // white vertex colours: the glass shader multiplies its tint by them
            for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = white;
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
            if (d.c.a < 1f && texFile == null)
            {
                var g = MakeGlass(model, tag, d.c);
                if (g != null) return g;
                d.c.a = 1f;   // no transparent shader: opaque tint
            }
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Lit");
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (s_lit == null) throw new InvalidOperationException("no URP lit shader");
            var mat = new Material(s_lit) { name = $"Bikes.{model}.{tag}", hideFlags = HideFlags.DontUnloadUnusedAsset };
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
                else Plugin.Log.LogWarning($"[Bikes] texture not found: {path} (plain colour instead)");
            }
            s_owned.Add(mat);
            return mat;
        }
    }
}
