using System;

namespace Driver
{
    /// <summary>Upper-body styles (as RIDE 4's rider menu names them; our own numbers).</summary>
    public enum RideStyleKind { Balanced, ShouldersOut, OldSchool }

    /// <summary>Which foot goes down at a standstill.</summary>
    public enum FootSide { Left, Right }

    /// <summary>What the rider reads each frame (all from Driver's own reads: the game's inputs, Bikes.Lean's roll).</summary>
    internal struct RideSense
    {
        public float Speed;              // m/s (VehicleMovement.CurrentSpeed), -1 = unknown
        public float Lean;               // deg, + = leaning left (Bikes.Lean's roll)
        public float Steer;              // -1..1, + = right (the game's TurnInput, eased)
        public float Throttle, Brake;    // 0-1 (eased)
        public int Gear;                 // gear index, -1 = unknown
    }

    /// <summary>The [Bike] settings for one frame (a plain struct, read from the config each frame).</summary>
    internal struct RideOptions
    {
        public bool HangOff, Tuck, LegDangle, KneeDown, LookIntoCorner, FootDown;
        public RideStyleKind Style;
        public int BrakeFingers;         // 2 or 4
        public int FootSide;             // -1 left, +1 right
        public float MaxLean;            // deg, Bikes' [Look] MaxLean (50 when unknown)
    }

    /// <summary>The rider's layers for Solver.FrameBike, all eased (no step changes). Sides: -1 left, +1 right.</summary>
    internal struct RideOut
    {
        public bool On;
        public float HipX;               // m the hips sit off the seat centre (+ = right), rate-limited
        public float Lift;               // m the hips lift crossing the seat (chicane)
        public float BobY, SlideZ;       // m: small vertical bob, fore / aft slide on the seat (+ = forward)
        public float Body;               // deg the upper body leans in beyond the hips (+ = to the right)
        public float Pitch;              // deg the torso pitches forward (+) / sits up (-): tuck, launch, drive, braking, g
        public float Tuck, Launch, Brake, Drive;   // 0-1 weights (for the arms and head)
        public float HipK, KneeK;        // style scales of the hip slide and the inside knee
        public float LegOut, Waggle;     // 0-1 the inside leg off its peg under braking, -1..1 its sway
        public int LegSide;
        public float FootDown;           // 0-1 the foot on the ground at a standstill
        public int FootSide;
        public float KneeDown, ElbowDrop;   // 0-1 at the top of the lean range (inside knee wider, inside elbow down)
        public float HeadYaw, HeadRoll;  // deg: look into the corner (+ = right), head roll (+ = top to the right)
        public float HeadLevel;          // share of the lean the head rolls back toward the horizon
        public float ShiftW; public int ShiftDir;  // left foot on the shifter: 0-1 envelope, +1 up / -1 down
        public float Clutch;             // 0-1 two left fingers on the clutch lever
        public float Lever, LeverPull;   // 0-1 right fingers onto the brake lever, how far they pull it
        public int BrakeFingers;
        public float Twist;              // deg the right hand rolls the throttle back
    }

    /// <summary>
    /// The RIDE-style rider controller (Driver 0.4.0): turns speed, lean, steering, pedals and gear into eased body
    /// layers (RideOut). Original behaviour modelled on what players and reviewers describe of RIDE 4 / 5 and on real
    /// track technique; no assets or code from those games. Plain fields, no allocations, critically damped motion (the
    /// hip slide is a spring with a speed cap, so a side change is slow and visible, RIDE 5 style).
    ///   standstill: a foot down (below 1.5 m/s, 0.4 s); launch: forward over the tank, eased out by 15 m/s;
    ///   tuck: above 28 m/s on the throttle (over 60%), nearly upright, no brake; in 0.7 s, out in 0.25 s the moment the
    ///   throttle drops under 20%, the brake comes on or the lean passes 15 deg (RIDE 4's sit-up);
    ///   hard braking: sits up 20 deg and slides back; leg out: the inside leg leaves its peg under braking above 20 m/s,
    ///   back on the peg past 30% of MaxLean or under 20% brake;
    ///   hang-off: hips toward the inside of the lean 0.35 s ahead (spring, capped at 0.6 m/s), upper body 0.1 s behind,
    ///   knee down and elbow drop over 80% of MaxLean; chicane: the hips lift crossing the seat;
    ///   head: leads the lean (lean + rate x 0.35 s) and the steering, up to 26 deg yaw and 7 deg roll in; its tilt in
    ///   the world is half the bike's lean (whatever the upper body does); gear: the left foot taps the shifter, a downshift on the brakes pulls the clutch.
    /// </summary>
    internal sealed class RideBody
    {
        public RideOut Out;
        public string State { get; private set; } = "cruise";

