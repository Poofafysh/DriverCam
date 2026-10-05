using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DriverCam;

/// <summary>
/// 0.11.3: URP's motion blur off while the driver view is on a Bikes vehicle (the M2's own cabin, a bike's rider). The
/// blur reprojects the depth buffer with the camera's motion as if everything were standing still in the world, so a
/// cabin that moves with the camera at speed (the M2's dash, wheel and screen, half a metre from the eye) is smeared
/// across the screen. Every loaded VolumeProfile's active MotionBlur override is switched off once on entering (one
/// Resources scan, never per frame) and switched back on when the view leaves the Bikes vehicle, the driver view goes
/// off, or the plugin unloads. Profiles that had no active MotionBlur are not touched.
/// 0.11.4 note: this was not what smeared the M2's cabin. The game's own velocity blur (its URP renderer feature
/// MotionBlurVelocityFeature, the in-game motion blur setting) blurs every pixel along the car's speed except a mask
/// drawn from the vehicle layer(s); Bikes' car model sat on the Default layer. Bikes 0.2.3 puts it on the car's layer.
/// </summary>
internal static class MotionBlurGuard
{
    static readonly List<VolumeComponent> _off = new();
    static bool _on;
    static string _logged;

    /// <summary>Called each Apply: true while the driver view is on a Bikes vehicle. Cheap unless the state changes.</summary>
    internal static void Set(bool suppress)
    {
        if (suppress == _on) return;
        _on = suppress;
        if (suppress) Suppress(); else Restore();
    }

    static void Suppress()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<VolumeProfile>());
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i] != null ? all[i].TryCast<VolumeProfile>() : null;
                if (p == null || p.components == null) continue;
                var list = p.components;
                for (int k = 0; k < list.Count; k++)
                {
                    var c = list[k];
                    if (c == null || !c.active || c.TryCast<MotionBlur>() == null) continue;
                    c.active = false;
                    _off.Add(c);
                }
            }
            string log = _off.Count > 0 ? $"switched off {_off.Count} motion blur override(s)" : "no motion blur in the scene's profiles";
            if (_logged != log) { _logged = log; Plugin.Logger.LogInfo($"Driver view on a Bikes vehicle: {log} (back on when the view leaves it)."); }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Motion blur left as it is: {e.Message}");
        }
    }

    internal static void Restore()
    {
        _on = false;
        foreach (var c in _off)
        {
            try { if (c != null && !c.WasCollected) c.active = true; }
            catch (Exception) { /* the profile was unloaded */ }
        }
        _off.Clear();
    }
}
