using System;
using Game.Runtime.Vehicle;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace DriverCam;

/// <summary>
/// 0.11.2: the driver view on a Bikes motorcycle (Poofafysh's Bikes plugin). No assembly reference either way; it reads
/// Bikes' documented stable names, as the Driver plugin does:
/// - a bike is a body with a "Bikes.Lean" node (the bike frame: metres, origin on the ground midway between the axles,
///   +z forward; it leans with the bike) inside a body copy named "Bikes.&lt;Key&gt;_Body". Looked up once per body (the
///   car's VehicleSkin, then the camera's body and its parent), and once more a second later in case the body was still
///   being built; never per frame.
/// - the M2 G87, Bikes' car model, is a "Bikes.Car" node instead (the model, scaled to the donor's wheelbase,
///   with its own cabin: dash, wheel, seats, see-through glass). It's treated like a bike (IsCar): no donor cockpit,
///   HideCarBody or outline strip, so only the M2's own cabin shows (0.11.3: plus CarCabin's mirrors, cluster readout and
///   Driver seat data on Bikes' cabin sockets); the eye is Bikes' "Bikes.Eye" node
///   under Bikes.Car (the driver's eye in that cabin), else the same point estimated here; no horizon roll.
/// - the eye: the Driver plugin's rider when it rides the bike (its "Driver_Helmet" node under Bikes.Lean sits on the
///   head bone; the eye is Driver's eye_c socket on that bone, so the camera moves with the rider's tuck, hang-off and
///   head), looked up at most once a second while missing; else an estimate from Bikes' seat socket (AppDomain
///   "rogue.bikes.rider.&lt;Key&gt;", float[11], seat xyz first) or, without it, the S1000RR's seat.
/// - the horizon: the camera rolls with Bike.CameraLean of the bike's lean (0 = level horizon, the default).
/// While on a bike DriverView shows no cockpit (so no wheel, gauges or mirror cameras), skips the car's seat / eye and
/// DriverLink (a car model: CarCabin's mirrors, readout and DriverLink.PublishCar instead), and DriverCamBehaviour skips
/// HideCarBody and the outline strip. Everything comes back by itself on a game car.
/// Read only towards the game and the other plugins.
/// </summary>
internal static class BikeLink
{
    static IntPtr _bodyPtr;
    static float _recheckAt = -1f, _nextHeadLookup;
    static Transform _lean, _head;   // _lean: the bike frame, or the car model's root ("Bikes.Car") when _isCar
    static Transform _bars;          // Bikes' "Bikes.Bars" (the handlebar pivot): the eye stays behind and above it
    // 0.11.5: the eye at least this far behind / above the bars along the bike's own axes (metres). A tucked rider's
    // helmet sits almost over the bars, which put them below the view ("you can't even see the handle bars")
    const float BarsBack = 0.55f, BarsUp = 0.30f;
    /// <summary>Extra downward pitch on a motorcycle (degrees), so the bars, tank and clocks are in view.</summary>
    internal const float PitchDown = 8f;
    static Transform _eyeNode;       // a car model: Bikes' "Bikes.Eye" node (null = the estimate below)
    static bool _isCar;
    static string _key, _logged;
    static Vector3 _seatEye;     // bike frame, metres: the estimate when Driver's rider isn't there
    static string _seatFrom;

    // Driver's eye_c socket on its head bone (Driver/Assets/model/driver_sockets.json, head-bone local metres)
    static readonly Vector3 DriverEye = FM.V3(0f, 0.085f, -0.06f);
    // a sport-bike tuck's eye from the seat point (pelvis 26 deg, torso 52 deg from vertical, as Driver's ride_sportbike)
    const float TuckUp = 0.58f, TuckForward = 0.45f;
    // the M2 G87's driver eye in its model frame (metres), as Bikes' Garage.cs: used when Bikes.Eye is missing
    static readonly Vector3 CarEye = FM.V3(-0.37f, 1.12f, -0.40f);

    /// <summary>True while On and the vehicle is Bikes' car model (M2 G87): its own cabin, no lean.</summary>
    internal static bool IsCar => _isCar;

