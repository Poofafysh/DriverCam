using System;

namespace Driver
{
    /// <summary>
    /// One clip of driver_anims.dra ready to sample: bone indices resolved and the rotation offsets per track worked out
    /// once at load (the "cached curves": the baked frames from fbx_to_dra.py, blended frame to frame with nlerp).
    /// Rotation tracks only (the seated clips never move the pelvis). No allocations after Make.
    /// </summary>
    internal sealed class ClipSampler
    {
        public readonly string Name;
        public readonly bool Loop, Additive;
        public readonly float Length;      // seconds (a loop: frames / fps, it wraps; a one-shot: (frames - 1) / fps)
        public readonly int[] Bones;       // skeleton index per track, -1 = not in the skeleton (skipped)
        private readonly int[] _rotOff;    // float offset of each track's rotation inside a frame
        private readonly float[] _data;
        private readonly int _frames, _stride;
        private readonly float _fps;
        private int _f0, _f1; private float _u;

        private ClipSampler(RigFile.Clip c, string[] names)
        {
            Name = c.Name; _fps = c.Fps; _frames = c.Frames; _stride = c.Stride; _data = c.Data;
            Loop = (c.Flags & 1) != 0; Additive = (c.Flags & 2) != 0;
            Length = Loop ? _frames / _fps : Math.Max(0, _frames - 1) / _fps;
            Bones = new int[c.Bones.Length]; _rotOff = new int[c.Bones.Length];
            int o = 0;
            for (int t = 0; t < c.Bones.Length; t++)
            {
                Bones[t] = Array.IndexOf(names, c.Bones[t]);
                _rotOff[t] = o;
                o += ((c.Channels[t] & 1) != 0 ? 4 : 0) + ((c.Channels[t] & 2) != 0 ? 3 : 0);
            }
        }

        /// <summary>The clip, or null (logged) when it is missing or not the expected kind: additive = bone-local deltas
        /// (flags 2), else a seated "pose" clip (full local rotations, flags 0). Every track must be rotation-only.</summary>
        public static ClipSampler Make(RigFile f, string name, bool additive)
        {
            var c = f.GetClip(name);
            if (c == null) return null;
            if (((c.Flags & 2) != 0) != additive || (c.Flags & 4) != 0)
            {
                Plugin.Log.LogWarning($"[Driver] clip {name}: flags {c.Flags}, expected {(additive ? "additive" : "pose")}: not used");
                return null;
            }
            for (int t = 0; t < c.Channels.Length; t++)
                if (c.Channels[t] != 1) { Plugin.Log.LogWarning($"[Driver] clip {name} is not rotation-only: not used"); return null; }
            var s = new ClipSampler(c, f.Names);
            for (int t = 0; t < s.Bones.Length; t++)
                if (s.Bones[t] < 0) Plugin.Log.LogWarning($"[Driver] clip {name}: bone '{c.Bones[t]}' not in the skeleton (skipped)");
            return s;
        }

        /// <summary>Picks the two frames around time t (seconds; a loop wraps, a one-shot holds its ends).</summary>
        public void Seek(float t)
        {
            float fr = t * _fps;
            if (Loop)
            {
                fr -= MathF.Floor(fr / _frames) * _frames;
                _f0 = (int)fr; if (_f0 >= _frames) _f0 = _frames - 1;
                _f1 = (_f0 + 1) % _frames; _u = fr - _f0;
            }
            else
            {
                float last = _frames - 1;
                if (!(fr > 0f)) fr = 0f; else if (fr > last) fr = last;
                _f0 = (int)fr; if (_f0 > _frames - 2) _f0 = Math.Max(0, _frames - 2);
                _f1 = Math.Min(_frames - 1, _f0 + 1); _u = fr - _f0; if (_u > 1f) _u = 1f;
            }
        }

        /// <summary>Track k's rotation at the last Seek.</summary>
        public Quat Rot(int k)
        {
            int o0 = _f0 * _stride + _rotOff[k], o1 = _f1 * _stride + _rotOff[k];
            var d = _data;
            return Quat.Nlerp(new Quat(d[o0], d[o0 + 1], d[o0 + 2], d[o0 + 3]), new Quat(d[o1], d[o1 + 1], d[o1 + 2], d[o1 + 3]), _u);
        }

        public int Track(int bone) => Array.IndexOf(Bones, bone);
    }

    /// <summary>What the clip layers do this frame (filled by <see cref="AnimEvents"/>, read by Solver.Frame).</summary>
    internal struct AnimIn
    {
        public bool On;
        public float Brace;      // 0-1, brake_brace scrubbed by it
        public float JoltT;      // seconds into crash_jolt, -1 = not playing
        public float ShiftW;     // 0-1 right hand off the wheel toward the shifter
        public float ShiftPush;  // -1 (pushed forward: downshift) .. +1 (pulled back: upshift)
        public float CelebT;     // seconds into celebrate, -1 = not playing
    }

