using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using FM = RogueShared.FastMath;

namespace DriverCam;

/// <summary>Adjustable settings for one mirror (bound in Plugin, saved per car by CarPresets).</summary>
internal sealed class MirrorSettings
{
    public string Name;
    public ConfigEntry<bool> Enabled;
    public ConfigEntry<float> OffsetX, OffsetY, OffsetZ, Yaw, Pitch, Fov;
    public float Aspect = 4f;   // image width / height

    public IEnumerable<ConfigEntryBase> All()
    {
        yield return Enabled; yield return OffsetX; yield return OffsetY; yield return OffsetZ;
        yield return Yaw; yield return Pitch; yield return Fov;
    }
}

/// <summary>
/// Working mirrors: for each mirror glass in the cockpit (rear-view, left and right side mirrors) a small camera
/// just in front of the glass looks backwards and renders into a texture shown flipped on the glass.
///
/// Cost control (each extra camera repeats URP's culling, its own shadow-map pass and the draw submission, which
/// matters far more than its few pixels): the cameras skip shadows ([View] MirrorShadows), depth / colour copies,
/// post-processing, anti-aliasing and per-frame volume updates; they draw only [View] MirrorDrawDistance metres;
/// the two side mirrors take turns ([View] SideMirrorRate); and a mirror whose glass is outside the main camera's view
/// (head turned away) is not drawn at all. That last check uses the final camera pose of the same frame, so a mirror
/// coming into view is drawn in that frame and never shows a stale picture.
/// </summary>
internal static class MirrorView
{
    sealed class Mirror
    {
        public MirrorSettings Settings;
        public Camera Camera;
        public RenderTexture Texture;
        public Material Material;
        public Renderer Glass;
        public int Slot;              // 0 = rear-view, 1 = left, 2 = right: side mirrors take turns by slot
        public float AppliedFar = -1f;
        public int AppliedShadows = -1;
    }

    /// <summary>Cockpit material tag -> mirror.</summary>
    static readonly Dictionary<string, Mirror> _mirrors = new();

    public static bool IsMirrorTag(string tag) => tag == "mirror_glass" || tag == "mirror_left" || tag == "mirror_right";

    static Mirror Get(string tag)
    {
        if (_mirrors.TryGetValue(tag, out var m)) return m;
        var s = tag == "mirror_left" ? Plugin.LeftMirror : tag == "mirror_right" ? Plugin.RightMirror : Plugin.RearMirror;
        m = new Mirror { Settings = s, Slot = tag == "mirror_left" ? 1 : tag == "mirror_right" ? 2 : 0 };
        _mirrors[tag] = m;
        return m;
    }

