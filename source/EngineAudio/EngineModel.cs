using System;

namespace EngineAudio
{
    /// <summary>What the engine model needs each frame (all read from the game; nothing is written back to the car).</summary>
    internal struct EngineInput
    {
        public float Speed;          // m/s
        public int Gear;             // the game's gearbox: 0 = first
        public float GearProgress;   // 0 at the gear's shift-in speed, 1 at its shift-out speed
        public bool Shifting;        // the gearbox is mid-shift
        public float Throttle;       // 0-1 (0 when not accelerating)
        public bool TopGear;         // in the gearbox's last gear (where a race is mostly spent: at top speed)
        public float Slip;           // 0-1 drift intensity (wheelspin flare under throttle)
        public float Dt;             // seconds
    }

    /// <summary>
    /// A simulated engine (design doc "Engine Audio - Realistic Engine Sound"): an RPM that follows the game's own
    /// gearbox, with a drop on every upshift, a flare when you rev standing still, a rev limiter at the top of the lower
    /// gears and spring smoothing. Plain .NET, tested outside the game.
    ///
    /// Top speed (0.2.0): a race is mostly spent flat out in the last gear at the car's top speed. There the engine holds
    /// a steady high note at TopSpeedShare of redline (not the limiter: the car is at its speed limit, not the engine at
    /// its rev limit), with a slow natural wander of about +-1% (load, road, wind) so the note lives instead of sitting on
    /// one frequency. A drift under throttle flares the revs (the driven wheels spin up).
    /// </summary>
    internal sealed class EngineModel
    {
        public float Idle = 900f, Redline = 7500f;
        public float ShiftDropShare = 0.58f;   // RPM right after an upshift, as a share of redline
        public float TopSpeedShare = 0.93f;    // RPM held at top speed in the last gear, as a share of redline
        public bool Limiter = true;
        public bool DriftFlare = true;

        public float Rpm { get; private set; } = 900f;
        public int Gear { get; private set; }
        /// <summary>RPM position inside the current gear's span (0 = shift-in RPM, 1 = redline): where to play the gear's sweep.</summary>
        public float Span { get; private set; }
        /// <summary>Set for one frame when the gear went up (blow-off, shift sound).</summary>
        public bool Upshifted { get; private set; }
        public bool Downshifted { get; private set; }
        public bool OnLimiter { get; private set; }

        private float _limiterClock, _fastFollow;
        private int _lastGear = -1;
        private float _wander, _wanderTarget, _wanderClock;
        private readonly Random _rng = new Random(7);

        /// <summary>RPM where a gear's span starts: idle for first gear, the post-shift RPM for the others.</summary>
        public float LowRpm(int gear) => gear <= 0 ? Idle : Redline * ShiftDropShare;

        public void Reset() { Rpm = Idle; _lastGear = -1; _fastFollow = 0f; }

        public void Step(EngineInput f)
        {
            Upshifted = Downshifted = OnLimiter = false;
            if (f.Dt <= 0f) return;
            int gear = Math.Max(0, f.Gear);
            if (_lastGear >= 0 && gear > _lastGear) { Upshifted = true; _fastFollow = 0.25f; }
            else if (_lastGear >= 0 && gear < _lastGear) { Downshifted = true; _fastFollow = 0.2f; }
            _lastGear = gear;
            Gear = gear;

            float throttle = Clamp01(f.Throttle);
            float p = Clamp01(f.GearProgress);
            float target;
            if (f.Speed < 1.5f)
                target = Idle + throttle * 0.55f * (Redline - Idle);   // standing still: blip the throttle, it revs
            else
            {
                float low = LowRpm(gear);
                float top = f.TopGear ? Redline * Clamp(TopSpeedShare, 0.7f, 0.99f) : Redline * 0.985f;
                target = low + (top - low) * p;
                target += throttle * 0.05f * Redline * (1f - p);       // a little flare under load low in the gear
                if (!f.Shifting && throttle < 0.05f) target -= 0.03f * Redline * (1f - p);   // lifting: slightly lower note
                if (f.TopGear)
                {
                    // the steady top-speed note wanders a little (new random target every 0.4-1.1 s, eased): it lives
                    _wanderClock -= f.Dt;
                    if (_wanderClock <= 0f) { _wanderClock = 0.4f + 0.7f * (float)_rng.NextDouble(); _wanderTarget = (float)_rng.NextDouble() * 2f - 1f; }
                    _wander += (_wanderTarget - _wander) * (1f - (float)Math.Exp(-1.5f * f.Dt));
                    target += _wander * 0.011f * Redline * p;
                }
                if (DriftFlare && f.Slip > 0f) target += 0.06f * Redline * Clamp01(f.Slip) * throttle;   // wheelspin in a drift
            }

            // rev limiter: holding the top of a lower gear at full throttle bounces off the limiter (~12 Hz). Never in the
            // last gear: at top speed the car is at its speed limit, the engine isn't banging off its rev limiter
            if (Limiter && !f.TopGear && f.Speed >= 1.5f && p > 0.985f && throttle > 0.85f && !f.Shifting)
            {
                _limiterClock += f.Dt;
                OnLimiter = true;
                target = Redline - ((int)(_limiterClock * 24f) % 2 == 0 ? 0f : 260f);
            }
            else _limiterClock = 0f;

            target = Math.Max(Idle * 0.95f, Math.Min(Redline * 0.995f, target));
            // spring follow: rises faster than it falls; right after a shift it snaps to the new gear quickly
            float k = _fastFollow > 0f ? 16f : target > Rpm ? 9f : 5f;
            if (OnLimiter) k = 40f;
            _fastFollow = Math.Max(0f, _fastFollow - f.Dt);
            Rpm += (target - Rpm) * (1f - (float)Math.Exp(-k * f.Dt));

            float lowNow = LowRpm(gear);
            Span = Clamp01((Rpm - lowNow) / Math.Max(1f, Redline - lowNow));
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
