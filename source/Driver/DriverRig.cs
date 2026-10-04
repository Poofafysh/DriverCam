using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using FM = RogueShared.FastMath;

namespace Driver
{
    /// <summary>
    /// The driver's Unity objects. Assets (meshes, materials, textures) are made once and kept until the plugin is
    /// switched off; the scene objects (Driver_Root, 55 bone transforms, the body renderer, the helmet renderer under the
    /// head bone) are rebuilt when a scene change destroys them.
    ///
    /// - Body: one SkinnedMeshRenderer (Bone4, no motion vectors, not updated offscreen, hand-set bounds). Two meshes share
    ///   the vertex data: the full body and a head-less one for the driver view (every triangle touching a MASK bit0
    ///   vertex, neck and collar, left out; the torso has a neck plug).
    /// - CPU fallback: when the skinning self-test fails, a MeshFilter + MeshRenderer whose vertices are skinned in managed
    ///   code every frame (only while visible).
    /// - Helmet + visor: one MeshRenderer under the head bone (bone-local vertices, never skinned).
    /// - Materials: URP Lit (_BaseMap, _BaseColor, _Smoothness, _Metallic, _EMISSION + _EmissionMap + _EmissionColor) with
    ///   the car's material as a fallback shader; plus a copy of the car's own Rogue_Outline material as the outline pass.
    /// </summary>
    internal sealed class DriverRig
    {
        private readonly RigFile _f;
        private readonly Solver _s;
        private readonly string _folder;

        // assets (kept across scenes)
        private readonly List<UnityEngine.Object> _assets = new List<UnityEngine.Object>();
        private Mesh _full, _headless, _helmetMesh, _cpuMesh;
        private Material _suit, _helmetMat, _visor, _outline;
        private Material[] _bodyMats, _helmetMats;
        private Vector3[] _cpuV, _cpuN;        // managed results
        private Il2CppStructArray<Vector3> _cpuVArr, _cpuNArr;
        private int[] _fullTris, _headTris;

        // scene objects
        public GameObject Root;
        public Transform RootT;
        public Transform[] Bones;
        private GameObject _bodyGo, _helmetGo;
        private SkinnedMeshRenderer _smr;
        private MeshFilter _cpuFilter;
        private MeshRenderer _cpuRenderer, _helmet;
        public bool Cpu { get; private set; }

        private int _visible = -1, _driverView = -1;

        public DriverRig(RigFile f, Solver s, string folder) { _f = f; _s = s; _folder = folder; }

        public bool Alive => Root != null && !Root.WasCollected && RootT != null && _helmet != null && (Cpu ? _cpuRenderer != null : _smr != null);

        // ------------------------------------------------------------------------------------------ assets
        private T Own<T>(T o) where T : UnityEngine.Object
        {
            o.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _assets.Add(o);
            return o;
        }

        private void EnsureAssets(Material outlineTemplate, Material carMaterial)
        {
            if (_full != null && !_full.WasCollected) return;
            var lod = _f.GetLod(0);
            int n = lod.Pos.Length;
            var v = new Vector3[n]; var nr = new Vector3[n]; var uv = new Vector2[n];
            var bw = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                v[i] = lod.Pos[i].U; nr[i] = lod.Nrm[i].U; uv[i] = FM.V2(lod.Uv[i * 2], lod.Uv[i * 2 + 1]);
                // the BoneWeight setters are stripped from the game build: write the fields
                var w = default(BoneWeight);
                w.m_BoneIndex0 = lod.BoneIdx[i * 4]; w.m_BoneIndex1 = lod.BoneIdx[i * 4 + 1];
                w.m_BoneIndex2 = lod.BoneIdx[i * 4 + 2]; w.m_BoneIndex3 = lod.BoneIdx[i * 4 + 3];
                w.m_Weight0 = lod.Weight[i * 4] / 255f; w.m_Weight1 = lod.Weight[i * 4 + 1] / 255f;
                w.m_Weight2 = lod.Weight[i * 4 + 2] / 255f; w.m_Weight3 = lod.Weight[i * 4 + 3] / 255f;
                bw[i] = w;
            }
            // bind poses from the rest skeleton (mesh space = Driver_Root space at scale 1)
            var bind = new Matrix4x4[_s.N];
            for (int b = 0; b < _s.N; b++)
            {
                _s.RestWorld(b, out var p, out var q);
                bind[b] = Inverse(p, q);
            }
            var tris = new int[lod.Idx.Length];
            int keep = 0;
            var head = new List<int>(lod.Idx.Length);
            for (int i = 0; i < lod.Idx.Length; i += 3)
            {
                int a = lod.Idx[i], b = lod.Idx[i + 1], c = lod.Idx[i + 2];
                tris[i] = a; tris[i + 1] = b; tris[i + 2] = c;
                bool masked = lod.Mask != null && ((lod.Mask[a] | lod.Mask[b] | lod.Mask[c]) & 1) != 0;
                if (!masked) { head.Add(a); head.Add(b); head.Add(c); keep++; }
            }
            _fullTris = tris; _headTris = head.ToArray();
            _full = Own(MakeSkinned("Driver.Body", v, nr, uv, bw, bind, _fullTris));
            _headless = Own(MakeSkinned("Driver.BodyNoHead", v, nr, uv, bw, bind, _headTris));

