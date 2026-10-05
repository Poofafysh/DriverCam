using System;
using UnityEngine;

namespace DriverCam;

/// <summary>
/// Publishes where the driver sits for the Driver plugin (3D driver in the player car) as AppDomain data
/// "rogue.drivercam" (float[26]) plus "rogue.drivercam.car" (the car id string). No assembly reference either way:
/// the reader looks the array up by name. Body frame = VehicleProvider.BodyTransform position and rotation, metres
/// (the same frame as DriverView's eye). Written only while the driver view is active (DriverView.ApplyPose).
///   [0] 1 = format version   [1] 1 = data valid for the car in rogue.drivercam.car   [2] 1 = driver view active
///   [3] generation (+1 whenever any published position changes)   [4] Time.unscaledTime of the last write
///   [5..7] eye   [8..10] steering wheel pivot (rim centre)   [11..14] wheel rotation, unspun (x, y, z, w)
///   [15] rim radius   [16] wheel spin, degrees (-turn * SteerAngle)   [17..19] driver seat cushion top centre
///   [20..22] driver seat back, front-bottom point   [23] SteerAngle   [24] Driver.Pitch   [25] Driver.LookIntoTurn
/// [1] is 0 for the procedural (non-model) cockpit: no steering wheel or seat to publish.
/// In a Bikes car model (the M2 G87) CarCabin publishes the model's own seat / eye / wheel instead (PublishCar), and
/// takes it back ([1] and [2] = 0) when it leaves the car model, so it never stands for the donor car.
/// </summary>
internal static class DriverLink
{
    internal const string Key = "rogue.drivercam", CarKey = "rogue.drivercam.car";
    internal const float RimRadius = 0.185f;   // the rim centreline radius of every shipped .dcm steering wheel (pivot space)
    internal static readonly float[] Shared = new float[26];
    static bool _installed;
    static string _car;

    static void Install()
    {
        _installed = true;
        Shared[0] = 1f;
        AppDomain.CurrentDomain.SetData(Key, Shared);
    }

    /// <summary>Once per pose while the driver view is active: everything in the shaken body frame (shakenPos / shakenRot).</summary>
    internal static void Publish(Vector3 shakenPos, Quaternion shakenRot, Vector3 eye, float turn)
    {
        var pivot = Cockpit.WheelPivot;
        bool valid = pivot != null && Cockpit.HasDriverSeat;
        Vector3 cushion = default, back = default;
        if (valid) Cockpit.DriverSeatWorld(out cushion, out back);
        float steer = Plugin.SteerAngle.Value;
        _carModel = false;
        Write(shakenPos, shakenRot, eye, valid, valid ? pivot.position : default, valid ? pivot.rotation : Quaternion.identity,
              valid ? RimRadius * pivot.lossyScale.x : 0f, cushion, back, -turn * steer, steer);
    }

    /// <summary>
    /// 0.11.3: the same data for a Bikes car model's own cabin (CarCabin, the M2 G87): world points and the wheel's world
    /// rotation (unspun), its rim radius in metres, and its spin and lock (0.11.4: -turn x SteerAngle and SteerAngle as
    /// for a cockpit wheel when Bikes 0.2.3's turning Bikes.SteeringWheel is there; both 0 for a wheel fixed in the body).
    /// Valid for the car id (the donor's, as the Driver plugin names the car) only until InvalidateCar.
    /// </summary>
    internal static void PublishCar(Vector3 shakenPos, Quaternion shakenRot, Vector3 eyeWorld, Vector3 wheelPos, Quaternion wheelRot,
                                    float rim, Vector3 cushion, Vector3 back, float spin, float steer)
    {
        var inv = Quaternion.Inverse(shakenRot);
        _carModel = true;
        Write(shakenPos, shakenRot, inv * (eyeWorld - shakenPos), true, wheelPos, wheelRot, rim, cushion, back, spin, steer);
    }

    /// <summary>Left a car model (CarCabin.Leave): its seat must not stand for the donor car, so [1] and [2] go off.</summary>
    internal static void InvalidateCar()
    {
        if (!_carModel) return;
        _carModel = false;
        Shared[1] = Shared[2] = 0f;
        Shared[3] += 1f;
    }

    static bool _carModel;   // the last write was a car model's cabin (PublishCar)

    static void Write(Vector3 shakenPos, Quaternion shakenRot, Vector3 eye, bool valid, Vector3 wheelPos, Quaternion wheelRot,
                      float rim, Vector3 cushion, Vector3 back, float spin, float steer)
    {
        if (!_installed) Install();
        var car = Cockpit.CarId;
        if (!ReferenceEquals(car, _car)) { _car = car; AppDomain.CurrentDomain.SetData(CarKey, car); }
        var inv = Quaternion.Inverse(shakenRot);
        bool changed = false;
        Set(5, eye, ref changed);
        if (valid)
        {
            Set(8, inv * (wheelPos - shakenPos), ref changed);
            var r = inv * wheelRot;
            Set(11, r.x, ref changed); Set(12, r.y, ref changed); Set(13, r.z, ref changed); Set(14, r.w, ref changed);
            Set(15, rim, ref changed);
            Set(17, inv * (cushion - shakenPos), ref changed);
            Set(20, inv * (back - shakenPos), ref changed);
        }
        Shared[16] = spin;
        Set(23, steer, ref changed);
        Set(24, Plugin.Pitch.Value, ref changed);
        Set(25, Plugin.LookIntoTurn.Value, ref changed);
        if (changed) Shared[3] += 1f;
        Shared[1] = valid ? 1f : 0f;
        Shared[2] = 1f;
        Shared[4] = Time.unscaledTime;
    }

    static void Set(int i, Vector3 v, ref bool changed)
    {
        Set(i, v.x, ref changed); Set(i + 1, v.y, ref changed); Set(i + 2, v.z, ref changed);
    }

    static void Set(int i, float v, ref bool changed)
    {
        if (Math.Abs(Shared[i] - v) > 1e-4f) { Shared[i] = v; changed = true; }
    }

    /// <summary>The driver view was left (or reset): the seat data stays valid for the car, only [2] goes off.</summary>
    internal static void SetView(bool active) => Shared[2] = active ? 1f : 0f;

    internal static void Uninstall()
    {
        Shared[1] = Shared[2] = 0f;
        if (!_installed) return;
        _installed = false;
        try { AppDomain.CurrentDomain.SetData(Key, null); AppDomain.CurrentDomain.SetData(CarKey, null); } catch (Exception) { /* unloading */ }
    }
}
