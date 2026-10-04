using System;

namespace Driver
{
    /// <summary>
    /// The driver's pose, all in managed maths (no Unity calls). "Root space" = the root bone's space: the body frame
    /// divided by the driver's scale s (the root bone carries the scale), so the solver works in the model's own units.
    ///
    /// Fit (once per car / seat change; the rules match Assets/model/build_driver.py solve_seated):
    ///   s = clamp((eye.y - seatTop.y) / 0.74, 0.85, 1.3); the base pose is the clip seated_base;
    ///   hip_c onto (eye.x, seatTop.y + 0.02 s, seatBack.z + 0.13 s); spine_01..03 turn (40/30/30) to bring eye_c onto the
    ///   eye; neck eased and head level (+ DriverCam's Pitch); legs: two-bone IK with the heel on a floor 0.325 s under the
    ///   cushion and the ankle at 86% of the leg's reach; reach order when the wheel is far: grip angle down to -30 deg,
    ///   clavicle protraction up to 0.05 m, spine lean up to 10 deg; the rest is a soft shortfall (logged).
    /// Every frame: the base, plus breathing (additive clip), head look, both hands on the spun rim (two-bone IK, the
    /// hand's grip socket aligned to the rim; half the wrist twist onto lowerarm_twist_01) and the right foot on the pedal.
    /// </summary>
    internal sealed class Solver
    {
        private readonly RigFile _f;
        public readonly int N;
        private readonly int[] _par;
        private readonly Vec[] _restLp, _restWp;
        private readonly Quat[] _restLq, _restWq;
        public readonly Vec[] Lp, Wp;
        public readonly Quat[] Lq, Wq;
        private readonly Vec[] _baseLp, _snapLp;
        private readonly Quat[] _baseLq, _snapLq;
        private readonly Vec[] _clipLp;
        private readonly Quat[] _clipLq;

        private readonly int _root, _pelvis, _sp1, _sp2, _sp3, _neck, _head, _footR;
        private readonly int[] _clav = new int[2], _upper = new int[2], _lower = new int[2], _twist = new int[2], _hand = new int[2];
        private readonly int[] _thigh = new int[2], _calf = new int[2], _foot = new int[2], _ball = new int[2];
        private readonly (int bone, Vec pos, Quat rot) _eye, _hip;
        private readonly (int bone, Vec pos, Quat rot)[] _grip = new (int, Vec, Quat)[2];

        private readonly RigFile.Clip _breathe;
        private readonly int[] _breatheBones;

        /// <summary>Bones the per-frame pose changes (written every frame); the rest are written once per fit.</summary>
        public readonly int[] FrameBones;

        // the fit
        public float Scale = 1f, Drop, Prot, Lean, ShortCm, StretchShortCm, EyeErrCm;
        private Vec _w; private Quat _wq = Quat.Identity; private float _rim = 0.185f;
        private Quat _headLevel = Quat.Identity;
        private float _lookIntoTurn;

