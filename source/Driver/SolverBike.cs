using System;
using System.Collections.Generic;

namespace Driver
{
    /// <summary>
    /// Where the rider goes on a Bikes motorcycle, in the bike frame ("Bikes.Lean": metres, origin on the ground midway
    /// between the axles, +z forward, +y up). The right side's grip and peg; the left side is mirrored.
    /// </summary>
    internal sealed class BikeSeat
    {
        public string Key, Source;       // model key (S1000RR, SportBike); "bikes" (published by Bikes) | "table" | "table (unknown model)"
        public Vec Seat, Grip, Peg;
        public float HipAbove = 0.10f;   // hip joints above the seat point
        public float KneeHalf = 0.20f;   // knee half-width (knees against the tank)

        public bool Same(BikeSeat o) => o != null && Key == o.Key && Source == o.Source && (Seat - o.Seat).Length < 1e-4f &&
                                        (Grip - o.Grip).Length < 1e-4f && (Peg - o.Peg).Length < 1e-4f &&
                                        MathF.Abs(HipAbove - o.HipAbove) < 1e-4f && MathF.Abs(KneeHalf - o.KneeHalf) < 1e-4f;

        public bool Plausible => Seat.Finite && Grip.Finite && Peg.Finite && Seat.y > 0.4f && Seat.y < 1.3f && Grip.x > 0.1f &&
                                 Grip.x < 0.6f && Grip.y > 0.5f && Grip.y < 1.5f && Grip.z > Seat.z && Peg.x > 0.05f && Peg.x < 0.5f &&
                                 Peg.y > 0.1f && Peg.y < Seat.y && HipAbove > -0.05f && HipAbove < 0.3f && KneeHalf > 0.08f && KneeHalf < 0.4f;

        /// <summary>The Bikes author's sockets per model (Bikes 0.1.0), the fallback when Bikes publishes none.</summary>
        private static readonly Dictionary<string, BikeSeat> Table = new Dictionary<string, BikeSeat>
        {
            ["S1000RR"] = new BikeSeat { Key = "S1000RR", Seat = new Vec(0f, 0.82f, -0.18f), Grip = new Vec(0.32f, 0.86f, 0.38f), Peg = new Vec(0.17f, 0.36f, -0.30f) },
            ["SportBike"] = new BikeSeat { Key = "SportBike", Seat = new Vec(0f, 0.85f, -0.20f), Grip = new Vec(0.33f, 0.90f, 0.40f), Peg = new Vec(0.18f, 0.38f, -0.32f) },
        };

        /// <summary>
        /// The seat for model key: AppDomain data "rogue.bikes.rider.&lt;key&gt;" when Bikes publishes it (float[11]: seat xyz,
        /// right grip xyz, right peg xyz, hip above the seat, knee half-width), else the table, else the S1000RR's.
        /// </summary>
        public static BikeSeat For(string key)
        {
            if (!string.IsNullOrEmpty(key) && AppDomain.CurrentDomain.GetData("rogue.bikes.rider." + key) is float[] a && a.Length >= 11)
            {
                var b = new BikeSeat
                {
                    Key = key, Source = "bikes", Seat = new Vec(a[0], a[1], a[2]), Grip = new Vec(MathF.Abs(a[3]), a[4], a[5]),
                    Peg = new Vec(MathF.Abs(a[6]), a[7], a[8]), HipAbove = a[9], KneeHalf = a[10],
                };
                if (b.Plausible) return b;
            }
            if (key != null && Table.TryGetValue(key, out var t)) return Copy(t, key, "table");
            return Copy(Table["S1000RR"], key ?? "unknown", "table (unknown model: S1000RR sockets)");
        }

        private static BikeSeat Copy(BikeSeat t, string key, string src) =>
            new BikeSeat { Key = key, Source = src, Seat = t.Seat, Grip = t.Grip, Peg = t.Peg, HipAbove = t.HipAbove, KneeHalf = t.KneeHalf };
    }

