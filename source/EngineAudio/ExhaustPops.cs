using System;
using UnityEngine;

namespace EngineAudio
{
    /// <summary>
    /// Exhaust pops and crackles on the overrun, synthesized (no recordings). Until 0.3.0 the pops reused the game's
    /// turbo blow-off recordings, whose bang comes 130-300 ms into the clip (a hiss that builds up) and which most cars
    /// don't have, so the pop always came late and by a different amount per gear. A real overrun pop is unburnt fuel
    /// igniting in the hot exhaust: a sharp, short bang right after the lift, then a few irregular crackles while the revs
    /// fall with the throttle shut.
    ///
    /// Clips (made once on a worker thread from Plugin.Load, plain .NET; turned into AudioClips on the main thread):
    /// six pops, 45-90 ms each: a low thump (an 80-140 Hz decaying sine with a fast downward pitch drop) plus a burst of
    /// band-passed noise (the crack), a 1 ms attack, so the bang starts at sample 0 (no lead-in), soft-clipped.
    ///
    /// Timing (Update, every frame):
    /// - a lift-off (the gas past 0.5, then below 0.2, over any number of frames) above PopMinRpm of the redline and above 8 m/s starts a sequence;
    /// - the first pop comes 70-150 ms later (the overrun takes that long to reach the exhaust), the next ones every
    ///   60-220 ms, more of them the higher the revs (1 at PopMinRpm, up to 5 near the redline);
    /// - pressing the gas again, the revs falling below PopMinRpm or the car slowing below 8 m/s ends it at once; a new
    ///   sequence can start 0.6 s after the last one ended.
    /// Each pop picks a variant at random (never the same one twice in a row), with a little pitch and volume variety,
    /// on one of three voices so pops never cut each other off. The first sequences of each car are logged (lift RPM,
    /// delay to the first pop, pop count, span) so their timing can be checked.
    /// </summary>
    internal sealed class ExhaustPops
    {
        private const int Rate = 44100, Variants = 6;
        private static AudioClip[] s_clips;
        private static volatile float[][] s_data;
        private static volatile string s_dataError;
        private static System.Threading.Tasks.Task s_prepare;
        private static bool s_failed;

        private readonly AudioSource[] _voices;
        private readonly System.Random _rng = new System.Random(17);
        private int _voice, _last = -1;
        private float _time, _next = -1f, _cooldown, _seqStart, _liftRpm, _firstDelay;
        private int _left, _count;
        private bool _active, _armed, _silent = true;
        private int _logged;