        public Solver(RigFile f)
        {
            _f = f;
            N = f.Names.Length;
            _par = f.Parent;
            _restLp = (Vec[])f.RestPos.Clone(); _restLq = (Quat[])f.RestRot.Clone();
            Lp = new Vec[N]; Lq = new Quat[N]; Wp = new Vec[N]; Wq = new Quat[N];
            _baseLp = new Vec[N]; _baseLq = new Quat[N]; _snapLp = new Vec[N]; _snapLq = new Quat[N];
            _clipLp = new Vec[N]; _clipLq = new Quat[N];
            _restWp = new Vec[N]; _restWq = new Quat[N];
            _dirty = new bool[N];
            Array.Copy(_restLp, Lp, N); Array.Copy(_restLq, Lq, N);
            FK();
            Array.Copy(Wp, _restWp, N); Array.Copy(Wq, _restWq, N);

            _root = B("root"); _pelvis = B("pelvis"); _sp1 = B("spine_01"); _sp2 = B("spine_02"); _sp3 = B("spine_03");
            _neck = B("neck_01"); _head = B("head");
            string[] sf = { "_l", "_r" };
            for (int s = 0; s < 2; s++)
            {
                _clav[s] = B("clavicle" + sf[s]); _upper[s] = B("upperarm" + sf[s]); _lower[s] = B("lowerarm" + sf[s]);
                _twist[s] = B("lowerarm_twist_01" + sf[s]); _hand[s] = B("hand" + sf[s]);
                _thigh[s] = B("thigh" + sf[s]); _calf[s] = B("calf" + sf[s]); _foot[s] = B("foot" + sf[s]); _ball[s] = B("ball" + sf[s]);
                _grip[s] = f.Sockets["grip" + sf[s]];
            }
            _footR = _foot[1];
            _eye = f.Sockets["eye_c"]; _hip = f.Sockets["hip_c"];

            // the base clip: full local rotations (and the pelvis position)
            Array.Copy(_restLp, _clipLp, N); Array.Copy(_restLq, _clipLq, N);
            var c = f.GetClip("seated_base");
            if (c != null && (c.Flags & 2) == 0)
            {
                int o = 0;
                for (int t = 0; t < c.Bones.Length; t++)
                {
                    int b = Array.IndexOf(f.Names, c.Bones[t]);
                    if ((c.Channels[t] & 1) != 0) { if (b >= 0) _clipLq[b] = new Quat(c.Data[o], c.Data[o + 1], c.Data[o + 2], c.Data[o + 3]).Normalized; o += 4; }
                    if ((c.Channels[t] & 2) != 0) { if (b >= 0 && (b == _pelvis || b == _root)) _clipLp[b] = new Vec(c.Data[o], c.Data[o + 1], c.Data[o + 2]); o += 3; }
                }
            }
            else Plugin.Log.LogWarning("[Driver] clip seated_base missing: the fit starts from the A-pose");

            _breathe = f.GetClip("breathe_add");
            if (_breathe != null)
            {
                _breatheBones = new int[_breathe.Bones.Length];
                for (int t = 0; t < _breathe.Bones.Length; t++)
                {
                    _breatheBones[t] = Array.IndexOf(f.Names, _breathe.Bones[t]);
                    if (_breatheBones[t] < 0) Plugin.Log.LogWarning($"[Driver] clip breathe_add: bone '{_breathe.Bones[t]}' not in the skeleton (skipped)");
                    if (_breathe.Channels[t] != 1) { _breathe = null; Plugin.Log.LogWarning("[Driver] clip breathe_add is not rotation-only: not used"); break; }
                }
            }

            var fb = new System.Collections.Generic.List<int> { _sp2, _sp3, _neck, _head, _footR };
            for (int s = 0; s < 2; s++) { fb.Add(_clav[s]); fb.Add(_upper[s]); fb.Add(_lower[s]); fb.Add(_twist[s]); fb.Add(_hand[s]); }
            if (_breatheBones != null) foreach (var b in _breatheBones) if (b >= 0 && !fb.Contains(b)) fb.Add(b);
            FrameBones = fb.ToArray();
        }

        private int B(string n)
        {
            int i = Array.IndexOf(_f.Names, n);
            if (i < 0) throw new InvalidOperationException("bone " + n);
            return i;
        }

        // ------------------------------------------------------------------------------------------ basics
        public void FK()
        {
            for (int i = 0; i < N; i++)
            {
                int p = _par[i];
                if (p < 0) { Wq[i] = Lq[i]; Wp[i] = Lp[i]; }
                else { Wq[i] = (Wq[p] * Lq[i]).Normalized; Wp[i] = Wp[p] + Wq[p] * Lp[i]; }
            }
        }

        private readonly bool[] _dirty;

        /// <summary>FK of bone b and everything under it (bones are stored parents first).</summary>
        private void FKFrom(int b)
        {
            for (int i = b; i < N; i++)
            {
                int p = _par[i];
                bool d = i == b || (p >= b && _dirty[p]);
                _dirty[i] = d;
                if (!d) continue;
                if (p < 0) { Wq[i] = Lq[i]; Wp[i] = Lp[i]; }
                else { Wq[i] = (Wq[p] * Lq[i]).Normalized; Wp[i] = Wp[p] + Wq[p] * Lp[i]; }
            }
        }

        private void SetWorldRot(int b, Quat q)
        {
            int p = _par[b];
            Lq[b] = p < 0 ? q.Normalized : (Wq[p].Inv * q).Normalized;
            FKFrom(b);
        }

