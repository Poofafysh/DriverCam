using System;

namespace Police
{
    /// <summary>
    /// 0.9.0: how chasers and rivals move, so they drive smoothly like a person instead of jumping (plain C#, no game or
    /// Unity calls: per-frame maths for every driven car, also run by the offline test harness).
    ///
    /// Why (game code read in GameAssembly.dll, 2026-10-04): AIPathFollower.HandleMovement runs in FixedUpdate (50 Hz)
    /// and puts the kinematic, interpolated rigidbody at path point + right x CurrentLaneOffset through a 0.1 s
    /// Vector3.SmoothDamp; AIPathFollower.HandleSpeed runs in Update and smooth-damps Speed towards MaxSpeed with
    /// speedSmoothness (a critically damped spring: a step in MaxSpeed gives an acceleration spike of up to
    /// (2 / smoothness)^2 x the step, hundreds of m/s^2). Up to 0.8.2 we wrote a MaxSpeed that jumped every time the
    /// planner's cap changed, cut Speed at once when too close, moved the offset at a constant sideways speed
    /// (MoveTowards: the sideways speed switched between 0 and 4.5 m/s in one frame) and turned the look by that speed,
    /// which led the car's real sideways motion by the game's 0.1 s smoothing.
    ///
    /// Now:
    /// - Lateral (<see cref="StepLat"/>): the planner's target goes through a critically damped spring with a bounded
    ///   sideways speed, sideways acceleration (scaled with speed, so the yaw rate a/v stays bounded) and jerk.
    ///   <see cref="LatState.PX"/> / PV model the game's own 0.1 s SmoothDamp on top: where the car really is sideways,
    ///   which the look's yaw follows (pointing where the body actually goes).
    /// - Longitudinal (<see cref="StepSpeed"/>): a commanded speed with bounded acceleration, braking and jerk, written
    ///   as both MaxSpeed and Speed every frame (with speedDampVelocity = its acceleration), so the game's smooth-damp has
    ///   nothing left to do and a hand-back continues from the same speed and acceleration.
    /// </summary>
    internal struct LatState
    {
        public float X, V, A;   // commanded offset (m, + = right), its sideways speed (m/s) and acceleration (m/s^2)
        public float PX, PV;    // the car's real sideways place / speed (the game's 0.1 s SmoothDamp of X)
    }

    internal struct LatLimits
    {
        public float Omega;     // spring stiffness (1/s): about 2 / Omega s behind a moving target
        public float VMax;      // m/s sideways
        public float AMax;      // m/s^2 sideways
        public float JMax;      // m/s^3 sideways
    }

    internal struct SpeedState
    {
        public float V, A;      // commanded speed (m/s) and acceleration (m/s^2)
    }

    internal static class DriveFeel
    {
        // lateral
        internal const float Omega = 6f, OmegaEvade = 10f;               // 1/s (stiff: the limits below make it smooth)
        internal const float Preview = 2f / Omega;                        // s: aim this far ahead on the line (the spring's lag)
        internal const float AngleGrip = 0.30f, AngleDrift = 0.36f, AngleEvade = 0.60f;   // tan of the largest heading off the road
        internal const float YawRate = 0.50f, YawRateEvade = 1.0f;       // rad/s: sideways acceleration = speed x this
        internal const float AMin = 3f, AMaxNormal = 11f, AMinEvade = 10f, AMaxEvade = 18f;
        internal const float VMinLat = 2f;                                // m/s sideways at any speed (pull-away, evading slowly)
        internal const float JNormal = 45f, JEvade = 400f;                // m/s^3 (evading you: almost at once)
        internal const float PhysSmooth = 0.1f;                           // s: HandleMovement's SmoothDamp time
        internal const float VLatAbs = 12f;                               // m/s sideways: a sanity bound only
        internal const float YawSmooth = 20f;                             // 1/s: the look's yaw follows the heading this fast (no steps)
        // longitudinal
        internal const float BrakeNormal = 10f, BrakeHard = 13.5f;       // m/s^2 (the planner's envelope assumes 10)
        internal const float JerkSpeed = 25f, JerkSpeedHard = 60f;        // m/s^3
        internal const float UrgentDecel = 6f;                            // a needed deceleration above this brakes hard

        /// <summary>
        /// The sideways limits at speed <paramref name="v"/> (m/s): sideways speed at most <paramref name="rate"/> and
        /// the heading angle's worth of v; sideways acceleration v x yaw rate (bounded both ways), so the yaw rate of a
        /// car pointing where it goes stays under the yaw-rate constant. Evading you: quicker in every respect.
        /// </summary>
        internal static LatLimits Limits(float v, float rate, bool evading, bool drift)
        {
            v = Math.Max(0f, v);
            float angle = evading ? AngleEvade : drift ? AngleDrift : AngleGrip;
            float vmax = Math.Min(rate * (evading ? 1.6f : 1f), Math.Max(VMinLat, v * angle));
            float amax = evading ? Clamp(v * YawRateEvade, AMinEvade, AMaxEvade) : Clamp(v * YawRate, AMin, AMaxNormal);
            return new LatLimits { Omega = evading ? OmegaEvade : Omega, VMax = vmax, AMax = amax, JMax = evading ? JEvade : JNormal };
        }

