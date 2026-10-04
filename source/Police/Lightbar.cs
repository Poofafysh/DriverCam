using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Police
{
    /// <summary>
    /// A patrol car's lightbar, all created and owned by us: a black base bar, a red and a blue lens, a soft glow halo
    /// over each lens (an additive, camera-facing quad: reads as a real light from far away and at night) and two point
    /// lights. Never parented to the car: Place() moves it onto the roof every LateUpdate, so a pooled car that gets
    /// reused can never carry our objects along. Colliders of the primitives are disabled and destroyed at once.
    /// Materials: lenses / base from "Universal Render Pipeline/Unlit" (always bright, ignores scene light), halos from
    /// RogueShared.Fx.Transparent (additive). Shared by every lightbar, created once, destroyed by DestroyShared().
    /// Unity APIs used are all in dump.cs: GameObject.CreatePrimitive, Shader.Find, new Material(Shader), SetColor(string),
    /// Light.type/color/intensity/range/shadows/enabled, Renderer.sharedMaterial/enabled/shadowCastingMode, Transform pose.
    /// </summary>
    internal sealed class Lightbar
    {
        internal enum Look { Idle, FlashRed, FlashBlue, Off }

        private static readonly Color Red = new Color(1f, 0.06f, 0.05f), Blue = new Color(0.12f, 0.32f, 1f);
        private static readonly Color RedDim = new Color(0.35f, 0.03f, 0.03f), BlueDim = new Color(0.03f, 0.07f, 0.35f);

        // shared materials (one set for every lightbar)
        private static Material s_base, s_redOn, s_redOff, s_blueOn, s_blueOff, s_haloRed, s_haloBlue;
        private static Texture2D s_glow;
        private static bool s_made;
        private static string s_shaderName = "?";

        private GameObject _baseGo, _redGo, _blueGo, _redHalo, _blueHalo;
        private Transform _baseT, _redT, _blueT, _redHaloT, _blueHaloT;
        private Renderer _redR, _blueR, _redHaloR, _blueHaloR;
        private Light _redLight, _blueLight;
        private Look _look = (Look)(-1);
        private bool _lightOn;

        internal static Lightbar Create()
        {
            MakeShared();
            var bar = new Lightbar();
            try
            {
                bar._baseGo = Part("Police.Lightbar.Base", PrimitiveType.Cube, new Vector3(1.0f, 0.07f, 0.26f), s_base, out bar._baseT, out _);
                bar._redGo = Part("Police.Lightbar.Red", PrimitiveType.Cube, new Vector3(0.42f, 0.11f, 0.22f), s_redOff, out bar._redT, out bar._redR);
                bar._blueGo = Part("Police.Lightbar.Blue", PrimitiveType.Cube, new Vector3(0.42f, 0.11f, 0.22f), s_blueOff, out bar._blueT, out bar._blueR);
                if (s_haloRed != null)
                {
                    bar._redHalo = Part("Police.Lightbar.HaloRed", PrimitiveType.Quad, new Vector3(2.4f, 2.4f, 1f), s_haloRed, out bar._redHaloT, out bar._redHaloR);
                    bar._blueHalo = Part("Police.Lightbar.HaloBlue", PrimitiveType.Quad, new Vector3(2.4f, 2.4f, 1f), s_haloBlue, out bar._blueHaloT, out bar._blueHaloR);
                }
                bar._redLight = MakeLight(bar._redGo, Red);
                bar._blueLight = MakeLight(bar._blueGo, Blue);
                bar.Show(Look.Idle, false);
                return bar;
            }
            catch
            {
                bar.Destroy();   // never leave half a lightbar behind
                throw;
            }
        }

        private static GameObject Part(string name, PrimitiveType type, Vector3 size, Material mat, out Transform t, out Renderer r)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) { col.enabled = false; UnityEngine.Object.Destroy(col); }
            t = go.transform;
            t.localScale = size;
            t.position = new Vector3(0f, -1000f, 0f);   // out of sight until the first Place()
            r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (mat != null) r.sharedMaterial = mat;
            }
            return go;
        }

        private static Light MakeLight(GameObject host, Color c)
        {
            var light = host.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = c;
            light.range = 10f;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
            light.enabled = false;
            return light;
        }

        /// <summary>Once: the shared materials. Missing shaders leave the primitives' default look (lights still flash).</summary>
        private static void MakeShared()
        {
            if (s_made) return;
            s_made = true;
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");   // Unity null: == only, never ??
            if (sh != null)
            {
                s_shaderName = sh.name;
                s_base = Solid(sh, new Color(0.03f, 0.03f, 0.035f));
                s_redOn = Solid(sh, Red); s_redOff = Solid(sh, RedDim);
                s_blueOn = Solid(sh, Blue); s_blueOff = Solid(sh, BlueDim);
            }
            try
            {
                s_glow = RogueShared.Fx.GlowTexture(64);
                s_haloRed = RogueShared.Fx.Transparent("Police.Halo.Red", s_glow, true, out _);
                s_haloBlue = RogueShared.Fx.Transparent("Police.Halo.Blue", s_glow, true, out bool transparent);
                if (s_haloRed != null) s_haloRed.SetColor("_BaseColor", new Color(1f, 0.15f, 0.1f, 0.9f));
                if (s_haloBlue != null) s_haloBlue.SetColor("_BaseColor", new Color(0.2f, 0.4f, 1f, 0.9f));
                if (!transparent) { RogueShared.Fx.Kill(s_haloRed); RogueShared.Fx.Kill(s_haloBlue); s_haloRed = s_haloBlue = null; }   // an opaque square would look wrong
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Police] lightbar halos off: {e.Message}"); s_haloRed = s_haloBlue = null; }
            Plugin.Log.LogInfo($"[Police] lightbar materials: shader '{s_shaderName}', halos {(s_haloRed != null ? "on" : "off")}");
        }

        private static Material Solid(Shader sh, Color c)
        {
            var m = new Material(sh);
            m.SetColor("_BaseColor", c);
            if (sh.name.EndsWith("/Lit")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c); }
            return m;
        }

        internal static void DestroyShared()
        {
            foreach (var m in new[] { s_base, s_redOn, s_redOff, s_blueOn, s_blueOff, s_haloRed, s_haloBlue }) RogueShared.Fx.Kill(m);
            RogueShared.Fx.Kill(s_glow);
            s_base = s_redOn = s_redOff = s_blueOn = s_blueOff = s_haloRed = s_haloBlue = null; s_glow = null;
            s_made = false;
        }

            /// <summary>Onto the roof: base centred, lenses offset sideways by +-0.25 m; a halo that is on faces the camera
        /// (a hidden halo isn't moved: it is placed again the frame it comes on, before it is drawn).</summary>
        internal void Place(Vector3 roof, Quaternion rotation, Vector3 right, Vector3 up, Vector3 camPos)
        {
            if (_baseT != null) _baseT.SetPositionAndRotation(roof + up * 0.035f, rotation);
            Vector3 lensY = up * 0.11f;
            Vector3 redPos = roof + lensY - right * 0.25f, bluePos = roof + lensY + right * 0.25f;
            if (_redT != null) _redT.SetPositionAndRotation(redPos, rotation);
            if (_blueT != null) _blueT.SetPositionAndRotation(bluePos, rotation);
            if (_redHaloT != null && _look == Look.FlashRed) Face(_redHaloT, redPos, camPos);
            if (_blueHaloT != null && _look == Look.FlashBlue) Face(_blueHaloT, bluePos, camPos);
        }

        private static void Face(Transform t, Vector3 pos, Vector3 cam)
        {
            Vector3 to = pos - cam;
            if (to.sqrMagnitude < 1e-4f) return;
            // a little towards the camera so the car body doesn't cut the halo
            t.SetPositionAndRotation(pos - to.normalized * 0.4f, Quaternion.LookRotation(to));
        }

        /// <summary>
        /// Idle: dim lenses. Flash: one side bright, its halo on, and its real light when <paramref name="light"/> (the
        /// Runner only allows that for the chasers nearest the camera: 0.6.0 perf). Off: both dark (ESCAPED / released).
        /// Call Place after Show in the same frame, so a halo that just came on is facing the camera when drawn.
        /// </summary>
        internal void Show(Look look, bool light)
        {
            if (look == _look && light == _lightOn) return;   // changes only when the look changes (4x a second while flashing)
            bool lookChanged = look != _look;
            _look = look; _lightOn = light;
            bool red = look == Look.FlashRed, blue = look == Look.FlashBlue;
            if (lookChanged)
            {
                if (_redR != null && s_redOn != null) _redR.sharedMaterial = red ? s_redOn : s_redOff;
                if (_blueR != null && s_blueOn != null) _blueR.sharedMaterial = blue ? s_blueOn : s_blueOff;
                if (_redHaloR != null) _redHaloR.enabled = red;
                if (_blueHaloR != null) _blueHaloR.enabled = blue;
            }
            SetLight(_redLight, red && light);
            SetLight(_blueLight, blue && light);
        }

        private static void SetLight(Light light, bool on)
        {
            if (light == null) return;   // (one native write pair per change, 4x a second at most)
            light.intensity = on ? 7f : 0f;
            light.enabled = on;
        }

        /// <summary>Destroys this lightbar's objects. Safe to call twice and after a scene change (already gone).</summary>
        internal void Destroy()
        {
            RogueShared.Fx.Kill(_baseGo); RogueShared.Fx.Kill(_redGo); RogueShared.Fx.Kill(_blueGo);
            RogueShared.Fx.Kill(_redHalo); RogueShared.Fx.Kill(_blueHalo);
            _baseGo = _redGo = _blueGo = _redHalo = _blueHalo = null;
            _baseT = _redT = _blueT = _redHaloT = _blueHaloT = null;
            _redR = _blueR = _redHaloR = _blueHaloR = null; _redLight = _blueLight = null;
        }
    }
}