        private void RotateWorld(int b, Quat q) => SetWorldRot(b, q * Wq[b]);

        private Vec Sock((int bone, Vec pos, Quat rot) s) => Wp[s.bone] + Wq[s.bone] * s.pos;

        private void Save(Vec[] lp, Quat[] lq) { Array.Copy(Lp, lp, N); Array.Copy(Lq, lq, N); }
        private void Load(Vec[] lp, Quat[] lq) { Array.Copy(lp, Lp, N); Array.Copy(lq, Lq, N); FK(); }

        /// <summary>The fitted base pose into Lp / Lq (after the self-test or a rebuild used the arrays).</summary>
        public void LoadBase() => Load(_baseLp, _baseLq);

        /// <summary>The A-pose (for the skinning self-test).</summary>
        public void Rest() { Array.Copy(_restLp, Lp, N); Array.Copy(_restLq, Lq, N); FK(); }

        public void RestWorld(int b, out Vec p, out Quat q) { p = _restWp[b]; q = _restWq[b]; }

        /// <summary>Two-bone IK (ported from build_driver.ik2): b1 aims at the elbow / knee, rolled so its hinge (local X) is
        /// normal to the IK plane, then b2 aims at the end. Returns the distance the end is short of the target.</summary>
        private float Ik2(int b1, int b2, int b3, Vec target, Vec pole)
        {
            Vec A = Wp[b1], Bp = Wp[b2], C = Wp[b3];
            float l1 = (Bp - A).Length, l2 = (C - Bp).Length;
            var d = target - A;
            float dist = Math.Clamp(d.Length, 1e-4f, (l1 + l2) * 0.999f);
            var dn = d.Normalized;
            var pv = pole - A;
            var pn = (pv - dn * Vec.Dot(pv, dn)).Normalized;
            float ca = Math.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
            float ang = MathF.Acos(ca);
            var elbow = A + dn * (ca * l1) + pn * (MathF.Sin(ang) * l1);
            var end = A + dn * dist;
            RotateWorld(b1, Quat.FromTo(Bp - A, elbow - A));
            var ax = (Wp[b2] - A).Normalized;
            var nrm = Vec.Cross(elbow - A, end - elbow);
            if (nrm.Length > 1e-6f)
            {
                nrm = nrm.Normalized;
                var X = Wq[b1] * Vec.Right;
                if (Vec.Dot(X, nrm) < 0f) nrm = -nrm;
                var Xp = (X - ax * Vec.Dot(X, ax)).Normalized;
                float a2 = MathF.Acos(Math.Clamp(Vec.Dot(Xp, nrm), -1f, 1f));
                if (Vec.Dot(Vec.Cross(Xp, nrm), ax) < 0f) a2 = -a2;
                RotateWorld(b1, Quat.AngleAxisRad(a2, ax));
            }
            Bp = Wp[b2]; C = Wp[b3];
            RotateWorld(b2, Quat.FromTo(C - Bp, end - Bp));
            return (target - end).Length;
        }

