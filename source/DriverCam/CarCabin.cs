using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using FM = RogueShared.FastMath;

namespace DriverCam;

/// <summary>
/// 0.11.3: DriverCam's working parts in a Bikes car model's own cabin (the M2 G87, BikeLink.IsCar). No donor cockpit;
/// instead, built once per car body from the empty sockets Bikes puts under "Bikes.Car" (its Garage.Socket: pose = the
/// surface's centre, +z into it, localScale (w, h, 1) = its size in model metres):
/// - mirrors: a flat glass quad under "Bikes.MirrorC" / "Bikes.MirrorL" / "Bikes.MirrorR" with MirrorView's material for
///   the rear-view / left / right mirror (MirrorView.SetGlass), so the same mirror cameras, settings and cost rules apply;
/// - the cluster: the Vector W8's digital readout (cockpit_Vektor.dcm part RL_Digital: 3 speed digits + 18 rpm bars, its
///   gauge face atlas) under "Bikes.Cluster", scaled to the socket's width and registered with Gauges, so it shows the
///   HUD's speed and the RPM (EngineAudio's, else GaugeRpm) like the W8's own panel;
/// - the driver: DriverLink.PublishCar with the M2's seat, eye and steering wheel (measured from BMW_M2_G87.csm, model
///   frame, scaled with Bikes.Car), so the Driver plugin sits in the M2's seat with its hands on the M2's wheel;
/// - 0.11.4, the steering wheel: Bikes 0.2.3 puts the M2's wheel on its own node "Bikes.SteeringWheel" (rim centre, +z
///   along the column toward the dash at rest); it is turned about its z exactly like a cockpit wheel (Cockpit.UpdatePose:
///   -turn x View.SteerAngle) and that spin and lock are published, so the Driver plugin's hands follow the rim. Its rest
///   rotation is put back in Leave. Without the node (Bikes 0.2.2: the wheel is part of the body) the spin published is 0.
/// Everything it builds lives under Bikes' body (gone with it); its meshes and the material are freed in Leave.
/// Missing sockets (Bikes 0.2.1 or older) = no mirrors / readout, logged once. Read only towards the game and Bikes.
/// </summary>
internal static class CarCabin
{
    // the M2 G87's cabin in its model frame (metres), from BMW_M2_G87.csm: steering wheel rim centre, its column axis
    // (toward the dash) and up, the rim's centreline radius; the driver seat cushion's top centre and the seat back's
    // front-bottom point
    static readonly Vector3 WheelCentre = FM.V3(-0.375f, 0.85f, 0.19f);
    static readonly Vector3 WheelForward = FM.V3(0f, -0.34f, 0.94f), WheelUp = FM.V3(0f, 0.94f, 0.34f);
    const float WheelRim = 0.175f;
    static readonly Vector3 SeatTop = FM.V3(-0.375f, 0.42f, -0.22f), SeatBack = FM.V3(-0.375f, 0.47f, -0.35f);
    const string ReadoutCockpit = "Vektor", ReadoutPart = "RL_Digital";

    static IntPtr _rootPtr;
    static Transform _root;
    static Quaternion _wheelRotLocal;
    static Transform _steer;               // Bikes' "Bikes.SteeringWheel" (0.2.3), null = the wheel is part of the body
    static Quaternion _steerRest;          // its localRotation as Bikes built it, put back in Unturn / Leave
    static bool _turned;                   // _steer is off its rest pose
    static bool _gauges;                  // the readout is registered with Gauges
    static readonly List<GameObject> _objects = new();
    static readonly List<Mesh> _meshes = new();
    static Material _gaugeMat;
    static CockpitModel _readoutModel;
    static string _logged;

    /// <summary>True while the cabin is built for a car model (between Enter and Leave).</summary>
    internal static bool Active => _root != null && !_root.WasCollected;

