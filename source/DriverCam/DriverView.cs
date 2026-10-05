using System;
using Game.Runtime.Cameras;
using UnityEngine;

namespace DriverCam;

/// <summary>
/// Places the camera at the driver's eyes each frame while the Driver mode is active, overriding
/// whatever pose the game's camera controller computed.
/// </summary>
internal static class DriverView
{
    static Transform _body;
    static Bounds _bodyBounds; // in the body's rotation frame, meters, relative to body.position
    static int _measuredVersion = -1;
    static float _turn;
    static float _lastErrorTime = -999f;

    public static bool IsActive(CameraControllerInGame ctrl) => ctrl != null && DriverMode.Is(ctrl.CurrentCameraMode);

    // Where the driver's eye was placed last frame, for exporting the car around it
    public static Transform LastBody;
    public static Vector3 LastHeadWorld;
    public static Quaternion LastBodyRotation = Quaternion.identity;
    public static float LastModelScale = 1f;
    public static Vector3 LastHeadInBody;

    /// <summary>Which car this is, from its main body mesh name ("Saber_Body" -> "Saber").</summary>
    public static string CarId(Transform body)
    {
        foreach (var r in body.GetComponentsInChildren<MeshRenderer>(false))
        {
            var n = r.name;
            if (n.Contains("Glitch") || n.Contains("Ghost")) continue;
            int i = n.IndexOf("_Body", StringComparison.OrdinalIgnoreCase);
            if (i > 0) return n.Substring(0, i);
        }
        return body.parent != null ? body.parent.name : body.name;
    }

    // The car's body mesh and its rest offset from the body transform: the game shakes the body visually
    // (VehicleVisuals.HandleBodyShake), so the cockpit follows the mesh itself rather than the vehicle transform.
    static Transform _bodyMesh;
    static Matrix4x4 _restMeshToBodyInv = Matrix4x4.identity;
    static CameraControllerInGame _ctrl;
    static Vector3 _head;
    static bool _active, _onBike;   // _onBike: a Bikes motorcycle (BikeLink): no cockpit, mirrors or car seat
    static bool _carCabin;          // CarCabin is built for a Bikes car model (the M2 G87)

    public static void Apply(CameraControllerInGame ctrl)
    {
        try
        {
            _ctrl = ctrl;
            _active = IsActive(ctrl);
            if (!_active)
            {
                Cockpit.SetVisible(false);
                MirrorView.SetActive(false);
                DriverLink.SetView(false);
                MotionBlurGuard.Set(false);
                CarCabin.Unturn();   // 0.11.4: the M2's wheel turns only in the driver view; straight again outside it
                return;
            }

            var vehicle = ctrl.VehicleProvider;
            var body = vehicle != null ? vehicle.BodyTransform : null;   // Il2Cpp object: explicit null check, not ?.
            var cam = ctrl.CurrentCamera;
            if (body == null || cam == null) return;

            if (_body == null || _body.WasCollected || _body.Pointer != body.Pointer)
            {
                _body = body;
                Cockpit.CaptureStyle(body);
                Cockpit.SelectCar(CarId(body));
                _bodyBounds = MeasureBody(body, vehicle.HoodCameraPivot);
                FindBodyMesh(body);
                _measuredVersion = -1;
            }

            float turnInput = Mathf.Clamp(vehicle.TurnInput, -1f, 1f);
            _turn = Mathf.Lerp(_turn, turnInput, 1f - Mathf.Exp(-10f * Time.deltaTime));

            // 0.11.2: a Bikes motorcycle shows only the bike and its rider, the M2 G87 only its own cabin (the donor's
            // cockpit, built or not, stays hidden: no wheel, gauges or mirror cameras). A car skips this block, so its path below is unchanged.
            _onBike = BikeLink.On(body);
            MotionBlurGuard.Set(_onBike);   // the blur smears a cabin / rider that moves with the camera (MotionBlurGuard)
            if (_onBike)
            {
                Cockpit.SetVisible(false);
                if (BikeLink.IsCar)
                {
                    // 0.11.3: the M2's own cabin gets DriverCam's mirrors, cluster readout and Driver seat data (CarCabin)
                    CarCabin.Enter(BikeLink.CarRoot, body);
                    _carCabin = true;
                }
                else
                {
                    if (_carCabin) { CarCabin.Leave(); _carCabin = false; }
                    MirrorView.SetActive(false);
                    DriverLink.SetView(false);
                }
                ApplyPose();
                return;
            }
            if (_carCabin)
            {
                // back from a car model on this body (Bike.DriverView switched off): CarCabin took the gauges and mirrors,
                // so the cockpit is built again
                CarCabin.Leave();
                _carCabin = false;
                _measuredVersion = -1;
            }
            _head = HeadInBodyFrame();

            if (_measuredVersion != Plugin.SettingsVersion || !Cockpit.Alive)
            {
                if (_measuredVersion >= 0)
                    Plugin.Logger.LogInfo($"Rebuilding cockpit: {(Cockpit.Alive ? "settings changed" : "cockpit object was lost")}.");
                Cockpit.Rebuild(body, _bodyBounds, _head);
                _measuredVersion = Plugin.SettingsVersion;
            }
            Cockpit.SetVisible(Plugin.ShowCockpit.Value);
            ApplyPose();
        }
        catch (Exception e)
        {
            LogThrottled(e);
        }
    }

