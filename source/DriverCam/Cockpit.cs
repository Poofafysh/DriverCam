using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DriverCam;

/// <summary>
/// A simple procedural cockpit (dashboard, steering wheel, pillars, roof edge, mirror) built from
/// primitives, since the game's cars have no modeled interior. Lives unparented and is posed at the
/// driver's head every frame so the car's own transform scale can't distort it.
/// </summary>
internal static class Cockpit
{
    static GameObject _root;
    static Transform _wheel;
    static Material _dark, _darker, _trim, _glass;
    static Shader _shader;
    static Material _shaderTemplate;

    static readonly Color DashColor = new(0.07f, 0.07f, 0.08f);
    static readonly Color WheelColor = new(0.03f, 0.03f, 0.035f);
    static readonly Color TrimColor = new(0.14f, 0.14f, 0.16f);
    static readonly Color GlassColor = new(0.45f, 0.5f, 0.58f);

    public static bool Alive => _root != null && !_root.WasCollected;

    public static void SetVisible(bool visible)
    {
        if (Alive && _root.activeSelf != visible) _root.SetActive(visible);
    }

    static bool _usingModel;
    static float _modelScale = 1f;

    /// <summary>The cockpit model for the car being driven (set when the car changes).</summary>
    public static CockpitModel Current { get; private set; }
    public static string CarId { get; private set; }

    public static void SelectCar(string carId)
    {
        CarId = carId;
        Current = CockpitModel.LoadFor(carId);
        PartLayout.Prefix = carId + "/";
        CarPresets.SelectCar(carId);
    }

    /// <summary>True when the current cockpit is fitted to this car in its own coordinates.</summary>
    public static bool Fitted => Current != null && Current.BodyFrame;

    /// <summary>Scale that fits the generic Blender cockpit (built at real-car size) to this car; fitted cockpits use only the size setting.</summary>
    public static float ModelScale(Bounds car)
    {
        if (Current == null) return 1f;
        if (Current.BodyFrame) return Plugin.CockpitScale.Value;
        return car.size.x / Current.ReferenceWidth * Plugin.CockpitScale.Value;
    }

    /// <summary>How far left of the car's center line the driver's eyes go, in meters (generic cockpit only).</summary>
    public static float DriverSideOffset(Bounds car)
    {
        return Current == null || Current.BodyFrame ? 0f : Current.DriverSide * ModelScale(car);
    }

    /// <summary>Poses the cockpit: fitted cockpits sit on the car body, generic ones on the driver's eye.</summary>
    public static void UpdatePose(Vector3 headWorld, Vector3 bodyPos, Quaternion bodyRot, float turn)
    {
        if (!Alive) return;
        _root.transform.SetPositionAndRotation(Fitted ? bodyPos : headWorld, bodyRot);
        if (_usingModel)
        {
            _root.transform.localScale = Vector3.one * _modelScale;
            if (_wheel != null) _wheel.localRotation = Quaternion.Euler(0f, 0f, -turn * Plugin.SteerAngle.Value);
        }
        else if (_wheel != null)
        {
            _wheel.localRotation = Quaternion.Euler(22f, 0f, 0f) * Quaternion.Euler(0f, 0f, -turn * Plugin.SteerAngle.Value);
        }
    }