    /// <summary>
    /// The rider on a Bikes motorcycle (the steps and numbers of Assets/model/anim_clips.py ride_fit / ride_frame, which
    /// also makes the base pose clip ride_sportbike). Root space = the bike frame (the rider is parented under
    /// "Bikes.Lean" at scale 1, so it leans with the bike).
    ///   FitBike: ride_sportbike; the hip joints onto the seat point + HipAbove; each leg by two-bone IK with the ball of
    ///   the foot on the peg (heel up) and the knee swung out to KneeHalf (against the tank).
    ///   FrameBike: idle / look / brake brace / crash jolt / celebrate's torso layers; hang-off (hips slide up to 0.15 m to
    ///   the inside with the lean, full at 30 deg; past 30 deg the upper body adds up to 12 deg; the inside knee opens);
    ///   head look (HeadLook) and the head rolled 30% back toward the horizon; both hands on the grips (two-bone IK, elbows
    ///   out, arms stretched up to 12% when short), turned with "Bikes.Bars" when Bikes has one; the celebrate fist pump.
    /// No allocations per frame.
    /// </summary>
    internal sealed partial class Solver
    {
        // anim_clips.RIDE
        private const float BallUp = 0.035f, HandTilt = 12f, HangHip = 0.15f, HangFull = 30f, HangRoll = 6f, HangSpine = 3f,
                             HangBody = 12f, HangBodySpan = 15f, HangKnee = 0.12f, HeadLevel = 0.3f;
        private static readonly Vec ArmPole = new Vec(0.55f, -0.30f, -0.15f), ToeDir = new Vec(0.10f, -0.70f, 1f), KneeFwd = new Vec(0f, 0.45f, 1f);

        private Vec[] _rideLp;
        private Quat[] _rideLq;
        private int[] _fbBike;
        private BikeSeat _bike;

        /// <summary>driver_anims.dra has the ride_sportbike base pose (no bike rider without it).</summary>
        public bool HasRide { get; private set; }
        /// <summary>The last fit was FitBike (FrameBike per frame).</summary>
        public bool Bike { get; private set; }
        public float KneeGapCm, HangCm, HangBodyDeg;

        private void InitBike(List<int> carFrameBones)
        {
            _rideLp = new Vec[N]; _rideLq = new Quat[N];
            HasRide = BasePose("ride_sportbike", _rideLp, _rideLq);
            var fb = new List<int>(carFrameBones) { _pelvis };
            for (int s = 0; s < 2; s++) { fb.Add(_thigh[s]); fb.Add(_calf[s]); fb.Add(_foot[s]); }
            fb.Sort();
            for (int i = fb.Count - 1; i > 0; i--) if (fb[i] == fb[i - 1]) fb.RemoveAt(i);
            _fbBike = fb.ToArray();
        }

        // ------------------------------------------------------------------------------------------ fit
        public void FitBike(BikeSeat b)
        {
            Bike = true; _bike = b; Scale = 1f;
            Drop = 0f; Prot = 0f; Lean = 0f; EyeErrCm = 0f; KnobMode = 0; KnobShortCm = 0f; KnobLean = 0f; _lookIntoTurn = 0f;
            Load(_rideLp, _rideLq);
            ShiftPelvis(b.Seat + new Vec(0f, b.HipAbove, 0f) - (Wp[_thigh[0]] + Wp[_thigh[1]]) * 0.5f);
            LegsBike(0f, 0f);
            KneeGapCm = (Wp[_calf[1]] - Wp[_calf[0]]).Length * 100f;
            Save(_baseLp, _baseLq);
            // reach report at the base (arms are solved per frame)
            CaptureArms();
            ShortCm = MathF.Max(ArmBike(0, Quat.Identity, Vec.Zero, false, false), ArmBike(1, Quat.Identity, Vec.Zero, false, false)) * 100f;
            CaptureArms();
            StretchShortCm = MathF.Max(ArmBike(0, Quat.Identity, Vec.Zero, false, true), ArmBike(1, Quat.Identity, Vec.Zero, false, true)) * 100f;
            Load(_baseLp, _baseLq);
            MeasureLook();
        }