    /// <summary>
    /// Runs right before the frame is drawn (after every LateUpdate, including the game's body shake), so the
    /// camera, cockpit and mirror use exactly the pose the car is rendered with.
    /// </summary>
    public static void OnBeforeRender()
    {
        try
        {
            if (_active && _ctrl != null && !_ctrl.WasCollected && IsActive(_ctrl)) ApplyPose();
        }
        catch (Exception e)
        {
            LogThrottled(e);
        }
    }

    static void ApplyPose()
    {
        var body = _body;
        var cam = _ctrl == null ? null : _ctrl.CurrentCamera;
        if (body == null || body.WasCollected || cam == null) return;
        if (_onBike) { ApplyBikePose(body, cam); return; }

        // Stable vehicle frame and the shaken (rendered) body frame
        var stablePos = body.position;
        var stableRot = body.rotation;
        BodyFrame(out var shakenPos, out var shakenRot);

        // The cockpit is part of the car: it always moves with the rendered body
        var cockpitHead = shakenPos + shakenRot * _head;
        Cockpit.UpdatePose(cockpitHead, shakenPos, shakenRot, _turn);
        DriverLink.Publish(shakenPos, shakenRot, _head, _turn);   // seat / eye / wheel for the Driver plugin

        // The driver's head follows the shake by the configured amount
        float follow = Mathf.Clamp01(Plugin.HeadFollowsShake.Value);
        var frameRot = Quaternion.Slerp(stableRot, shakenRot, follow);
        var framePos = Vector3.Lerp(stablePos, shakenPos, follow);
        var headWorld = framePos + frameRot * _head;
        // head turn from the HeadLook plugin when installed (right stick / right mouse), else 0; never in Edit mode,
        // where the right stick moves the seat, eye and parts
        float headYaw = 0f, headPitch = 0f;
        if (!EditMode.Active) HeadLookLink.Get(out headYaw, out headPitch);
        var lookRot = frameRot * Quaternion.Euler(Plugin.Pitch.Value - headPitch, _turn * Plugin.LookIntoTurn.Value + headYaw, 0f);
        cam.transform.SetPositionAndRotation(headWorld, lookRot);
        cam.nearClipPlane = Plugin.NearClip.Value;

        LastBody = body;
        LastHeadWorld = headWorld;
        LastBodyRotation = stableRot;
        LastModelScale = Cockpit.ModelScale(_bodyBounds);
        LastHeadInBody = _head;

        MirrorView.UpdatePose(shakenRot, cam);
    }

