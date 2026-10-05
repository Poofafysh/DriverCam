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

        // 0.2.0 clip layers (null = not in driver_anims.dra: that layer is skipped)
        private readonly ClipSampler _idle, _steerL, _steerR, _lookL, _lookR, _brace, _jolt, _shift, _celeb;
        private readonly bool[] _ikArm;          // upperarm / lowerarm / lowerarm_twist_01 / hand: the IK owns them
        private readonly Quat _knobGrip;          // the right hand's grip-socket frame on top of a gear knob (palm down)
        private readonly Quat[] _armIk = new Quat[4], _armAlt = new Quat[4];
        private readonly Vec[] _armIkP = new Vec[3];
        public float JoltLength => _jolt != null ? _jolt.Length : 0f;
        public float CelebLength => _celeb != null ? _celeb.Length : 0f;
        public string ClipSummary { get; }

        /// <summary>How far look_right turns the face at 0, 1/8 .. 8/8 of the clip (measured on the fitted base at each fit,
        /// about 79 deg at the end): HeadLook's yaw is mapped through it onto the clip time, the rest is procedural.</summary>
        private readonly float[] _lookTab = new float[9];

        /// <summary>Bones the per-frame pose changes (written every frame); the rest are written once per fit.</summary>
        public readonly int[] FrameBones;

        // the fit
        public float Scale = 1f, Drop, Prot, Lean, ShortCm, StretchShortCm, EyeErrCm;
        private Vec _w; private Quat _wq = Quat.Identity; private float _rim = 0.185f;
        private Quat _headLevel = Quat.Identity;
        private float _lookIntoTurn;
        /// <summary>1 = the cockpit's gear knob (root space in _knob), -1 = cockpit unknown (shift clip's arm), 0 = no shift hand.</summary>
        public int KnobMode;
        public float KnobShortCm, KnobLean;
        private Vec _knob;

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

            _ikArm = new bool[N];
            for (int s = 0; s < 2; s++) { _ikArm[_upper[s]] = true; _ikArm[_lower[s]] = true; _ikArm[_twist[s]] = true; _ikArm[_hand[s]] = true; }
            _idle = ClipSampler.Make(f, "idle_seated", true);
            _steerL = ClipSampler.Make(f, "steer_left", true); _steerR = ClipSampler.Make(f, "steer_right", true);
            _lookL = ClipSampler.Make(f, "look_left", true); _lookR = ClipSampler.Make(f, "look_right", true);
            if (_lookL == null || _lookR == null) _lookL = _lookR = null;
            if (_steerL == null || _steerR == null) _steerL = _steerR = null;
            _brace = ClipSampler.Make(f, "brake_brace", true);
            _jolt = ClipSampler.Make(f, "crash_jolt", true);
            _shift = ClipSampler.Make(f, "shift", false);
            _celeb = ClipSampler.Make(f, "celebrate", false);
            var have = new System.Collections.Generic.List<string>();
            if (_idle != null) have.Add("idle"); if (_steerL != null) have.Add("steer"); if (_lookL != null) have.Add("look");
            if (_brace != null) have.Add("brake brace"); if (_jolt != null) have.Add("crash jolt"); if (_shift != null) have.Add("shift");
            if (_celeb != null) have.Add("celebrate");
            ClipSummary = have.Count > 0 ? string.Join(", ", have) : "none";

            // right hand on a knob from above: palm down and a little forward, fingers forward-left (same axes as ArmSide)
            {
                var y1 = new Vec(0f, -0.97f, 0.24f).Normalized;
                var z1 = new Vec(-0.6f, 0f, 0.8f);
                z1 = (z1 - y1 * Vec.Dot(z1, y1)).Normalized;
                var x1 = Vec.Cross(y1, z1);
                _knobGrip = (Quat.FromAxes(x1, y1, z1) * _grip[1].rot.Inv).Normalized;
            }

            var fb = new System.Collections.Generic.List<int> { _sp2, _sp3, _neck, _head, _footR };
            for (int s = 0; s < 2; s++) { fb.Add(_clav[s]); fb.Add(_upper[s]); fb.Add(_lower[s]); fb.Add(_twist[s]); fb.Add(_hand[s]); }
            if (_breatheBones != null) foreach (var b in _breatheBones) if (b >= 0 && !fb.Contains(b)) fb.Add(b);
            fb.Add(_sp1);   // the knob lean
            foreach (var cs in new[] { _idle, _steerL, _steerR, _lookL, _lookR, _brace, _jolt, _shift, _celeb })
                if (cs != null) foreach (var b in cs.Bones) if (b >= 0 && !fb.Contains(b)) fb.Add(b);
            fb.Sort();
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

            // look_right's face turn over its length (for mapping HeadLook's yaw onto it)
            if (_lookR != null)
            {
                float f0 = FaceYaw(), top = 0f;
                for (int i = 0; i <= 8; i++)
                {
                    Load(_baseLp, _baseLq);
                    Layer(_lookR, i / 8f * _lookR.Length, 1f);
                    FK();
                    top = MathF.Max(top, FaceYaw() - f0);   // kept rising, so the inverse lookup is well defined
                    _lookTab[i] = i == 0 ? 0f : top;
                }
                Load(_baseLp, _baseLq);
            }

            // the shift hand: the cockpit's knob, or (cockpit unknown) the shift clip's own arm, or none (no knob: automatic)
            KnobMode = st.ShifterState == 1 ? 1 : st.ShifterState < 0 && _shift != null ? -1 : 0;
            KnobShortCm = 0f; KnobLean = 0f;
            if (KnobMode == 1)
            {
                _knob = st.Shifter / s;
                CaptureArms();
                float sh0 = KnobReach(0f);
                // out of reach: lean the torso toward it (the shoulder moves ~0.45 per radian), up to 14 deg
                KnobLean = Math.Clamp(sh0 / 0.45f * (180f / MathF.PI), 0f, 14f);
                Load(_baseLp, _baseLq);
                if (KnobLean > 0f) KnobLeanApply(1f);
                CaptureArms();
                KnobShortCm = KnobReach(0f) * s * 100f;
                Load(_baseLp, _baseLq);
            }
        }

        /// <summary>The head's facing (its rest forward carried by its world rotation) as a yaw in degrees, + = right.</summary>
        private float FaceYaw()
        {
            var fw = (Wq[_head] * _restWq[_head].Inv) * Vec.Fwd;
            return MathF.Atan2(fw.x, fw.z) * (180f / MathF.PI);
        }

        /// <summary>Right hand onto the knob top (push: +1 pulled back, -1 pushed forward, 3.5 cm), from _armStart.</summary>
        private float KnobReach(float push)
        {
            var grip = _knob + new Vec(0f, 0.02f, -0.035f * push);
            return Reach(1, grip, _knobGrip, new Vec(0.45f, -0.25f, -0.25f), true);
        }

        /// <summary>Lean spine_01..03 toward the knob by KnobLean * w (40/30/30), head kept level.</summary>
        private void KnobLeanApply(float w)
        {
            var h = _knob - Wp[_sp1]; h.y = 0f;
            if (h.Length < 1e-3f) return;
            var ax = Vec.Cross(Vec.Up, h.Normalized);
            float a = KnobLean * w;
            var hq = Wq[_head];
            RotateWorld(_sp1, Quat.AngleAxis(a * 0.4f, ax));
            RotateWorld(_sp2, Quat.AngleAxis(a * 0.3f, ax));
            RotateWorld(_sp3, Quat.AngleAxis(a * 0.3f, ax));
            SetWorldRot(_head, hq);
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
            var H = (Quat.FromAxes(x1, y1, z1) * _grip[side].rot.Inv).Normalized;
            return Reach(side, grip, H, new Vec(side == 0 ? -0.35f : 0.35f, -0.5f, 0.10f), stretch);
        }

        /// <summary>The arm from its base rotations onto a grip point with the hand frame H (two-bone IK, pole = shoulder +
        /// poleOff, optional 12% stretch, half the wrist twist on lowerarm_twist_01). Returns how far it is still short.</summary>
        private float Reach(int side, Vec grip, Quat H, Vec poleOff, bool stretch)
        {
            var g = _grip[side];
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
            var pole = sh + poleOff;
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
        /// <summary>
        /// The frame's pose into Lp / Lq (FrameBones changed). Angles in degrees; t = seconds for the idle loop.
        /// Order: fitted base; idle_seated (or breathe_add with the clips off); torso layers (steer scrubbed by the turn,
        /// look by the head yaw, brake_brace, crash_jolt, the torso part of shift / celebrate); the knob lean; the rest of
        /// the head turn + pitch; both hands on the rim (IK); the right arm blended toward the knob / the clip arm; the foot.
        /// Arm bones are never layered (the IK owns them). No allocations.
        /// </summary>
        public void Frame(float spinDeg, float turn, float headYaw, float headPitch, float throttle, float brake, float t, in AnimIn a)
        {
            Load(_baseLp, _baseLq);
            bool clips = a.On;
            if (clips && _idle != null) Layer(_idle, t, 1f);
            else if (_breathe != null)
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
            }
            // head look: HeadLook's yaw / pitch (+ DriverCam's look into the turn)
            float yaw = Math.Clamp(headYaw + turn * _lookIntoTurn, -80f, 80f), pitch = Math.Clamp(headPitch, -40f, 40f);
            float celebW = 0f, shiftW = 0f;
            if (clips)
            {
                float tn = Math.Clamp(turn, -1f, 1f), at = MathF.Abs(tn);
                if (_steerL != null && at > 0.01f) { var c = tn > 0f ? _steerR : _steerL; Layer(c, at * c.Length, 1f); }
                if (_lookL != null && MathF.Abs(yaw) > 0.05f && _lookTab[8] > 1f)
                {
                    // the clip carries the turn up to its full span (spine, neck and head together); beyond it the head turns on
                    float ay = MathF.Abs(yaw), k = 1f, done = _lookTab[8];
                    for (int i = 0; i < 8; i++)
                        if (ay < _lookTab[i + 1])
                        {
                            float span = _lookTab[i + 1] - _lookTab[i];
                            k = (i + (span > 1e-4f ? (ay - _lookTab[i]) / span : 0f)) / 8f; done = ay;
                            break;
                        }
                    var c = yaw > 0f ? _lookR : _lookL;
                    Layer(c, k * c.Length, 1f);
                    yaw -= (yaw > 0f ? 1f : -1f) * done;
                }
                if (_brace != null && a.Brace > 0.001f) Layer(_brace, a.Brace * _brace.Length, 1f);
                if (_jolt != null && a.JoltT >= 0f) Layer(_jolt, a.JoltT, 1f);
                if (_celeb != null && a.CelebT >= 0f)
                {
                    celebW = MathF.Min(AnimEvents.Smooth(a.CelebT / 0.3f), AnimEvents.Smooth((_celeb.Length - a.CelebT) / 0.45f));
                    if (celebW > 0.001f) Layer(_celeb, a.CelebT, celebW);
                }
                if (celebW <= 0.001f && KnobMode != 0 && a.ShiftW > 0.001f)
                {
                    shiftW = a.ShiftW;
                    if (_shift != null) Layer(_shift, 0.25f + 0.17f * MathF.Abs(a.ShiftPush), shiftW);   // its shoulder / head part
                }
            }
            FK();
            if (shiftW > 0f && KnobMode == 1 && KnobLean > 0f) KnobLeanApply(shiftW);
            if (yaw != 0f || pitch != 0f)
            {
                // 35% neck, 65% head
                RotateWorld(_neck, Quat.AngleAxis(yaw * 0.35f, Vec.Up) * Quat.AngleAxis(-pitch * 0.35f, Vec.Right));
                RotateWorld(_head, Quat.AngleAxis(yaw * 0.65f, Vec.Up) * Quat.AngleAxis(-pitch * 0.65f, Vec.Right));
            }
            ArmsFrame(spinDeg);
            if (celebW > 0.001f) RightArmBlend(celebW, -1, a.CelebT, _celeb);
            else if (shiftW > 0f)
            {
                if (KnobMode == 1) RightArmBlend(shiftW, 1, a.ShiftPush, null);
                else if (_shift != null) RightArmBlend(shiftW, -1, 0.25f + 0.17f * MathF.Abs(a.ShiftPush), _shift);
            }
            // right foot: toes down with the throttle (18 deg), a little less for the brake
            float press = MathF.Max(Math.Clamp(throttle, 0f, 1f) * 18f, Math.Clamp(brake, 0f, 1f) * 14f);
            if (press > 0.01f) RotateWorld(_footR, Quat.AngleAxis(press, Vec.Right));
        }

        /// <summary>Layers clip c at time ct with weight w onto Lq (no FK): additive tracks as deltas; a pose clip's tracks
        /// as their change from seated_base. Arm bones are skipped (the IK owns them).</summary>
        private void Layer(ClipSampler c, float ct, float w)
        {
            c.Seek(ct);
            var bones = c.Bones;
            for (int k = 0; k < bones.Length; k++)
            {
                int b = bones[k];
                if (b < 0 || _ikArm[b]) continue;
                var d = c.Rot(k);
                if (!c.Additive) d = _clipLq[b].Inv * d;
                if (w < 0.999f) d = Quat.Nlerp(Quat.Identity, d, w);
                Lq[b] = (Lq[b] * d).Normalized;
            }
        }

        /// <summary>
        /// The right arm (after the rim IK) blended by w toward: mode 1 = the knob (IK, push -1..1), or mode -1 = clip c's own
        /// arm rotations at time ct (relative to the clavicle; the twist as in seated_base). Local rotations nlerp'd, so the
        /// hand travels on an arc between the rim and the target.
        /// </summary>
        private void RightArmBlend(float w, int mode, float ct, ClipSampler c)
        {
            int up = _upper[1], lo = _lower[1], tw = _twist[1], hd = _hand[1];
            _armIk[0] = Lq[up]; _armIk[1] = Lq[lo]; _armIk[2] = Lq[tw]; _armIk[3] = Lq[hd];
            _armIkP[0] = Lp[lo]; _armIkP[1] = Lp[tw]; _armIkP[2] = Lp[hd];
            if (mode == 1)
            {
                KnobReach(Math.Clamp(ct, -1f, 1f));
                _armAlt[0] = Lq[up]; _armAlt[1] = Lq[lo]; _armAlt[2] = Lq[tw]; _armAlt[3] = Lq[hd];
                // Reach may have stretched the forearm: blend the lengths too
                Lp[lo] = Vec.Lerp(_armIkP[0], Lp[lo], w); Lp[tw] = Vec.Lerp(_armIkP[1], Lp[tw], w); Lp[hd] = Vec.Lerp(_armIkP[2], Lp[hd], w);
            }
            else
            {
                c.Seek(ct);
                int ku = c.Track(up), kl = c.Track(lo), kh = c.Track(hd);
                _armAlt[0] = ku >= 0 ? c.Rot(ku) : _clipLq[up];
                _armAlt[1] = kl >= 0 ? c.Rot(kl) : _clipLq[lo];
                _armAlt[2] = _clipLq[tw];
                _armAlt[3] = kh >= 0 ? c.Rot(kh) : _clipLq[hd];
                Lp[lo] = Vec.Lerp(_armIkP[0], _restLp[lo], w); Lp[tw] = Vec.Lerp(_armIkP[1], _restLp[tw], w); Lp[hd] = Vec.Lerp(_armIkP[2], _restLp[hd], w);
            }
            Lq[up] = Quat.Nlerp(_armIk[0], _armAlt[0], w);
            Lq[lo] = Quat.Nlerp(_armIk[1], _armAlt[1], w);
            Lq[tw] = Quat.Nlerp(_armIk[2], _armAlt[2], w);
            Lq[hd] = Quat.Nlerp(_armIk[3], _armAlt[3], w);
            FKFrom(up);
        }

        /// <summary>CPU skinning matrices for bone b: v' = s * (Wp + D * (v - restWp)), D = Wq * restWq^-1.</summary>
        public void SkinDelta(int b, out Quat d, out Vec restP, out Vec p)
        {
            d = (Wq[b] * _restWq[b].Inv).Normalized; restP = _restWp[b]; p = Wp[b];
        }
    }
}