    public static Material GlassMaterial(string tag, Shader litShader)
    {
        var m = Get(tag);
        if (m.Material != null && !m.Material.WasCollected) return m.Material;

        int w = Mathf.Clamp(Plugin.MirrorResolution.Value, 64, 2048);
        if (tag != "mirror_glass") w /= 2;   // side mirrors are small on screen
        int h = Mathf.Max(32, Mathf.RoundToInt(w / m.Settings.Aspect));
        m.Texture = Keep.Hold(new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "DriverCam_" + m.Settings.Name });
        m.Texture.Create();

        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = Keep.Hold(new Material(unlit != null ? unlit : litShader) { name = "DriverCam_" + m.Settings.Name + "Glass" });
        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap", m.Texture);
            mat.SetTextureScale("_BaseMap", new Vector2(-1f, 1f));   // mirrors show the world left/right reversed
            mat.SetTextureOffset("_BaseMap", new Vector2(1f, 0f));
        }
        mat.mainTexture = m.Texture;
        mat.mainTextureScale = new Vector2(-1f, 1f);
        mat.mainTextureOffset = new Vector2(1f, 0f);
        mat.color = Color.white;
        if (unlit == null && mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetTexture("_EmissionMap", m.Texture);
            mat.SetColor("_EmissionColor", Color.white);
        }
        mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
        m.Material = mat;
        return mat;
    }

    /// <summary>The glass' width / height before GlassMaterial first makes the mirror's texture (CarCabin's mirror quads).</summary>
    public static void SetAspect(string tag, float aspect) => Get(tag).Settings.Aspect = Mathf.Clamp(aspect, 0.5f, 8f);

    public static void SetGlass(string tag, Renderer glass)
    {
        var m = Get(tag);
        m.Glass = glass;
        var size = glass.localBounds.size;
        if (size.y > 1e-4f) m.Settings.Aspect = Mathf.Clamp(Mathf.Max(size.x, size.z) / size.y, 0.5f, 8f);
    }

    public static void SetActive(bool active)
    {
        foreach (var m in _mirrors.Values)
            if (m.Camera != null && !m.Camera.WasCollected && m.Camera.enabled != active && !active) m.Camera.enabled = false;
    }

    /// <summary>
    /// Places every mirror camera just in front of its glass, looking backwards along the car, and decides which
    /// mirrors draw this frame. Runs twice a frame (after the camera update and right before rendering); the decision
    /// depends only on the frame number and the final camera pose, so both runs agree.
    /// </summary>
    public static void UpdatePose(Quaternion carRotation, Camera mainCamera)
    {
        int frame = Time.frameCount;
        int sideRate = Math.Max(1, Math.Min(4, Plugin.SideMirrorRate.Value));
        Transform mainT = null;
        Vector3 camPos = default, camFwd = default;
        float tanDiag = 0f;
        foreach (var m in _mirrors.Values)
        {
            var s = m.Settings;
            bool want = s.Enabled.Value && Plugin.ShowCockpit.Value && m.Glass != null && !m.Glass.WasCollected
                        && m.Texture != null && m.Glass.gameObject.activeInHierarchy;
            // side mirrors take turns: with rate 2 the left one draws on odd frames, the right one on even frames
            if (want && m.Slot > 0 && sideRate > 1 && (frame + m.Slot) % sideRate != 0) want = false;
            Bounds glass = default;
            if (want)
            {
                glass = m.Glass.bounds;
                if (mainT == null)
                {
                    mainT = mainCamera.transform;
                    camPos = mainT.position;
                    camFwd = mainT.forward;
                    float aspect = mainCamera.aspect;
                    tanDiag = MathF.Tan(mainCamera.fieldOfView * 0.5f * (MathF.PI / 180f)) * MathF.Sqrt(1f + aspect * aspect);
                }
                if (!InView(glass, camPos, camFwd, tanDiag)) want = false;
            }
            if (!want)
            {
                if (m.Camera != null && !m.Camera.WasCollected && m.Camera.enabled) m.Camera.enabled = false;
                continue;
            }
            if (m.Camera == null || m.Camera.WasCollected)
            {
                var go = Keep.Hold(new GameObject("DriverCam_" + s.Name + "Camera"));
                UnityEngine.Object.DontDestroyOnLoad(go);
                m.Camera = Keep.Hold(go.AddComponent<Camera>());
                m.Camera.targetTexture = m.Texture;
                m.Camera.nearClipPlane = 0.05f;
                m.Camera.depth = -10f;
                m.AppliedFar = -1f;
                m.AppliedShadows = -1;
            }
            ApplyCost(m);
            if (!m.Camera.enabled) m.Camera.enabled = true;

            // plain field maths (Shared/FastMath.cs) where it is easy: Unity's vector operators are slow interop calls
            var look = carRotation * Quaternion.Euler(s.Pitch.Value, 180f + s.Yaw.Value, 0f);
            var offset = FM.Rotate(carRotation, FM.V3(s.OffsetX.Value, s.OffsetY.Value, s.OffsetZ.Value - 0.03f));
            m.Camera.transform.SetPositionAndRotation(FM.Add(glass.center, offset), look);
            m.Camera.fieldOfView = s.Fov.Value;
            m.Camera.cullingMask = mainCamera.cullingMask;
        }
    }

    /// <summary>
    /// True when the glass' bounding sphere overlaps the main camera's view cone (the cone around the frustum's
    /// diagonal, so it errs on the side of drawing). Behind or beside the head = false.
    /// </summary>
    static bool InView(Bounds glass, Vector3 camPos, Vector3 camFwd, float tanDiag)
    {
        var c = glass.center;
        float r = FM.Length(glass.extents);
        float dx = c.x - camPos.x, dy = c.y - camPos.y, dz = c.z - camPos.z;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist <= r + 0.05f) return true;   // the head is at the glass
        float cosToGlass = (dx * camFwd.x + dy * camFwd.y + dz * camFwd.z) / dist;
        float angle = MathF.Acos(Math.Clamp(cosToGlass, -1f, 1f));
        float half = MathF.Atan(tanDiag) + MathF.Asin(Math.Min(1f, r / dist)) + 0.05f;   // + ~3 degrees of margin
        return angle <= half;
    }

    /// <summary>
    /// The cheap camera setup: draw distance, and through URP's per-camera data no shadow pass (unless MirrorShadows),
    /// no depth / colour texture copies, no post-processing or anti-aliasing (a new camera's URP defaults already had
    /// post-processing and anti-aliasing off, so the picture only loses its shadows) and no per-frame volume update.
    /// Applied when a value changes; a failure is logged once and the mirrors keep working with URP's defaults.
    /// </summary>
    static void ApplyCost(Mirror m)
    {
        float far = Math.Clamp(Plugin.MirrorDrawDistance.Value, 20f, 1000f);
        if (far != m.AppliedFar)
        {
            m.Camera.farClipPlane = far;
            m.AppliedFar = far;
        }
        int shadows = Plugin.MirrorShadows.Value ? 1 : 0;
        if (shadows == m.AppliedShadows || _urpFailed) return;
        m.AppliedShadows = shadows;
        try
        {
            var data = CameraExtensions.GetUniversalAdditionalCameraData(m.Camera);
            if (data == null) return;
            data.renderShadows = shadows == 1;
            data.requiresDepthOption = CameraOverrideOption.Off;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            CameraExtensions.SetVolumeFrameworkUpdateMode(m.Camera, VolumeFrameworkUpdateMode.ViaScripting);
            CameraExtensions.UpdateVolumeStack(m.Camera, data); // once, so the camera's own stack starts from the scene's volumes
        }
        catch (Exception e)
        {
            _urpFailed = true;
            Plugin.Logger.LogWarning($"Mirror cameras keep URP's default settings (shadows on): {e.Message}");
        }
    }

    static bool _urpFailed;
}
