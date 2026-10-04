using System;
using UnityEngine;

namespace EngineAudio
{
    /// <summary>
    /// One sound layer (on-load, off-load or idle) played as an RPM-addressed recording: two of our own AudioSources
    /// (voices) on the same clip. Each frame the louder voice's play position is steered towards the target position with
    /// a small pitch correction; when it has drifted more than GrainMax (or the clip changed, or it ran out), the other
    /// voice starts at the target and the two crossfade. A steady RPM is held as overlapping grains of the same spot of the
    /// recording, a rising RPM is played forward, and nothing ever fades through silence.
    ///
    /// Each voice has its own level (0-1) that ramps towards 0 or 1 over Fade seconds; its volume is the layer volume x
    /// sin(level x pi/2), so a crossfade (one ramping up, one down) is equal-power. A new grain never restarts a voice that
    /// is still audible during a running crossfade: it waits for the fade to finish, unless the clip changed, in which case
    /// the quieter voice is restarted and the other keeps fading out from where it is.
    ///
    /// Steady RPM (0.2.0, most of a race is held at top speed): re-grabbing the same 0.11 s of the recording ten times a
    /// second sounds like a buzzing loop. When the target hardly moves (under 0.25 s of recording per second), each grain
    /// starts at a random point up to SteadyWindow before the target and simply plays forward at its own pace until it is
    /// SteadyWindow past it; then the next random grain crossfades in over SteadyFade. The note is the same, but the texture
    /// keeps changing like a real engine held at one RPM.
    /// </summary>
    internal sealed class Layer
    {
        private const float GrainMax = 0.11f;     // seconds of drift before a new grain (RPM moving)
        private const float Fade = 0.07f;         // crossfade length, seconds (RPM moving)
        private const float SteadyWindow = 0.32f; // a steady RPM plays the recording within this many seconds of the target
        private const float SteadyFade = 0.16f;   // and crossfades its grains over this long
        private const float SteadyBelow = 0.25f;  // "steady" once the target moves slower than this (recording s per s)
        private const float SteadyAbove = 0.55f;  // and no longer once it moves faster than this (hysteresis: the top-speed
                                                  // wander moves the target 0.3-0.4 s/s on 12-16 s clips; it stays steady)

        private sealed class Voice
        {
            public AudioSource S;
            public float Level, Target;           // 0-1 ramp position and where it is going
        }

        private readonly Voice _a, _b;
        private Voice _lead;                      // the voice the layer is following (fading in or full)
        private AudioClip _clip;
        private float _fade = Fade;
        private float _lastTarget = float.NaN, _targetVel, _pace = 1f;
        private bool _steady;
        private readonly System.Random _rng;

        public float Volume { get; private set; }
        public string ClipName { get; private set; } = "-";

        public Layer(AudioSource a, AudioSource b, int seed)
        {
            _a = new Voice { S = a }; _b = new Voice { S = b }; _lead = _a;
            _rng = new System.Random(seed);
        }

        private Voice Other(Voice v) => ReferenceEquals(v, _a) ? _b : _a;
        private bool Fading => _a.Level != _a.Target || _b.Level != _b.Target;