        private void ShiftPelvis(Vec d)
        {
            int pp = _par[_pelvis];
            Lp[_pelvis] = Lp[_pelvis] + (pp < 0 ? d : Wq[pp].Inv * d);
            FKFrom(_pelvis);
        }

        /// <summary>Balls of the feet on the pegs (heel up), knees swung out to the tank (+ extra width per side).</summary>
        private void LegsBike(float outL, float outR)
        {
            for (int side = 0; side < 2; side++)
            {
                float sg = side == 0 ? -1f : 1f;
                int th = _thigh[side], ca = _calf[side], ft = _foot[side];
                float footLen = (_restWp[_ball[side]] - _restWp[ft]).Length;
                var td = new Vec(sg * ToeDir.x, ToeDir.y, ToeDir.z).Normalized;
                var ball = new Vec(sg * _bike.Peg.x, _bike.Peg.y + BallUp, _bike.Peg.z);
                var ank = ball - td * footLen;
                float l1 = (Wp[ca] - Wp[th]).Length, l2 = (Wp[ft] - Wp[ca]).Length;
                var knee = KneePoint(Wp[th], ank, l1, l2, sg, _bike.KneeHalf + (side == 0 ? outL : outR));
                Ik2(th, ca, ft, ank, knee);
                var rd = (_restWp[_ball[side]] - _restWp[ft]).Normalized;
                SetWorldRot(ft, Quat.FromTo(rd, td) * _restWq[ft]);
            }
        }

        /// <summary>The knee on the IK circle of (hip, ankle), swung from the forward-up side until its x is sg * width.</summary>
        private static Vec KneePoint(Vec hip, Vec ank, float l1, float l2, float sg, float width)
        {
            var d = ank - hip;
            float dist = Math.Clamp(d.Length, 1e-4f, (l1 + l2) * 0.999f);
            var dn = d.Normalized;
            float ca = Math.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
            var c = hip + dn * (ca * l1);
            float r = MathF.Sqrt(MathF.Max(0f, 1f - ca * ca)) * l1;
            var u0 = (KneeFwd - dn * Vec.Dot(KneeFwd, dn)).Normalized;
            var o = Vec.Cross(dn, u0).Normalized;
            if (o.x * sg < 0f) o = -o;
            float lo = -0.6f, hi = 1.3f;
            for (int i = 0; i < 24; i++)
            {
                float ph = 0.5f * (lo + hi);
                var k = c + u0 * (r * MathF.Cos(ph)) + o * (r * MathF.Sin(ph));
                if (k.x * sg < width) lo = ph; else hi = ph;
            }
            float p = 0.5f * (lo + hi);
            return c + u0 * (r * MathF.Cos(p)) + o * (r * MathF.Sin(p));
        }

        /// <summary>One hand onto its grip (turned about the bars' pivot by bars when hasBars), the grip socket along the bar
        /// (pinky -> index toward the tank), palm down, fingers forward and HandTilt down. Returns the shortfall (m).</summary>
        private float ArmBike(int side, Quat bars, Vec pivot, bool hasBars, bool stretch)
        {
            float sg = side == 0 ? -1f : 1f;
            var g = new Vec(sg * _bike.Grip.x, _bike.Grip.y, _bike.Grip.z);
            float t = HandTilt * (MathF.PI / 180f);
            var Y = new Vec(0f, -MathF.Cos(t), -MathF.Sin(t));
            var Z = new Vec(-sg, 0f, 0f);
            var X = Vec.Cross(Y, Z);   // the socket frame is mirrored between the hands: X is the distal axis on the left only
            if (hasBars) { g = pivot + bars * (g - pivot); X = bars * X; Y = bars * Y; Z = bars * Z; }
            var H = (Quat.FromAxes(X, Y, Z) * _grip[side].rot.Inv).Normalized;
            return Reach(side, g, H, new Vec(sg * ArmPole.x, ArmPole.y, ArmPole.z), stretch);
        }

