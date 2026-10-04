using System;

namespace DriverCam;

/// <summary>
/// The tachometer's own engine when EngineAudio isn't publishing its RPM (not installed, F1 off, disabled, stale): the
/// same shape as EngineAudio's EngineModel, in the dial's own numbers. It climbs through each of the game's gears from
/// idle (first gear) or 58% of the red line (after an upshift) to 98.5% of it, holds 93% in the last gear at top speed,
/// revs with the throttle standing still, and follows with a spring (faster up than down, quick right after a shift).
/// Plain .NET, kept standalone so DriverCam and EngineAudio stay independent.
/// </summary>
internal sealed class GaugeRpm
{
    static readonly float[] FallbackRatios = { 0f, 0.25f, 0.4f, 0.6f, 0.8f };   // gears from speed, as EngineAudio

    float _rpm = -1f, _fastFollow;
    int _lastGear = -1;

    /// <summary>Takes over from EngineAudio's value without a jump.</summary>
    public void Sync(float rpm) { _rpm = rpm; }

    public float Step(in GaugeRead r, float idle, float red, float dt)
    {
        if (_rpm < 0f) _rpm = idle;
        if (dt <= 0f) return _rpm;
        int gear; float p;
        if (r.HasGearbox) { gear = Math.Max(0, r.Gear); p = r.GearProgress; }
        else FallbackGear(r.SpeedFactor, out gear, out p);
        int gears = r.HasGearbox ? Math.Max(2, r.Gears) : FallbackRatios.Length;
        if (_lastGear >= 0 && gear != _lastGear) _fastFollow = 0.25f;
        _lastGear = gear;

        p = p < 0f ? 0f : p > 1f ? 1f : p;
        float target;
        if (r.Speed < 1.5f) target = idle + r.Throttle * 0.55f * (red - idle);   // standing still: blipping revs it
        else
        {
            float low = gear == 0 ? idle : 0.58f * red;
            float top = gear >= gears - 1 ? 0.93f * red : 0.985f * red;
            target = low + (top - low) * p;
        }
        float k = _fastFollow > 0f ? 16f : target > _rpm ? 9f : 5f;
        _fastFollow = Math.Max(0f, _fastFollow - dt);
        _rpm += (target - _rpm) * (1f - (float)Math.Exp(-k * dt));
        return _rpm;
    }

    static void FallbackGear(float speedFactor, out int gear, out float progress)
    {
        gear = 0;
        for (int i = FallbackRatios.Length - 1; i >= 0; i--) if (speedFactor >= FallbackRatios[i]) { gear = i; break; }
        float lo = FallbackRatios[gear], hi = gear + 1 < FallbackRatios.Length ? FallbackRatios[gear + 1] : 1f;
        progress = Math.Max(0f, Math.Min(1f, (speedFactor - lo) / Math.Max(0.01f, hi - lo)));
    }
}