    /// <summary>Bikes' car model root ("Bikes.Car") while IsCar, else null.</summary>
    internal static Transform CarRoot => _isCar ? _lean : null;

    /// <summary>The bike frame is still there (checked before each pose: OnBeforeRender runs after Apply's check).</summary>
    internal static bool Alive => _lean != null && !_lean.WasCollected;

    /// <summary>
    /// True while the driven body is a Bikes motorcycle or Bikes' car model (M2 G87) and Bike.DriverView is on. Cheap: a pointer compare per call;
    /// the hierarchy is searched once per body (plus one recheck a second later).
    /// </summary>
    internal static bool On(Transform body)
    {
        if (body == null || !Plugin.BikeDriverView.Value) return false;
        float now = Time.unscaledTime;
        if (body.Pointer != _bodyPtr)
        {
            _bodyPtr = body.Pointer;
            Find(body);
            _recheckAt = _lean == null ? now + 1f : -1f;
        }
        else if (_recheckAt > 0f && now >= _recheckAt)
        {
            _recheckAt = -1f;
            Find(body);
        }
        if (_lean != null && _lean.WasCollected) { _lean = null; _head = null; _eyeNode = null; _bars = null; }
        return _lean != null;
    }

    static void Find(Transform body)
    {
        _lean = null; _head = null; _eyeNode = null; _bars = null; _isCar = false; _key = null; _nextHeadLookup = 0f;
        Transform lean = null;
        var veh = VehicleManager.Instance;
        var skin = veh != null ? veh.VehicleSkin : null;
        if (skin != null) lean = FindVehicle(skin.transform);
        if (lean == null) lean = FindVehicle(body);
        if (lean == null && body.parent != null) lean = FindVehicle(body.parent);
        if (lean == null) return;
        _lean = lean;
        _isCar = lean.gameObject.name == "Bikes.Car";
        for (var t = lean; t != null && _key == null; t = t.parent)
        {
            string n = t.gameObject.name;
            int k = n.IndexOf("_Body", StringComparison.Ordinal);
            if (n.StartsWith("Bikes.", StringComparison.Ordinal) && k > 6) _key = n.Substring(6, k - 6);
        }
        if (_key == null) _key = "unknown";
        if (_isCar)
        {
            _eyeNode = FindNamed(lean, "Bikes.Eye");
            _seatFrom = _eyeNode != null ? "Bikes' eye socket" : "an estimate of the M2's seat";
            string clog = _key + "|car|" + _seatFrom;
            if (_logged != clog)
            {
                _logged = clog;
                Plugin.Logger.LogInfo($"Bikes car {_key}: driver view in its own cabin, without the donor car's cockpit or HideCarBody (mirrors and cluster readout on Bikes' sockets, CarCabin); eye at {_seatFrom}.");
            }
            return;
        }
        SeatEye();
        string log = _key + "|" + _seatFrom;
        if (_logged != log)
        {
            _logged = log;
            Plugin.Logger.LogInfo($"Bikes motorcycle {_key}: driver view without the car's cockpit, mirrors, gauges or HideCarBody; " +
                                  $"eye at the rider's head (Driver's rider when it rides the bike, else {_seatFrom}), horizon lean {Plugin.BikeCameraLean.Value:0.##}.");
        }
    }

    /// <summary>The estimated eye from the bike's seat socket (Bikes' AppDomain data, else the S1000RR's seat).</summary>
    static void SeatEye()
    {
        float sx = 0f, sy = 0.82f, sz = -0.18f;
        _seatFrom = "an estimate from the S1000RR's seat";
        if (AppDomain.CurrentDomain.GetData("rogue.bikes.rider." + _key) is float[] a && a.Length >= 3 &&
            float.IsFinite(a[0]) && float.IsFinite(a[1]) && float.IsFinite(a[2]) && a[1] > 0.4f && a[1] < 1.3f)
        {
            sx = a[0]; sy = a[1]; sz = a[2];
            _seatFrom = "an estimate from Bikes' seat socket";
        }
        _seatEye = FM.V3(sx, sy + TuckUp, sz + TuckForward);
    }