        // ------------------------------------------------------------------------------------------ fit
        public void Fit(Seat st)
        {
            float s = Math.Clamp((st.Eye.y - st.SeatTop.y) / 0.74f, 0.85f, 1.3f);
            Scale = s;
            var E = st.Eye / s; var ST = st.SeatTop / s; var BK = st.SeatBack / s;
            _w = st.WheelPos / s; _wq = st.WheelRot; _rim = st.RimRadius / s; _lookIntoTurn = st.LookIntoTurn;

            Load(_clipLp, _clipLq);
            // pelvis: hip_c onto the seat
            // (kept within 0.12 behind / 0.04 ahead of the eye, so an unusually deep or reclined seat can't fold the torso)
            var hipT = new Vec(E.x, ST.y + 0.02f, Math.Clamp(BK.z + 0.13f, E.z - 0.12f, E.z + 0.04f));
            var delta = hipT - Sock(_hip);
            int pp = _par[_pelvis];
            Lp[_pelvis] = Lp[_pelvis] + (pp < 0 ? delta : Wq[pp].Inv * delta);
            FK();
            // spine: eye_c onto the eye (direction from spine_01), spread 40/30/30
            for (int it = 0; it < 6; it++)
            {
                var piv = Wp[_sp1];
                var q = Quat.FromTo(Sock(_eye) - piv, E - piv);
                q.ToAngleAxis(out float ang, out var ax);
                if (Math.Abs(ang) < 1e-5f) break;
                RotateWorld(_sp1, Quat.AngleAxisRad(ang * 0.4f, ax));
                RotateWorld(_sp2, Quat.AngleAxisRad(ang * 0.3f, ax));
                RotateWorld(_sp3, Quat.AngleAxisRad(ang * 0.3f, ax));
            }
            // neck eased toward upright, head level (+ DriverCam's pitch: + = down, as Euler x)
            SetWorldRot(_neck, Quat.Nlerp(Wq[_neck], _restWq[_neck], 0.6f));
            _headLevel = Quat.AngleAxis(st.Pitch, Vec.Right) * _restWq[_head];
            SetWorldRot(_head, _headLevel);
            EyeErrCm = (Sock(_eye) - E).Length * s * 100f;

            // legs (unaffected by lean / protraction)
            float leg = (_restLp[_calf[0]]).Length + (_restLp[_foot[0]]).Length;
            for (int side = 0; side < 2; side++)
            {
                float sg = side == 0 ? -1f : 1f;   // left = -x in Unity space
                var hip = Wp[_thigh[side]];
                float ay = ST.y - 0.325f;
                float dz = hip.y - ay;
                float hor = MathF.Sqrt(MathF.Max(0.01f, 0.86f * leg * 0.86f * leg - dz * dz));
                var ank = new Vec(hip.x + sg * 0.03f, ay, hip.z + hor + (side == 1 ? 0.02f : 0f));
                var pole = hip + new Vec(sg * 0.25f, 0.9f, 1.0f);
                Ik2(_thigh[side], _calf[side], _foot[side], ank, pole);
                var rd = (_restWp[_ball[side]] - _restWp[_foot[side]]).Normalized;
                var td = new Vec(sg * 0.06f, MathF.Sin(18f * MathF.PI / 180f), MathF.Cos(18f * MathF.PI / 180f)).Normalized;
                SetWorldRot(_foot[side], Quat.FromTo(rd, td) * _restWq[_foot[side]]);
            }
            Save(_snapLp, _snapLq);

            // reach order when short: grip angle (deg), clavicle protraction (m), spine lean (deg)
            float[] prm = { 0f, 0f, 0f };
            float[] max = { 30f, 0.05f, 10f };
            for (int k = 0; k < 3; k++)
            {
                if (Try(prm) <= 0.002f) break;
                prm[k] = max[k];
                if (Try(prm) > 0.002f) continue;
                float lo = 0f, hi = max[k];
                for (int i = 0; i < 10; i++)
                {
                    float mid = 0.5f * (lo + hi); prm[k] = mid;
                    if (Try(prm) <= 0.002f) hi = mid; else lo = mid;
                }
                prm[k] = hi;
            }
            Drop = prm[0]; Prot = prm[1]; Lean = prm[2];
            ShortCm = Try(prm) * s * 100f;
            CaptureArms();
            StretchShortCm = MathF.Max(ArmSide(0, 0f, Drop, true), ArmSide(1, 0f, Drop, true)) * s * 100f;
            // the per-frame base: torso with lean and protraction (arms are solved every frame)
            Load(_snapLp, _snapLq);
            LeanProt(Lean, Prot);
            Save(_baseLp, _baseLq);
        }

        private float Try(float[] prm)
        {
            Load(_snapLp, _snapLq);
            LeanProt(prm[2], prm[1]);
            return Arms(0f, prm[0]);
        }

        private void LeanProt(float lean, float prot)
        {
            if (lean > 0f)
            {
                RotateWorld(_sp1, Quat.AngleAxis(lean * 0.4f, Vec.Right));
                RotateWorld(_sp2, Quat.AngleAxis(lean * 0.3f, Vec.Right));
                RotateWorld(_sp3, Quat.AngleAxis(lean * 0.3f, Vec.Right));
                SetWorldRot(_head, _headLevel);
            }
            if (prot > 0f)
                for (int side = 0; side < 2; side++)
                {
                    int c = _clav[side];
                    var dir = (Wp[_upper[side]] - Wp[c]);
                    float clen = dir.Length;
                    var ax = Vec.Cross(dir.Normalized, Vec.Fwd);   // turns the shoulder toward the front
                    if (ax.Length < 1e-4f || clen < 1e-3f) continue;
                    RotateWorld(c, Quat.AngleAxisRad(MathF.Asin(MathF.Min(0.9f, prot / clen)), ax));
                }
        }