            // helmet: visor first, helmet shell last (an extra outline material draws the last submesh)
            RigFile.Rigid helm = null, vis = null;
            foreach (var r in _f.Rigids) { if (r.Name == "helmet") helm = r; else if (r.Name == "visor") vis = r; }
            _helmetMesh = Own(MakeRigid("Driver.Helmet", vis, helm));

            // materials
            _suit = Own(MakeMaterial(Slot(0), carMaterial));
            _helmetMat = Own(MakeMaterial(Slot(1), carMaterial));
            _visor = Own(MakeMaterial(Slot(2), carMaterial));
            if (outlineTemplate != null && !outlineTemplate.WasCollected)
            {
                _outline = Own(new Material(outlineTemplate) { name = "Driver.Outline" });
            }
            bool outline = _outline != null && Plugin.Outline.Value;
            _bodyMats = outline && (Slot(0)?.Flags & 1) != 0 ? new[] { _suit, _outline } : new[] { _suit };
            var hm = new List<Material>();
            if (vis != null) hm.Add(_visor);
            if (helm != null) hm.Add(_helmetMat);
            if (outline && helm != null && (Slot(1)?.Flags & 1) != 0) hm.Add(_outline);
            _helmetMats = hm.ToArray();
            Plugin.Log.LogInfo($"[Driver] model ready: {n} vertices, {lod.Idx.Length / 3} triangles ({keep} without the head), {_s.N} bones, " +
                               $"helmet {_helmetMesh.vertexCount} vertices, outline {(outline ? "on" : "off")}");
        }

        private RigFile.MatDef Slot(int i) => i < _f.Mats.Count ? _f.Mats[i] : null;

        private static Matrix4x4 Inverse(Vec p, Quat q)
        {
            var inv = q.Inv;
            var t = -(inv * p);
            var x = inv * Vec.Right; var y = inv * Vec.Up; var z = inv * Vec.Fwd;   // columns of R^-1
            var m = default(Matrix4x4);
            m.m00 = x.x; m.m10 = x.y; m.m20 = x.z;
            m.m01 = y.x; m.m11 = y.y; m.m21 = y.z;
            m.m02 = z.x; m.m12 = z.y; m.m22 = z.z;
            m.m03 = t.x; m.m13 = t.y; m.m23 = t.z; m.m33 = 1f;
            return m;
        }