    /// <summary>
    /// The camera on a Bikes motorcycle: at the rider's eye (BikeLink.Eye), the upright body frame (with the head
    /// following the shake by HeadFollowsShake as in a car) rolled by Bike.CameraLean of the bike's lean, then the same
    /// pitch, look-into-turn and HeadLook as in a car. The cockpit is left alone; a car model (the M2) then gets
    /// CarCabin.Pose (its mirrors, cluster readout and the Driver plugin's seat data).
    /// </summary>
    static void ApplyBikePose(Transform body, Camera cam)
    {
        if (!BikeLink.Alive) return;   // the bike body went between Apply and this pose: the next Apply sorts it out
        BodyFrame(out var shakenPos, out var shakenRot);
        float follow = Mathf.Clamp01(Plugin.HeadFollowsShake.Value);
        // 0.11.3: in a car model the frame is the model's own rotation (Bikes.Car, under the game's visual body, which it
        // rolls, pitches and smooths after the rigidbody), read here in OnBeforeRender with the eye from Bikes.Eye: the
        // camera is rigid in the cabin (the donor's hidden body mesh, shakenRot, need not move with Bikes.Car)
        var cabin = BikeLink.CarRoot;
        var cabinRot = cabin != null ? cabin.rotation : shakenRot;
        var frameRot = Quaternion.Slerp(body.rotation, cabinRot, follow);
        var eye = BikeLink.Eye(frameRot);
        float roll = BikeLink.Roll(frameRot);
        float headYaw = 0f, headPitch = 0f;
        if (!EditMode.Active) HeadLookLink.Get(out headYaw, out headPitch);
        var lookRot = frameRot * Quaternion.Euler(0f, 0f, roll) * Quaternion.Euler(Plugin.Pitch.Value - headPitch, _turn * Plugin.LookIntoTurn.Value + headYaw, 0f);
        cam.transform.SetPositionAndRotation(eye, lookRot);
        cam.nearClipPlane = Plugin.NearClip.Value;
        if (_carCabin && BikeLink.IsCar) CarCabin.Pose(shakenPos, shakenRot, cabinRot, eye, cam, _turn);   // mirrors, readout, Driver seat, wheel spin
    }

    /// <summary>The body frame as currently rendered: the body mesh's pose with its rest offset removed.</summary>
    static void BodyFrame(out Vector3 pos, out Quaternion rot)
    {
        if (_bodyMesh == null || _bodyMesh.WasCollected)
        {
            pos = _body.position;
            rot = _body.rotation;
            return;
        }
        var m = _bodyMesh.localToWorldMatrix * _restMeshToBodyInv;
        pos = m.MultiplyPoint3x4(Vector3.zero);
        rot = m.rotation;
    }

    static void FindBodyMesh(Transform body)
    {
        _bodyMesh = null;
        foreach (var r in body.GetComponentsInChildren<MeshRenderer>(false))
        {
            var n = r.name;
            if (n.Contains("Glitch") || n.Contains("Ghost")) continue;
            if (n.IndexOf("_Body", StringComparison.OrdinalIgnoreCase) > 0) { _bodyMesh = r.transform; break; }
        }
        if (_bodyMesh == null) return;
        // Offset between the vehicle transform and the mesh at rest (no rotation/scale baked in: rotation-only frame)
        var bodyFrame = Matrix4x4.TRS(body.position, body.rotation, Vector3.one);
        _restMeshToBodyInv = (bodyFrame.inverse * _bodyMesh.localToWorldMatrix).inverse;
    }

    static void LogThrottled(Exception e)
    {
        if (Time.unscaledTime - _lastErrorTime > 5f)
        {
            _lastErrorTime = Time.unscaledTime;
            Plugin.Logger.LogError(e);
        }
    }

    /// <summary>Driver's eye position in the body's rotation frame (meters, relative to body.position).</summary>
    static Vector3 HeadInBodyFrame()
    {
        var offset = new Vector3(Plugin.OffsetX.Value, Plugin.OffsetY.Value, Plugin.OffsetZ.Value);
        if (Cockpit.Fitted) return Cockpit.Current.Eye + offset;

        var b = _bodyBounds;
        return new Vector3(
            b.center.x - Cockpit.DriverSideOffset(b) + Plugin.OffsetX.Value,
            b.min.y + b.size.y * Plugin.HeadHeight.Value + Plugin.OffsetY.Value,
            b.center.z + b.size.z * Plugin.HeadForward.Value + Plugin.OffsetZ.Value);
    }

