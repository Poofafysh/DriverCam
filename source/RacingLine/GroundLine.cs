using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace RacingLine
{
    /// <summary>
    /// The racing line drawn ON the road, ahead of the car, like Forza's / The Crew's driving line: a flat ribbon of
    /// chevrons laid on the road surface, coloured per point by what to do there at your current speed (green = on pace,
    /// amber = lift, red = brake), orange where the line goes around traffic, faint where traffic leaves no way past.
    ///
    /// One mesh we own (fixed topology, 2 vertices per line sample, at most MaxSamples): each frame only the vertex,
    /// colour and UV arrays are rewritten in place (Il2Cpp arrays kept for the session, so nothing is converted or
    /// allocated per frame) and re-assigned. Unused vertices collapse onto the last point with alpha 0. The height under
    /// each sample is found once by a downward raycast on the road layer (Street, 11) and cached per sample; until it is
    /// known the path's own height is used. Material: RogueShared.Fx.Transparent (the game's URP Particles/Unlit,
    /// alpha-blended, vertex colour x chevron texture). If no such shader exists, Ok stays false and the Runner keeps the
    /// old IMGUI dots. Everything is destroyed by Destroy() (plugin off, unload).
    /// </summary>
    internal sealed class GroundLine
    {
        internal const int MaxSamples = 420;   // 400 m ahead at 1 m steps, plus margin
        private const int StreetLayer = 11;
        private const float Lift = 0.05f;      // above the road, below the car's wheels' contact patch visually

        private GameObject _go;
        private Mesh _mesh;
        private Material _mat;
        private Texture2D _tex;
        private Il2CppStructArray<Vector3> _verts;
        private Il2CppStructArray<Color> _cols;
        private Il2CppStructArray<Vector2> _uvs;
        private Line _cacheFor;
        private float[] _groundY;
        private bool _visible;

        /// <summary>Per-sample inputs for the next Commit, filled by the Runner: lateral offset (m) and colour.</summary>
        internal readonly float[] Offset = new float[MaxSamples];
        internal readonly Color[] Tint = new Color[MaxSamples];

        internal bool Ok { get; private set; }
        internal bool Transparent { get; private set; }

        internal static GroundLine Create()
        {
            var g = new GroundLine();
            try { g.Build(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RacingLine] ground line unavailable, using the dot preview: {e.Message}");
                g.Destroy();
            }
            return g;
        }

        private void Build()
        {
            _tex = RogueShared.Fx.ChevronTexture();
            _mat = RogueShared.Fx.Transparent("RacingLine.Ground", _tex, false, out bool transparent);
            if (_mat == null) { Plugin.Log.LogWarning("[RacingLine] no URP unlit shader found: ground line off, using the dot preview"); return; }
            Transparent = transparent;

            int nv = MaxSamples * 2;
            _verts = new Il2CppStructArray<Vector3>(nv);
            _cols = new Il2CppStructArray<Color>(nv);
            _uvs = new Il2CppStructArray<Vector2>(nv);
            var tris = new int[(MaxSamples - 1) * 6];
            for (int i = 0, t = 0; i < MaxSamples - 1; i++)
            {
                int a = i * 2;
                tris[t++] = a; tris[t++] = a + 2; tris[t++] = a + 1;
                tris[t++] = a + 1; tris[t++] = a + 2; tris[t++] = a + 3;
            }
            _mesh = new Mesh { name = "RacingLine.Ground" };
            _mesh.MarkDynamic();
            _mesh.vertices = _verts;
            _mesh.colors = _cols;
            _mesh.uv = _uvs;
            _mesh.triangles = tris;

            _go = new GameObject("RacingLine.Ground");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = _go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _go.SetActive(false);
            Ok = true;
            Plugin.Log.LogInfo($"[RacingLine] ground line ready ({(transparent ? "transparent" : "opaque fallback")} material, shader '{ShaderName(_mat)}')");
        }

        private static string ShaderName(Material m)
        {
            var sh = m != null ? m.shader : null;
            return sh == null ? "?" : sh.name;
        }

        internal void Hide()
        {
            if (!_visible || _go == null) return;
            _visible = false;
            _go.SetActive(false);
        }

        /// <summary>
        /// Lays the ribbon over samples [start, start + count) of the line, using Offset[k] / Tint[k] for sample start + k.
        /// width in metres; fades in over the first few metres and out over the last fifth.
        /// </summary>
        internal void Commit(Line line, int start, int count, float width)
        {
            if (!Ok || _go == null) return;
            count = Math.Min(count, MaxSamples);
            if (count < 2) { Hide(); return; }
            if (!ReferenceEquals(line, _cacheFor))
            {
                _cacheFor = line;
                if (_groundY == null || _groundY.Length < line.N) _groundY = new float[line.N];
                for (int i = 0; i < line.N; i++) _groundY[i] = float.NaN;
            }

            int raycasts = 0;
            float half = width * 0.5f;
            float fadeIn = 4f / line.Step, fadeOut = Math.Max(2f, count * 0.2f);
            Vector3 last = Vector3.zero;
            for (int k = 0; k < count; k++)
            {
                int i = start + k;
                float off = Offset[k];
                float cx = line.Px[i] + line.Nx[i] * off, cz = line.Pz[i] + line.Nz[i] * off;
                float y = _groundY[i];
                if (float.IsNaN(y))
                {
                    y = line.Py[i];
                    if (raycasts < 60)
                    {
                        raycasts++;
                        if (Physics.Raycast(new Vector3(cx, line.Py[i] + 4f, cz), Vector3.down, out RaycastHit hit, 10f, 1 << StreetLayer, QueryTriggerInteraction.Ignore))
                            y = hit.point.y;
                        _groundY[i] = y;   // a miss caches the path height (no retry every frame)
                    }
                }
                // across the road: the line's own right-hand normal (flat), so the ribbon lies on the road
                float rx = line.Nx[i] * half, rz = line.Nz[i] * half;
                int v = k * 2;
                var c = Tint[k];
                float a = c.a * Mathf.Clamp01(k / fadeIn) * Mathf.Clamp01((count - 1 - k) / fadeOut);
                c.a = a;
                _verts[v] = new Vector3(cx - rx, y + Lift, cz - rz);
                _verts[v + 1] = new Vector3(cx + rx, y + Lift, cz + rz);
                _cols[v] = c; _cols[v + 1] = c;
                float along = i * line.Step / 3f;   // one chevron every 3 m, anchored to the road (doesn't slide with the car)
                _uvs[v] = new Vector2(0f, along); _uvs[v + 1] = new Vector2(1f, along);
                last = new Vector3(cx, y + Lift, cz);
            }
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int k = count; k < MaxSamples; k++)
            {
                int v = k * 2;
                _verts[v] = last; _verts[v + 1] = last;
                _cols[v] = clear; _cols[v + 1] = clear;
            }
            _mesh.vertices = _verts;
            _mesh.colors = _cols;
            _mesh.uv = _uvs;
            _mesh.RecalculateBounds();
            if (!_visible) { _visible = true; _go.SetActive(true); }
        }

        internal void Destroy()
        {
            Ok = false;
            RogueShared.Fx.Kill(_go); RogueShared.Fx.Kill(_mesh); RogueShared.Fx.Kill(_mat); RogueShared.Fx.Kill(_tex);
            _go = null; _mesh = null; _mat = null; _tex = null;
            _verts = null; _cols = null; _uvs = null; _cacheFor = null; _visible = false;
        }
    }
}