    /// <summary>Rebuilds the cockpit around the head position (all values in the body frame, meters).</summary>
    public static void Rebuild(Transform body, Bounds car, Vector3 head)
    {
        if (Alive) { Keep.Release(_root); Object.Destroy(_root); }
        EnsureMaterials(body);

        var model = Current;
        _usingModel = model != null;
        if (_usingModel)
        {
            BuildFromModel(model, body, car);
            return;
        }

        _root = Keep.Hold(new GameObject("DriverCam_Cockpit"));
        int layer = body.gameObject.layer;

        float w = car.size.x;
        float roofY = car.max.y - head.y - 0.04f;
        float beltY = car.min.y + car.size.y * 0.58f - head.y;

        // Dashboard and instrument hood in front of the driver
        Part("Dash", new Vector3(0f, beltY - 0.08f, 0.78f), new Vector3(w * 0.92f, 0.2f, 0.5f), Quaternion.identity, _dark, layer);
        Part("DashTop", new Vector3(0f, beltY + 0.03f, 0.62f), new Vector3(w * 0.9f, 0.04f, 0.22f), Quaternion.identity, _darker, layer);
        Part("Binnacle", new Vector3(0f, beltY + 0.08f, 0.55f), new Vector3(0.42f, 0.09f, 0.18f), Quaternion.Euler(-12f, 0f, 0f), _darker, layer);

        // Steering wheel: a ring of short segments, three spokes and a hub on a tilted pivot
        var pivot = new GameObject("WheelPivot").transform;
        pivot.SetParent(_root.transform, false);
        pivot.localPosition = new Vector3(0f, beltY - 0.06f, 0.42f);
        _wheel = pivot;

        const int segments = 18;
        const float radius = 0.18f;
        float segLen = 2f * Mathf.PI * radius / segments * 1.15f;
        for (int i = 0; i < segments; i++)
        {
            float a = i * 360f / segments;
            float rad = a * Mathf.Deg2Rad;
            Part("Rim", new Vector3(Mathf.Cos(rad) * radius, Mathf.Sin(rad) * radius, 0f), new Vector3(segLen, 0.032f, 0.032f),
                Quaternion.Euler(0f, 0f, a + 90f), _darker, layer, pivot);
        }
        foreach (float a in new[] { 0f, 180f, 270f })
        {
            float rad = a * Mathf.Deg2Rad;
            Part("Spoke", new Vector3(Mathf.Cos(rad) * radius * 0.5f, Mathf.Sin(rad) * radius * 0.5f, 0.005f), new Vector3(radius, 0.03f, 0.018f),
                Quaternion.Euler(0f, 0f, a), _dark, layer, pivot);
        }
        Part("Hub", new Vector3(0f, 0f, 0.01f), new Vector3(0.08f, 0.08f, 0.04f), Quaternion.identity, _trim, layer, pivot);
        Part("Column", new Vector3(0f, 0f, 0.15f), new Vector3(0.05f, 0.05f, 0.26f), Quaternion.identity, _darker, layer, pivot);

        // Windshield frame: A-pillars, roof header, mirror
        foreach (float side in new[] { -1f, 1f })
        {
            var bottom = new Vector3(side * w * 0.46f, beltY, 0.65f);
            var top = new Vector3(side * w * 0.41f, roofY, 0.02f);
            Beam("APillar", bottom, top, 0.07f, _trim, layer);
            Part("DoorTop", new Vector3(side * w * 0.48f, beltY - 0.02f, -0.15f), new Vector3(0.07f, 0.05f, 1.3f), Quaternion.identity, _trim, layer);
        }
        Part("RoofHeader", new Vector3(0f, roofY, 0.02f), new Vector3(w * 0.86f, 0.06f, 0.14f), Quaternion.identity, _trim, layer);
        Part("MirrorStem", new Vector3(0f, roofY - 0.04f, 0.1f), new Vector3(0.02f, 0.06f, 0.02f), Quaternion.identity, _darker, layer);
        Part("Mirror", new Vector3(0f, roofY - 0.09f, 0.1f), new Vector3(0.26f, 0.07f, 0.025f), Quaternion.identity, _glass, layer);

        _root.SetActive(false);
    }

    // ---------------------------------------------------------------- Blender cockpit

    static readonly Dictionary<string, Material> _tagMaterials = new();

    // Each movable group of the model: its transform and the center it pivots around (model space)
    static readonly Dictionary<string, (Transform t, Vector3 center)> _groups = new();

    /// <summary>Group names in the order they appear in the model, for the Parts editor.</summary>
    public static readonly List<string> GroupNames = new();