    // Anything bigger than this isn't part of the car's shell (helper meshes, effects with oversized bounds)
    const float MaxPartSize = 8f;

    /// <summary>Measures the car's visible body in an unscaled frame aligned with the body.</summary>
    static Bounds MeasureBody(Transform body, Transform hoodPivot)
    {
        var inv = Quaternion.Inverse(body.rotation);
        var origin = body.position;
        var bounds = new Bounds();
        bool any = false;
        int used = 0, rejected = 0;

        foreach (var r in body.GetComponentsInChildren<Renderer>(false))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;

            var lb = r.localBounds;
            var t = r.transform;
            var part = new Bounds();
            for (int i = 0; i < 8; i++)
            {
                var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = inv * (t.TransformPoint(corner) - origin);
                if (i == 0) part = new Bounds(p, Vector3.zero);
                else part.Encapsulate(p);
            }

            var size = part.size;
            if (size.x > MaxPartSize || size.y > MaxPartSize || size.z > MaxPartSize)
            {
                if (rejected++ < 10) Plugin.Logger.LogInfo($"  ignoring oversized part '{r.name}' size={size}");
                continue;
            }

            if (!any) { bounds = part; any = true; }
            else bounds.Encapsulate(part);
            if (used < 60) LogPart(r, size);
            used++;
        }

        bool plausible = any && bounds.size.x is > 0.8f and < 4f && bounds.size.y is > 0.5f and < 3f && bounds.size.z is > 1.5f and < 8f;
        if (!plausible)
        {
            // Fall back to a typical car built around the game's own hood camera point
            var hood = hoodPivot != null ? inv * (hoodPivot.position - origin) : new Vector3(0f, 1.1f, 1.2f);
            var fallback = new Bounds(new Vector3(hood.x, hood.y - 0.45f, hood.z - 1.0f), new Vector3(1.9f, 1.35f, 4.4f));
            Plugin.Logger.LogWarning($"Car body measurement looked wrong (size={bounds.size}); using hood-based estimate instead.");
            bounds = fallback;
        }

        Plugin.Logger.LogInfo($"Measured car body '{body.name}': {used} meshes used, {rejected} ignored, size={bounds.size}, center={bounds.center}");
        return bounds;
    }

    static readonly System.Collections.Generic.HashSet<string> _loggedShaders = new();

    /// <summary>Diagnostics: what each car part is and how its material draws (culling, outline passes).</summary>
    static void LogPart(Renderer r, Vector3 size)
    {
        var sb = new System.Text.StringBuilder($"  part '{r.name}' size={size}");
        foreach (var m in r.sharedMaterials)
        {
            if (m == null) continue;
            var sh = m.shader;
            sb.Append($" | mat '{m.name}' shader '{(sh != null ? sh.name : null)}' passes=[");
            for (int p = 0; p < m.passCount; p++) sb.Append(p == 0 ? "" : ",").Append(m.GetPassName(p));
            sb.Append(']');
            foreach (var prop in new[] { "_Cull", "_CullMode", "_RenderFace", "_Outline", "_OutlineSize", "_OutlineWidth", "_Surface" })
                if (m.HasProperty(prop)) sb.Append($" {prop}={m.GetFloat(prop)}");

            if (sh != null && _loggedShaders.Add(sh.name))
            {
                var props = new System.Text.StringBuilder();
                for (int i = 0; i < sh.GetPropertyCount(); i++) props.Append(sh.GetPropertyName(i)).Append(' ');
                Plugin.Logger.LogInfo($"  shader '{sh.name}' properties: {props}");
            }
        }
        Plugin.Logger.LogInfo(sb.ToString());
    }

    public static void Reset()
    {
        _body = null;
        _active = false;
        _onBike = false;
        if (_carCabin) { CarCabin.Leave(); _carCabin = false; }
        MotionBlurGuard.Restore();
        BikeLink.Reset();
        Cockpit.SetVisible(false);
        MirrorView.SetActive(false);
        DriverLink.SetView(false);
    }
}