        public const float HangHip = 0.15f, HangFull = 30f, HangBodySpan = 15f, HipSpeed = 0.6f, HipOmega = 12f;
        public const float TuckSpeed = 28f, TuckOutSpeed = 24f, LegOutSpeed = 20f, BrakeSpeed = 15f;
        public const float ShiftLen = 0.22f, ClutchLen = 0.2f, Lead = 0.35f;

        private bool _init, _tuckOn, _legOn, _launchOn, _brakeOn, _footOn, _fastOn;
        private float _lean, _leanRate, _spdF, _accel, _hipV, _yaw1, _roll1, _clock;
        private float _tuckR, _launchR, _brakeR, _driveR, _legR, _footR, _kneeR, _leverR, _brakeT;
        private float _shiftT = -1f, _clutchT = -1f;
        private int _gear = -1, _lastSide = 1, _legSide = 1;

        public RideBody() { Reset(); }

        /// <summary>Back to the seated cruise pose; the gear baseline is taken fresh (a restart never taps the shifter).</summary>
        public void Reset()
        {
            Out = default; Out.LegSide = 1; Out.FootSide = -1; Out.HeadLevel = 0.5f; Out.HipK = 1f; Out.KneeK = 1f; Out.BrakeFingers = 2;
            State = "cruise";
            _init = false; _tuckOn = false; _legOn = false; _launchOn = false; _brakeOn = false; _footOn = false; _fastOn = false;
            _lean = 0f; _leanRate = 0f; _spdF = 0f; _accel = 0f; _hipV = 0f; _yaw1 = 0f; _roll1 = 0f;
            _tuckR = 0f; _launchR = 0f; _brakeR = 0f; _driveR = 0f; _legR = 0f; _footR = 0f; _kneeR = 0f; _leverR = 0f; _brakeT = 0f;
            _shiftT = -1f; _clutchT = -1f; _gear = -1; _lastSide = 1; _legSide = 1;
        }