    static void BuildFromModel(CockpitModel model, Transform body, Bounds car)
    {
        _modelScale = ModelScale(car);
        _root = Keep.Hold(new GameObject("DriverCam_Cockpit"));
        _root.transform.localScale = Vector3.one * _modelScale;
        _wheel = null;
        _groups.Clear();
        GroupNames.Clear();
        int layer = body.gameObject.layer;

        // Gather parts per group so each group can be moved, rotated and scaled around its own center
        var partsByGroup = new Dictionary<string, List<CockpitModel.Part>>();
        foreach (var part in model.Parts)
        {
            if (!partsByGroup.TryGetValue(part.Group, out var list))
            {
                list = new List<CockpitModel.Part>();
                partsByGroup[part.Group] = list;
                GroupNames.Add(part.Group);
            }
            list.Add(part);
        }

        foreach (var (groupName, parts) in partsByGroup)
        {
            var center = GroupCenter(parts);
            var group = new GameObject("Group_" + groupName).transform;
            group.SetParent(_root.transform, false);
            _groups[groupName] = (group, center);

            // Merge the group's static parts per material; the steering wheel gets its own spinning pivot
            var staticByTag = new Dictionary<string, (List<Vector3> v, List<Vector3> n, List<Vector2> uv)>();
            foreach (var part in parts)
            {
                if (part.HasPivot)
                {
                    var pivot = new GameObject(part.Name + "Pivot").transform;
                    pivot.SetParent(group, false);
                    pivot.localPosition = part.PivotPos - center;
                    pivot.localRotation = Quaternion.LookRotation(part.PivotForward, part.PivotUp);
                    var spin = new GameObject(part.Name).transform;
                    spin.SetParent(pivot, false);
                    foreach (var (tag, lists) in part.ByTag)
                        AddMesh(spin, part.Name + "_" + tag, lists.verts, lists.normals, lists.uvs, TagMaterial(tag, body), layer, Textured(tag) || IsMirror(tag));
                    if (part.Name == "SteeringWheel") _wheel = spin;
                    continue;
                }

                foreach (var (tag, lists) in part.ByTag)
                {
                    if (!staticByTag.TryGetValue(tag, out var acc))
                    {
                        acc = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>());
                        staticByTag[tag] = acc;
                    }
                    foreach (var v in lists.verts) acc.v.Add(v - center);
                    acc.n.AddRange(lists.normals);
                    acc.uv.AddRange(lists.uvs);
                }
            }

            foreach (var (tag, acc) in staticByTag)
                            {
                var uv = IsMirror(tag) ? PlanarUVs(acc.v) : acc.uv;
                var r = AddMesh(group, groupName + "_" + tag, acc.v, acc.n, uv, TagMaterial(tag, body), layer, Textured(tag) || IsMirror(tag));
                if (IsMirror(tag)) MirrorView.SetGlass(tag, r);
            }
        }