    /// <summary>
    /// Turns game events into clip times and weights, all plain fields (no allocations): gear change -> shift hand,
    /// collision count up -> crash_jolt, level completed -> celebrate, hard braking -> brake_brace. Events() runs from
    /// Update (throttled game reads), Step() once per drawn frame with the game's delta time (nothing moves while paused).
    /// Every baseline (gear, hits, level completed) is taken fresh after Reset, so a restart, a new car or a scene change
    /// never fires an event by itself.
    /// </summary>
    internal sealed class AnimEvents
    {
        public AnimIn Out;
        private readonly float _joltLen, _celebLen;
        private int _gear = -1, _hits = -1, _won = -1;
        private float _clock, _shiftUntil = -1f, _shiftStart = -1f, _shiftRaw;
        private int _shiftDir;

        public const float ShiftIn = 0.2f, ShiftHold = 0.45f, ShiftOut = 0.25f;

        public AnimEvents(float joltLen, float celebLen)
        {
            _joltLen = joltLen; _celebLen = celebLen;
            Reset();
        }

        public void Reset()
        {
            Out = default; Out.JoltT = -1f; Out.CelebT = -1f;
            _gear = -1; _hits = -1; _won = -1;
            _shiftUntil = -1f; _shiftStart = -1f; _shiftRaw = 0f; _shiftDir = 0;
        }

        /// <summary>The game's state (-1 = unknown). Returns a short event name for the log, or null.</summary>
        public string Events(int gear, int hits, int won, bool shiftOn, bool celebOn, bool joltOn)
        {
            string ev = null;
            if (gear >= 0)
            {
                if (_gear >= 0 && gear != _gear && shiftOn)
                {
                    _shiftDir = gear > _gear ? 1 : -1;
                    if (_clock > _shiftUntil + ShiftOut || _shiftStart < 0f) _shiftStart = _clock;   // a new reach (else the hand is still out: hold longer)
                    _shiftUntil = _clock + ShiftHold;
                    ev = $"shift {_gear + 1} -> {gear + 1}";
                }
                _gear = gear;
            }
            if (hits >= 0)
            {
                if (_hits >= 0 && hits > _hits && joltOn && _joltLen > 0f && (Out.JoltT < 0f || Out.JoltT > 0.25f))
                {
                    Out.JoltT = 0f;
                    ev = $"crash jolt (hits {hits})";
                }
                _hits = hits;
            }
            if (won >= 0)
            {
                if (_won == 0 && won == 1 && celebOn && _celebLen > 0f) { Out.CelebT = 0f; ev = "celebrate (level completed)"; }
                _won = won;
            }
            return ev;
        }

        public void Step(float dt, float brake, float speed)
        {
            _clock += dt;
            Out.On = true;
            // brake_brace: in over 0.15 s while braking hard at speed, out over 0.35 s
            float bt = brake > 0.6f && speed > 3f ? 1f : 0f;
            Out.Brace = bt > Out.Brace ? MathF.Min(bt, Out.Brace + dt / 0.15f) : MathF.Max(bt, Out.Brace - dt / 0.35f);
            if (Out.JoltT >= 0f) { Out.JoltT += dt; if (Out.JoltT >= _joltLen) Out.JoltT = -1f; }
            if (Out.CelebT >= 0f) { Out.CelebT += dt; if (Out.CelebT >= _celebLen) Out.CelebT = -1f; }
            // shift: the hand goes over in 0.2 s, stays 0.45 s after the last gear change, comes back in 0.25 s
            bool reach = _shiftStart >= 0f && _clock < _shiftUntil;
            _shiftRaw = reach ? MathF.Min(1f, _shiftRaw + dt / ShiftIn) : MathF.Max(0f, _shiftRaw - dt / ShiftOut);
            Out.ShiftW = Smooth(_shiftRaw);
            float pt = reach && _shiftRaw >= 0.9f && _clock - _shiftStart > ShiftIn ? _shiftDir : 0f;
            Out.ShiftPush = pt > Out.ShiftPush ? MathF.Min(pt, Out.ShiftPush + dt / 0.12f) : MathF.Max(pt, Out.ShiftPush - dt / 0.12f);
            if (_shiftRaw <= 0f && !reach) { _shiftStart = -1f; Out.ShiftPush = 0f; }
        }

        public static float Smooth(float x)
        {
            if (!(x > 0f)) return 0f;
            if (x >= 1f) return 1f;
            return x * x * (3f - 2f * x);
        }
    }
}
