using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// The plugin's own police car models (Assets/models/*.pcm, built in Blender by Assets/models/police_models.py; no
    /// game or third-party meshes). Each file is parsed once (on the main thread, at race start) into shared Unity meshes
    /// and materials that every patrol using the model shares, so a patrol costs a few renderers, not a mesh build.
    /// Destroyed by DestroyAll.
    ///
    /// Draw calls (0.6.0 perf pass): every opaque material tag (paint, glass, trim, lights, tyres, rims, ...) is drawn
    /// with ONE palette material: a small texture with a 4x4 texel block per tag for the colour, one for metallic (R) /
    /// smoothness (A) and one for emission, and every vertex's uv points at the centre of its tag's block (a face's uv is
    /// constant, so the GPU always samples mip 0 of that block: exactly the tag's values). The body is one opaque mesh
    /// (1 draw, casts shadows), the door decals a second mesh with their own alpha-clipped material (1 draw, no shadow:
    /// it lies 6 mm off the door, which already casts one), each wheel one mesh (1 draw, no shadow: inside the arch and
    /// under the body's shadow). So a patrol is 6 draws per camera pass and 1 per shadow cascade (it was 22 and 22).
    /// Shader variant: URP Lit with _EMISSION + _METALLICSPECGLOSSMAP + _NORMALMAP (the decal also _ALPHATEST_ON): both
    /// combinations are compiled into the game's URP Lit (every copy, ForwardLit and GBuffer passes, checked in the
    /// game's shader data), so they never fall back to another variant. The normal map is the shader's default flat one;
    /// the meshes carry tangents so _NORMALMAP gives exactly the vertex normal.
    /// Colours: the .pcm values are the same sRGB-space values the per-tag materials took through Material.SetColor;
    /// the textures are sRGB, so they sample to the same linear values (metallic, a linear quantity, is stored
    /// gamma-encoded in R for that reason when the project is in linear colour space). Emission: one _EmissionColor
    /// (the strongest tag's), each tag's emission map texel scaled so texel x colour = its old c x e.
    /// Livery: Classic = the palette as modelled; Interceptor = a second palette whose paint_b block is paint_a's.
    ///
    /// .pcm (text, Unity axes, metres, origin on the ground at the car's centre): `name`, `roof x y z`, `tex tag file`,
    /// `mat tag r g b smoothness metallic emission`, then `o part`, `p x y z` (wheel pivot, spins about local x),
    /// `v x y z nx ny nz [u v]` (the part's welded vertices), `m tag`, `t a b c` (triangles indexing the part's v list).
    /// The older unwelded `f`/`u` triangles (3 x position + normal [+ uv]) still load.
    /// Unity APIs used are all in dump.cs: new Mesh, vertices/normals/uv/tangents, SetTriangles(int[], int),
    /// RecalculateBounds, Material(Shader), SetColor/SetFloat/SetTexture, EnableKeyword, Texture2D(w, h, format, mips)
    /// + SetPixels32 + Apply, ImageConversion.LoadImage, QualitySettings.activeColorSpace.
    /// </summary>
    internal sealed class PoliceModel
    {
        public string Name;
        public Vector3 Roof;
        public Mesh Body;                                   // every opaque tag, one submesh (palette material)
        public readonly List<(Mesh mesh, Material mat)> Decals = new List<(Mesh, Material)>();   // textured tags (alpha clip)
        public readonly List<(Vector3 pivot, Mesh mesh)> Wheels = new List<(Vector3, Mesh)>();   // palette material
        public Bounds Bounds;
        public float WheelRadius = 0.36f;
        public int Triangles, Vertices;
        internal Material Classic;                          // the palette as modelled
        internal Material Interceptor;                      // paint_b drawn as paint_a (made on first use)
        internal Func<Material> MakeInterceptor;

        /// <summary>The palette material for a livery: Interceptor = the white panels black too; anything else = as modelled.</summary>
        public Material Paint(string livery)
        {
            if (!string.Equals(livery, "Interceptor", StringComparison.OrdinalIgnoreCase)) return Classic;
            if (Interceptor == null && MakeInterceptor != null) { Interceptor = MakeInterceptor(); MakeInterceptor = null; }
            return Interceptor != null ? Interceptor : Classic;
        }
    }

    internal static class PoliceModels
    {
        private const int Block = 4;   // texels per palette block side

        private static readonly List<PoliceModel> s_models = new List<PoliceModel>();
        private static readonly List<UnityEngine.Object> s_owned = new List<UnityEngine.Object>();
        private static readonly Dictionary<string, Material> s_palettes = new Dictionary<string, Material>();   // same palette + livery = one material
        private static readonly Dictionary<string, Material> s_decals = new Dictionary<string, Material>();     // same file + values = one material
        private static readonly Dictionary<string, Texture2D> s_textures = new Dictionary<string, Texture2D>(); // each decal png loaded once
        private static bool s_loaded;

        public static int Count => s_models.Count;

        private static string Folder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "Police");

        /// <summary>Loads every .pcm once (on the main thread, first time a patrol needs one). Returns how many loaded.</summary>
        public static int EnsureLoaded()
        {
            if (s_loaded) return s_models.Count;
            s_loaded = true;
            if (!Directory.Exists(Folder)) { Plugin.Log.LogWarning($"[Police] no police car models folder ({Folder}): boss-car looks instead"); return 0; }
            var t0 = DateTime.UtcNow;
            foreach (var path in Directory.GetFiles(Folder, "*.pcm"))
            {
                try
                {
                    var m = Load(path);
                    if (m != null) s_models.Add(m);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Police] police car model {Path.GetFileName(path)} failed to load: {e.Message}"); }
            }
            Plugin.Log.LogInfo($"[Police] {s_models.Count} police car models loaded ({string.Join(", ", s_models.ConvertAll(x => $"{x.Name} {x.Triangles} tris / {x.Vertices} verts"))}) " +
                               $"in {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms from {Folder}");
            return s_models.Count;
        }

        public static PoliceModel Get(int i) => s_models.Count == 0 ? null : s_models[((i % s_models.Count) + s_models.Count) % s_models.Count];

        public static void DestroyAll()
        {
            foreach (var o in s_owned) RogueShared.Fx.Kill(o);
            s_owned.Clear(); s_models.Clear(); s_palettes.Clear(); s_decals.Clear(); s_textures.Clear(); s_loaded = false;
        }

        // ------------------------------------------------------------------ parsing

        private sealed class PartData
        {
            public string Name;
            public bool HasPivot;
            public Vector3 Pivot;
            public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly Dictionary<string, List<int>> ByTag = new Dictionary<string, List<int>>();
            public readonly List<string> Order = new List<string>();

            public List<int> Tris(string tag)
            {
                if (!ByTag.TryGetValue(tag, out var l)) { l = new List<int>(); ByTag[tag] = l; Order.Add(tag); }
                return l;
            }
        }

        private struct MatData { public Color C; public float S, M, E; }

        private static PoliceModel Load(string path)
        {
            var inv = CultureInfo.InvariantCulture;
            var model = new PoliceModel { Name = Path.GetFileNameWithoutExtension(path) };
            var mats = new Dictionary<string, MatData>();
            var matOrder = new List<string>();
            var tex = new Dictionary<string, string>();
            var parts = new List<PartData>();
            PartData part = null; string tag = "paint_a";
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length < 2 || raw[0] == '#') continue;
                var t = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                    case "name": model.Name = t[1]; break;
                    case "roof": model.Roof = new Vector3(F(t, 1), F(t, 2), F(t, 3)); break;
                    case "tex": tex[t[1]] = t[2]; break;
                    case "mat":
                        if (!mats.ContainsKey(t[1])) matOrder.Add(t[1]);
                        mats[t[1]] = new MatData { C = new Color(F(t, 2), F(t, 3), F(t, 4)), S = F(t, 5), M = F(t, 6), E = F(t, 7) };
                        break;
                    case "o": part = new PartData { Name = t[1] }; parts.Add(part); break;
                    case "p": part.HasPivot = true; part.Pivot = new Vector3(F(t, 1), F(t, 2), F(t, 3)); break;
                    case "m": tag = t[1]; break;
                    case "v":
                        if (part == null) continue;
                        part.V.Add(new Vector3(F(t, 1), F(t, 2), F(t, 3)));
                        part.N.Add(new Vector3(F(t, 4), F(t, 5), F(t, 6)));
                        part.Uv.Add(t.Length >= 9 ? new Vector2(F(t, 7), F(t, 8)) : Vector2.zero);
                        break;
                    case "t":
                    {
                        if (part == null) continue;
                        int a = int.Parse(t[1], inv), b = int.Parse(t[2], inv), c = int.Parse(t[3], inv);
                        int n = part.V.Count;
                        if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) throw new InvalidDataException($"triangle index out of range in part {part.Name}");
                        var l = part.Tris(tag); l.Add(a); l.Add(b); l.Add(c);
                        break;
                    }
                    case "f":
                    case "u":
                    {
                        // older unwelded format: three corners written out in full
                        if (part == null) continue;
                        var l = part.Tris(tag);
                        int stride = t[0] == "u" ? 8 : 6;
                        for (int k = 0; k < 3; k++)
                        {
                            int b = 1 + k * stride;
                            l.Add(part.V.Count);
                            part.V.Add(new Vector3(F(t, b), F(t, b + 1), F(t, b + 2)));
                            part.N.Add(new Vector3(F(t, b + 3), F(t, b + 4), F(t, b + 5)));
                            part.Uv.Add(stride == 8 ? new Vector2(F(t, b + 6), F(t, b + 7)) : Vector2.zero);
                        }
                        break;
                    }
                }
            }

            // the palette: every mat tag in file order, then any tag used without a mat line (grey, as before)
            foreach (var p in parts)
                foreach (var tg in p.Order)
                    if (!tex.ContainsKey(tg) && !mats.ContainsKey(tg)) { mats[tg] = new MatData { C = Color.gray, S = 0.3f }; matOrder.Add(tg); }
            var slot = new Dictionary<string, int>();
            for (int i = 0; i < matOrder.Count; i++) slot[matOrder[i]] = i;
            int width = matOrder.Count * Block;

            model.Classic = Palette(matOrder, mats, null);
            if (mats.ContainsKey("paint_a") && mats.ContainsKey("paint_b"))
            {
                var order = matOrder; var src = mats;
                model.MakeInterceptor = () =>
                {
                    var swapped = new Dictionary<string, MatData>(src);
                    swapped["paint_b"] = src["paint_a"];
                    return Palette(order, swapped, "Interceptor");
                };
            }

            bool haveBounds = false;
            void Grow(Bounds b) { if (!haveBounds) { model.Bounds = b; haveBounds = true; } else model.Bounds.Encapsulate(b); }
            foreach (var p in parts)
            {
                // opaque tags into one mesh (uv -> the tag's palette block); each textured tag into its own decal mesh
                var opaque = new List<string>();
                foreach (var tg in p.Order) if (!tex.ContainsKey(tg) || p.HasPivot) opaque.Add(tg);
                var mesh = BuildMesh(model.Name + "." + p.Name, p, opaque, slot, width);
                if (mesh != null)
                {
                    var b = mesh.bounds; if (p.HasPivot) b.center += p.Pivot;
                    Grow(b);
                    model.Triangles += Tris(p, opaque); model.Vertices += mesh.vertexCount;
                    if (p.HasPivot)
                    {
                        model.Wheels.Add((p.Pivot, mesh));
                        model.WheelRadius = Mathf.Max(0.2f, mesh.bounds.extents.y);
                    }
                    else if (model.Body == null) model.Body = mesh;
                }
                if (p.HasPivot) continue;
                foreach (var tg in p.Order)
                {
                    if (!tex.TryGetValue(tg, out var file)) continue;
                    var one = new List<string> { tg };
                    var dm = BuildMesh(model.Name + "." + p.Name + "." + tg, p, one, null, 0);
                    if (dm == null) continue;
                    var mat = DecalMaterial(file, mats.TryGetValue(tg, out var d) ? d : new MatData { C = Color.white, S = 0.5f });
                    if (mat == null) { Plugin.Log.LogWarning($"[Police] {model.Name}: decal texture {file} missing: decals left out"); continue; }
                    Grow(dm.bounds);
                    model.Triangles += Tris(p, one); model.Vertices += dm.vertexCount;
                    model.Decals.Add((dm, mat));
                }
            }
            if (model.Body == null) throw new InvalidDataException("no body part");
            return model;
        }

        private static float F(string[] t, int i) => float.Parse(t[i], CultureInfo.InvariantCulture);

        private static int Tris(PartData p, List<string> tags)
        {
            int n = 0;
            foreach (var tg in tags) n += p.ByTag[tg].Count / 3;
            return n;
        }

        /// <summary>
        /// One single-submesh mesh from the given tags of a part (positions relative to the pivot for wheels, the model
        /// origin otherwise). slot != null: each vertex's uv is set to the centre of its tag's palette block (a vertex
        /// used by two tags is copied). Tangents are written (perpendicular to the normal) for the _NORMALMAP variant.
        /// </summary>
        private static Mesh BuildMesh(string name, PartData p, List<string> tags, Dictionary<string, int> slot, int paletteWidth)
        {
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
            var idx = new List<int>();
            var map = new Dictionary<int, int>();
            foreach (var tg in tags)
            {
                map.Clear();
                bool palette = slot != null && slot.ContainsKey(tg);
                var uvTag = palette ? new Vector2((slot[tg] * Block + Block * 0.5f) / paletteWidth, 0.5f) : Vector2.zero;
                foreach (int old in p.ByTag[tg])
                {
                    if (!map.TryGetValue(old, out int ni))
                    {
                        ni = verts.Count; map[old] = ni;
                        verts.Add(p.V[old]); norms.Add(p.N[old]); uvs.Add(palette ? uvTag : p.Uv[old]);
                    }
                    idx.Add(ni);
                }
            }
            if (verts.Count == 0 || idx.Count == 0) return null;
            var tangents = new Vector4[norms.Count];
            for (int i = 0; i < norms.Count; i++)
            {
                var n = norms[i];
                var tn = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right);
                float len = tn.magnitude;
                tangents[i] = len > 1e-6f ? new Vector4(tn.x / len, tn.y / len, tn.z / len, 1f) : new Vector4(1f, 0f, 0f, 1f);
            }
            var mesh = new Mesh { name = name };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts.ToArray();
            mesh.normals = norms.ToArray();
            mesh.tangents = tangents;
            mesh.uv = uvs.ToArray();
            mesh.subMeshCount = 1;
            mesh.SetTriangles(idx.ToArray(), 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);   // GPU only from here: no CPU copy kept
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;   // shared by patrols across scenes; freed by DestroyAll
            s_owned.Add(mesh);
            return mesh;
        }

        // ------------------------------------------------------------------ materials

        private static Shader s_lit;
        private static bool s_linearChecked, s_linear = true;

        private static Shader Lit()
        {
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Lit");
            if (s_lit == null) s_lit = Shader.Find("Universal Render Pipeline/Simple Lit");   // Unity null: == only
            if (s_lit == null) throw new InvalidOperationException("no URP lit shader");
            return s_lit;
        }

        /// <summary>Linear colour space (URP's default): sRGB textures are decoded and SetColor converts. Read once.</summary>
        private static bool Linear
        {
            get
            {
                if (!s_linearChecked)
                {
                    s_linearChecked = true;
                    try { s_linear = QualitySettings.activeColorSpace == ColorSpace.Linear; }
                    catch { s_linear = true; }
                }
                return s_linear;
            }
        }

        // Unity's exact gamma <-> linear conversion (what Color.linear / SetColor use; pow 2.2 above 1)
        private static float ToLinear(float v) => v <= 0.04045f ? v / 12.92f : v < 1f ? Mathf.Pow((v + 0.055f) / 1.055f, 2.4f) : Mathf.Pow(v, 2.2f);
        private static float ToGamma(float v) => v <= 0.0031308f ? v * 12.92f : v < 1f ? 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f : Mathf.Pow(v, 1f / 2.2f);
        private static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static Material Palette(List<string> order, Dictionary<string, MatData> mats, string livery)
        {
            var key = new System.Text.StringBuilder(livery ?? "Classic");
            foreach (var tg in order) { var d = mats[tg]; key.Append('|').Append(tg).Append(d.C.r).Append(',').Append(d.C.g).Append(',').Append(d.C.b).Append(',').Append(d.S).Append(',').Append(d.M).Append(',').Append(d.E); }
            if (s_palettes.TryGetValue(key.ToString(), out var cached) && cached != null) return cached;

            int w = order.Count * Block, h = Block;
            var albedo = new Color32[w * h]; var metal = new Color32[w * h]; var emit = new Color32[w * h];
            float eMax = 0f;
            foreach (var tg in order) eMax = Mathf.Max(eMax, mats[tg].E);
            bool lin = Linear;
            for (int i = 0; i < order.Count; i++)
            {
                var d = mats[order[i]];
                var a = new Color32(B(d.C.r), B(d.C.g), B(d.C.b), 255);
                var m = new Color32(B(lin ? ToGamma(Mathf.Clamp01(d.M)) : d.M), 0, 0, B(d.S));
                Color32 e = new Color32(0, 0, 0, 255);
                if (d.E > 0f && eMax > 0f)
                {
                    // texel x _EmissionColor (eMax) = c x e, in the space the shader adds them up in
                    float Ch(float c) => lin ? ToGamma(Mathf.Clamp01(ToLinear(c * d.E) / ToLinear(eMax))) : Mathf.Clamp01(c * d.E / eMax);
                    e = new Color32(B(Ch(d.C.r)), B(Ch(d.C.g)), B(Ch(d.C.b)), 255);
                }
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < Block; x++)
                    {
                        int k = y * w + i * Block + x;
                        albedo[k] = a; metal[k] = m; emit[k] = e;
                    }
            }
            string name = "Police.Palette." + (livery ?? "Classic");
            var mat = new Material(Lit()) { name = name };
            mat.SetColor("_BaseColor", Color.white);
            mat.SetTexture("_BaseMap", Tex(name + ".Albedo", w, h, albedo));
            if (mat.HasProperty("_MetallicGlossMap")) mat.SetTexture("_MetallicGlossMap", Tex(name + ".Metal", w, h, metal));
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f);   // the map's alpha x this
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);       // unused with the map
            if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", Tex(name + ".Emission", w, h, emit));
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", new Color(eMax, eMax, eMax, 1f));
            Keywords(mat);
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_owned.Add(mat);
            s_palettes[key.ToString()] = mat;
            return mat;
        }

        /// <summary>The variant every copy of the game's URP Lit has compiled (see the class summary).</summary>
        private static void Keywords(Material mat)
        {
            mat.EnableKeyword("_EMISSION");
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.EnableKeyword("_NORMALMAP");   // _BumpMap left at the shader's default flat normal map
        }

        private static Texture2D Tex(string name, int w, int h, Color32[] px)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
            t.SetPixels32(px);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.Apply(false, true);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_owned.Add(t);
            return t;
        }

        /// <summary>The door decal: its texture (loaded once per file), alpha-clipped; metallic / smoothness / colour from its mat line.</summary>
        private static Material DecalMaterial(string file, MatData d)
        {
            string key = $"{file}|{d.C.r},{d.C.g},{d.C.b},{d.S},{d.M}";
            if (s_decals.TryGetValue(key, out var cached) && cached != null) return cached;
            if (!s_textures.TryGetValue(file, out var tex) || tex == null)
            {
                var path = Path.Combine(Folder, file);
                if (!File.Exists(path)) return null;
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = file };
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) { RogueShared.Fx.Kill(tex); return null; }
                tex.wrapMode = TextureWrapMode.Clamp; tex.anisoLevel = 4;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                s_owned.Add(tex);
                s_textures[file] = tex;
            }
            var mat = new Material(Lit()) { name = "Police.Decal." + Path.GetFileNameWithoutExtension(file) };
            mat.SetColor("_BaseColor", d.C);
            mat.SetTexture("_BaseMap", tex);
            // transparent pixels are cut out (same alpha-clip setup DriverCam uses for its cockpit shell)
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = 2450;
            // metallic from a 1x1 map, smoothness = its alpha (1) x _Smoothness; no emission (black): the compiled variant
            var px = new[] { new Color32(B(Linear ? ToGamma(Mathf.Clamp01(d.M)) : d.M), 0, 0, 255) };
            if (mat.HasProperty("_MetallicGlossMap")) mat.SetTexture("_MetallicGlossMap", Tex(mat.name + ".Metal", 1, 1, px));
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", d.S);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
            Keywords(mat);
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_owned.Add(mat);
            s_decals[key] = mat;
            return mat;
        }
    }
}