        private static Mesh MakeSkinned(string name, Vector3[] v, Vector3[] n, Vector2[] uv, BoneWeight[] bw, Matrix4x4[] bind, int[] tris)
        {
            var m = new Mesh { name = name };
            m.vertices = v;
            m.normals = n;
            m.uv = uv;
            m.boneWeights = bw;
            m.bindposes = bind;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        private static Mesh MakeRigid(string name, RigFile.Rigid first, RigFile.Rigid second)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>();
            var subs = new List<int[]>();
            foreach (var r in new[] { first, second })
            {
                if (r == null) continue;
                int o = v.Count;
                for (int i = 0; i < r.Pos.Length; i++) { v.Add(r.Pos[i].U); n.Add(r.Nrm[i].U); uv.Add(FM.V2(r.Uv[i * 2], r.Uv[i * 2 + 1])); }
                var t = new int[r.Idx.Length];
                for (int i = 0; i < t.Length; i++) t[i] = r.Idx[i] + o;
                subs.Add(t);
            }
            var m = new Mesh { name = name };
            m.vertices = v.ToArray();
            m.normals = n.ToArray();
            m.uv = uv.ToArray();
            m.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) m.SetTriangles(subs[i], i);
            m.RecalculateBounds();
            return m;
        }

        private static Shader _lit;
        private readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();

        private Material MakeMaterial(RigFile.MatDef d, Material carMaterial)
        {
            if (_lit == null) _lit = Shader.Find("Universal Render Pipeline/Lit");
            Material m;
            if (_lit != null) m = new Material(_lit);
            else if (carMaterial != null) m = new Material(carMaterial);
            else throw new InvalidOperationException("no URP Lit shader and no car material to copy");
            m.name = "Driver." + (d != null ? d.Name : "plain");
            if (d == null) return m;
            m.SetColor("_BaseColor", FM.Rgba(d.R, d.G, d.B, d.A));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", d.Smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", d.Metallic);
            var tex = Texture(d.Texture);
            if (tex != null && m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            var em = Texture(d.EmissionTexture);
            if (em != null && m.HasProperty("_EmissionMap") && (d.ER > 0f || d.EG > 0f || d.EB > 0f))
            {
                m.EnableKeyword("_EMISSION");
                m.SetTexture("_EmissionMap", em);
                m.SetColor("_EmissionColor", FM.Rgba(d.ER, d.EG, d.EB, 1f));
            }
            return m;
        }

        private Texture2D Texture(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (_tex.TryGetValue(file, out var t) && t != null && !t.WasCollected) return t;
            string path = Path.Combine(_folder, file);
            if (!File.Exists(path)) { Plugin.Log.LogWarning($"[Driver] texture not found: {path} (plain colour instead)"); _tex[file] = null; return null; }
            t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Driver." + file };
            if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(t); Plugin.Log.LogWarning($"[Driver] texture unreadable: {path}"); _tex[file] = null; return null; }
            t.anisoLevel = 4;
            _tex[file] = Own(t);
            return t;
        }

        // ------------------------------------------------------------------------------------------ scene objects
        /// <summary>Builds Driver_Root, the bones and the renderers (once per scene). The skinning self-test runs on the first build.</summary>
        public void Build(int layer, Material outlineTemplate, Material carMaterial, bool forceCpu, ref int selfTest)
        {
            DestroyObjects();
            EnsureAssets(outlineTemplate, carMaterial);
            Root = new GameObject("Driver_Root");
            RootT = Root.transform;
            Root.layer = layer;
            Bones = new Transform[_s.N];
            _s.Rest();
            for (int b = 0; b < _s.N; b++)
            {
                var go = new GameObject(_f.Names[b]);
                go.layer = layer;
                var t = go.transform;
                int p = _f.Parent[b];
                t.SetParent(p < 0 ? RootT : Bones[p], false);
                t.SetLocalPositionAndRotation(_s.Lp[b].U, _s.Lq[b].U);
                Bones[b] = t;
            }
            _bodyGo = new GameObject("Driver_Body");
            _bodyGo.layer = layer;
            _bodyGo.transform.SetParent(RootT, false);

            Cpu = forceCpu || selfTest < 0;
            if (!Cpu)
            {
                _smr = _bodyGo.AddComponent<SkinnedMeshRenderer>();
                _smr.quality = SkinQuality.Bone4;
                _smr.updateWhenOffscreen = false;
                _smr.skinnedMotionVectors = false;
                _smr.bones = new Il2CppReferenceArray<Transform>(Bones);
                _smr.rootBone = Bones[0];
                _smr.sharedMesh = _full;
                _smr.sharedMaterials = new Il2CppReferenceArray<Material>(_bodyMats);
                _smr.shadowCastingMode = ShadowCastingMode.On;
                _smr.receiveShadows = true;
                // bounds in the root bone's space (model units, the root carries the scale): seated body + reach
                var bd = default(Bounds);
                bd.center = FM.V3(0f, 0.85f, 0.15f);
                bd.extents = FM.V3(0.7f, 0.9f, 0.85f);
                _smr.localBounds = bd;
                if (selfTest == 0) selfTest = SelfTest() ? 1 : -1;
                if (selfTest < 0)
                {
                    UnityEngine.Object.Destroy(_smr); _smr = null;
                    Cpu = true;
                    Plugin.Log.LogWarning("[Driver] skinning self-test failed: using CPU skinning (about 0.3 ms a frame while the driver shows)");
                }
            }
            if (Cpu) MakeCpuRenderer(layer);

            _helmetGo = new GameObject("Driver_Helmet");
            _helmetGo.layer = layer;
            _helmetGo.transform.SetParent(Bones[Array.IndexOf(_f.Names, "head")], false);
            _helmetGo.AddComponent<MeshFilter>().sharedMesh = _helmetMesh;
            _helmet = _helmetGo.AddComponent<MeshRenderer>();
            _helmet.sharedMaterials = new Il2CppReferenceArray<Material>(_helmetMats);
            _helmet.shadowCastingMode = ShadowCastingMode.On;
            _visible = -1; _driverView = -1;
            SetVisible(false);
        }

        private void MakeCpuRenderer(int layer)
        {
            if (_cpuMesh == null || _cpuMesh.WasCollected)
            {
                var lod = _f.GetLod(0);
                int n = lod.Pos.Length;
                _cpuMesh = Own(new Mesh { name = "Driver.BodyCpu" });
                _cpuMesh.MarkDynamic();
                _cpuV = new Vector3[n]; _cpuN = new Vector3[n];
                for (int i = 0; i < n; i++) { _cpuV[i] = lod.Pos[i].U; _cpuN[i] = lod.Nrm[i].U; }
                _cpuVArr = new Il2CppStructArray<Vector3>(_cpuV);
                _cpuNArr = new Il2CppStructArray<Vector3>(_cpuN);
                _cpuMesh.vertices = _cpuVArr;
                _cpuMesh.normals = _cpuNArr;
                _cpuMesh.uv = _full.uv;
                _cpuMesh.triangles = _fullTris;
            }
            _cpuFilter = _bodyGo.AddComponent<MeshFilter>();
            _cpuFilter.sharedMesh = _cpuMesh;
            _cpuRenderer = _bodyGo.AddComponent<MeshRenderer>();
            _cpuRenderer.sharedMaterials = new Il2CppReferenceArray<Material>(_bodyMats);
            _cpuRenderer.shadowCastingMode = ShadowCastingMode.On;
        }

        /// <summary>Poses the A-pose with lowerarm_l turned 30 deg, bakes the skinned mesh and compares one wrist vertex with
        /// the managed skinning maths. True when they agree within 5 mm.</summary>
        private bool SelfTest()
        {
            try
            {
                var lod = _f.GetLod(0);
                int hand = Array.IndexOf(_f.Names, "hand_l"), lower = Array.IndexOf(_f.Names, "lowerarm_l");
                int k = -1;
                for (int i = 0; i < lod.Pos.Length && k < 0; i++)
                    if (lod.BoneIdx[i * 4] == hand && lod.Weight[i * 4] >= 250) k = i;
                if (k < 0) { Plugin.Log.LogWarning("[Driver] self-test: no wrist vertex found; trusting GPU skinning"); return true; }
                _s.Rest();
                _s.Lq[lower] = (_s.Lq[lower] * Quat.AngleAxis(30f, Vec.Right)).Normalized;
                _s.FK();
                for (int b = 0; b < _s.N; b++) Bones[b].SetLocalPositionAndRotation(_s.Lp[b].U, _s.Lq[b].U);
                var baked = new Mesh();
                _smr.BakeMesh(baked);
                var bv = baked.vertices;
                bool ok = bv != null && bv.Length > k;
                Vec got = ok ? Vec.From(bv[k]) : Vec.Zero;
                UnityEngine.Object.Destroy(baked);
                var want = SkinVertex(lod, k, 1f);
                float err = (got - want).Length, moved = (want - lod.Pos[k]).Length;
                ok = ok && err < 0.005f && moved > 0.02f;
                Plugin.Log.LogInfo($"[Driver] skinning self-test {(ok ? "OK" : "FAILED")}: wrist vertex moved {moved * 100f:0.0} cm, GPU vs managed {err * 1000f:0.0} mm");
                _s.Rest();
                for (int b = 0; b < _s.N; b++) Bones[b].SetLocalPositionAndRotation(_s.Lp[b].U, _s.Lq[b].U);
                return ok;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Driver] skinning self-test crashed ({e.Message})");
                return false;
            }
        }

        private Vec SkinVertex(RigFile.Lod lod, int i, float scale)
        {
            var v = lod.Pos[i];
            var acc = Vec.Zero;
            for (int j = 0; j < 4; j++)
            {
                int w = lod.Weight[i * 4 + j];
                if (w == 0) continue;
                _s.SkinDelta(lod.BoneIdx[i * 4 + j], out var d, out var rp, out var p);
                acc = acc + (p + d * (v - rp)) * (w / 255f);
            }
            return acc * scale;
        }

        // per-frame CPU skinning buffers (bone deltas)
        private Quat[] _dq; private Vec[] _drp, _dp;

        /// <summary>CPU fallback: skins the body into the dynamic mesh (Driver_Root space, scale included).</summary>
        public void CpuSkin(float scale)
        {
            if (!Cpu || _cpuMesh == null) return;
            var lod = _f.GetLod(0);
            int nb = _s.N;
            if (_dq == null) { _dq = new Quat[nb]; _drp = new Vec[nb]; _dp = new Vec[nb]; }
            for (int b = 0; b < nb; b++) _s.SkinDelta(b, out _dq[b], out _drp[b], out _dp[b]);
            int n = lod.Pos.Length;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var v = lod.Pos[i]; var nr = lod.Nrm[i];
                Vec pa = Vec.Zero, na = Vec.Zero;
                for (int j = 0; j < 4; j++)
                {
                    int w = lod.Weight[i * 4 + j];
                    if (w == 0) continue;
                    int b = lod.BoneIdx[i * 4 + j];
                    float f = w / 255f;
                    pa = pa + (_dp[b] + _dq[b] * (v - _drp[b])) * f;
                    na = na + (_dq[b] * nr) * f;
                }
                pa = pa * scale;
                var o = default(Vector3); o.x = pa.x; o.y = pa.y; o.z = pa.z;
                _cpuVArr[i] = o;
                var on = default(Vector3); on.x = na.x; on.y = na.y; on.z = na.z;
                _cpuNArr[i] = on;
                if (pa.x < minX) minX = pa.x; if (pa.y < minY) minY = pa.y; if (pa.z < minZ) minZ = pa.z;
                if (pa.x > maxX) maxX = pa.x; if (pa.y > maxY) maxY = pa.y; if (pa.z > maxZ) maxZ = pa.z;
            }
            _cpuMesh.vertices = _cpuVArr;
            _cpuMesh.normals = _cpuNArr;
            var bd = default(Bounds);
            bd.center = FM.V3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            bd.extents = FM.V3((maxX - minX) * 0.5f + 0.05f, (maxY - minY) * 0.5f + 0.05f, (maxZ - minZ) * 0.5f + 0.05f);
            _cpuMesh.bounds = bd;
        }

        // ------------------------------------------------------------------------------------------ visibility
        public void SetVisible(bool on)
        {
            int v = on ? 1 : 0;
            if (v == _visible) return;
            _visible = v;
            if (_smr != null) _smr.forceRenderingOff = !on;
            if (_cpuRenderer != null) _cpuRenderer.forceRenderingOff = !on;
            if (_helmet != null) _helmet.forceRenderingOff = !on;
        }

        public bool Visible => _visible == 1;

        /// <summary>Driver view: the head-less body and a shadow-only helmet (never seen from inside, its shadow kept).</summary>
        public void SetDriverView(bool driver)
        {
            int d = driver ? 1 : 0;
            if (d == _driverView) return;
            _driverView = d;
            if (_smr != null) _smr.sharedMesh = driver ? _headless : _full;
            if (_cpuMesh != null && Cpu) _cpuMesh.triangles = driver ? _headTris : _fullTris;
            if (_helmet != null) _helmet.shadowCastingMode = driver ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        }

        /// <summary>Writes bones to their transforms (all of them, or only the per-frame ones).</summary>
        public void WriteBones(int[] only)
        {
            if (only == null) { for (int b = 0; b < _s.N; b++) Bones[b].SetLocalPositionAndRotation(_s.Lp[b].U, _s.Lq[b].U); return; }
            for (int i = 0; i < only.Length; i++) { int b = only[i]; Bones[b].SetLocalPositionAndRotation(_s.Lp[b].U, _s.Lq[b].U); }
        }

        // ------------------------------------------------------------------------------------------ teardown
        public void DestroyObjects()
        {
            if (Root != null && !Root.WasCollected) { try { UnityEngine.Object.Destroy(Root); } catch { /* scene gone */ } }
            Root = null; RootT = null; Bones = null; _bodyGo = null; _helmetGo = null; _smr = null; _cpuFilter = null; _cpuRenderer = null; _helmet = null;
            _visible = -1; _driverView = -1;
        }

        public void DestroyAll()
        {
            DestroyObjects();
            foreach (var o in _assets) { try { if (o != null && !o.WasCollected) UnityEngine.Object.Destroy(o); } catch { /* gone */ } }
            _assets.Clear(); _tex.Clear();
            _full = _headless = _helmetMesh = _cpuMesh = null;
            _suit = _helmetMat = _visor = _outline = null;
            _cpuVArr = _cpuNArr = null;
        }
    }
}