        ApplyLayout();
        _root.SetActive(false);
        Plugin.Logger.LogInfo($"Built cockpit {model.Source} for {CarId} at scale {_modelScale:0.00} with parts: {string.Join(", ", GroupNames)}.");
    }

    static Vector3 GroupCenter(List<CockpitModel.Part> parts)
    {
        var b = new Bounds();
        bool any = false;
        foreach (var part in parts)
        {
            if (part.HasPivot)
            {
                if (!any) { b = new Bounds(part.PivotPos, Vector3.zero); any = true; }
                else b.Encapsulate(part.PivotPos);
                continue;
            }
            foreach (var lists in part.ByTag.Values)
                foreach (var v in lists.verts)
                {
                    if (!any) { b = new Bounds(v, Vector3.zero); any = true; }
                    else b.Encapsulate(v);
                }
        }
        return b.center;
    }

    /// <summary>Applies the saved Parts-editor placement to every group without rebuilding meshes.</summary>
    public static void ApplyLayout()
    {
        foreach (var (name, (t, center)) in _groups)
        {
            if (t == null || t.WasCollected) continue;
            var e = PartLayout.Get(name);
            t.localPosition = center + e.Position;
            t.localRotation = Quaternion.Euler(e.Rotation);
            t.localScale = Vector3.one * e.Scale;
        }
    }

    /// <summary>The cockpit model's current world scale (part offsets are in model units).</summary>
    public static float CurrentModelScale => _modelScale;

    /// <summary>Shows or hides one group (Edit mode blinks the selected part).</summary>
    public static void SetGroupVisible(string group, bool visible)
    {
        if (_groups.TryGetValue(group, out var g) && g.t != null && !g.t.WasCollected && g.t.gameObject.activeSelf != visible)
            g.t.gameObject.SetActive(visible);
    }

    /// <summary>UVs for a flat part (the mirror glass): spread over its two largest dimensions.</summary>
    static List<Vector2> PlanarUVs(List<Vector3> verts)
    {
        var b = new Bounds(verts[0], Vector3.zero);
        foreach (var v in verts) b.Encapsulate(v);
        var uvs = new List<Vector2>(verts.Count);
        foreach (var v in verts)
            uvs.Add(new Vector2((v.x - b.min.x) / Mathf.Max(1e-4f, b.size.x), (v.y - b.min.y) / Mathf.Max(1e-4f, b.size.y)));
        return uvs;
    }

    static Renderer AddMesh(Transform parent, string name, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, Material mat, int layer, bool textured)
    {
        var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
        mesh.vertices = verts.ToArray();
        mesh.normals = normals.ToArray();
        if (uvs != null && uvs.Count == verts.Count) mesh.uv = uvs.ToArray();
        var tris = new int[verts.Count];
        for (int i = 0; i < tris.Length; i++) tris[i] = i;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        var go = new GameObject(name);
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        // Textured parts (the interior shell) draw their own window outlines in the texture
        var outline = Plugin.CockpitOutline.Value && !textured ? OutlineMaterial() : null;
        if (outline != null) r.sharedMaterials = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(new[] { mat, outline });
        else r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }

    // ---------------------------------------------------------------- game-style outline

    /// <summary>The game's own cartoon-outline material, captured from the player's car.</summary>
    public static Material OutlineTemplate;
    static Material _outline;
    static float _outlineThicknessApplied = -1f;

    public static void CaptureStyle(Transform body)
    {
        foreach (var r in body.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null) continue;
                var name = m.shader.name;
                if (OutlineTemplate == null && name.Contains("Rogue_Outline")) OutlineTemplate = m;
                if (!_loggedAccessory && name.Contains("Car_Accesories") && !name.Contains("Decal")) LogMaterial(m);
            }
    }

    static Material OutlineMaterial()
    {
        if (OutlineTemplate == null || OutlineTemplate.WasCollected) return null;
        if (_outline == null || _outline.WasCollected)
        {
            _outline = Keep.Hold(new Material(OutlineTemplate) { name = "DriverCam_Outline" });
            _outline.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _outlineThicknessApplied = -1f;
        }
        // The cockpit is scaled up to fit the car, so thin the outline back down to match the car's own
        float wanted = Plugin.OutlineThickness.Value / Mathf.Max(0.1f, _modelScale);
        if (!Mathf.Approximately(wanted, _outlineThicknessApplied) && _outline.HasProperty("_Outline_Thickness"))
        {
            _outline.SetFloat("_Outline_Thickness", OutlineTemplate.GetFloat("_Outline_Thickness") * wanted);
            _outlineThicknessApplied = wanted;
        }
        return _outline;
    }

    static bool _loggedAccessory;

    /// <summary>Diagnostics for matching the game's toon look: every property of the car's accessory material.</summary>
    static void LogMaterial(Material m)
    {
        _loggedAccessory = true;
        var sh = m.shader;
        var sb = new System.Text.StringBuilder($"Accessory material '{m.name}' ({sh.name}):");
        for (int i = 0; i < sh.GetPropertyCount(); i++)
        {
            var prop = sh.GetPropertyName(i);
            var type = sh.GetPropertyType(i).ToString();
            string value = type switch
            {
                "Color" => m.GetColor(prop).ToString(),
                "Vector" => m.GetVector(prop).ToString(),
                "Float" or "Range" or "Int" => m.GetFloat(prop).ToString("0.###"),
                "Texture" => m.GetTexture(prop)?.name ?? "none",
                _ => "?",
            };
            sb.Append($" {prop}={value};");
        }
        Plugin.Logger.LogInfo(sb.ToString());
    }

    static bool Textured(string tag) => Current != null && Current.TagTextures.ContainsKey(tag);
    static bool IsMirror(string tag) => MirrorView.IsMirrorTag(tag);

    static readonly Dictionary<string, Texture2D> _textures = new();

    static Texture2D LoadTexture(string file)
    {
        var path = System.IO.Path.Combine(Current.ModelFolder, file);
        if (_textures.TryGetValue(path, out var cached) && cached != null && !cached.WasCollected) return cached;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        var bytes = System.IO.File.ReadAllBytes(path);
        if (!ImageConversion.LoadImage(tex, bytes))
        {
            Plugin.Logger.LogWarning($"Couldn't load cockpit texture {path}");
            return null;
        }
        tex.name = file;
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 4;
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _textures[path] = Keep.Hold(tex);
        return tex;
    }

    /// <summary>Alpha-cutout material for textured tags: the texture's transparent pixels become holes (windows).</summary>
    static Material TexturedMaterial(string tag)
    {
        var key = "tex:" + Current.Source + ":" + tag;
        if (!_tagMaterials.TryGetValue(key, out var m) || m == null || m.WasCollected)
        {
            m = Keep.Hold(Make(_shader, _shaderTemplate, Color.white));
            var tex = LoadTexture(Current.TagTextures[tag]);
            if (tex != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                m.mainTexture = tex;
            }
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = 2450;
            _tagMaterials[key] = m;
        }
        m.color = Color.white;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.2f);
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            var tex = m.mainTexture;
            if (tex != null && m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", tex);
            m.SetColor("_EmissionColor", Color.white * Plugin.InteriorBrightness.Value);
        }
        return m;
    }

    static Material TagMaterial(string tag, Transform body)
    {
        if (IsMirror(tag)) return MirrorView.GlassMaterial(tag, _shader);
        if (Textured(tag)) return TexturedMaterial(tag);
        // Neon palette to sit alongside the game's HUD: deep navy/purple cabin, cyan and hot-pink highlights
        var (color, smoothness, metallic, glow) = tag switch
        {
            "interior_dark" => (new Color(0.07f, 0.06f, 0.14f), 0.25f, 0f, 1f),
            "interior_mid" => (new Color(0.15f, 0.12f, 0.26f), 0.2f, 0f, 1f),
            "trim_dark" => (new Color(0.1f, 0.08f, 0.19f), 0.3f, 0f, 1f),
            "paint" => (CarPaintColor(body), 0.6f, 0.2f, 1f),
            "metal" => (new Color(0.62f, 0.66f, 0.8f), 0.75f, 0.6f, 1f),
            "glass" => (new Color(0.25f, 0.35f, 0.6f), 0.95f, 0.3f, 1.5f),
            "gauge_face" => (new Color(0.02f, 0.02f, 0.06f), 0.6f, 0f, 1f),
            "gauge_ring" => (new Color(0.1f, 0.9f, 1f), 0.6f, 0f, 4f),
            "needle" => (new Color(1f, 0.15f, 0.55f), 0.4f, 0f, 5f),
            "accent" => (new Color(0.1f, 0.85f, 1f), 0.5f, 0f, 5f),
            _ => (new Color(0.12f, 0.1f, 0.2f), 0.2f, 0f, 1f),
        };

        if (!_tagMaterials.TryGetValue(tag, out var m) || m == null || m.WasCollected)
        {
            m = Keep.Hold(Make(_shader, _shaderTemplate, color));
            _tagMaterials[tag] = m;
        }

        m.color = color;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);

        // A little self-glow keeps the cockpit readable on dark night tracks
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", color * (Plugin.InteriorBrightness.Value * glow));
        }
        return m;
    }

    static void Beam(string name, Vector3 from, Vector3 to, float thickness, Material mat, int layer)
    {
        var dir = to - from;
        Part(name, (from + to) * 0.5f, new Vector3(thickness, thickness, dir.magnitude), Quaternion.LookRotation(dir), mat, layer);
    }

    static void Part(string name, Vector3 localPos, Vector3 scale, Quaternion localRot, Material mat, int layer, Transform parent = null)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.layer = layer;

        // Primitives come with a collider; the cockpit must never touch the physics
        var col = go.GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = false;
            Object.Destroy(col);
        }

        var t = go.transform;
        t.SetParent(parent ?? _root.transform, false);
        t.localPosition = localPos;
        t.localRotation = localRot;
        t.localScale = scale;

        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    static void EnsureMaterials(Transform body)
    {
        if (_dark != null && !_dark.WasCollected)
        {
            _trim.color = CarPaintColor(body);
            return;
        }

        Shader shader = null;
        foreach (var name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Unlit" })
        {
            shader = Shader.Find(name);
            if (shader != null) break;
        }

        Material template = null;
        if (shader == null)
        {
            foreach (var r in body.GetComponentsInChildren<MeshRenderer>(true))
                if (r.sharedMaterial != null) { template = r.sharedMaterial; break; }
        }
        _shader = shader;
        _shaderTemplate = template;
        Plugin.Logger.LogInfo($"Cockpit material shader: {(shader != null ? shader.name : template != null ? "copied from car: " + template.shader.name : "none")}");

        _dark = Keep.Hold(Make(shader, template, DashColor));
        _darker = Keep.Hold(Make(shader, template, WheelColor));
        _trim = Keep.Hold(Make(shader, template, CarPaintColor(body)));
        _glass = Keep.Hold(Make(shader, template, GlassColor));
    }

    /// <summary>The car's main paint color, so pillars and roof trim match the outside of the car.</summary>
    static Color CarPaintColor(Transform body)
    {
        foreach (var r in body.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty("_Primary_Color"))
                {
                    var c = m.GetColor("_Primary_Color");
                    return new Color(c.r * 0.8f, c.g * 0.8f, c.b * 0.8f, 1f);
                }
        return TrimColor;
    }

    static Material Make(Shader shader, Material template, Color color)
    {
        var m = shader != null ? new Material(shader) : new Material(template);
        m.color = color;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.25f);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return m;
    }
}