        public static void Prepare()
        {
            if (s_prepare != null) return;
            s_prepare = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var d = new float[Variants][];
                    for (int i = 0; i < Variants; i++) d[i] = Synth(i);
                    s_data = d;
                }
                catch (Exception e) { s_dataError = e.Message; }
            });
        }

        /// <summary>Ready (clips made), Pending (worker still busy) or Failed. Main thread only.</summary>
        public static TireSqueal.State EnsureClips()
        {
            if (s_clips != null) return TireSqueal.State.Ready;
            if (s_failed) return TireSqueal.State.Failed;
            if (s_dataError != null) { s_failed = true; throw new InvalidOperationException("pop synthesis failed: " + s_dataError); }
            var d = s_data;
            if (d == null) { if (s_prepare == null) Prepare(); return TireSqueal.State.Pending; }
            try
            {
                var clips = new AudioClip[Variants];
                for (int i = 0; i < Variants; i++)
                {
                    var c = AudioClip.Create("EngineAudio.Pop." + i, d[i].Length, 1, Rate, false);
                    c.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    if (!c.SetData(d[i], 0)) { UnityEngine.Object.Destroy(c); throw new InvalidOperationException("AudioClip.SetData failed"); }
                    clips[i] = c;
                }
                s_clips = clips;
                s_data = null;
                return TireSqueal.State.Ready;
            }
            catch { s_failed = true; DestroyClips(); throw; }
        }

        public static void DestroyClips()
        {
            if (s_clips != null) foreach (var c in s_clips) { try { if (c != null) UnityEngine.Object.Destroy(c); } catch { /* shutting down */ } }
            s_clips = null;
        }

        public ExhaustPops(GameObject host, UnityEngine.Audio.AudioMixerGroup mixer)
        {
            _voices = new AudioSource[3];
            for (int i = 0; i < _voices.Length; i++) { _voices[i] = Helpers.NewVoice(host, mixer); _voices[i].volume = 1f; }
        }

        /// <summary>throttle = raw gas 0-1; rpmShare = 0 at idle, 1 at redline; speed m/s; volume = the engine's master volume.</summary>
        public void Update(float throttle, float rpmShare, float speed, float minShare, float dt, float volume, string car)
        {
            if (dt <= 0f) return;
            _silent = false;
            _cooldown -= dt;
            _time += dt;
            float now = _time;
            // lift-off: armed once the gas is past half, fired when it drops below 0.2, however many frames that takes
            // (a keyboard does it in one step, a gamepad trigger over several)
            if (throttle > 0.5f) _armed = true;
            bool liftOff = _armed && throttle < 0.2f;
            if (liftOff) _armed = false;

            if (_active && (throttle > 0.3f || rpmShare < minShare || speed < 8f)) End(now, car, "ended");
            if (!_active && liftOff && _cooldown <= 0f && rpmShare >= minShare && speed > 8f)
            {
                _active = true;
                _seqStart = now; _liftRpm = rpmShare; _count = 0;
                float over = (rpmShare - minShare) / (1f - minShare > 0.05f ? 1f - minShare : 0.05f);
                _left = 1 + (int)Math.Round(4f * (over < 0f ? 0f : over > 1f ? 1f : over));
                _firstDelay = 0.07f + 0.08f * (float)_rng.NextDouble();
                _next = now + _firstDelay;
            }
            if (_active && now >= _next)
            {
                Pop(volume);
                _count++;
                _left--;
                if (_left <= 0) End(now, car, "done");
                else _next = now + 0.06f + 0.16f * (float)_rng.NextDouble();
            }
        }

        private void Pop(float volume)
        {
            int v;
            do v = _rng.Next(Variants); while (v == _last && Variants > 1);
            _last = v;
            var s = _voices[_voice];
            _voice = (_voice + 1) % _voices.Length;
            s.pitch = 0.92f + 0.16f * (float)_rng.NextDouble();
            s.PlayOneShot(s_clips[v], volume * (0.75f + 0.25f * (float)_rng.NextDouble()));
        }

        private void End(float now, string car, string why)
        {
            if (_logged < 6 && _count > 0)
            {
                _logged++;
                Plugin.Log.LogInfo($"[EngineAudio] pops ({car}): lift at {_liftRpm * 100f:0}% revs, first pop +{_firstDelay * 1000f:0} ms, {_count} pop(s) over {(now - _seqStart) * 1000f:0} ms ({why})");
            }
            _active = false;
            _cooldown = 0.6f;
        }

        /// <summary>Stops everything (pause, the game stopped its engine, release).</summary>
        public void Silence()
        {
            if (_silent) return;   // called every frame while paused or switched off: only the first call does work
            _silent = true;
            _active = false; _armed = false;
            foreach (var s in _voices) if (s != null) s.Stop();
        }

        // ------------------------------------------------------------------ synthesis (plain .NET: runs on the worker)

        private static float[] Synth(int variant)
        {
            uint seed = (uint)(101 + variant * 7919);
            float Next() { seed = 1664525u * seed + 1013904223u; return seed / 4294967296f * 2f - 1f; }
            float len = 0.045f + 0.009f * variant;               // 45-90 ms
            float f0 = 80f + 12f * variant;                       // thump 80-140 Hz
            float crack = 0.55f + 0.08f * (variant % 3);          // crack share
            int n = (int)(Rate * len);
            var o = new float[n];
            double ph = 0;
            float lp = 0f, lp2 = 0f;
            float kLo = 1f - MathF.Exp(-2f * MathF.PI * 700f / Rate), kHi = 1f - MathF.Exp(-2f * MathF.PI * 4500f / Rate);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate, u = t / len;
                float attack = Math.Min(1f, t / 0.001f);
                float f = f0 * (1.6f - 0.6f * Math.Min(1f, u * 3f));   // pitch drops fast
                ph += 2.0 * Math.PI * f / Rate;
                float thump = (float)Math.Sin(ph) * MathF.Exp(-6f * u);
                float nz = Next();
                lp += (nz - lp) * kHi;           // noise below 4.5 kHz
                lp2 += (lp - lp2) * kLo;         // minus below 700 Hz: a 0.7-4.5 kHz crack band
                float crk = (lp - lp2) * 3f * MathF.Exp(-14f * u);
                float x = attack * ((1f - crack) * thump + crack * crk);
                o[i] = MathF.Tanh(2.2f * x) * Math.Min(1f, (1f - u) / 0.15f);   // ends at 0
            }
            float peak = 1e-6f; for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(o[i]));
            for (int i = 0; i < n; i++) o[i] = o[i] / peak * 0.9f;
            return o;
        }
    }
}