        /// <summary>Starts the lateral state at the car's offset, standing still sideways.</summary>
        internal static void ResetLat(ref LatState s, float offset)
        {
            s.X = s.PX = offset; s.V = s.A = s.PV = 0f;
        }

        /// <summary>
        /// One frame of the lateral controller towards <paramref name="target"/>: a cascaded critically damped spring
        /// (wanted sideways speed = Omega/2 x error, never more than VMax or what can still stop at the target), its
        /// acceleration bounded by AMax and moved at most JMax per second. Sub-steps of at most 20 ms (stable at low frame
        /// rates). Then the game's own 0.1 s SmoothDamp of the result (PX / PV).
        /// </summary>
        internal static void StepLat(ref LatState s, float target, float dt, in LatLimits l)
        {
            if (!(dt > 0f) || float.IsNaN(target)) return;
            int n = Math.Min(10, Math.Max(1, (int)Math.Ceiling(dt / 0.02f)));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                float e = target - s.X;
                float vRef = 0.5f * l.Omega * e;
                // the fastest sideways speed that can still stop at the target: braking at 0.8 AMax after building it up
                // at JMax (v^2 / 2a + v a / J <= |e|), so the jerk limit never carries it past the target
                float ab = 0.8f * l.AMax, tj = ab / l.JMax;
                float stop = ab * (MathF.Sqrt(tj * tj + 2f * Math.Abs(e) / ab) - tj);
                vRef = Clamp(vRef, -Math.Min(l.VMax, stop), Math.Min(l.VMax, stop));
                float aDes = Clamp(2f * l.Omega * (vRef - s.V), -l.AMax, l.AMax);
                s.A = MoveTowards(s.A, aDes, l.JMax * h);
                s.V += s.A * h;
                // above VMax (the limits just tightened, e.g. evading ended) the spring brakes it at AMax: no clamp, no jump
                if (s.V > VLatAbs) { s.V = VLatAbs; if (s.A > 0f) s.A = 0f; } else if (s.V < -VLatAbs) { s.V = -VLatAbs; if (s.A < 0f) s.A = 0f; }
                s.X += s.V * h;
            }
            SmoothDamp(ref s.PX, ref s.PV, s.X, PhysSmooth, dt);
        }

        /// <summary>The look's yaw one frame on: towards <paramref name="want"/> (degrees) at YawSmooth (an emergency cut can't step it).</summary>
        internal static float FollowYaw(float yaw, float want, float dt) => dt > 0f ? yaw + (want - yaw) * (1f - MathF.Exp(-dt * YawSmooth)) : yaw;

        /// <summary>The heading (degrees, + = right) of a car moving <paramref name="sideways"/> m/s across at v m/s.</summary>
        internal static float Heading(float sideways, float v) => v > 1f ? MathF.Atan2(sideways, v) * (180f / MathF.PI) : 0f;

        /// <summary>
        /// One frame of the speed controller towards <paramref name="want"/> (m/s): the acceleration it wants approaches
        /// the target time-optimally for the jerk (sqrt(2 J |error|), so it reaches 0 just as the speed arrives), bounded by
        /// <paramref name="accel"/> / <paramref name="brake"/>, and moves at most <paramref name="jerk"/> per second. Never
        /// steps past the wanted speed.
        /// </summary>
        internal static void StepSpeed(ref SpeedState s, float want, float dt, float accel, float brake, float jerk)
        {
            if (!(dt > 0f) || float.IsNaN(want)) return;
            want = Math.Max(0f, want);
            float e = want - s.V;
            float lim = e >= 0f ? accel : brake;
            float aDes = Math.Sign(e) * Math.Min(lim, 0.9f * MathF.Sqrt(2f * jerk * Math.Abs(e)));
            s.A = MoveTowards(s.A, aDes, jerk * dt);
            float v = s.V + s.A * dt;
            if ((e >= 0f && v > want && s.A > 0f) || (e < 0f && v < want && s.A < 0f)) { v = want; s.A = MoveTowards(s.A, 0f, jerk * dt); }
            s.V = Math.Max(0f, v);
            if (s.V <= 0f && s.A < 0f) s.A = 0f;
        }