        /// <summary>Right fist over the shoulder (palm forward), pumping twice a second (celebrate on a bike).</summary>
        private void FistReach(float ct)
        {
            float pump = 0.5f + 0.5f * MathF.Cos(2f * MathF.PI * Math.Max(0f, ct - 0.3f) / 0.5f);
            var target = Wp[_upper[1]] + new Vec(0.16f, 0.38f + 0.08f * pump, 0.10f);
            var H = (Quat.FromAxes(new Vec(0f, -1f, 0f), new Vec(0f, 0f, 1f), new Vec(-1f, 0f, 0f)) * _grip[1].rot.Inv).Normalized;
            Reach(1, target, H, new Vec(0.5f, -0.2f, -0.2f), true);
        }

        /// <summary>(hip slide 0-1, extra upper-body degrees, side +1 = right) for a lean in degrees (+ = leaning left).</summary>
        private static void HangAmounts(float lean, bool on, out float h, out float body, out float side)
        {
            h = 0f; body = 0f; side = 0f;
            float a = MathF.Abs(lean);
            if (!on || a < 1e-3f) return;
            h = AnimEvents.Smooth(a / HangFull);
            body = HangBody * AnimEvents.Smooth((a - HangFull) / HangBodySpan);
            side = lean > 0f ? -1f : 1f;
        }

        // ------------------------------------------------------------------------------------------ per frame
        /// <summary>
        /// The rider's frame pose into Lp / Lq (FrameBones changed). lean = the bike's lean in degrees (+ = left), bars =
        /// the bars' turn about pivot (hasBars false: the grips stay put), head yaw / pitch from HeadLook.
        /// </summary>
        public void FrameBike(float lean, bool hangOff, bool hasBars, Quat bars, Vec pivot, float headYaw, float headPitch, float t, in AnimIn a)
        {
            Load(_baseLp, _baseLq);
            bool clips = a.On;
            Idle(clips, t);
            float yaw = Math.Clamp(headYaw, -80f, 80f), pitch = Math.Clamp(headPitch, -40f, 40f);
            float celebW = 0f;
            if (clips)
            {
                LookLayer(ref yaw);
                if (_brace != null && a.Brace > 0.001f) Layer(_brace, a.Brace * _brace.Length, 1f);
                if (_jolt != null && a.JoltT >= 0f) Layer(_jolt, a.JoltT, 1f);
                if (_celeb != null && a.CelebT >= 0f)
                {
                    celebW = MathF.Min(AnimEvents.Smooth(a.CelebT / 0.3f), AnimEvents.Smooth((_celeb.Length - a.CelebT) / 0.45f));
                    if (celebW > 0.001f) Layer(_celeb, a.CelebT, celebW);
                }
            }
            FK();
            HangAmounts(lean, hangOff, out float h, out float body, out float side);
            HangCm = h * HangHip * 100f; HangBodyDeg = body;
            if (h > 0f)
            {
                ShiftPelvis(new Vec(side * HangHip * h, -0.02f * h, 0f));
                RotateWorld(_pelvis, Quat.AngleAxis(-side * HangRoll * h, Vec.Fwd));
                RotateWorld(_sp1, Quat.AngleAxis(-side * (body + HangSpine * h), Vec.Fwd));
                LegsBike(side < 0f ? HangKnee * h : 0f, side > 0f ? HangKnee * h : 0f);
            }
            if (yaw != 0f || pitch != 0f)
            {
                RotateWorld(_neck, Quat.AngleAxis(yaw * 0.35f, Vec.Up) * Quat.AngleAxis(-pitch * 0.35f, Vec.Right));
                RotateWorld(_head, Quat.AngleAxis(yaw * 0.65f, Vec.Up) * Quat.AngleAxis(-pitch * 0.65f, Vec.Right));
            }
            if (MathF.Abs(lean) > 1e-3f) RotateWorld(_head, Quat.AngleAxis(-HeadLevel * lean, Vec.Fwd));   // toward the horizon
            CaptureArms();
            ArmBike(0, bars, pivot, hasBars, true);
            ArmBike(1, bars, pivot, hasBars, true);
            if (celebW > 0.001f) RightArmBlend(celebW, 2, a.CelebT, null);
        }
    }
}