        public void Step(float dt, in RideSense s, in RideOptions o)
        {
            if (!(dt > 0f)) return;
            if (dt > 0.1f) dt = 0.1f;
            _clock += dt;
            float M = Math.Clamp(o.MaxLean > 0f ? o.MaxLean : 50f, 15f, 65f);
            float L = float.IsFinite(s.Lean) ? Math.Clamp(s.Lean, -80f, 80f) : 0f, aL = MathF.Abs(L);
            float T = Clamp01(s.Throttle), B = Clamp01(s.Brake), S = float.IsFinite(s.Steer) ? Math.Clamp(s.Steer, -1f, 1f) : 0f;
            bool known = s.Speed >= 0f && float.IsFinite(s.Speed);
            float v = known ? s.Speed : 0f;

            // lean rate (deg/s) and the lean 0.35 s ahead; speed and its rate (m/s^2)
            if (!_init) { _init = true; _lean = L; _leanRate = 0f; _spdF = v; _accel = 0f; }
            _leanRate += ((L - _lean) / dt - _leanRate) * K(dt, 0.06f);
            _lean = L;
            if (!float.IsFinite(_leanRate)) _leanRate = 0f;
            float Lp = Math.Clamp(L + _leanRate * Lead, -M, M);
            float prevSpd = _spdF;
            _spdF += (v - _spdF) * K(dt, 0.12f);
            float acc = known ? (_spdF - prevSpd) / dt : 0f;
            _accel += (Math.Clamp(acc, -30f, 30f) - _accel) * K(dt, 0.3f);

            // style
            float hipK = 1f, bodyMax = 12f, kneeK = 1f, elbowK = 0.7f;
            if (o.Style == RideStyleKind.ShouldersOut) { bodyMax = 22f; elbowK = 1f; }
            else if (o.Style == RideStyleKind.OldSchool) { hipK = 0.6f; bodyMax = 6f; kneeK = 0.5f; elbowK = 0f; }
            Out.HipK = hipK; Out.KneeK = kneeK;

            // ---- standstill: a foot down; launch
            // hysteresis: down below 1.2 m/s, up again above 2.0 m/s, so creeping doesn't wobble the foot
            if (v < 1.2f) _footOn = true; else if (v > 2f) _footOn = false;
            bool foot = o.FootDown && known && _footOn && (T < 0.5f || v < 0.4f);
            _footR = Approach(_footR, foot ? 1f : 0f, dt / (foot ? 0.4f : 0.3f));
            Out.FootDown = Smooth(_footR); Out.FootSide = o.FootSide < 0 ? -1 : 1;
            if (known && v < 1f && T > 0.5f) _launchOn = true;
            if (!known || T < 0.3f || v > 15f) _launchOn = false;
            _launchR = Approach(_launchR, _launchOn ? 1f : 0f, dt / (_launchOn ? 0.2f : 0.4f));
            Out.Launch = Smooth(_launchR) * (1f - Smooth((v - 3f) / 12f));

            // ---- tuck (RIDE 4: in on the throttle at speed, out the moment you lift, brake or lean)
            bool tuckIn = o.Tuck && known && v > TuckSpeed && T > 0.6f && aL < 12f && B < 0.05f;
            bool tuckOut = !o.Tuck || !known || v < TuckOutSpeed || T < 0.2f || B > 0.1f || aL > 15f;
            if (tuckIn) _tuckOn = true; else if (tuckOut) _tuckOn = false;
            _tuckR = Approach(_tuckR, _tuckOn ? 1f : 0f, dt / (_tuckOn ? 0.7f : 0.25f));
            Out.Tuck = Smooth(_tuckR);

            // ---- hard braking: sit up, slide back, brace
            if (v > BrakeSpeed) _fastOn = true; else if (v < BrakeSpeed - 3f) _fastOn = false;
            bool fast = !known || _fastOn;
            if (B > 0.5f && fast) _brakeOn = true; else if (B < 0.35f || !fast) _brakeOn = false;
            _brakeR = Approach(_brakeR, _brakeOn ? 1f : 0f, dt / (_brakeOn ? 0.15f : 0.3f));
            Out.Brake = Smooth(_brakeR);

            // ---- exit drive: on the throttle while the bike stands up
            float sideL = L > 0f ? -1f : 1f;
            bool drive = T > 0.5f && aL > 4f && _leanRate * -sideL < -8f;   // |lean| falling
            _driveR = Approach(_driveR, drive ? 1f : 0f, dt / (drive ? 0.25f : 0.4f));
            Out.Drive = Smooth(_driveR) * (1f - Out.Tuck);

            // ---- hang-off: hips toward the inside of the coming lean, a spring capped at 0.6 m/s: a side change takes about 0.6 s
            float hT = 0f;
            if (o.HangOff) hT = (Lp > 0f ? -1f : 1f) * HangHip * hipK * Smooth(MathF.Abs(Lp) / HangFull);
            hT += Out.FootSide * 0.03f * Out.FootDown;   // a little toward the planted foot
            int n = (int)MathF.Ceiling(dt / 0.01f); float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                _hipV += (HipOmega * HipOmega * (hT - Out.HipX) - 2f * HipOmega * _hipV) * h;
                _hipV = Math.Clamp(_hipV, -HipSpeed, HipSpeed);
                Out.HipX += _hipV * h;
            }
            if (MathF.Abs(Out.HipX) > 0.05f) _lastSide = Out.HipX > 0f ? 1 : -1;
            float cross = 1f - Sq(Math.Clamp(Out.HipX / HangHip, -1f, 1f));
            Out.Lift += (0.03f * cross * Math.Clamp(MathF.Abs(_hipV) / (0.6f * HipSpeed), 0f, 1f) - Out.Lift) * K(dt, 0.05f);
            // the upper body 0.1 s behind the hips; more past HangFull (style)
            float bT = o.HangOff ? sideL * bodyMax * Smooth((aL - HangFull) / HangBodySpan) : 0f;
            Out.Body += (bT - Out.Body) * K(dt, 0.1f);
            // knee down and elbow drop at the top of the lean range
            float kd = o.HangOff && o.KneeDown ? Smooth((aL - 0.8f * M) / (0.15f * M)) : 0f;
            _kneeR += (kd - _kneeR) * K(dt, 0.12f);
            Out.KneeDown = _kneeR; Out.ElbowDrop = _kneeR * elbowK;