        /// <summary>
        /// Whether smooth braking (hard limit, ramped in at the hard jerk from the current acceleration) still stops the
        /// closing within <paramref name="room"/> m; false = only an emergency cut avoids contact.
        /// </summary>
        internal static bool CanBrakeInTime(float room, float closing, float currentAccel)
        {
            if (!(closing > 1f)) return true;   // barely closing: the ramp handles it (no cut for a few km/h)
            float braking = Math.Max(0f, -currentAccel);
            float ramp = Math.Max(0f, (BrakeHard - braking) / JerkSpeedHard);   // s until full braking
            float eff = room - closing * ramp * 0.5f;
            if (eff <= 0f) return false;
            return closing * closing / (2f * eff) <= BrakeHard;
        }

        /// <summary>
        /// 0.9.0, rivals and chasers: one frame of the commanded speed towards <paramref name="want"/>. Normal
        /// braking BrakeNormal at the normal jerk; a car needing more than UrgentDecel (plan.NeedDecel) brakes up to
        /// BrakeHard at the hard jerk and caps the wanted speed at that car's; when even that can't stop the closing
        /// before contact (DriveFeel.CanBrakeInTime), the speed is cut at once to that car's speed (cutFrom = the speed
        /// before, else NaN) and counted on the meter.
        /// </summary>
        internal static void Longitudinal(ref SpeedState spd, float want, in PlanOutput plan, float dt, float accel, FeelMeter feel, out float cutFrom)
        {
            cutFrom = float.NaN;
            float need = plan.NeedDecel;
            bool urgent = need > DriveFeel.UrgentDecel;
            if (need > 0f && plan.NeedClosing > 0f && !plan.NeedEscapes && !plan.NeedAlongside && !DriveFeel.CanBrakeInTime(plan.NeedRoom, plan.NeedClosing, spd.A))
            {
                float cut = Math.Max(0f, Math.Min(spd.V, plan.NeedSpeed - 0.5f));
                if (cut < spd.V - 0.1f) { cutFrom = spd.V; spd.V = cut; spd.A = 0f; feel?.Cut(); return; }
            }
            if (urgent && !plan.NeedEscapes) want = Math.Min(want, plan.NeedSpeed);
            float brake = urgent ? DriveFeel.Clamp(need * 1.25f + 1f, DriveFeel.BrakeNormal, DriveFeel.BrakeHard) : DriveFeel.BrakeNormal;
            DriveFeel.StepSpeed(ref spd, want, dt, accel, brake, urgent ? DriveFeel.JerkSpeedHard : DriveFeel.JerkSpeed);
        }

        /// <summary>Unity's Mathf.SmoothDamp (the formula the game uses), in plain C#.</summary>
        internal static void SmoothDamp(ref float cur, ref float vel, float target, float smoothTime, float dt)
        {
            smoothTime = Math.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime, x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = cur - target;
            float temp = (vel + omega * change) * dt;
            vel = (vel - omega * temp) * exp;
            float o = target + (change + temp) * exp;
            if (target - cur > 0f == o > target) { o = target; vel = (o - target) / dt; }
            cur = o;
        }

