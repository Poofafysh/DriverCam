using System;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace EngineAudio
{
    /// <summary>
    /// Tyre squeal for the player's car, layered over the game's own drift sound. The game's drift loops are a soft
    /// mid-range rubber hiss (around 780 Hz, noise-like); a squealing tyre is a tonal howl with harmonics and a
    /// stick-slip roughness. The clips are synthesized once per session (no recordings, nothing from other games):
    /// - two 2 s seamless loops (fundamental 520 Hz and 680 Hz; 0.3.0 lowered them from 960 / 1240 Hz, which played
    ///   far too shrill): six slightly inharmonic partials falling off steeply, a slow random pitch walk (+-3%) and a
    ///   6.5 Hz wobble, amplitude roughness from 30 Hz low-passed noise (stick-slip), a little 1.5-6 kHz hiss,
    ///   soft-clipped; the loop seam is an equal-power crossfade;
    /// - three chirps (0.16-0.3 s) with a pitch sweep: drift start (up), direction flip (down-up), release (falling).
    /// Driven every frame by the slip angle (where the body points vs where the car goes) and the game's drift state:
    /// the level rises fast and falls slower; small angles use the lower loop, big ones blend into the higher; pitch rises
    /// with the level and speed, times Tires.Pitch (read live). Chirps fire on a drift start, a direction flip and the
    /// release (at most every 0.3 s).
    /// Silent in the air and below about 20 km/h. Unity calls used are in dump.cs: AudioClip.Create(name, samples,
    /// channels, frequency, stream), AudioClip.SetData(float[], int), AudioSource clip/loop/time/volume/pitch/Play/Stop/
    /// PlayOneShot.
    /// The samples (about 50 ms of maths) are computed on a worker thread when the plugin loads (Prepare: plain .NET, no
    /// Unity call); the clips are made from them on the main thread the first time a car needs them (EnsureClips).
    /// </summary>
    internal sealed class TireSqueal
    {
        private const int Rate = 44100;
        private static AudioClip s_loopA, s_loopB;
        private static AudioClip[] s_chirps;       // 0 = start, 1 = flip, 2 = release
        private static bool s_failed;
        private static volatile float[][] s_data;  // samples from the worker: loop A, loop B, chirps 0-2
        private static volatile string s_dataError;
        private static System.Threading.Tasks.Task s_prepare;

        private readonly AudioSource _a, _b, _chirp;
        private readonly AudioSource[] _all;
        private readonly System.Random _rng = new System.Random(5);
        private float _level, _lastLevel, _lastSign, _cooldown, _time, _wokeAt = -1f;
        private bool _wasDrifting, _armed, _rearm = true, _silent = true;

        public float Level => _level;

        /// <summary>Starts computing the samples on a worker thread (call once from Load). Plain .NET only.</summary>
        public static void Prepare()
        {
            if (s_prepare != null) return;
            s_prepare = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    s_data = new[]
                    {
                        Synth(2.0f, 520f, 11, false, 1f, 1f),
                        Synth(2.0f, 680f, 23, false, 1f, 1f),
                        Synth(0.22f, 570f, 31, true, 0.88f, 1.15f),
                        Synth(0.16f, 620f, 37, true, 1.10f, 0.92f),
                        Synth(0.30f, 540f, 41, true, 1.05f, 0.80f),
                    };
                }
                catch (Exception e) { s_dataError = e.Message; }
            });
        }

        public enum State { Ready, Pending, Failed }

        /// <summary>Makes the clips from the prepared samples (main thread, once). Pending while the worker is still busy.</summary>
        public static State EnsureClips()
        {
            if (s_loopA != null && s_loopB != null && s_chirps != null) return State.Ready;
            if (s_failed) return State.Failed;
            if (s_dataError != null) { s_failed = true; throw new InvalidOperationException("synthesis failed: " + s_dataError); }
            var d = s_data;
            if (d == null) { if (s_prepare == null) Prepare(); return State.Pending; }
            try
            {
                s_loopA = Make("EngineAudio.Squeal.A", d[0]);
                s_loopB = Make("EngineAudio.Squeal.B", d[1]);
                s_chirps = new[] { Make("EngineAudio.Chirp.Start", d[2]), Make("EngineAudio.Chirp.Flip", d[3]), Make("EngineAudio.Chirp.Release", d[4]) };
                s_data = null;   // the clips hold the samples now
                return State.Ready;
            }
            catch { s_failed = true; DestroyClips(); throw; }
        }

        /// <summary>Plugin unload only (live squeals use the clips).</summary>
        public static void DestroyClips()
        {
            foreach (var c in new[] { s_loopA, s_loopB }) Kill(c);
            if (s_chirps != null) foreach (var c in s_chirps) Kill(c);
            s_loopA = s_loopB = null; s_chirps = null;
        }

        private static void Kill(AudioClip c) { try { if (c != null) UnityEngine.Object.Destroy(c); } catch { /* shutting down */ } }

        private static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (!clip.SetData(data, 0)) { UnityEngine.Object.Destroy(clip); throw new InvalidOperationException("AudioClip.SetData failed"); }
            return clip;
        }

        public TireSqueal(GameObject host, UnityEngine.Audio.AudioMixerGroup mixer)
        {
            _a = Helpers.NewVoice(host, mixer); _a.clip = s_loopA; _a.loop = true;
            _b = Helpers.NewVoice(host, mixer); _b.clip = s_loopB; _b.loop = true;
            _chirp = Helpers.NewVoice(host, mixer);
            _chirp.volume = 1f;   // PlayOneShot's scale multiplies the source volume (NewVoice leaves it at 0)
            _all = new[] { _a, _b, _chirp };
        }

        /// <summary>
        /// slipDeg = signed slip angle (body vs travel, degrees); speed m/s; drifting = the game's drift state; volume =
        /// squeal volume at full intensity; pitchMul = Tires.Pitch (1 = as synthesized).
        /// </summary>
        public void Update(float slipDeg, float speed, bool drifting, bool grounded, float dt, float volume, float pitchMul)
        {
            if (dt <= 0f) return;
            _time += dt;
            if (_silent) _wokeAt = _time;   // after Silence: no chirp while the level ramps back up (0.15 s)
            bool woke = _time - _wokeAt < 0.15f;
            _silent = false;
            float a = Math.Abs(slipDeg);
            float slipI = Smooth01((a - 5f) / 25f);
            float want = drifting ? Math.Max(slipI, 0.45f + 0.55f * slipI) : slipI * 0.8f;   // hard cornering squeals a little too
            want *= Clamp01((speed - 5f) / 15f) * (grounded ? 1f : 0f);
            _level += (want - _level) * (1f - MathF.Exp(-(want > _level ? 25f : 6f) * dt));

            // chirps: a drift starting, the car flicking the other way mid-drift, the tyres letting go
            _cooldown -= dt;
            float sign = Math.Sign(slipDeg);
            bool start = drifting && !_wasDrifting || _lastLevel < 0.25f && _level >= 0.25f;
            bool flip = _level > 0.35f && a > 6f && _lastSign != 0f && sign != _lastSign;
            if (_level <= 0.2f) _rearm = true;                                   // a squeal has really ended
            if (_level > 0.35f && _rearm) _armed = true;                         // a real squeal: its end gets one release
            bool release = _armed && (_level <= 0.2f || _wasDrifting && !drifting);
            if (release) { _armed = false; _rearm = false; }
            if (woke) start = flip = false;
            if (_cooldown <= 0f && grounded && speed > 6f && (start || flip || release))
            {
                int i = start ? 0 : flip ? 1 : 2;
                _chirp.pitch = (0.95f + 0.1f * (float)_rng.NextDouble()) * pitchMul;
                _chirp.PlayOneShot(s_chirps[i], volume * (i == 2 ? 0.6f : 0.85f));
                _cooldown = 0.3f;
            }
            _wasDrifting = drifting;
            _lastLevel = _level;
            if (a > 6f) _lastSign = sign;

            // the loops: lower squeal at small angles, blending into the higher one as the angle grows
            float total = volume * MathF.Pow(_level, 1.2f);
            float wb = Clamp01((a - 12f) / 20f);
            float pitch = (0.94f + 0.12f * _level + 0.08f * Clamp01(speed / 50f) + 0.02f * MathF.Sin(_time * 4.4f) * _level) * pitchMul;
            Voice(_a, total * MathF.Cos(wb * Mathf.PI * 0.5f), pitch);
            Voice(_b, total * MathF.Sin(wb * Mathf.PI * 0.5f), pitch * 1.03f);
        }

        private void Voice(AudioSource s, float volume, float pitch)
        {
            if (volume > 0.002f)
            {
                if (!s.isPlaying) { s.Play(); s.time = (float)_rng.NextDouble() * 1.8f; }   // a different spot every time
                s.volume = volume;
                s.pitch = pitch;
            }
            else
            {
                s.volume = 0f;
                if (s.isPlaying && _level < 0.01f) s.Stop();
            }
        }

        /// <summary>Stops everything at once (pause, the game stopped its engine sound, release). Cheap when already silent.</summary>
        public void Silence()
        {
            if (_silent) return;
            _silent = true;
            _level = 0f; _lastLevel = 0f; _armed = false; _rearm = true;   // _wasDrifting kept: un-pausing mid-drift is no new start
            for (int i = 0; i < _all.Length; i++)
            {
                var s = _all[i];
                if (s == null) continue;
                if (ReferenceEquals(s, _chirp)) { s.Stop(); continue; }   // one-shots don't show in isPlaying; the source stays at 1
                s.volume = 0f;
                if (s.isPlaying) s.Stop();
            }
        }

        // ------------------------------------------------------------------ synthesis (plain .NET; mirrors the prototype)

        private static float[] Synth(float seconds, float f0, int seed, bool chirp, float from, float to)
        {
            int n = (int)(Rate * seconds), tail = chirp ? 0 : (int)(Rate * 0.15f), total = n + tail;
            var buf = new float[total];
            uint s = (uint)seed;
            float Next() { s = 1664525u * s + 1013904223u; return s / 4294967296f * 2f - 1f; }
            float[] harm = { 1f, 0.42f, 0.20f, 0.10f, 0.05f, 0.025f };   // steep fall-off: a howl, not a whistle
            float[] inh = { 1f, 2.003f, 2.997f, 4.01f, 5.02f, 5.98f };
            var ph = new double[6];
            float walk = 0f, walkT = 0f, rough = 0f, lp = 0f, lp2 = 0f;
            float kr = 1f - MathF.Exp(-2f * MathF.PI * 30f / Rate);
            float kh = 1f - MathF.Exp(-2f * MathF.PI * 1500f / Rate);
            float kh2 = 1f - MathF.Exp(-2f * MathF.PI * 6000f / Rate);
            float kw = 1f - MathF.Exp(-2f * MathF.PI * 3f / Rate);
            const double Tau = Math.PI * 2.0;
            for (int i = 0; i < total; i++)
            {
                float t = (float)i / Rate;
                if (i % 2205 == 0) walkT = Next();
                walk += (walkT - walk) * kw;
                float f = chirp ? f0 * (from + (to - from) * Math.Min(1f, t / seconds)) : f0;
                f *= 1f + 0.03f * walk + 0.012f * MathF.Sin(2f * MathF.PI * 6.5f * t + 0.7f * MathF.Sin(2f * MathF.PI * 0.9f * t));
                float nz = Next();
                rough += (nz - rough) * kr;
                float amp = 0.72f + 0.6f * rough;
                double sum = 0;
                for (int k = 0; k < 6; k++)
                {
                    ph[k] += Tau * f * inh[k] / Rate;
                    if (ph[k] > Tau) ph[k] -= Tau;
                    sum += harm[k] * Math.Sin(ph[k]);
                }
                lp += (nz - lp) * kh;          // noise low-passed at 1.5 kHz
                lp2 += (nz - lp - lp2) * kh2;  // its high-passed rest, low-passed at 6 kHz: a 1.5-6 kHz hiss band
                float x = MathF.Tanh(1.5f * (amp * (float)sum * 0.55f + 0.07f * lp2));
                if (chirp) x *= Math.Min(1f, t / 0.008f) * MathF.Exp(-3f * t / seconds) * Math.Min(1f, (1f - t / seconds) / 0.1f);   // ends at 0
                buf[i] = x;
            }
            float[] o;
            if (tail > 0)
            {
                o = new float[n];
                Array.Copy(buf, o, n);
                for (int j = 0; j < tail; j++)   // seamless loop: the overrun fades out over the start
                {
                    float u = (float)j / tail;
                    o[j] = buf[j] * MathF.Sqrt(u) + buf[n + j] * MathF.Sqrt(1f - u);
                }
            }
            else o = buf;
            // level: loops at 0.1 RMS; chirps at 0.14 RMS over their first 50 ms
            int m = chirp ? Math.Min(o.Length, Rate / 20) : o.Length;
            double e = 0; for (int i = 0; i < m; i++) e += o[i] * o[i];
            float g = (chirp ? 0.14f : 0.1f) / Math.Max(1e-6f, (float)Math.Sqrt(e / m));
            for (int i = 0; i < o.Length; i++) o[i] = Math.Clamp(o[i] * g, -1f, 1f);   // System.Math: this runs on the worker, no Unity call
            return o;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        private static float Smooth01(float v) { v = Clamp01(v); return v * v * (3f - 2f * v); }
    }
}