    /// <summary>Each Apply while the driver view is in a Bikes car model: builds once per car model root (a pointer compare otherwise).</summary>
    internal static void Enter(Transform root, Transform body)
    {
        if (root == null) return;
        if (root.Pointer == _rootPtr && Active) return;
        Leave();
        _rootPtr = root.Pointer;
        _root = root;
        _wheelRotLocal = Quaternion.LookRotation(WheelForward, WheelUp);
        int layer = body.gameObject.layer;
        Transform mc = null, ml = null, mr = null, cl = null;
        var all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null) continue;
            switch (t.gameObject.name)
            {
                case "Bikes.MirrorC": mc = t; break;
                case "Bikes.MirrorL": ml = t; break;
                case "Bikes.MirrorR": mr = t; break;
                case "Bikes.Cluster": cl = t; break;
                case "Bikes.SteeringWheel": _steer = t; _steerRest = t.localRotation; break;
            }
        }
        int mirrors = 0;
        if (Mirror(mc, "mirror_glass", layer)) mirrors++;
        if (Mirror(ml, "mirror_left", layer)) mirrors++;
        if (Mirror(mr, "mirror_right", layer)) mirrors++;
        string readout = cl == null ? "no Bikes.Cluster socket" : Readout(cl, layer);
        string log = $"{mirrors}|{readout}";
        if (_logged != log)
        {
            _logged = log;
            Plugin.Logger.LogInfo($"Bikes car cabin: {mirrors} of 3 mirrors on Bikes' mirror sockets{(mirrors < 3 ? " (Bikes 0.2.2 or newer has all three)" : "")}; cluster: {readout}; " +
                                  $"steering wheel {(_steer != null ? "turns with the steering (Bikes.SteeringWheel)" : "fixed in the body (Bikes 0.2.3 or newer turns it)")}; driver seat, eye and wheel published for the Driver plugin.");
        }
    }

    /// <summary>A mirror glass quad under its socket (size from the socket's scale), drawn by MirrorView. False when the socket is missing.</summary>
    static bool Mirror(Transform socket, string tag, int layer)
    {
        if (socket == null) return false;
        var s = socket.localScale;
        float w = Math.Max(0.02f, s.x), h = Math.Max(0.02f, s.y);
        var go = Child(socket, "DriverCam_" + tag, layer);
        go.transform.localScale = FM.V3(1f / w, 1f / h, 1f);   // undo the socket's size scale: the mesh carries the size
        var mesh = Keep.Hold(new Mesh { name = "DriverCam_" + tag });
        float x = w * 0.5f, y = h * 0.5f;
        // in the socket's plane, facing -z (the driver); u from the driver's left to right, as Cockpit.PlanarUVs
        mesh.vertices = new[] { FM.V3(-x, -y, 0f), FM.V3(x, -y, 0f), FM.V3(x, y, 0f), FM.V3(-x, y, 0f) };
        mesh.normals = new[] { FM.V3(0f, 0f, -1f), FM.V3(0f, 0f, -1f), FM.V3(0f, 0f, -1f), FM.V3(0f, 0f, -1f) };
        mesh.uv = new[] { FM.V2(0f, 0f), FM.V2(1f, 0f), FM.V2(1f, 1f), FM.V2(0f, 1f) };
        mesh.triangles = new[] { 0, 3, 2, 0, 2, 1 };
        mesh.RecalculateBounds();
        _meshes.Add(mesh);
        MirrorView.SetAspect(tag, w / h);   // sizes the mirror's texture if no cockpit made it yet
        var r = AddRenderer(go, mesh, MirrorView.GlassMaterial(tag, LitShader()));
        MirrorView.SetGlass(tag, r);
        return true;
    }

    /// <summary>The W8's digital readout under the cluster socket, registered with Gauges. Returns what the log says.</summary>
    static string Readout(Transform socket, int layer)
    {
        if (_readoutModel == null) _readoutModel = CockpitModel.LoadFor(ReadoutCockpit);
        var model = _readoutModel;
        if (model == null || model.Source != "cockpit_" + ReadoutCockpit + ".dcm") return $"no cockpit_{ReadoutCockpit}.dcm (no digital readout)";
        CockpitModel.Part part = null;
        foreach (var p in model.Parts) if (p.Name == ReadoutPart && p.IsGauge) { part = p; break; }
        if (part == null || !part.ByTag.TryGetValue("gauges", out var lists) || lists.verts.Count < part.QuadCursor * 6)
            return $"no {ReadoutPart} in cockpit_{ReadoutCockpit}.dcm (no digital readout)";
        var mat = GaugeMaterial(model, false);
        if (mat == null) return "no gauge face texture (no digital readout)";
        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (var v in lists.verts) { if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x; }
        float panel = Math.Max(0.01f, maxX - minX);
        var s = socket.localScale;
        float k = Math.Max(0.02f, s.x) / panel;   // the readout fills the socket's width
        var go = Child(socket, "DriverCam_ClusterReadout", layer);
        go.transform.localScale = FM.V3(k / Math.Max(0.02f, s.x), k / Math.Max(0.02f, s.y), k / Math.Max(0.02f, s.z));
        // as Cockpit.AddMesh for a pivot part: not welded, corners in file order (Gauges addresses the quads by order)
        var mesh = Keep.Hold(new Mesh { name = "DriverCam_ClusterReadout" });
        var tris = new int[lists.verts.Count];
        for (int i = 0; i < tris.Length; i++) tris[i] = i;
        mesh.vertices = lists.verts.ToArray();
        mesh.normals = lists.normals.ToArray();
        mesh.uv = lists.uvs.ToArray();
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        _meshes.Add(mesh);
        AddRenderer(go, mesh, mat);
        Gauges.Clear(model);
        Gauges.Register(go.transform, part, mesh, lists.uvs);
        _gauges = true;
        return $"the W8's digital readout ({ReadoutPart}, {panel * k * 100f:0} cm wide in model units)";
    }

    static GameObject Child(Transform parent, string name, int layer)
    {
        var go = Keep.Hold(new GameObject(name));
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FM.V3(0f, 0f, 0f);
        go.transform.localRotation = Quaternion.identity;
        _objects.Add(go);
        return go;
    }

    static Renderer AddRenderer(GameObject go, Mesh mesh, Material mat)
    {
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }

    static Shader LitShader()
    {
        var s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Universal Render Pipeline/Simple Lit");
        return s;
    }

    // ---------------------------------------------------------------- the readout's face atlas (km/h / mph)

    static readonly Dictionary<string, Texture2D> _textures = new();

    static Texture2D Texture(CockpitModel model, bool mph)
    {
        string tag = mph && Cockpit.HasMphFace(model) ? "gauges_mph" : "gauges";
        if (!model.TagTextures.TryGetValue(tag, out var file)) return null;
        var path = System.IO.Path.Combine(model.ModelFolder, file);
        if (_textures.TryGetValue(path, out var cached) && cached != null && !cached.WasCollected) return cached;
        if (!System.IO.File.Exists(path)) return null;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(tex); return null; }
        tex.name = file;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 4;
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _textures[path] = Keep.Hold(tex);
        return tex;
    }

    /// <summary>As Cockpit.TexturedMaterial for the "gauges" tag: alpha cutout (the atlas' empty texels are holes), self-lit.</summary>
    static Material GaugeMaterial(CockpitModel model, bool mph)
    {
        var tex = Texture(model, mph);
        if (tex == null) return null;
        if (_gaugeMat == null || _gaugeMat.WasCollected)
        {
            var shader = LitShader();
            if (shader == null) return null;
            _gaugeMat = Keep.Hold(new Material(shader) { name = "DriverCam_CarClusterReadout" });
            _gaugeMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (_gaugeMat.HasProperty("_AlphaClip")) _gaugeMat.SetFloat("_AlphaClip", 1f);
            if (_gaugeMat.HasProperty("_Cutoff")) _gaugeMat.SetFloat("_Cutoff", 0.5f);
            _gaugeMat.EnableKeyword("_ALPHATEST_ON");
            _gaugeMat.renderQueue = 2450;
            if (_gaugeMat.HasProperty("_Smoothness")) _gaugeMat.SetFloat("_Smoothness", 0.2f);
            _gaugeMat.color = Color.white;
        }
        SetTexture(tex);
        return _gaugeMat;
    }

    static void SetTexture(Texture2D tex)
    {
        if (_gaugeMat.mainTexture == tex) return;
        if (_gaugeMat.HasProperty("_BaseMap")) _gaugeMat.SetTexture("_BaseMap", tex);
        _gaugeMat.mainTexture = tex;
        if (_gaugeMat.HasProperty("_EmissionColor"))
        {
            _gaugeMat.EnableKeyword("_EMISSION");
            if (_gaugeMat.HasProperty("_EmissionMap")) _gaugeMat.SetTexture("_EmissionMap", tex);
            _gaugeMat.SetColor("_EmissionColor", Color.white);   // a lit display: readable in the dim cabin
        }
    }

    /// <summary>Gauges' km/h / mph switch (Gauges.Step, when the game's unit changes): the readout's face follows.</summary>
    internal static void SetGaugeFace(bool mph)
    {
        if (!_gauges || _readoutModel == null || _gaugeMat == null || _gaugeMat.WasCollected) return;
        var tex = Texture(_readoutModel, mph);
        if (tex != null) SetTexture(tex);
    }

    // ---------------------------------------------------------------- every pose

    /// <summary>
    /// Each pose in the car model (after the camera is placed): the readout (Gauges, once a frame), the mirror cameras and
    /// the Driver plugin's seat data (in the shaken body frame the Driver plugin uses too); the mirror cameras look back
    /// along the model's own frame (cabinRot), as the camera does.
    /// </summary>
    internal static void Pose(Vector3 shakenPos, Quaternion shakenRot, Quaternion cabinRot, Vector3 eyeWorld, Camera cam, float turn)
    {
        if (!Active) return;
        if (_gauges) Gauges.Update();
        MirrorView.UpdatePose(cabinRot, cam);
        var root = _root;
        var wheel = root.TransformPoint(WheelCentre);
        var wheelRot = root.rotation * _wheelRotLocal;
        float spin = 0f, steer = 0f;
        if (_steer != null && !_steer.WasCollected)
        {
            // as Cockpit.UpdatePose turns a cockpit's wheel: about the wheel's own z (the column), -turn x SteerAngle
            steer = Plugin.SteerAngle.Value;
            spin = -turn * steer;
            _steer.localRotation = _steerRest * Quaternion.Euler(0f, 0f, spin);
            _turned = true;
            wheel = _steer.position;
            wheelRot = _steer.parent.rotation * _steerRest;   // unspun: the Driver plugin adds the spin itself
        }
        float rim = WheelRim * root.lossyScale.x;
        DriverLink.PublishCar(shakenPos, shakenRot, eyeWorld, wheel, wheelRot, rim, root.TransformPoint(SeatTop), root.TransformPoint(SeatBack), spin, steer);
    }

    /// <summary>The steering wheel back to Bikes' rest pose (driver view off, Leave); a bool compare when it already is.</summary>
    internal static void Unturn()
    {
        if (!_turned) return;
        _turned = false;
        if (_steer != null && !_steer.WasCollected) _steer.localRotation = _steerRest;
    }

    /// <summary>Left the car model (car change, view off, Bike.DriverView off): mirrors off, readout and seat data dropped.</summary>
    internal static void Leave()
    {
        if (_rootPtr == IntPtr.Zero && _objects.Count == 0) return;
        _rootPtr = IntPtr.Zero;
        _root = null;
        Unturn();
        _steer = null;
        MirrorView.SetActive(false);
        DriverLink.InvalidateCar();
        if (_gauges) { Gauges.Clear(null); _gauges = false; }
        foreach (var go in _objects)
        {
            if (go != null && !go.WasCollected) UnityEngine.Object.Destroy(go);
            Keep.Release(go);
        }
        _objects.Clear();
        foreach (var m in _meshes)
        {
            if (m != null && !m.WasCollected) UnityEngine.Object.Destroy(m);
            Keep.Release(m);
        }
        _meshes.Clear();
    }
}