        internal static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        internal static float MoveTowards(float cur, float target, float maxDelta)
            => Math.Abs(target - cur) <= maxDelta ? target : cur + Math.Sign(target - cur) * maxDelta;
    }

    /// <summary>
    /// 0.9.0 smoothness meter for one driven car: per-second window ([Debug] Smoothness) and its whole drive (the race
    /// summary). Yaw rate from the look's yaw, lateral jerk from the commanded sideways acceleration, longitudinal jerk
    /// and speed reversals (the acceleration changing sign beyond +-1 m/s^2) from the commanded speed, planner flips
    /// (its target moving more than <see cref="FlipMetres"/> in one frame), emergency cuts.
    /// </summary>
    internal sealed class FeelMeter
    {
        internal const float FlipMetres = 0.75f;
        private float _lastYaw = float.NaN, _lastLatA = float.NaN, _lastLongA = float.NaN, _lastTarget = float.NaN;
        private int _accelSign;
        // window
        internal float WinTime, WinYaw, WinLatJerk, WinLongJerk;
        internal int WinFlips, WinCuts, WinRev;
        // whole drive
        internal float Time, MaxYaw, MaxLatJerk, MaxLongJerk, SumWinYaw;
        internal int Wins, Flips, Cuts, Revs;

        internal void Reset()
        {
            _lastYaw = _lastLatA = _lastLongA = _lastTarget = float.NaN; _accelSign = 0;
            WinTime = WinYaw = WinLatJerk = WinLongJerk = 0f; WinFlips = WinCuts = WinRev = 0;
            Time = MaxYaw = MaxLatJerk = MaxLongJerk = SumWinYaw = 0f; Wins = Flips = Cuts = Revs = 0;
        }

        /// <summary>One driven frame. Returns true when a one-second window just closed (the caller may log it, then call EndWindow).</summary>
        internal bool Frame(float dt, float yaw, float latA, float longA, float target)
        {
            if (!(dt > 0f)) return false;
            if (!float.IsNaN(_lastYaw)) { float r = Math.Abs(yaw - _lastYaw) / dt; if (r > WinYaw) WinYaw = r; }
            if (!float.IsNaN(_lastLatA)) { float j = Math.Abs(latA - _lastLatA) / dt; if (j > WinLatJerk) WinLatJerk = j; }
            if (!float.IsNaN(_lastLongA)) { float j = Math.Abs(longA - _lastLongA) / dt; if (j > WinLongJerk) WinLongJerk = j; }
            if (!float.IsNaN(_lastTarget) && !float.IsNaN(target) && Math.Abs(target - _lastTarget) > FlipMetres) WinFlips++;
            int sign = longA > 1f ? 1 : longA < -1f ? -1 : 0;
            if (sign != 0) { if (_accelSign != 0 && sign != _accelSign) WinRev++; _accelSign = sign; }
            _lastYaw = yaw; _lastLatA = latA; _lastLongA = longA; _lastTarget = target;
            WinTime += dt; Time += dt;
            return WinTime >= 1f;
        }

        internal void Cut() { WinCuts++; }

        /// <summary>Folds the window into the whole drive and starts a new one.</summary>
        internal void EndWindow()
        {
            if (WinTime <= 0f) return;
            MaxYaw = Math.Max(MaxYaw, WinYaw); MaxLatJerk = Math.Max(MaxLatJerk, WinLatJerk); MaxLongJerk = Math.Max(MaxLongJerk, WinLongJerk);
            SumWinYaw += WinYaw; Wins++;
            Flips += WinFlips; Cuts += WinCuts; Revs += WinRev;
            WinTime = WinYaw = WinLatJerk = WinLongJerk = 0f; WinFlips = WinCuts = WinRev = 0;
        }

        internal string WindowText()
            => $"yaw rate max {WinYaw:0} deg/s, lateral jerk max {WinLatJerk:0} m/s^3, planner flips {WinFlips}, emergency cuts {WinCuts}, " +
               $"speed reversals {WinRev}, speed jerk max {WinLongJerk:0} m/s^3";
    }

    /// <summary>
    /// 0.9.0 per-race smoothness tally (rivals and chasers), keyed by your car like DaredevilTally; the summary line is
    /// logged at the race end whatever [Debug] Smoothness says, so builds can be compared from the log.
    /// </summary>
    internal static class SmoothTally
    {
        private static IntPtr _car;
        private static int _rivals, _chasers, _flips, _cuts, _revs;
        private static float _time, _sumRivalYaw, _sumChaserYaw, _sumWinYaw, _wins, _maxLatJerk;

        /// <summary>Adds one car's drive (its last window folded in first). Nothing for a car driven under half a second.</summary>
        internal static void Add(IntPtr raceCar, FeelMeter m, bool rival)
        {
            if (m == null) return;
            m.EndWindow();
            if (m.Time < 0.5f) { m.Reset(); return; }
            if (raceCar != _car) Reset(raceCar);
            if (rival) { _rivals++; _sumRivalYaw += m.MaxYaw; } else { _chasers++; _sumChaserYaw += m.MaxYaw; }
            _flips += m.Flips; _cuts += m.Cuts; _revs += m.Revs; _time += m.Time;
            _sumWinYaw += m.SumWinYaw; _wins += m.Wins;
            _maxLatJerk = Math.Max(_maxLatJerk, m.MaxLatJerk);
            m.Reset();
        }

        /// <summary>The summary for this race (null if nothing was driven in it); the tally then starts over.</summary>
        internal static string TakeSummary(IntPtr raceCar)
        {
            if (raceCar != _car || _rivals + _chasers == 0) return null;
            float min = Math.Max(1f / 60f, _time / 60f);
            string s = $"[Police] smoothness: rivals {_rivals} (avg max yaw {(_rivals > 0 ? _sumRivalYaw / _rivals : 0f):0} deg/s), " +
                       $"chasers {_chasers} (avg max yaw {(_chasers > 0 ? _sumChaserYaw / _chasers : 0f):0} deg/s), typical yaw rate {(_wins > 0 ? _sumWinYaw / _wins : 0f):0} deg/s, " +
                       $"planner flips {_flips / min:0.0}/min, emergency cuts {_cuts}, speed reversals {_revs / min:0.0}/min, " +
                       $"lateral jerk max {_maxLatJerk:0} m/s^3, driven {_time:0} s";
            Reset(raceCar);
            return s;
        }

        private static void Reset(IntPtr raceCar)
        {
            _car = raceCar;
            _rivals = _chasers = _flips = _cuts = _revs = 0;
            _time = _sumRivalYaw = _sumChaserYaw = _sumWinYaw = _wins = _maxLatJerk = 0f;
        }
    }
}