        /// <summary>Both hands on the rim at the given spin (degrees) and grip-angle drop; returns the worst shortfall.</summary>
        private readonly Quat[] _armStart = new Quat[8];

        private void CaptureArms()
        {
            for (int side = 0; side < 2; side++)
            {
                _armStart[side * 4] = Lq[_upper[side]]; _armStart[side * 4 + 1] = Lq[_lower[side]];
                _armStart[side * 4 + 2] = Lq[_hand[side]]; _armStart[side * 4 + 3] = Lq[_twist[side]];
            }
        }

        private float Arms(float spinDeg, float drop)
        {
            CaptureArms();
            float w = MathF.Max(ArmSide(0, spinDeg, drop, false), ArmSide(1, spinDeg, drop, false));
            FK();
            return w;
        }

        /// <summary>
        /// One hand on the rim at rim angle (165 deg left / 15 deg right, minus the drop) + the spin, its grip socket aligned
        /// to the rim (pinky to index toward 12 o'clock, palm at the hub and a little toward the dash). With stretch, an arm
        /// that can't reach is lengthened up to 12% (the forearm skin stretches a little instead of the hand floating).
        /// Returns how far the grip is still short (root units).
        /// </summary>
        private float ArmSide(int side, float spinDeg, float drop, bool stretch)
        {
            float a = Math.Clamp(spinDeg, -100f, 100f) * (MathF.PI / 180f);   // beyond +-100 deg the grips slide along the rim
            var X = _wq * Vec.Right; var U = _wq * Vec.Up; var Fw = _wq * Vec.Fwd;
            float ca = MathF.Cos(a), sa = MathF.Sin(a);
            var Xs = X * ca + U * sa; var Us = U * ca - X * sa;
            float th = (side == 0 ? 165f + drop : 15f - drop) * (MathF.PI / 180f);
            float ct = MathF.Cos(th), st = MathF.Sin(th);
            var radial = Xs * ct + Us * st;
            var grip = _w + radial * _rim - Fw * 0.01f;
            var tang = Us * ct - Xs * st;
            var z1 = Vec.Dot(tang, Us) > 0f ? tang : -tang;   // pinky -> index toward 12 o'clock
            var y1 = Fw * 0.3f - radial;                       // palm: at the hub, a little toward the dash
            y1 = (y1 - z1 * Vec.Dot(y1, z1)).Normalized;
            var x1 = Vec.Cross(y1, z1);
            var g = _grip[side];
            var H = (Quat.FromAxes(x1, y1, z1) * g.rot.Inv).Normalized;
            var wrist = grip - H * g.pos;
            int up = _upper[side], lo = _lower[side], hd = _hand[side], tw_ = _twist[side];
            // start from the base arm (lengths and rotations), so every call gives the same answer for the same input
            Lp[lo] = _restLp[lo]; Lp[hd] = _restLp[hd]; Lp[tw_] = _restLp[tw_];
            Lq[up] = _armStart[side * 4]; Lq[lo] = _armStart[side * 4 + 1]; Lq[hd] = _armStart[side * 4 + 2]; Lq[tw_] = _armStart[side * 4 + 3];
            FKFrom(up);
            var sh = Wp[up];
            if (stretch)
            {
                float reach = _restLp[lo].Length + _restLp[hd].Length;
                float k = Math.Clamp((wrist - sh).Length / (0.999f * reach), 1f, 1.12f);
                if (k > 1f)
                {
                    Lp[lo] = _restLp[lo] * k; Lp[hd] = _restLp[hd] * k; Lp[tw_] = _restLp[tw_] * k;
                    FKFrom(lo);
                }
            }
            var pole = sh + new Vec(side == 0 ? -0.35f : 0.35f, -0.5f, 0.10f);
            float shortBy = Ik2(up, lo, hd, wrist, pole);
            SetWorldRot(hd, H);
            // half the forearm twist onto lowerarm_twist_01 (bone axis = local z)
            var D = (Wq[lo].Inv * H) * _restLq[hd].Inv;
            float tw = 2f * MathF.Atan2(D.z, D.w);
            if (tw > MathF.PI) tw -= 2f * MathF.PI;
            if (tw < -MathF.PI) tw += 2f * MathF.PI;
            Lq[tw_] = (Quat.AngleAxisRad(0.5f * tw, Vec.Fwd) * _restLq[tw_]).Normalized;
            FKFrom(tw_);
            return shortBy;
        }

