using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

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
    }

    /// <summary>Cockpit material tag -> mirror.</summary>
    static readonly Dictionary<string, Mirror> _mirrors = new();

    public static bool IsMirrorTag(string tag) => tag == "mirror_glass" || tag == "mirror_left" || tag == "mirror_right";

    static Mirror Get(string tag)
    {
        if (_mirrors.TryGetValue(tag, out var m)) return m;
        var s = tag == "mirror_left" ? Plugin.LeftMirror : tag == "mirror_right" ? Plugin.RightMirror : Plugin.RearMirror;
        m = new Mirror { Settings = s };
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

    /// <summary>Places every mirror camera just in front of its glass, looking backwards along the car.</summary>
    public static void UpdatePose(Quaternion carRotation, Camera mainCamera)
    {
        foreach (var m in _mirrors.Values)
        {
            var s = m.Settings;
            bool want = s.Enabled.Value && Plugin.ShowCockpit.Value && m.Glass != null && !m.Glass.WasCollected
                        && m.Texture != null && m.Glass.gameObject.activeInHierarchy;
            if (!want)
            {
                if (m.Camera != null && !m.Camera.WasCollected) m.Camera.enabled = false;
                continue;
            }
            if (m.Camera == null || m.Camera.WasCollected)
            {
                var go = Keep.Hold(new GameObject("DriverCam_" + s.Name + "Camera"));
                Object.DontDestroyOnLoad(go);
                m.Camera = Keep.Hold(go.AddComponent<Camera>());
                m.Camera.targetTexture = m.Texture;
                m.Camera.nearClipPlane = 0.05f;
                m.Camera.farClipPlane = 400f;
                m.Camera.depth = -10f;
            }
            m.Camera.enabled = true;

            var look = carRotation * Quaternion.Euler(s.Pitch.Value, 180f + s.Yaw.Value, 0f);
            var offset = new Vector3(s.OffsetX.Value, s.OffsetY.Value, s.OffsetZ.Value - 0.03f);
            m.Camera.transform.SetPositionAndRotation(m.Glass.bounds.center + carRotation * offset, look);
            m.Camera.fieldOfView = s.Fov.Value;
            m.Camera.cullingMask = mainCamera.cullingMask;
        }
    }
}
