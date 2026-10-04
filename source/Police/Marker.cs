using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Police
{
    /// <summary>
    /// A 3D marker above a patrol car, replacing the flat IMGUI box: a faceted gem (octahedron, flat-shaded, lit by the
    /// scene, slightly emissive) that spins and bobs above the roof. Blue = patrol, amber = you're in its notice zone,
    /// red = chasing. It grows with distance (stays findable at 400+ m), shrinks away towards ~35 m and is hidden closer,
    /// where the car itself and its lightbar are the cue. One shared mesh and three shared materials (URP Lit, else
    /// Unlit), destroyed by DestroyShared(). Not parented to the car.
    /// </summary>
    internal sealed class Marker
    {
        internal enum State { Idle, Zone, Chase }

        private static Mesh s_mesh;
        private static Material s_idle, s_zone, s_chase;
        private static bool s_made;

        private GameObject _go;
        private Transform _t;
        private Renderer _r;
        private State _state = (State)(-1);
        private float _phase;

        internal static Marker Create()
        {
            MakeShared();
            if (s_mesh == null || s_idle == null) return null;
            var m = new Marker();
            try
            {
                m._go = new GameObject("Police.Marker");
                m._t = m._go.transform;
                m._t.position = new Vector3(0f, -1000f, 0f);
                m._go.AddComponent<MeshFilter>().sharedMesh = s_mesh;
                var r = m._go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                m._r = r;
                m._phase = UnityEngine.Random.value * 6.28f;
                m.Set(State.Idle);
                return m;
            }
            catch { m.Destroy(); throw; }
        }

        private static void MakeShared()
        {
            if (s_made) return;
            s_made = true;
            // octahedron: 6 tips, 8 faces, unshared vertices for flat facets
            var tips = new[] { new Vector3(0, 1.3f, 0), new Vector3(0, -1.3f, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(-1, 0, 0), new Vector3(0, 0, -1) };
            int[] faces = { 0, 3, 2, 0, 4, 3, 0, 5, 4, 0, 2, 5, 1, 2, 3, 1, 3, 4, 1, 4, 5, 1, 5, 2 };
            var v = new Vector3[faces.Length];
            var t = new int[faces.Length];
            for (int i = 0; i < faces.Length; i++) { v[i] = tips[faces[i]] * 0.5f; t[i] = i; }
            s_mesh = new Mesh { name = "Police.Marker" };
            s_mesh.vertices = v;
            s_mesh.triangles = t;
            s_mesh.RecalculateNormals();
            s_mesh.RecalculateBounds();

            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");   // Unity null: == only, never ??
            if (sh == null) { Plugin.Log.LogWarning("[Police] no URP shader for the 3D markers: markers off"); return; }
            s_idle = Gem(sh, new Color(0.25f, 0.5f, 1f));
            s_zone = Gem(sh, new Color(1f, 0.7f, 0.1f));
            s_chase = Gem(sh, new Color(1f, 0.15f, 0.1f));
        }

        private static Material Gem(Shader sh, Color c)
        {
            var m = new Material(sh);
            m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.85f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.1f);
            if (sh.name.EndsWith("/Lit")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * 0.55f); }
            return m;
        }

        internal static void DestroyShared()
        {
            RogueShared.Fx.Kill(s_mesh); RogueShared.Fx.Kill(s_idle); RogueShared.Fx.Kill(s_zone); RogueShared.Fx.Kill(s_chase);
            s_mesh = null; s_idle = s_zone = s_chase = null; s_made = false;
        }

        internal void Set(State s)
        {
            if (s == _state || _r == null) return;
            _state = s;
            _r.sharedMaterial = s == State.Chase ? s_chase : s == State.Zone ? s_zone : s_idle;
        }

        /// <summary>Above the roof; size from the camera distance; hidden when close (the car is the cue) or far off-road.</summary>
        internal void Place(Vector3 roof, Vector3 camPos, float time)
        {
            if (_t == null) return;
            float dist = Vector3.Distance(roof, camPos);
            bool show = dist > 35f && dist < 900f;
            if (_r.enabled != show) _r.enabled = show;
            if (!show) return;
            float size = Mathf.Clamp(dist * 0.011f, 0.7f, 5f) * Mathf.Clamp01((dist - 35f) / 25f + 0.2f);
            float bob = Mathf.Sin(time * 2.2f + _phase) * 0.15f * size;
            _t.SetPositionAndRotation(roof + Vector3.up * (1.2f + size * 0.8f + bob), Quaternion.Euler(0f, time * 90f + _phase * 57f, 0f));
            _t.localScale = new Vector3(size, size, size);
        }

        internal void Destroy()
        {
            RogueShared.Fx.Kill(_go);
            _go = null; _t = null; _r = null;
        }
    }
}