        /// <summary>
        /// Per frame: each hand follows the spin; a hand that would come off the rim (short by more than 1.5 cm, or more than
        /// at the straight-ahead grip) slides back along the rim toward its straight-ahead spot instead (bisection).
        /// </summary>
        private void ArmsFrame(float spinDeg)
        {
            CaptureArms();
            for (int side = 0; side < 2; side++)
            {
                float full = ArmSide(side, spinDeg, Drop, true);
                if (full <= 0.015f || spinDeg == 0f) continue;
                float at0 = ArmSide(side, 0f, Drop, true);
                float limit = MathF.Max(0.015f, at0 + 0.005f);
                if (full <= limit) { ArmSide(side, spinDeg, Drop, true); continue; }
                float lo = 0f, hi = 1f;
                for (int i = 0; i < 5; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (ArmSide(side, spinDeg * mid, Drop, true) <= limit) lo = mid; else hi = mid;
                }
                ArmSide(side, spinDeg * lo, Drop, true);
            }
        }

        // ------------------------------------------------------------------------------------------ per frame
        /// <summary>The frame's pose into Lp / Lq (FrameBones changed). Angles in degrees; t = seconds for the breathing loop.</summary>
        public void Frame(float spinDeg, float turn, float headYaw, float headPitch, float throttle, float brake, float t)
        {
            Load(_baseLp, _baseLq);
            if (_breathe != null)
            {
                var c = _breathe;
                float fr = t * c.Fps;
                float len = c.Frames;
                fr -= MathF.Floor(fr / len) * len;
                int f0 = (int)fr; if (f0 >= c.Frames) f0 = c.Frames - 1;
                int f1 = (f0 + 1) % c.Frames; float u = fr - f0;
                for (int k = 0; k < _breatheBones.Length; k++)
                {
                    int b = _breatheBones[k];
                    if (b < 0) continue;
                    int o0 = f0 * c.Stride + k * 4, o1 = f1 * c.Stride + k * 4;
                    var d = Quat.Nlerp(new Quat(c.Data[o0], c.Data[o0 + 1], c.Data[o0 + 2], c.Data[o0 + 3]),
                                       new Quat(c.Data[o1], c.Data[o1 + 1], c.Data[o1 + 2], c.Data[o1 + 3]), u);
                    Lq[b] = (Lq[b] * d).Normalized;
                }
                FK();
            }
            // head look: HeadLook's yaw / pitch (+ DriverCam's look into the turn), 35% neck, 65% head
            float yaw = Math.Clamp(headYaw + turn * _lookIntoTurn, -80f, 80f), pitch = Math.Clamp(headPitch, -40f, 40f);
            if (yaw != 0f || pitch != 0f)
            {
                RotateWorld(_neck, Quat.AngleAxis(yaw * 0.35f, Vec.Up) * Quat.AngleAxis(-pitch * 0.35f, Vec.Right));
                RotateWorld(_head, Quat.AngleAxis(yaw * 0.65f, Vec.Up) * Quat.AngleAxis(-pitch * 0.65f, Vec.Right));
            }
            ArmsFrame(spinDeg);
            // right foot: toes down with the throttle (18 deg), a little less for the brake
            float press = MathF.Max(Math.Clamp(throttle, 0f, 1f) * 18f, Math.Clamp(brake, 0f, 1f) * 14f);
            if (press > 0.01f) RotateWorld(_footR, Quat.AngleAxis(press, Vec.Right));
        }

        /// <summary>CPU skinning matrices for bone b: v' = s * (Wp + D * (v - restWp)), D = Wq * restWq^-1.</summary>
        public void SkinDelta(int b, out Quat d, out Vec restP, out Vec p)
        {
            d = (Wq[b] * _restWq[b].Inv).Normalized; restP = _restWp[b]; p = Wp[b];
        }
    }
}
