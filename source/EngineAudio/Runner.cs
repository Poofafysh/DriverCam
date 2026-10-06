using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using FM = RogueShared.FastMath;

namespace EngineAudio
{
    /// <summary>
    /// Drives the player's engine sound from a simulated RPM (EngineModel) every LateUpdate, i.e. after the game's own
    /// VehicleSoundManager.Update has run:
    /// - the game's engine sources stay where they are and keep running their own logic, but muted (AudioSource.mute,
    ///   which no game code touches); every source we muted is unmuted when EngineAudio turns off (F1 / config / error),
    ///   when the car changes and when the plugin unloads.
    /// - seven AudioSources of our own on a child object of the car's engine-sound object (destroyed with the car):
    ///   on-load A/B, off-load A/B, idle A/B (Layer) and one spare one-shot source. Same clips, same mixer group (Engine),
    ///   2D, like the game's. Exhaust pops (ExhaustPops) add three more sources; the game's own blow-off sources stay
    ///   muted while EngineAudio is on.
    /// - tyre squeal (TireSqueal): three more sources on the same object, on the game's drift-sound mixer group, driven by
    ///   the slip angle = the angle between where the visible body points and where it is actually going (its position
    ///   change), plus the game's drift state. Layered over the game's own drift hiss, which is left alone.
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }


        private readonly EngineModel _engine = new EngineModel();
        private float _load;   // eased throttle
        private IntPtr _onClipPtr, _offClipPtr;      // clip name cache (an Il2Cpp string read allocates)
        private string _onClipName, _offClipName;
        private CarRead _car;
        private IntPtr _builtFor;
        private GameObject _voices;
        private Layer _on, _off, _idle;
        private AudioSource _oneShot;
        private TireSqueal _tires;
        private ExhaustPops _pops;
        private bool _popsOff;
        private float _nextPopsTry;
        private string _engineName = "car";
        private bool _tiresOff, _haveBody, _tiresPending = true;
        private float _nextTiresTry;
        private UnityEngine.Audio.AudioMixerGroup _engineMixer;
        private Vector3 _lastBodyPos;
        private float _slip;   // signed slip angle, degrees, smoothed
        private AudioClip[] _accel = Array.Empty<AudioClip>(), _decel = Array.Empty<AudioClip>(), _blow = Array.Empty<AudioClip>();
        private readonly List<AudioSource> _muted = new List<AudioSource>();
        private bool _broken, _active;
        internal static bool SessionOn = true;   // F1 (also read by TrafficEngines)
        private int _errors;
        private float _nextRetry;
        private Keyboard _kb;
        private UnityEngine.InputSystem.Controls.KeyControl _key;
        private Key _keyName = Key.None;
        private float _nextKb;

        private void Awake()
        {
            useGUILayout = false;   // OnGUI only uses GUI.* (no GUILayout / GUI.Window), so skip Unity's extra Layout pass of OnGUI every frame
            EngineLink.Install();   // AppDomain "rogue.engineaudio": the simulated RPM for DriverCam's tachometer
        }

        private void Update()
        {
            if (_broken) return;
            try
            {
                float now = Time.unscaledTime;
                if (now >= _nextKb || _keyName != Plugin.ToggleKey.Value)
                {
                    _nextKb = now + 2f;
                    _keyName = Plugin.ToggleKey.Value;
                    var kb = Keyboard.current;
                    _kb = kb; _key = kb != null && _keyName != Key.None ? kb[_keyName] : null;
                }
                if (_key != null && _key.wasPressedThisFrame)
                {
                    SessionOn = !SessionOn;
                    Plugin.Log.LogInfo($"[EngineAudio] {_keyName}: {(SessionOn ? "on" : "off (the game's own engine sound)")} for this session");
                }
            }
            catch (Exception e) { Fault(e); }
        }

        private void LateUpdate()
        {
            if (_broken) return;
            using var perf = RogueShared.Perf.Scope("EngineAudio.LateUpdate");
            try { Step(); }
            catch (Exception e) { Fault(e); }
        }

        private void OnDestroy()
        {
            Release("plugin unloaded");
            EngineLink.Uninstall();
            TireSqueal.DestroyClips();
            ExhaustPops.DestroyClips();
        }

        private void Step()
        {
            bool want = Plugin.Enabled.Value && SessionOn && GameApi.CarOk;
            if (!want) { if (_active) Release(SessionOn ? "disabled" : "F1"); return; }
            if (!GameApi.Read(ref _car)) { if (_active) Release("no player car"); return; }
            if (_car.Car != _builtFor || _voices == null)
            {
                if (_active) Release("new car", false);   // keep the fresh car's cached refs: build on this same frame
                if (Time.unscaledTime < _nextRetry) return;
                if (!Build()) { _nextRetry = Time.unscaledTime + 1f; return; }
            }
            MuteGame();

            // the game stops its own engine sound while paused (timeScale 0), in tutorials and at setup
            // (VehicleSoundManager.StopAllSounds): follow it, and come back when it does
            if (Time.timeScale <= 0f || !GameEngineRunning()) { SilenceAll(); EngineLink.Publish(_engine, _load); return; }   // RPM held, still live

            // ---- the simulated engine
            int gear; float progress;
            if (_car.HasGearbox) { gear = _car.Gear; progress = _car.GearProgress; }
            else Helpers.FallbackGear(_car.SpeedFactor, out gear, out progress);
            _engine.Idle = FM.Clamp(Plugin.IdleRpm.Value, 500f, 2000f);
            _engine.Redline = FM.Clamp(Plugin.RedlineRpm.Value, _engine.Idle + 2000f, 12000f);
            _engine.Limiter = Plugin.Limiter.Value;
            float dt = Time.deltaTime;
            // the raw input is 0 or 1 on a keyboard: ease it over ~0.1 s so on-load / off-load really crossfade
            float rawThrottle = _car.LevelEnded ? 0f : _car.Throttle;
            if (dt > 0f) _load += (rawThrottle - _load) * (1f - MathF.Exp(-dt / 0.1f));
            float throttle = _load;
            UpdateSlip(dt);
            float slipI = FM.Clamp01((Math.Abs(_slip) - 5f) / 25f);
            if (_car.Drifting) slipI = FM.Max(slipI, 0.4f);
            int gears = _car.HasGearbox ? Math.Max(2, _car.Gears) : 5;
            _engine.TopSpeedShare = FM.Clamp(Plugin.TopSpeedRpm.Value, 0.7f, 0.99f);
            _engine.DriftFlare = Plugin.DriftFlare.Value;
            _engine.Step(new EngineInput { Speed = _car.Speed, Gear = gear, GearProgress = progress, Shifting = _car.Shifting, Throttle = throttle,
                                           TopGear = gear >= gears - 1, Slip = _car.Grounded || !GameApi.TiresOk ? slipI : 0f, Dt = dt });
            EngineLink.Publish(_engine, throttle);
            if (dt <= 0f) return;   // paused: leave the voices as they are

            // ---- layers: on-load (rev-up sweep of this gear) / off-load (rev-down sweep) / idle loop
            float master = FM.Max(0f, _car.MaxVolume) * FM.Clamp(Plugin.Volume.Value, 0f, 2f);
            float span = _engine.Span;
            float load = Smooth(throttle);
            float idleW = _car.Speed < 4f ? FM.Clamp01(1f - (_engine.Rpm - _engine.Idle) / (_engine.Idle * 0.8f)) : 0f;
            float onW = MathF.Sin(load * Mathf.PI * 0.5f) * (1f - idleW);
            float offW = MathF.Cos(load * Mathf.PI * 0.5f) * (1f - idleW) * 0.8f;
            float pitch = FM.Clamp(_car.GamePitch, 0.5f, 2f);   // the game's boost pitch (1.2) carries over
            // RPM pitch: where the recording plays only moves the pitch a little (and a steady top-speed grain not at all),
            // so the pitch itself rises with the RPM, from PitchAtIdle to PitchAtRedline: a held redline screams high
            float rpmShare = FM.Clamp01((_engine.Rpm - _engine.Idle) / FM.Max(1f, _engine.Redline - _engine.Idle));
            float pLow = FM.Clamp(Plugin.PitchLow.Value, 0.5f, 1.5f), pHigh = FM.Clamp(Plugin.PitchHigh.Value, 0.8f, 2f);
            float rpmPitch = pLow + (pHigh - pLow) * rpmShare;
            float layerPitch = FM.Min(pitch * rpmPitch, 2f);   // ceiling: boost x redline never turns shrill

            var onClip = Helpers.Pick(_accel, Math.Min(_engine.Gear, 3));
            var offClip = Helpers.Pick(_decel, FM.Clamp(_engine.Gear, 1, 3));
            var idleClip = Helpers.Pick(_decel, 0);
            float onPos = onClip == null ? 0f : onClip.length * (0.04f + 0.9f * span), onPitch = layerPitch;
            float offPos = offClip == null ? 0f : offClip.length * (0.04f + 0.9f * (1f - span)), offPitch = layerPitch;
            if (Plugin.MatchPitch.Value)
            {
                // 0.4.0: the RPM's share of the redline picks the moment of the recording with that measured pitch, and
                // the rest is an exact pitch ratio (PitchCurves): the note follows the RPM in every gear
                float rho = FM.Clamp(_engine.Rpm / FM.Max(1f, _engine.Redline), 0.1f, 1.1f);
                float tone = FM.Clamp(Plugin.Tone.Value, 0.7f, 1.4f);
                if (onClip != null && PitchCurves.Find(PitchCurves.NameOf(onClip, ref _onClipPtr, ref _onClipName), onClip.length, rho, true, out float p1, out float r1))
                { onPos = p1; onPitch = FM.Clamp(pitch * r1 * tone, 0.5f, 2f); }
                if (offClip != null && PitchCurves.Find(PitchCurves.NameOf(offClip, ref _offClipPtr, ref _offClipName), offClip.length, rho, false, out float p2, out float r2))
                { offPos = p2; offPitch = FM.Clamp(pitch * r2 * tone, 0.5f, 2f); }
            }
            _on.Update(onClip, onPos, onW * master, onPitch, false, dt);
            _off.Update(offClip, offPos, offW * master, offPitch, false, dt);
            float idlePitch = FM.Clamp(_engine.Rpm / _engine.Idle, 0.85f, 1.6f) * pitch;
            _idle.Update(idleClip, 0f, idleW * master, idlePitch, true, dt);

            // ---- tyre squeal (Tires.Enabled is read live; the clips may still be in the making at the first car)
            if (!Plugin.TiresEnabled.Value) _tires?.Silence();
            else
            {
                if (_tires == null && _tiresPending && Time.unscaledTime >= _nextTiresTry) { _nextTiresTry = Time.unscaledTime + 0.5f; BuildTires(_engineMixer); }
                if (_tires != null) _tires.Update(_slip, _car.Speed, _car.Drifting, _car.Grounded, dt, FM.Clamp(Plugin.TiresVolume.Value, 0f, 2f),
                                               FM.Clamp(Plugin.TiresPitch.Value, 0.5f, 1.5f));
            }

            // ---- exhaust pops (synthesized, ExhaustPops): a lift-off at high RPM starts a timed sequence of pops and crackles
            if (!Plugin.Pops.Value) _pops?.Silence();
            else
            {
                if (_pops == null && !_popsOff && Time.unscaledTime >= _nextPopsTry) { _nextPopsTry = Time.unscaledTime + 0.5f; BuildPops(_engineMixer); }
                if (_pops != null) _pops.Update(rawThrottle, rpmShare, _car.Speed, FM.Clamp(Plugin.PopMinRpm.Value, 0.3f, 0.95f), dt, master, _engineName);
            }
        }

        private static float Smooth(float x) { x = FM.Clamp01(x); return x * x * (3f - 2f * x); }

        /// <summary>
        /// Slip angle from the visible body: its heading vs its horizontal movement since the last frame (atan2 of the
        /// cross and dot products; + = moving to the body's right). Only above 3 m/s; jumps (respawn, teleport) are skipped.
        /// </summary>
        private void UpdateSlip(float dt)
        {
            if (!GameApi.TiresOk || dt <= 0f) return;
            if (_haveBody)
            {
                Vector3 v = (_car.BodyPos - _lastBodyPos) / dt, f = _car.BodyForward;
                float vx = v.x, vz = v.z, fx = f.x, fz = f.z;
                float v2 = vx * vx + vz * vz;
                if (v2 > 9f && v2 < 150f * 150f && fx * fx + fz * fz > 0.01f)
                {
                    float ang = MathF.Atan2(fz * vx - fx * vz, fx * vx + fz * vz) * Mathf.Rad2Deg;
                    _slip += (ang - _slip) * (1f - MathF.Exp(-dt * 15f));
                }
                else _slip *= MathF.Exp(-dt * 8f);
            }
            _lastBodyPos = _car.BodyPos;
            _haveBody = true;
        }

        // ------------------------------------------------------------------ build / release

        private bool Build()
        {
            if (_voices != null) { try { UnityEngine.Object.Destroy(_voices); } catch { /* gone */ } _voices = null; }   // a half-built try
            if (!GameApi.Sources(out var accel, out var decel, out var blow, out var host) || host == null) return false;
            _accel = Helpers.Clips(accel); _decel = Helpers.Clips(decel); _blow = Helpers.Clips(blow);
            if (Helpers.Pick(_accel, 3) == null || Helpers.Pick(_decel, 0) == null) return false;   // clips not assigned yet (setup runs a little later)
            var mixer = accel[0] != null ? accel[0].outputAudioMixerGroup : null;

            _voices = new GameObject("EngineAudio.Voices");
            try { BuildVoices(host, mixer); }
            catch { try { UnityEngine.Object.Destroy(_voices); } catch { /* gone */ } _voices = null; throw; }
            _muted.Clear();
            foreach (var arr in new[] { accel, decel, blow }) foreach (var s in arr) if (s != null) _muted.Add(s);
            _builtFor = _car.Car;
            _active = true;
            _engine.Reset();
            _load = 0f;
            _haveBody = false; _slip = 0f;
            _engineMixer = mixer;
            _tiresPending = true;
            string tires = BuildTires(mixer);
            _engineName = _accel.Length > 0 && _accel[0] != null ? _accel[0].name.Replace(" accel gear 01", "") : "car";
            _pops = null; _nextPopsTry = 0f;
            Plugin.Log.LogInfo($"[EngineAudio] engine voices ready: rev-up {Helpers.Names(_accel)}; rev-down {Helpers.Names(_decel)}; blow-offs {_blow.Length}; " +
                               $"{(_car.HasGearbox ? "the game's gearbox" : "simulated gears")}, mixer group '{(mixer != null ? mixer.name : "none")}', " +
                               $"clip load type {(_accel.Length > 0 && _accel[0] != null ? _accel[0].loadType.ToString() : "?")}; {tires}; " +
                               $"pitch matching {(Plugin.MatchPitch.Value ? $"on ({PitchCurves.Measured(_accel, _decel)} recordings measured)" : "off")}");
            return true;
        }

        /// <summary>
        /// The tyre squeal on the voices object. Pending (retried every 0.5 s) while the worker is still making the samples;
        /// any failure switches only the squeal off for the session.
        /// </summary>
        private string BuildTires(UnityEngine.Audio.AudioMixerGroup engineMixer)
        {
            _tires = null;
            if (!GameApi.TiresOk) { _tiresPending = false; return "tyre squeal off (game check)"; }
            if (!Plugin.TiresEnabled.Value) { _tiresPending = true; return "tyre squeal off (config; it starts if you switch it on)"; }
            if (_tiresOff) { _tiresPending = false; return "tyre squeal off (earlier error)"; }
            if (_voices == null) return "tyre squeal waiting for the engine voices";
            try
            {
                var state = TireSqueal.EnsureClips();
                if (state == TireSqueal.State.Pending) { _tiresPending = true; return "tyre squeal: clips still being made (it starts in a moment)"; }
                _tiresPending = false;
                if (state == TireSqueal.State.Failed) { _tiresOff = true; return "tyre squeal off (clips failed)"; }
                var drift = GameApi.DriftMixer();
                _tires = new TireSqueal(_voices, drift != null ? drift : engineMixer);
                string msg = $"tyre squeal on (mixer group '{(drift != null ? drift.name : engineMixer != null ? engineMixer.name : "none")}')";
                if (!_tiresLogged) { _tiresLogged = true; Plugin.Log.LogInfo($"[EngineAudio] {msg}"); }
                return msg;
            }
            catch (Exception e)
            {
                _tiresOff = true; _tiresPending = false; _tires = null;
                Plugin.Log.LogWarning($"[EngineAudio] tyre squeal switched off for this session (the game's drift sound stays): {e.Message}");
                return "tyre squeal off (error)";
            }
        }

        /// <summary>The exhaust pops on the voices object; pending while the worker makes the clips; an error switches only them off.</summary>
        private void BuildPops(UnityEngine.Audio.AudioMixerGroup mixer)
        {
            if (_voices == null) return;
            try
            {
                var state = ExhaustPops.EnsureClips();
                if (state == TireSqueal.State.Pending) return;
                if (state == TireSqueal.State.Failed) { _popsOff = true; return; }
                _pops = new ExhaustPops(_voices, mixer);
            }
            catch (Exception e)
            {
                _popsOff = true; _pops = null;
                Plugin.Log.LogWarning($"[EngineAudio] exhaust pops switched off for this session: {e.Message}");
            }
        }

        private bool _tiresLogged;

        private void BuildVoices(GameObject host, UnityEngine.Audio.AudioMixerGroup mixer)
        {
            _voices.transform.SetParent(host.transform, false);
            _on = new Layer(Helpers.NewVoice(_voices, mixer), Helpers.NewVoice(_voices, mixer), 101);
            _off = new Layer(Helpers.NewVoice(_voices, mixer), Helpers.NewVoice(_voices, mixer), 202);
            _idle = new Layer(Helpers.NewVoice(_voices, mixer), Helpers.NewVoice(_voices, mixer), 303);
            _oneShot = Helpers.NewVoice(_voices, mixer);
            _oneShot.volume = 1f;   // PlayOneShot's scale multiplies the source volume (NewVoice leaves it at 0)
        }

        private void MuteGame()
        {
            for (int i = 0; i < _muted.Count; i++) { var s = _muted[i]; if (s != null && !s.mute) s.mute = true; }
        }

        /// <summary>Any of the game's (muted) engine sources still playing = the game's engine sound is on.</summary>
        private bool GameEngineRunning()
        {
            for (int i = 0; i < _muted.Count; i++) { var s = _muted[i]; if (s != null && s.isPlaying) return true; }
            return false;
        }

        private void SilenceAll()
        {
            _on?.Silence(); _off?.Silence(); _idle?.Silence(); _tires?.Silence(); _pops?.Silence();
            if (_oneShot != null) _oneShot.Stop();   // one-shots don't show in isPlaying
        }

        /// <summary>Hands the engine sound back to the game: unmute its sources, remove ours. Never throws.</summary>
        private void Release(string why, bool forget = true)
        {
            EngineLink.Off();   // readers (DriverCam's tachometer) fall back to their own model
            if (!_active && _voices == null && _muted.Count == 0) return;
            foreach (var s in _muted) { try { if (s != null) s.mute = false; } catch { /* gone with the car */ } }
            _muted.Clear();
            try { _on?.Silence(); _off?.Silence(); _idle?.Silence(); _tires?.Silence(); _pops?.Silence(); } catch { /* gone */ }
            try { if (_voices != null) UnityEngine.Object.Destroy(_voices); } catch { /* gone */ }
            _voices = null; _on = _off = _idle = null; _oneShot = null; _tires = null; _pops = null; _haveBody = false;
            _builtFor = IntPtr.Zero;
            if (_active) Plugin.Log.LogInfo($"[EngineAudio] engine sound handed back to the game ({why})");
            _active = false;
            if (forget) GameApi.Forget();
        }

        private void Fault(Exception e)
        {
            _errors++;
            Plugin.Log.LogWarning($"[EngineAudio] error ({_errors}/3): {e.Message}");
            if (_errors >= 3)
            {
                _broken = true;
                Release("errors");
                Plugin.Log.LogError($"[EngineAudio] switched off for this session; the game's own engine sound is back. Last error: {e}");
            }
        }

        // ------------------------------------------------------------------ debug overlay

        private bool _overlayOff;
        private string _overlayText = "";
        private float _nextOverlayText;

        /// <summary>
        /// Debug readout. GUI.Label(Rect, string) only: the (Rect, string, GUIStyle) overload is stripped from this build.
        /// The text is rebuilt at most 10 times a second; any error switches the overlay off for the session.
        /// </summary>
        private void OnGUI()
        {
            if (_broken || _overlayOff || !_active || !Plugin.Overlay.Value) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            try
            {
                if (Time.unscaledTime >= _nextOverlayText)
                {
                    _nextOverlayText = Time.unscaledTime + 0.1f;
                    _overlayText = $"EngineAudio  {_engine.Rpm:0} rpm  gear {_engine.Gear + 1}  span {_engine.Span:0.00}  throttle {_load:0.00}" +
                                   $"{(_engine.OnLimiter ? "  LIMITER" : "")}  on {(_on != null ? _on.Volume : 0f):0.00} {(_on != null ? _on.ClipName : "-")}" +
                                   $"  off {(_off != null ? _off.Volume : 0f):0.00} {(_off != null ? _off.ClipName : "-")}  idle {(_idle != null ? _idle.Volume : 0f):0.00}" +
                                   $"  slip {_slip:0} deg{(_car.Drifting ? " DRIFT" : "")}  squeal {(_tires != null ? _tires.Level : 0f):0.00}";
                }
                int old = GUI.skin.label.fontSize;
                GUI.skin.label.fontSize = 14;
                try { GUI.Label(new Rect(12, Screen.height * 0.5f, 900, 24), _overlayText); }
                finally { GUI.skin.label.fontSize = old; }   // shared skin: CurbFeel / DriverCam labels keep their size
            }
            catch (Exception e)
            {
                _overlayOff = true;
                Plugin.Log.LogWarning($"[EngineAudio] debug overlay switched off for this session: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Helpers kept out of the injected Runner. Why NewVoice is here: 0.1.0's first run failed to load because a local
    /// function in BuildVoices that used both a parameter and `this` compiled to an instance method of Runner taking a
    /// `ref` to a compiler-made closure struct; ClassInjector.RegisterTypeInIl2Cpp converts every instance method of an
    /// injected MonoBehaviour and threw a NullReferenceException in ConvertMethodInfo on it (no IL2CPP class for that
    /// struct). Static methods are never converted, so they are safe here or in Runner.
    /// </summary>
    internal static class Helpers
    {
        /// <summary>One of our engine AudioSources: 2D like the game's, silent until a layer uses it, on the game's mixer group.</summary>
        internal static AudioSource NewVoice(GameObject go, UnityEngine.Audio.AudioMixerGroup mixer)
        {
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.dopplerLevel = 0f;
            s.priority = 0;
            s.volume = 0f;
            if (mixer != null) s.outputAudioMixerGroup = mixer;
            return s;
        }

        private static readonly float[] FallbackRatios = { 0f, 0.25f, 0.4f, 0.6f, 0.8f };   // the game's gearbox, if unreadable

        internal static AudioClip Pick(AudioClip[] clips, int i)
        {
            if (clips.Length == 0) return null;
            i = Math.Max(0, Math.Min(i, clips.Length - 1));
            for (int k = i; k >= 0; k--) if (clips[k] != null) return clips[k];
            return null;
        }

        internal static void FallbackGear(float speedFactor, out int gear, out float progress)
        {
            gear = 0;
            for (int i = FallbackRatios.Length - 1; i >= 0; i--) if (speedFactor >= FallbackRatios[i]) { gear = i; break; }
            float lo = FallbackRatios[gear], hi = gear + 1 < FallbackRatios.Length ? FallbackRatios[gear + 1] : 1f;
            progress = FM.Clamp01((speedFactor - lo) / FM.Max(0.01f, hi - lo));
        }

        internal static AudioClip[] Clips(AudioSource[] s)
        {
            var c = new AudioClip[s.Length];
            for (int i = 0; i < s.Length; i++) c[i] = s[i] != null ? s[i].clip : null;
            return c;
        }

        internal static string Names(AudioClip[] c)
        {
            var n = new List<string>();
            foreach (var x in c) n.Add(x == null ? "-" : $"{x.name} {x.length:0.0}s");
            return string.Join(", ", n);
        }
    }
}
