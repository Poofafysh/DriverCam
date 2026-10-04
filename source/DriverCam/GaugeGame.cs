using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace DriverCam;

/// <summary>What the working gauges read from the player's car each frame. Read only: nothing is written to the game.</summary>
internal struct GaugeRead
{
    public float Speed;         // m/s, |VehicleMovement.CurrentSpeed| (the HUD's speed before its unit multiplier)
    public float SpeedFactor;   // speed / VehicleMovement.CurrentMaxSpeed (gears from speed, when the gearbox is unreadable)
    public float Throttle;      // VehicleInputHandler.Throttle, 0 when not Accelerating
    public bool HasGearbox;
    public int Gear;            // VehicleGearboxHandler.CurrentGearIndex (0 = first)
    public int Gears;           // VehicleGearboxHandler.gearArray.Length, 5 if unreadable
    public float GearProgress;  // VehicleGearboxHandler.CurrentGearProgress (0 at the gear's shift-in speed, 1 at shift-out)
    public bool Shifting;       // VehicleGearboxHandler.IsShifting
}

/// <summary>
/// The only gauge file that touches game types (the same members EngineAudio's GameApi reads, plus the HUD's unit):
/// VehicleManager.Instance -> VehicleMovement.CurrentSpeed / CurrentMaxSpeed, VehicleInputHandler.Throttle / Accelerating,
/// VehicleGearboxHandler.CurrentGearIndex / CurrentGearProgress / IsShifting / gearArray, and
/// Singleton.Instance.SettingsManager.GameplaySettings.CurrentUnitSystem (Metric: 0 = KPH, 1 = MPH, the game's default).
/// The HUD (VehicleVisuals.Update -> GameEvents.OnSpeedUpdated) shows floor(CurrentSpeed x 3.6 x 1.1) in km/h or
/// floor(CurrentSpeed x 2.237 x 1.1) in mph; Gauges uses the same constants. The gearbox and the unit are optional:
/// if reading them fails once they are left alone for the session (gears from speed, mph).
/// </summary>
internal static class GaugeGame
{
    static VehicleManager _veh;
    static IntPtr _vehPtr;
    static VehicleMovement _move;
    static VehicleInputHandler _input;
    static VehicleGearboxHandler _gearbox;
    static int _gears = 5;
    static bool _gearboxOff, _unitOff;

    /// <summary>Reads the local player's car (cached per car object). False with no car.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Read(ref GaugeRead r)
    {
        var veh = VehicleManager.Instance;
        if (veh == null) { Forget(); return false; }
        if (_veh == null || _vehPtr != veh.Pointer || _move == null || _input == null)
        {
            _veh = veh; _vehPtr = veh.Pointer;
            _move = veh.VehicleMovement;
            _input = veh.VehicleInputHandler;
            _gearbox = null; _gears = 5;
            if (!_gearboxOff)
            {
                try
                {
                    _gearbox = GearboxOf(veh);
                    if (_gearbox != null) _gears = GearCount(_gearbox);
                }
                catch (Exception e) { GearboxFailed(e); }
            }
            if (_move == null || _input == null) { _veh = null; return false; }
        }
        r.Speed = Math.Abs(_move.CurrentSpeed);
        float max = _move.CurrentMaxSpeed;
        r.SpeedFactor = max > 1f ? FM.Clamp01(r.Speed / max) : 0f;
        r.Throttle = _input.Accelerating ? FM.Clamp01(_input.Throttle) : 0f;
        r.HasGearbox = _gearbox != null;
        r.Gears = _gears;
        if (_gearbox != null)
        {
            try { ReadGearbox(_gearbox, ref r); }
            catch (Exception e) { GearboxFailed(e); r.HasGearbox = false; }
        }
        return true;
    }

    /// <summary>The game's speed unit: 0 = km/h, 1 = mph, -1 = not readable right now (menus).</summary>
    internal static int Unit()
    {
        if (_unitOff) return 1;
        try { return UnitRaw(); }
        catch (Exception e)
        {
            _unitOff = true;
            Plugin.Logger.LogWarning($"Gauges: can't read the game's speed unit, showing mph (the game's default): {e.Message}");
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int UnitRaw()
    {
        var s = Singleton.Instance;
        if (s == null) return -1;
        var settings = s.SettingsManager;
        if (settings == null) return -1;
        var gameplay = settings.GameplaySettings;
        if (gameplay == null) return -1;
        return (int)gameplay.CurrentUnitSystem;
    }

    static void GearboxFailed(Exception e)
    {
        _gearboxOff = true;
        _gearbox = null;
        Plugin.Logger.LogWarning($"Gauges: can't read the game's gearbox, the tachometer follows gears worked out from speed: {e.Message}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static VehicleGearboxHandler GearboxOf(VehicleManager veh) => veh.VehicleGearboxHandler;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int GearCount(VehicleGearboxHandler gearbox)
    {
        var arr = gearbox.gearArray;
        return arr != null && arr.Length >= 2 ? arr.Length : 5;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ReadGearbox(VehicleGearboxHandler g, ref GaugeRead r)
    {
        r.Gear = g.CurrentGearIndex;
        r.GearProgress = g.CurrentGearProgress;
        r.Shifting = g.IsShifting;
    }

    internal static void Forget() { _veh = null; _vehPtr = IntPtr.Zero; _move = null; _input = null; _gearbox = null; _gears = 5; }
}