            // ---- leg out under braking (inside leg; the side of the steering, else the lean, else the last hang-off)
            bool legGo = o.LegDangle && known && v > LegOutSpeed && B > 0.4f && aL < 0.3f * M && Out.Tuck < 0.5f;
            bool legStay = o.LegDangle && known && v > 10f && B >= 0.2f && aL <= 0.3f * M;
            _brakeT = legGo || (_legOn && legStay) ? _brakeT + dt : 0f;
            if (!_legOn && legGo && _brakeT >= 0.08f)
            {
                _legOn = true;
                if (_legR <= 0.001f) _legSide = MathF.Abs(S) > 0.15f ? (S > 0f ? 1 : -1) : aL > 3f ? (int)sideL : _lastSide;
            }
            if (_legOn && !legStay) _legOn = false;
            _legR = Approach(_legR, _legOn ? 1f : 0f, dt / (_legOn ? 0.25f : 0.2f));
            Out.LegOut = Smooth(_legR); Out.LegSide = _legSide;
            Out.Waggle = 0.7f * MathF.Sin(_clock * 2f * MathF.PI * 2.1f) + 0.3f * MathF.Sin(_clock * 2f * MathF.PI * 3.3f + 1f);

            // ---- head: leads the lean and the steering, then rolls half the lean back toward the horizon
            float look = 0f;
            if (o.LookIntoCorner)
                look = Math.Clamp(-Lp / 25f + S * 0.8f * (known ? Smooth(v / 5f) : 1f), -1f, 1f);
            _yaw1 += (look * 26f - _yaw1) * K(dt, 0.08f);
            Out.HeadYaw += (_yaw1 - Out.HeadYaw) * K(dt, 0.08f);
            _roll1 += (look * 7f - _roll1) * K(dt, 0.08f);
            Out.HeadRoll += (_roll1 - Out.HeadRoll) * K(dt, 0.08f);
            Out.HeadLevel = 0.5f;

            // ---- torso: pitch from tuck / launch / drive / braking and the g of speeding up / slowing down; small bob
            Out.Pitch = 26f * Out.Tuck + 12f * Out.Launch + 5f * Out.Drive - 20f * Out.Brake + Math.Clamp(-_accel * 0.5f, -4f, 4f);
            Out.SlideZ = -0.04f * Out.Tuck - 0.04f * Out.Brake + Math.Clamp(-_accel * 0.003f, -0.012f, 0.012f);
            float vib = Smooth(v / 30f) * (1f - 0.7f * Out.FootDown);
            Out.BobY = 0.004f * vib * (0.6f * MathF.Sin(_clock * 2f * MathF.PI * 1.7f) + 0.4f * MathF.Sin(_clock * 2f * MathF.PI * 3.1f + 0.7f));

            // ---- gear: left foot taps the shifter; a downshift on the brakes pulls the clutch
            if (s.Gear >= 0)
            {
                if (_gear >= 0 && s.Gear != _gear)
                {
                    Out.ShiftDir = s.Gear > _gear ? 1 : -1;
                    _shiftT = 0f;
                    if (Out.ShiftDir < 0 && B > 0.3f) _clutchT = 0f;
                }
                _gear = s.Gear;
            }
            Out.ShiftW = Pulse(ref _shiftT, dt, ShiftLen);
            Out.Clutch = Pulse(ref _clutchT, dt, ClutchLen);

            // ---- hands: brake fingers onto the lever, throttle roll
            _leverR = Approach(_leverR, B > 0.03f ? 1f : 0f, dt / (B > 0.03f ? 0.1f : 0.25f));
            Out.Lever = Smooth(_leverR); Out.LeverPull = B;
            Out.BrakeFingers = o.BrakeFingers >= 4 ? 4 : 2;
            Out.Twist = 22f * T;
            Out.On = true;

            State = Out.FootDown > 0.5f ? "standstill (foot down)"
                  : Out.Launch > 0.5f ? "launch"
                  : Out.LegOut > 0.5f ? "braking, leg out"
                  : Out.Brake > 0.5f ? "hard braking"
                  : Out.Tuck > 0.5f ? "tuck"
                  : Out.KneeDown > 0.5f ? "knee down"
                  : MathF.Abs(Out.HipX) > 0.6f * HangHip * hipK ? "hang-off"
                  : "cruise";
        }

        /// <summary>A one-shot 0-1-0 envelope (sin) while t runs (t = -1: idle).</summary>
        private static float Pulse(ref float t, float dt, float len)
        {
            if (t < 0f) return 0f;
            t += dt;
            if (t >= len) { t = -1f; return 0f; }
            return MathF.Sin(MathF.PI * t / len);
        }

        private static float K(float dt, float tau) => 1f - MathF.Exp(-dt / tau);
        private static float Approach(float x, float target, float step) => x < target ? MathF.Min(target, x + step) : MathF.Max(target, x - step);
        private static float Clamp01(float x) => float.IsFinite(x) ? Math.Clamp(x, 0f, 1f) : 0f;
        private static float Sq(float x) => x * x;
        private static float Smooth(float x) => AnimEvents.Smooth(x);
    }
}
