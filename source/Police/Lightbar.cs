using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Police
{
    /// <summary>
    /// A patrol car's lightbar: two small cubes (red, blue) and two point lights, all created and owned by us.
    /// Never parented to the car: Place() moves them onto the car's roof every LateUpdate, so a pooled car that gets
    /// reused can never carry our objects along. Each cube's collider is disabled and destroyed at once (physics is
    /// unaffected), each cube gets its own Material copy (destroyed with it). Unity APIs used are all in dump.cs:
    /// GameObject.CreatePrimitive, GetComponent/AddComponent, Collider.enabled, Renderer.sharedMaterial /
    /// shadowCastingMode / receiveShadows / enabled, new Material(Material), Material.HasProperty/SetColor(int),
    /// Shader.PropertyToID, Light.type/color/intensity/range/shadows/enabled, Transform.position/rotation/localScale,
    /// Object.Destroy.
    /// </summary>
    internal sealed class Lightbar
    {
        internal enum Look { Idle, FlashRed, FlashBlue, Off }

        private static readonly Color Red = new Color(1f, 0.08f, 0.06f), Blue = new Color(0.1f, 0.3f, 1f);
        private static readonly Color RedDim = new Color(0.35f, 0.03f, 0.03f), BlueDim = new Color(0.03f, 0.08f, 0.35f);
        private static readonly Vector3 CubeSize = new Vector3(0.45f, 0.14f, 0.28f);

        private static int s_colorId = -1;        // _BaseColor (URP Lit) or _Color (built-in), chosen once
        private static int s_emissionId = -1;
        private static bool s_checked, s_noColor;

        private GameObject _redGo, _blueGo;
        private Transform _redT, _blueT;
        private Material _redMat, _blueMat;
        private Light _redLight, _blueLight;
        private Look _look = (Look)(-1);

        internal static Lightbar Create()
        {
            var bar = new Lightbar();
            try
            {
                bar.MakeCube(true);
                bar.MakeCube(false);
                bar.Show(Look.Idle);
                return bar;
            }
            catch
            {
                bar.Destroy();   // never leave half a lightbar behind
                throw;
            }
        }

        private void MakeCube(bool red)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (red) _redGo = go; else _blueGo = go;   // tracked before anything else can throw
            go.name = red ? "Police.Lightbar.Red" : "Police.Lightbar.Blue";
            var col = go.GetComponent<Collider>();
            if (col != null) { col.enabled = false; UnityEngine.Object.Destroy(col); }
            var t = go.transform;
            t.localScale = CubeSize;
            t.position = new Vector3(0f, -1000f, 0f);   // out of sight until the first Place()

            var r = go.GetComponent<Renderer>();
            Material mat = null;
            if (r != null)
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                // CreatePrimitive's default material uses a built-in shader this URP build doesn't ship (it renders as
                // Hidden/InternalErrorShader), so build our own from a URP shader the game itself uses
                var shader = PickShader();
                var src = r.sharedMaterial;
                if (shader != null) mat = new Material(shader);
                else if (src != null) mat = new Material(src);
                if (mat != null)
                {
                    if (s_emissionKeyword) mat.EnableKeyword("_EMISSION");
                    r.sharedMaterial = mat;
                    CheckShader(mat);
                }
            }

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = red ? Red : Blue;
            light.range = 9f;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
            light.enabled = false;

            if (red) { _redT = t; _redMat = mat; _redLight = light; }
            else { _blueT = t; _blueMat = mat; _blueLight = light; }
        }

        private static Shader s_shader;
        private static bool s_shaderSearched, s_emissionKeyword;

        /// <summary>
        /// Once: an unlit URP shader if the build has one (always bright, ignores scene lighting), else URP Lit (used by
        /// hundreds of the game's own materials) with emission on. Null = keep the primitive's material.
        /// </summary>
        private static Shader PickShader()
        {
            if (s_shaderSearched) return s_shader;
            s_shaderSearched = true;
            foreach (var name in new[] { "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Particles/Unlit", "Universal Render Pipeline/Lit" })
            {
                var sh = Shader.Find(name);
                if (sh != null) { s_shader = sh; s_emissionKeyword = name.EndsWith("/Lit"); break; }
            }
            return s_shader;
        }

        /// <summary>Once: which colour property the shader has. Logged, so a pink / uncoloured cube can be explained.</summary>
        private static void CheckShader(Material mat)
        {
            if (s_checked) return;
            s_checked = true;
            string shader = "?";
            try { var sh = mat.shader; if (sh != null) shader = sh.name; } catch { /* name only for the log */ }
            int baseColor = Shader.PropertyToID("_BaseColor"), color = Shader.PropertyToID("_Color");
            if (mat.HasProperty(baseColor)) s_colorId = baseColor;
            else if (mat.HasProperty(color)) s_colorId = color;
            else s_noColor = true;
            int emission = Shader.PropertyToID("_EmissionColor");
            if (mat.HasProperty(emission)) s_emissionId = emission;
            Plugin.Log.LogInfo($"[Police] lightbar material: shader '{shader}', colour {(s_noColor ? "none (cubes keep the default colour, lights still flash)" : "ok")}" +
                               $"{(s_emissionId >= 0 ? ", emission property present" : "")}");
        }

        /// <summary>Moves the cubes and lights onto the roof: top centre of the car, offset sideways by +-0.35 m.</summary>
        internal void Place(Vector3 roof, Quaternion rotation, Vector3 right)
        {
            if (_redT != null) { _redT.position = roof - right * 0.35f; _redT.rotation = rotation; }
            if (_blueT != null) { _blueT.position = roof + right * 0.35f; _blueT.rotation = rotation; }
        }

        /// <summary>Idle: cubes dim, lights off. Flash: one side bright with its light on. Off: both dark (ESCAPED / released).</summary>
        internal void Show(Look look)
        {
            if (look == _look) return;   // colours and lights change only when the look changes (4x a second while flashing)
            _look = look;
            bool red = look == Look.FlashRed, blue = look == Look.FlashBlue;
            SetColor(_redMat, red ? Red : look == Look.Off ? Color.black : RedDim, red);
            SetColor(_blueMat, blue ? Blue : look == Look.Off ? Color.black : BlueDim, blue);
            SetLight(_redLight, red);
            SetLight(_blueLight, blue);
        }

        private static void SetColor(Material mat, Color c, bool glow)
        {
            if (mat == null || s_noColor || s_colorId < 0) return;
            mat.SetColor(s_colorId, c);
            if (s_emissionId >= 0) mat.SetColor(s_emissionId, glow ? c * 2f : Color.black);
        }

        private static void SetLight(Light light, bool on)
        {
            if (light == null) return;
            light.intensity = on ? 6f : 0f;
            light.enabled = on;
        }

        /// <summary>Destroys everything this lightbar created. Safe to call twice and after a scene change (already gone).</summary>
        internal void Destroy()
        {
            Kill(_redMat); Kill(_blueMat);
            Kill(_redGo); Kill(_blueGo);   // takes the lights (components of the cubes) with them
            _redMat = _blueMat = null; _redGo = _blueGo = null; _redT = _blueT = null; _redLight = _blueLight = null;
        }

        private static void Kill(UnityEngine.Object o)
        {
            try { if (o != null) UnityEngine.Object.Destroy(o); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Police] lightbar cleanup: {e.Message}"); }
        }
    }
}
