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
    ///   the foot on the peg (heel up) and the knee swung out to KneeHalf (against the tank); the fingers wrap the grips.
    ///   FrameBike, 0.3.0 rider (RideOut.On false): idle / look / brake brace / crash jolt / celebrate's torso layers;
    ///   hang-off straight from the lean (hips up to 0.15 m to the inside, full at 30 deg; past 30 deg the upper body adds
    ///   up to 12 deg; the inside knee opens); head look (HeadLook), the head rolled 30% back toward the horizon; both
    ///   hands on the grips (two-bone IK, elbows out, arms stretched up to 12% when short), turned with "Bikes.Bars"; the
    ///   celebrate fist pump.
    ///   FrameBike, 0.4.0 ride style (RideOut.On, from RideBody): the same, with the hips, upper body, torso pitch (tuck,
    ///   launch, braking sit-up, drive), legs (leg out, foot down, shift tap, knee down), head lead and roll, arms (tucked
    ///   elbows, inside elbow drop, throttle roll) and fingers (brake / clutch levers) from the controller.
    /// No allocations per frame.
    /// </summary>
    internal sealed partial class Solver
    {
        // anim_clips.RIDE
        private const float BallUp = 0.035f, HandTilt = 12f, HangHip = 0.15f, HangFull = 30f, HangRoll = 6f, HangSpine = 3f,
                             HangBody = 12f, HangBodySpan = 15f, HangKnee = 0.12f, HeadLevel = 0.3f;
        private static readonly Vec ArmPole = new Vec(0.55f, -0.30f, -0.15f), ToeDir = new Vec(0.10f, -0.70f, 1f), KneeFwd = new Vec(0f, 0.45f, 1f);
        // 0.4.0 ride style (x outward per side)
        private static readonly Vec TuckPole = new Vec(0.30f, -0.55f, -0.25f), DropPole = new Vec(0.30f, -0.75f, 0f);
        private static readonly Vec DangleBall = new Vec(0.13f, -0.15f, 0.12f), DangleToe = new Vec(0.35f, -0.55f, 1f);
        private static readonly Vec GroundBall = new Vec(0.34f, 0.03f, 0.36f), GroundToe = new Vec(0.25f, -0.12f, 1f);
        private const float DangleKnee = 0.18f, DangleSway = 0.025f, GroundKnee = 0.06f, KneeDownOut = 0.08f;
        // finger curl (deg about each finger bone's local x, over the A-pose): around the grip; on a lever; pulling it
        private static readonly float[] GripCurl = { 55f, 60f, 40f }, LeverCurl = { 5f, 15f, 25f }, PullCurl = { 30f, 35f, 30f };

        private Vec[] _rideLp;
        private Quat[] _rideLq;
        private int[] _fbBike;
        private BikeSeat _bike;
        private int[] _finger;      // bone per (side * 12 + finger * 3 + joint): side 0 left / 1 right, finger index..pinky, joint 01..03
        private int[] _fingerBones; // the lever fingers (right index..pinky, left index + middle), written while they move
        private bool _fingersWere;

        /// <summary>driver_anims.dra has the ride_sportbike base pose (no bike rider without it).</summary>
        public bool HasRide { get; private set; }
        /// <summary>The last fit was FitBike (FrameBike per frame).</summary>
        public bool Bike { get; private set; }
        public float KneeGapCm, HangCm, HangBodyDeg;
        /// <summary>Lever finger bones (written only on frames where FingersDirty: a lever moved or just came back).</summary>
        public int[] FingerBones => _fingerBones;
        public bool FingersDirty { get; private set; }

        private void InitBike(List<int> carFrameBones)
        {
            _rideLp = new Vec[N]; _rideLq = new Quat[N];
            HasRide = BasePose("ride_sportbike", _rideLp, _rideLq);
            var fb = new List<int>(carFrameBones) { _pelvis };
            for (int s = 0; s < 2; s++) { fb.Add(_thigh[s]); fb.Add(_calf[s]); fb.Add(_foot[s]); }
            fb.Sort();
            for (int i = fb.Count - 1; i > 0; i--) if (fb[i] == fb[i - 1]) fb.RemoveAt(i);
            _fbBike = fb.ToArray();
            string[] fn = { "index", "middle", "ring", "pinky" }, sf = { "_l", "_r" };
            _finger = new int[24];
            var lever = new List<int>();
            for (int s = 0; s < 2; s++)
                for (int f = 0; f < 4; f++)
                    for (int j = 0; j < 3; j++)
                    {
                        int b = Array.IndexOf(_f.Names, fn[f] + "_0" + (j + 1) + sf[s]);
                        _finger[s * 12 + f * 3 + j] = b;
                        if (b >= 0 && (s == 1 || f < 2)) lever.Add(b);
                    }
            _fingerBones = lever.ToArray();
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
            for (int s = 0; s < 2; s++)
                for (int f = 0; f < 4; f++)
                    for (int j = 0; j < 3; j++) Curl(_finger[s * 12 + f * 3 + j], GripCurl[j]);   // the fingers wrap the grips
            FK();
            Save(_baseLp, _baseLq);
            _fingersWere = false; FingersDirty = false;
            // reach report at the base (arms are solved per frame)
            CaptureArms();
            ShortCm = MathF.Max(ArmBike(0, Quat.Identity, Vec.Zero, false, false), ArmBike(1, Quat.Identity, Vec.Zero, false, false)) * 100f;
            CaptureArms();
            StretchShortCm = MathF.Max(ArmBike(0, Quat.Identity, Vec.Zero, false, true), ArmBike(1, Quat.Identity, Vec.Zero, false, true)) * 100f;
            Load(_baseLp, _baseLq);
            MeasureLook();
        }

        /// <summary>Finger bone b curled deg about its local x over its A-pose rotation (no FK).</summary>
        private void Curl(int b, float deg)
        {
            if (b < 0) return;
            Lq[b] = (_restLq[b] * Quat.AngleAxis(deg, Vec.Right)).Normalized;
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
                var ball = new Vec(sg * _bike.Peg.x, _bike.Peg.y + BallUp, _bike.Peg.z);
                Leg(side, ball, new Vec(sg * ToeDir.x, ToeDir.y, ToeDir.z), _bike.KneeHalf + (side == 0 ? outL : outR));
            }
        }

        /// <summary>One leg: the ball of the foot onto ball with the foot along toeDir (ankle -> ball), the knee swung out
        /// to width (two-bone IK; a target out of reach leaves the foot as close as the leg allows).</summary>
        private void Leg(int side, Vec ball, Vec toeDir, float width)
        {
            float sg = side == 0 ? -1f : 1f;
            int th = _thigh[side], ca = _calf[side], ft = _foot[side];
            float footLen = (_restWp[_ball[side]] - _restWp[ft]).Length;
            var td = toeDir.Normalized;
            var ank = ball - td * footLen;
            float l1 = (Wp[ca] - Wp[th]).Length, l2 = (Wp[ft] - Wp[ca]).Length;
            var knee = KneePoint(Wp[th], ank, l1, l2, sg, width);
            Ik2(th, ca, ft, ank, knee);
            var rd = (_restWp[_ball[side]] - _restWp[ft]).Normalized;
            SetWorldRot(ft, Quat.FromTo(rd, td) * _restWq[ft]);
        }

        /// <summary>0.4.0 legs: each foot on its peg, or blended toward the leg-out dangle, the ground (standstill) or a
        /// shifter tap (left); the inside knee opens with the hip slide, wider at knee-down.</summary>
        private void LegsRide(in RideOut r, float hipX)
        {
            float hf = Math.Clamp(MathF.Abs(hipX) / HangHip, 0f, 1f);
            int inside = hipX >= 0f ? 1 : 0;
            for (int side = 0; side < 2; side++)
            {
                float sg = side == 0 ? -1f : 1f;
                var ball = new Vec(sg * _bike.Peg.x, _bike.Peg.y + BallUp, _bike.Peg.z);
                var td = new Vec(sg * ToeDir.x, ToeDir.y, ToeDir.z).Normalized;
                float width = _bike.KneeHalf;
                float dw = r.LegSide == (int)sg ? r.LegOut : 0f;
                if (side == inside) width += (HangKnee * r.KneeK * hf + KneeDownOut * r.KneeDown) * (1f - dw);
                if (dw > 0.001f)
                {
                    var db = ball + new Vec(sg * (DangleBall.x + DangleSway * r.Waggle), DangleBall.y, DangleBall.z);
                    ball = Vec.Lerp(ball, db, dw);
                    td = Vec.Lerp(td, new Vec(sg * DangleToe.x, DangleToe.y, DangleToe.z).Normalized, dw).Normalized;
                    width += DangleKnee * dw;
                }
                float fw = r.FootSide == (int)sg ? r.FootDown : 0f;
                if (fw > 0.001f)
                {
                    var gb = new Vec(sg * GroundBall.x, GroundBall.y, _bike.Peg.z + GroundBall.z);
                    float arc = MathF.Sin(MathF.PI * fw);
                    ball = Vec.Lerp(ball, gb, fw) + new Vec(sg * 0.05f * arc, 0.07f * arc, 0f);
                    td = Vec.Lerp(td, new Vec(sg * GroundToe.x, GroundToe.y, GroundToe.z).Normalized, fw).Normalized;
                    width += GroundKnee * fw;
                }
                if (side == 0 && r.ShiftW > 0.001f)
                {
                    float w = r.ShiftW * (1f - fw);
                    ball = ball + new Vec(0f, r.ShiftDir * 0.03f * w, 0.025f * w);
                    td = Quat.AngleAxis(-r.ShiftDir * 14f * w, Vec.Right) * td;
                }
                Leg(side, ball, td, width);
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
        private float ArmBike(int side, Quat bars, Vec pivot, bool hasBars, bool stretch) =>
            ArmBike(side, bars, pivot, hasBars, stretch, new Vec((side == 0 ? -1f : 1f) * ArmPole.x, ArmPole.y, ArmPole.z), 0f);

        /// <summary>As above, with the elbow pole (shoulder + pole) and the hand rolled roll deg about the bar (the
        /// throttle: + = the top of the hand comes back).</summary>
        private float ArmBike(int side, Quat bars, Vec pivot, bool hasBars, bool stretch, Vec pole, float roll)
        {
            float sg = side == 0 ? -1f : 1f;
            var g = new Vec(sg * _bike.Grip.x, _bike.Grip.y, _bike.Grip.z);
            float t = HandTilt * (MathF.PI / 180f);
            var Y = new Vec(0f, -MathF.Cos(t), -MathF.Sin(t));
            var Z = new Vec(-sg, 0f, 0f);
            var X = Vec.Cross(Y, Z);   // the socket frame is mirrored between the hands: X is the distal axis on the left only
            if (roll != 0f)
            {
                var q = Quat.AngleAxis(-roll, Vec.Right);
                X = q * X; Y = q * Y; Z = q * Z;
            }
            if (hasBars) { g = pivot + bars * (g - pivot); X = bars * X; Y = bars * Y; Z = bars * Z; }
            var H = (Quat.FromAxes(X, Y, Z) * _grip[side].rot.Inv).Normalized;
            return Reach(side, g, H, pole, stretch);
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

        /// <summary>Spine_01..03 pitched forward by deg (40/30/30, about the bike's lateral axis); the neck and head take
        /// back keep of it (40/60), so the eyes stay on the road.</summary>
        private void TorsoPitch(float deg, float keep)
        {
            if (MathF.Abs(deg) < 1e-3f) return;
            RotateWorld(_sp1, Quat.AngleAxis(deg * 0.4f, Vec.Right));
            RotateWorld(_sp2, Quat.AngleAxis(deg * 0.3f, Vec.Right));
            RotateWorld(_sp3, Quat.AngleAxis(deg * 0.3f, Vec.Right));
            RotateWorld(_neck, Quat.AngleAxis(-deg * keep * 0.4f, Vec.Right));
            RotateWorld(_head, Quat.AngleAxis(-deg * keep * 0.6f, Vec.Right));
        }

        /// <summary>The lever fingers: right (BrakeFingers of index..pinky) onto the brake lever and pulling it, left index +
        /// middle onto the clutch. FingersDirty while they move and on the frame they come back to the grip.</summary>
        private void FingersRide(in RideOut r)
        {
            bool live = r.On && (r.Lever > 0.001f || r.Clutch > 0.001f);
            FingersDirty = live || _fingersWere;
            _fingersWere = live;
            if (!FingersDirty) return;
            for (int s = 0; s < 2; s++)
            {
                float w = s == 1 ? r.Lever : r.Clutch, pull = s == 1 ? Math.Clamp(r.LeverPull, 0f, 1f) : 1f;
                int n = s == 1 ? r.BrakeFingers : 2, count = s == 1 ? 4 : 2;
                for (int f = 0; f < count; f++)
                    for (int j = 0; j < 3; j++)
                    {
                        float on = live && f < n ? w : 0f;
                        float lever = LeverCurl[j] + (PullCurl[j] - LeverCurl[j]) * pull;
                        Curl(_finger[s * 12 + f * 3 + j], GripCurl[j] + (lever - GripCurl[j]) * on);
                    }
                FKFrom(_hand[s]);
            }
        }

        // ------------------------------------------------------------------------------------------ per frame
        /// <summary>
        /// The rider's frame pose into Lp / Lq (FrameBones changed; FingerBones too when FingersDirty). lean = the bike's
        /// lean in degrees (+ = left), bars = the bars' turn about pivot (hasBars false: the grips stay put), head yaw /
        /// pitch from HeadLook, r = the ride-style layers (r.On false: the 0.3.0 rider).
        /// </summary>
        public void FrameBike(float lean, bool hangOff, bool hasBars, Quat bars, Vec pivot, float headYaw, float headPitch, float t, in AnimIn a, in RideOut r)
        {
            Load(_baseLp, _baseLq);
            bool clips = a.On, ride = r.On;
            Idle(clips, t);
            float yaw = Math.Clamp(headYaw, -80f, 80f), pitch = Math.Clamp(headPitch, -40f, 40f);
            float celebW = 0f;
            if (clips)
            {
                LookLayer(ref yaw);
                if (!ride && _brace != null && a.Brace > 0.001f) Layer(_brace, a.Brace * _brace.Length, 1f);   // ride style sits up instead
                if (_jolt != null && a.JoltT >= 0f) Layer(_jolt, a.JoltT, 1f);
                if (_celeb != null && a.CelebT >= 0f)
                {
                    celebW = MathF.Min(AnimEvents.Smooth(a.CelebT / 0.3f), AnimEvents.Smooth((_celeb.Length - a.CelebT) / 0.45f));
                    if (celebW > 0.001f) Layer(_celeb, a.CelebT, celebW);
                }
            }
            FK();
            // hips and upper body: 0.3.0 straight from the lean, or the ride controller's rate-limited slide
            float hipX, bodyR;
            if (ride) { hipX = r.HipX; bodyR = r.Body; }
            else
            {
                HangAmounts(lean, hangOff, out float h0, out float b0, out float s0);
                hipX = s0 * HangHip * h0; bodyR = s0 * b0;
            }
            float hf = Math.Clamp(MathF.Abs(hipX) / HangHip, 0f, 1f), hs = hipX >= 0f ? 1f : -1f;
            HangCm = MathF.Abs(hipX) * 100f; HangBodyDeg = MathF.Abs(bodyR);
            if (ride)
            {
                TorsoPitch(r.Pitch, 0.9f + 0.2f * r.Tuck);   // in a tuck the head comes up a little more (over the screen)
                ShiftPelvis(new Vec(hipX, -0.02f * hf + r.Lift + r.BobY, r.SlideZ));
                // the pelvis rolls with the slide, and toward the planted foot at a standstill (its hip comes down)
                float pr = hs * HangRoll * hf + r.FootSide * 5f * r.FootDown;
                if (MathF.Abs(pr) > 1e-3f) RotateWorld(_pelvis, Quat.AngleAxis(-pr, Vec.Fwd));
                float roll = bodyR + hs * HangSpine * hf;
                if (MathF.Abs(roll) > 1e-3f) RotateWorld(_sp1, Quat.AngleAxis(-roll, Vec.Fwd));
                if (MathF.Abs(r.HeadYaw) > 0.01f) RotateWorld(_sp3, Quat.AngleAxis(r.HeadYaw * 0.2f, Vec.Up));   // the shoulders follow the head
                LegsRide(in r, hipX);
                yaw = Math.Clamp(yaw + r.HeadYaw * 0.8f, -80f, 80f);
            }
            else if (hf > 0f)
            {
                ShiftPelvis(new Vec(hipX, -0.02f * hf, 0f));
                RotateWorld(_pelvis, Quat.AngleAxis(-hs * HangRoll * hf, Vec.Fwd));
                RotateWorld(_sp1, Quat.AngleAxis(-(bodyR + hs * HangSpine * hf), Vec.Fwd));
                LegsBike(hs < 0f ? HangKnee * hf : 0f, hs > 0f ? HangKnee * hf : 0f);
            }
            if (yaw != 0f || pitch != 0f)
            {
                RotateWorld(_neck, Quat.AngleAxis(yaw * 0.35f, Vec.Up) * Quat.AngleAxis(-pitch * 0.35f, Vec.Right));
                RotateWorld(_head, Quat.AngleAxis(yaw * 0.65f, Vec.Up) * Quat.AngleAxis(-pitch * 0.65f, Vec.Right));
            }
            if (ride)
            {
                // the head's tilt in the world: (1 - HeadLevel) of the lean plus the roll into the corner, whatever the
                // torso and the hang-off did (+ = to the left, as the lean)
                var u = (Wq[_head] * _restWq[_head].Inv) * Vec.Up;
                float rel = MathF.Atan2(-u.x, u.y) * (180f / MathF.PI);
                float c = Math.Clamp(lean * (1f - r.HeadLevel) - r.HeadRoll - (lean + rel), -60f, 60f);
                if (MathF.Abs(c) > 1e-3f)
                {
                    RotateWorld(_neck, Quat.AngleAxis(c * 0.3f, Vec.Fwd));
                    RotateWorld(_head, Quat.AngleAxis(c * 0.7f, Vec.Fwd));
                }
            }
            else if (MathF.Abs(lean) > 1e-3f) RotateWorld(_head, Quat.AngleAxis(-HeadLevel * lean, Vec.Fwd));   // 30% toward the horizon
            CaptureArms();
            if (ride)
            {
                float tuckW = MathF.Max(r.Tuck, 0.5f * r.Drive);
                for (int side = 0; side < 2; side++)
                {
                    float sg = side == 0 ? -1f : 1f;
                    var pole = new Vec(sg * ArmPole.x, ArmPole.y, ArmPole.z);
                    if (tuckW > 0.001f) pole = Vec.Lerp(pole, new Vec(sg * TuckPole.x, TuckPole.y, TuckPole.z), tuckW);
                    if (r.ElbowDrop > 0.001f && sg == hs && hf > 0.3f) pole = Vec.Lerp(pole, new Vec(sg * DropPole.x, DropPole.y, DropPole.z), r.ElbowDrop);
                    ArmBike(side, bars, pivot, hasBars, true, pole, side == 1 ? r.Twist : 0f);
                }
            }
            else
            {
                ArmBike(0, bars, pivot, hasBars, true);
                ArmBike(1, bars, pivot, hasBars, true);
            }
            FingersRide(in r);
            if (celebW > 0.001f) RightArmBlend(celebW, 2, a.CelebT, null);
        }
    }
}