        /// <summary>
        /// clip = the recording; target = where in it the engine is (seconds); volume = this layer's share (0 = fade both
        /// voices out, then stop them); pitchMul = the game's own pitch (boost); loopClip = the idle loop (no steering).
        /// </summary>
        public void Update(AudioClip clip, float target, float volume, float pitchMul, bool loopClip, float dt)
        {
            Volume = volume;
            if (clip == null || volume <= 0.0005f)
            {
                _a.Target = 0f; _b.Target = 0f;
                Ramp(dt, 0f);
                return;
            }
            float len = Math.Max(0.05f, clip.length);
            target = Mathf.Clamp(target, 0f, len - 0.05f);

            // how fast the engine moves through the recording (seconds of clip per second), smoothed
            if (!float.IsNaN(_lastTarget) && dt > 0f)
                _targetVel += ((target - _lastTarget) / dt - _targetVel) * (1f - Mathf.Exp(-dt * 6f));
            _lastTarget = target;
            float av = Math.Abs(_targetVel);
            _steady = !loopClip && (_steady ? av < SteadyAbove : av < SteadyBelow);
            bool steady = _steady;
            float window = Math.Min(SteadyWindow, 0.03f * len);   // short clips: a smaller window (less pitch spread)

            bool clipChanged = !ReferenceEquals(clip, _clip) || _clip == null;
            if (clipChanged) { _clip = clip; ClipName = clip.name; _lastTarget = float.NaN; StartGrain(target, loopClip, true, steady, len); }
            else if (!_lead.S.isPlaying || _lead.Level <= 0f && _lead.Target <= 0f) StartGrain(target, loopClip, true, steady, len);
            else if (!loopClip && !Fading)
            {
                float d = _lead.S.time - target;
                if (steady ? (d > window || d < -2f * window) : Math.Abs(d) > GrainMax) StartGrain(target, false, false, steady, len);
            }

            // pitch: the game's (boost) pitch, times (RPM moving) a correction pulling the lead voice towards the target;
            // a steady grain plays at its own pace (the RPM is in where it starts, not in a pitch bend)
            float paceTarget = !loopClip && !steady ? Mathf.Clamp(1f + 2.5f * (target - _lead.S.time), 0.88f, 1.15f) : 1f;
            _pace += (paceTarget - _pace) * (dt > 0f ? 1f - Mathf.Exp(-dt * 12f) : 0f);   // eased: no pitch jump on a mode change
            float pitch = _pace * pitchMul;
            _a.S.pitch = pitch;
            _b.S.pitch = pitch;
            Ramp(dt, volume);
        }

        /// <summary>Starts the target position on the quieter voice (the lead's partner unless forced mid-fade) and crossfades to it.</summary>
        private void StartGrain(float target, bool loop, bool forced, bool steady, float len)
        {
            if (steady && !loop) target = Mathf.Clamp(target - Math.Min(SteadyWindow, 0.03f * len) * (float)_rng.NextDouble(), 0f, len - 0.05f);
            _fade = steady && !loop ? SteadyFade : Fade;
            var next = Other(_lead);
            if (forced && next.Level > _lead.Level) next = _lead;   // restart whichever is quieter: never cut the audible one
            var prev = Other(next);
            next.S.clip = _clip;
            next.S.loop = loop;
            next.S.Play();
            next.S.time = target;
            next.Level = 0f;                                              // always fades in (also from silence: no click)
            next.Target = 1f;
            prev.Target = 0f;                                             // fades out from wherever it is
            _lead = next;
        }

        private void Ramp(float dt, float volume)
        {
            float step = dt > 0f ? dt / _fade : 0f;
            RampVoice(_a, step, volume);
            RampVoice(_b, step, volume);
        }

        private static void RampVoice(Voice v, float step, float volume)
        {
            if (v.Level < v.Target) v.Level = Math.Min(v.Target, v.Level + step);
            else if (v.Level > v.Target) v.Level = Math.Max(v.Target, v.Level - step);
            float g = Mathf.Sin(v.Level * Mathf.PI * 0.5f);
            v.S.volume = volume * g;
            if (v.Level <= 0f && v.Target <= 0f && v.S.isPlaying) v.S.Stop();
        }

        /// <summary>Stops both voices at once (pause, game stopped its engine, release).</summary>
        public void Silence()
        {
            Stop(_a); Stop(_b);
            _clip = null; ClipName = "-";
        }

        private static void Stop(Voice v)
        {
            v.Level = 0f; v.Target = 0f;
            if (v.S != null) { v.S.volume = 0f; if (v.S.isPlaying) v.S.Stop(); }
        }
    }
}