    /// <summary>A bike frame ("Bikes.Lean") or Bikes' car model ("Bikes.Car") under this transform, in one pass.</summary>
    static Transform FindVehicle(Transform under)
    {
        var all = under.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null) continue;
            string n = t.gameObject.name;
            if (n == "Bikes.Lean" || n == "Bikes.Car") return t;
        }
        return null;
    }

    static Transform FindNamed(Transform under, string name)
    {
        var all = under.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t != null && t.gameObject.name == name) return t;
        }
        return null;
    }

    /// <summary>
    /// The eye in world space: Driver's rider's eye when its helmet is under Bikes.Lean, else the seat estimate; plus the
    /// Bike.EyeUp / EyeForward offsets in the upright frame (frameRot). Call only while On.
    /// </summary>
    internal static Vector3 Eye(Quaternion frameRot)
    {
        if (_isCar)
        {
            // the driver's eye in the car model's own cabin (moves with the model, so with the body's shake)
            if (_eyeNode != null && _eyeNode.WasCollected) _eyeNode = null;
            var ce = _eyeNode != null ? _eyeNode.position : _lean.TransformPoint(CarEye);
            return FM.Add(ce, FM.Rotate(frameRot, FM.V3(0f, Plugin.BikeEyeUp.Value, Plugin.BikeEyeForward.Value)));
        }
        if (_head != null && _head.WasCollected) _head = null;
        if (_head == null && Time.unscaledTime >= _nextHeadLookup)
        {
            _nextHeadLookup = Time.unscaledTime + 1f;   // Driver builds (and rebuilds) its rider on its own schedule
            var helmet = FindNamed(_lean, "Driver_Helmet");
            _head = helmet != null ? helmet.parent : null;
            if (_bars == null) _bars = FindNamed(_lean, "Bikes.Bars");
            string log = _key + "|" + (_head != null ? "rider" : _seatFrom);
            if (_logged != log)
            {
                _logged = log;
                Plugin.Logger.LogInfo($"Bikes motorcycle {_key}: eye at {(_head != null ? "the Driver rider's head" : _seatFrom)}.");
            }
        }
        Vector3 eye;
        if (_head != null) eye = FM.Add(_head.position, FM.Rotate(_head.rotation, DriverEye));
        else
        {
            var lp = _lean.position;
            eye = FM.Add(lp, FM.Rotate(_lean.rotation, _seatEye));
        }
        if (_bars != null && _bars.WasCollected) _bars = null;
        if (_bars != null)
        {
            // keep the eye behind and above the bars in the bike's frame (it leans with the bike)
            var lr = _lean.rotation;
            var fwd = FM.Rotate(lr, FM.V3(0f, 0f, 1f));
            var up = FM.Rotate(lr, FM.V3(0f, 1f, 0f));
            var d = FM.Sub(eye, _bars.position);
            float along = FM.Dot(d, fwd), high = FM.Dot(d, up);
            if (along > -BarsBack) eye = FM.AddScaled(eye, fwd, -BarsBack - along);
            if (high < BarsUp) eye = FM.AddScaled(eye, up, BarsUp - high);
        }
        return FM.Add(eye, FM.Rotate(frameRot, FM.V3(0f, Plugin.BikeEyeUp.Value, Plugin.BikeEyeForward.Value)));
    }

    /// <summary>The camera's roll in degrees: Bike.CameraLean of the bike's lean, about the upright frame's forward axis.</summary>
    internal static float Roll(Quaternion frameRot)
    {
        float k = FM.Clamp01(Plugin.BikeCameraLean.Value);
        if (k <= 0f || _isCar) return 0f;   // a car model doesn't lean
        // the bike's up in the upright frame; a roll of +a about z turns (0, 1, 0) into (-sin a, cos a, 0)
        var up = FM.Rotate(Quaternion.Inverse(frameRot), FM.Rotate(_lean.rotation, FM.V3(0f, 1f, 0f)));
        float a = MathF.Atan2(-up.x, up.y) * (180f / MathF.PI);
        return a * k;
    }

    /// <summary>The bike body was left (car change, level end): drop the cached lookups.</summary>
    internal static void Reset()
    {
        _bodyPtr = IntPtr.Zero; _lean = null; _head = null; _eyeNode = null; _bars = null; _isCar = false; _key = null; _recheckAt = -1f;
    }
}
