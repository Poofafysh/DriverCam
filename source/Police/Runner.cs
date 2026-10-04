using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Police
{
    /// <summary>
    /// Patrols, noticing and the chase ("pursuit lite", doc phases 1-3).
    ///
    /// - Tick (every 0.15 s): read the player and our patrols; release patrols that are gone, back in the pool or left
    ///   150 m behind; notice (a crash within NoticeRange, or passing a patrol OverspeedKmh faster than it); step the chase
    ///   and its lead bar; pick a new patrol (a traffic car 300-700 m ahead) about once per PatrolSpacing metres.
    /// - LateUpdate: lightbars follow their cars (not parented) and flash at 2 Hz while chasing.
    /// - OnGUI: a marker above each patrol (blue idle, amber you're in its notice zone, red chasing), the lead bar and
    ///   toasts, top-centre below TrafficDensity's toast.
    /// - The chase uses the traffic AI itself: on the chaser's AIPathFollower, rubberBandingEnabled = false and
    ///   MaxSpeed = SpeedFactor x the player's top speed. Both are captured first and restored when the chase ends, when
    ///   the car goes back to the pool (at once: pooled cars keep serialized fields), and when the plugin switches off.
    /// - Circuit breakers: patrols, lightbars, HUD and the caught penalty each switch themselves off after an error;
    ///   5 errors in 10 s switch the whole plugin off for the session (everything restored and destroyed first).
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private const float TickSeconds = 0.15f;
        private const float PickMinAhead = 300f, PickMaxAhead = 700f, PickLaneClear = 100f;
        private const float ReleaseBehind = 150f, EscapeBehind = 130f;
        private const float CloseGap = 20f, GapSpan = 60f;
        private const float RisePerSecond = 6f, FallPerSecond = 8f;           // lead bar %/s from the gap
        private const float FastBonus = 2f, SlowPenalty = 4f;                 // %/s above 80% / below 60% of top speed
        private const float NearMissBonus = 5f, CrashPenalty = 15f;           // % per event
        private const float PenaltyKeepSeconds = 3f;

        private enum Outcome { None, Escaped, Caught }

        private sealed class Patrol
        {
            public MonoBehaviour Car, Pf;      // AIVehicleController / AIPathFollower, untyped (see GameApi)
            public IntPtr Ptr;
            public Transform T;                // the car's transform, fetched once (no wrapper per frame)
            public Collider Col;
            public Lightbar Bar;
            public CarState S;
            public float LastRel = float.NaN;  // player distance - car distance at the last tick (+ = player ahead)
            public float LastTravelled = float.NaN;
            public bool InZone;
            public float RoofHeight = 1.6f, NextHeight;
            public Vector3 Roof;
            public bool HasRoof;
            // chase
            public bool Chasing;
            public bool SavedRubber;
            public float SavedMax = float.NaN, Written = float.NaN, Offset;
        }

        private readonly List<Patrol> _patrols = new List<Patrol>();
        private readonly HashSet<IntPtr> _patrolPtrs = new HashSet<IntPtr>();
        private readonly List<MonoBehaviour> _candidates = new List<MonoBehaviour>();
        private readonly System.Random _rng = new System.Random();
        private Patrol _chaser;
        private PlayerState _player;
        private IntPtr _raceCar, _raceSpawner;
        private float _nextPatrolAt = float.NaN, _nextPickTry, _cooldownUntil, _lastTickTime = -1f, _nextTick;
        private int _lastHits = -1, _lastNear = -1;
        private float _bar, _chaseTime;
        private bool _sessionOn = true;
        private string _state = "", _offReason;

        // breakers
        private readonly Queue<float> _errors = new Queue<float>();
        private bool _broken, _tickOff, _visualsOff, _hudOff, _penaltyOff;
        private Action _tick, _visuals, _hud;

        // input, camera (refreshed every 2 s, not fetched every frame)
        private Keyboard _kb;
        private UnityEngine.InputSystem.Controls.KeyControl _f3;
        private float _nextKbFetch;
        private Camera _cam;
        private float _nextCamFetch;

        // HUD
        private string _toast;
        private float _toastUntil;
        private Color _toastColor;
        private string _barText = "";
        private int _barShownPct = -1, _barShownSec = -1;
        private GUIStyle _white, _label;
        private GUIContent _none, _barContent, _toastContent;
        private int _labelFont = -1;

        private static readonly Color MarkerIdle = new Color(0.25f, 0.5f, 1f, 0.95f);
        private static readonly Color MarkerZone = new Color(1f, 0.7f, 0.1f, 0.95f);
        private static readonly Color MarkerChase = new Color(1f, 0.15f, 0.1f, 0.95f);
        private static readonly Color Shade = new Color(0f, 0f, 0f, 0.65f);
        private static readonly Color Good = new Color(0.3f, 1f, 0.4f, 1f);
        private static readonly Color Bad = new Color(1f, 0.3f, 0.25f, 1f);
        private static readonly Color Warn = new Color(1f, 0.75f, 0.2f, 1f);

        // ------------------------------------------------------------------ Unity entry points

        private void Update()
        {
            if (_broken) return;
            using var perf = RogueShared.Perf.Scope("Police.Update");   // shared timing overlay (TrafficDensity [Perf]); free when off
            try
            {
                if (_tick == null) { _tick = Tick; _visuals = Visuals; _hud = Hud; }   // created once: no delegate per frame
                ReadHotkey();
                float now = Time.unscaledTime;
                if (now >= _nextTick)
                {
                    _nextTick = now + TickSeconds;
                    if (!_tickOff) Guard(ref _tickOff, "patrols", _tick);
                }
            }
            catch (Exception e) { Fault(e); }
        }

        private void LateUpdate()
        {
            if (_broken || _visualsOff || _patrols.Count == 0) return;
            using var perf = RogueShared.Perf.Scope("Police.LateUpdate");
            Guard(ref _visualsOff, "lightbars", _visuals);
        }

        private void OnGUI()
        {
            if (_broken || _hudOff) return;
            var ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;
            if (_patrols.Count == 0 && _chaser == null && (_toast == null || Time.unscaledTime > _toastUntil)) return;
            using var perf = RogueShared.Perf.Scope("Police.OnGUI");
            Guard(ref _hudOff, "HUD", _hud);
        }

        private void OnDestroy() => Shutdown("plugin unloaded");

        // ------------------------------------------------------------------ hotkey

        private void ReadHotkey()
        {
            float now = Time.unscaledTime;
            if (now >= _nextKbFetch)   // at most every 2 s, also while there is no keyboard
            {
                _nextKbFetch = now + 2f;
                var kb = Keyboard.current;
                if (kb == null) { _kb = null; _f3 = null; return; }
                if (_kb == null || _kb.Pointer != kb.Pointer) { _kb = kb; _f3 = kb.f3Key; }
            }
            if (_f3 == null || !_f3.wasPressedThisFrame) return;
            _sessionOn = !_sessionOn;
            Toast(_sessionOn ? "Police patrols ON" : "Police patrols OFF", _sessionOn ? Good : Warn);
            Plugin.Log.LogInfo($"[Police] F3: patrols {(_sessionOn ? "on" : "off")} for this session");
        }

        // ------------------------------------------------------------------ tick

        private string WhyOff()
        {
            if (!Plugin.Enabled.Value) return "disabled in config";
            if (IsMode("Off")) return "Mode = Off";
            if (!_sessionOn) return "off (F3)";
            if (!GameApi.TrafficOk || !GameApi.PlayerOk) return "game check failed (see log)";
            if (GameApi.IsMultiplayer()) return "multiplayer (single-player only)";
            return null;
        }

        private static bool IsMode(string m) => string.Equals(Plugin.Mode.Value?.Trim(), m, StringComparison.OrdinalIgnoreCase);

        private void SetState(string s)
        {
            if (s == _state) return;
            _state = s;
            Plugin.Log.LogInfo($"[Police] {s}");
        }

        private void Tick()
        {
            string off = WhyOff();
            if (off != null)
            {
                if (_patrols.Count > 0) ReleaseAll(off);
                if (!ReferenceEquals(off, _offReason)) { _offReason = off; SetState("idle: " + off); }   // no string built per tick
                return;
            }
            _offReason = null;
            if (!GameApi.ReadPlayer(ref _player))
            {
                if (_patrols.Count > 0) ReleaseAll("no player car");
                _raceCar = IntPtr.Zero;
                SetState("waiting for a race");
                return;
            }
            IntPtr spawner = GameApi.Spawner();
            if (spawner == IntPtr.Zero)
            {
                if (_patrols.Count > 0) ReleaseAll("no traffic spawner");
                SetState("waiting for traffic");
                return;
            }
            if (_player.Car != _raceCar || spawner != _raceSpawner)
            {
                // a new race (or a new car): nothing carries over
                if (_patrols.Count > 0) ReleaseAll("new race");
                _raceCar = _player.Car; _raceSpawner = spawner;
                _nextPatrolAt = float.NaN; _cooldownUntil = 0f; _lastHits = -1; _lastNear = -1; _lastTickTime = -1f; _nextPickTry = 0f;
                GameApi.ForgetScene();
            }
            if (_player.LevelEnded)
            {
                if (_patrols.Count > 0) ReleaseAll("race over");
                SetState("race over");
                return;
            }
            SetState(IsMode("Chill") ? "active (Chill: patrols never notice)" : "active");

            float now = Time.time;   // game time: stands still while paused
            float dt = _lastTickTime < 0f ? 0f : Mathf.Clamp(now - _lastTickTime, 0f, 0.5f);
            float rawDt = _lastTickTime < 0f ? 0f : Mathf.Max(0f, now - _lastTickTime);
            _lastTickTime = now;

            int hits = 0, nears = 0;
            if (_player.Hits >= 0 && _lastHits >= 0 && _player.Hits > _lastHits) hits = _player.Hits - _lastHits;
            if (_player.NearMisses >= 0 && _lastNear >= 0 && _player.NearMisses > _lastNear) nears = _player.NearMisses - _lastNear;
            _lastHits = _player.Hits; _lastNear = _player.NearMisses;

            UpdatePatrols(rawDt);
            bool wasChasing = _chaser != null;
            if (dt > 0f) Notice(hits, now);
            if (_chaser != null && wasChasing) StepChase(dt, hits, nears);   // a chase that just started doesn't also pay for the crash that started it
            MaybePick();
        }

        /// <summary>Reads every patrol; releases the ones that are gone, back in the pool, reused, or 150 m behind.</summary>
        private void UpdatePatrols(float rawDt)
        {
            float range = Mathf.Clamp(Plugin.NoticeRange.Value, 10f, 200f);
            for (int i = _patrols.Count - 1; i >= 0; i--)
            {
                var p = _patrols[i];
                bool ok = GameApi.ReadCar(p.Car, p.Pf, ref p.S);
                if (!ok || !p.S.Active)
                {
                    // gone or back in the pool: restore at once (pooled cars keep serialized fields)
                    if (p == _chaser) EndChase(Outcome.Escaped, ok ? "the chaser was despawned" : "the chaser is gone");
                    else Release(p, ok ? "returned to the pool" : "gone");
                    continue;
                }
                // a big jump in its own distance means the pool reused it between two ticks: it is a different car now
                if (!float.IsNaN(p.LastTravelled))
                {
                    float moved = p.S.Travelled - p.LastTravelled;
                    float limit = Mathf.Max(60f, Mathf.Abs(p.S.Speed) * rawDt * 2f + 20f);
                    if (moved < -5f || moved > limit)
                    {
                        // SetVehicle has already written a fresh MaxSpeed: give back only the serialized switch
                        if (p == _chaser) { RestoreChase(p, false); EndChase(Outcome.Escaped, "the chaser was despawned (reused)"); }
                        else Release(p, "reused by the pool");
                        continue;
                    }
                }
                p.LastTravelled = p.S.Travelled;
                float rel = _player.Distance - p.S.Road;
                if (p != _chaser && rel > ReleaseBehind) { Release(p, "left behind"); continue; }
                p.InZone = Mathf.Abs(rel) <= range;
            }
        }

        /// <summary>A patrol notices a crash within its zone, or the player blasting past it. One chase at a time, then a cooldown.</summary>
        private void Notice(int hits, float now)
        {
            bool canNotice = _chaser == null && now >= _cooldownUntil && IsMode("Normal");
            float range = Mathf.Clamp(Plugin.NoticeRange.Value, 10f, 200f);
            float overspeed = Mathf.Clamp(Plugin.OverspeedKmh.Value, 10f, 300f) / 3.6f;
            Patrol crashWitness = null; float crashDist = float.MaxValue;
            Patrol passed = null; float passedBy = 0f;
            for (int i = 0; i < _patrols.Count; i++)
            {
                var p = _patrols[i];
                float rel = _player.Distance - p.S.Road;
                if (canNotice && p != _chaser)
                {
                    float d = Mathf.Abs(rel);
                    if (hits > 0 && d <= range && d < crashDist) { crashWitness = p; crashDist = d; }
                    float faster = _player.Speed - p.S.Speed;
                    if (!float.IsNaN(p.LastRel) && p.LastRel < 0f && rel >= 0f && d <= range && faster > overspeed && passed == null)
                    { passed = p; passedBy = faster; }
                }
                p.LastRel = rel;
            }
            if (crashWitness != null) StartChase(crashWitness, $"crash {crashDist:0} m from it");
            else if (passed != null) StartChase(passed, $"passed it {passedBy * 3.6f:0} km/h faster");
        }

        private void MaybePick()
        {
            int max = Mathf.Clamp(Plugin.MaxPatrols.Value, 0, 4);
            float spacing = Mathf.Clamp(Plugin.PatrolSpacing.Value, 300f, 10000f);
            if (float.IsNaN(_nextPatrolAt)) _nextPatrolAt = _player.Distance + spacing * (0.2f + 0.6f * (float)_rng.NextDouble());
            if (_patrols.Count >= max || _player.Distance < _nextPatrolAt || Time.unscaledTime < _nextPickTry) return;
            _nextPickTry = Time.unscaledTime + 1f;   // no candidate: look again in a second, never every tick

            GameApi.FindCandidates(_player.Distance, _player.Lane, PickMinAhead, PickMaxAhead, PickLaneClear, _patrolPtrs, _candidates);
            if (_candidates.Count == 0) return;
            var car = _candidates[_rng.Next(_candidates.Count)];
            _candidates.Clear();   // drop the other wrappers

            var pf = GameApi.PathFollowerOf(car);
            if (pf == null) return;
            var p = new Patrol { Car = car, Pf = pf, Ptr = car.Pointer, T = car.transform, Col = GameApi.ColliderOf(car) };
            if (!GameApi.ReadCar(p.Car, p.Pf, ref p.S)) return;
            p.LastTravelled = p.S.Travelled;
            p.LastRel = _player.Distance - p.S.Road;
            if (!_visualsOff)
            {
                try { p.Bar = Lightbar.Create(); }
                catch (Exception e) { _visualsOff = true; Plugin.Log.LogWarning($"[Police] lightbars switched off for this session (markers stay): {e.Message}"); }
            }
            _patrols.Add(p);
            _patrolPtrs.Add(p.Ptr);
            _nextPatrolAt = _player.Distance + spacing * (0.6f + 0.8f * (float)_rng.NextDouble());
            if (Plugin.LogEvents.Value)
                Plugin.Log.LogInfo($"[Police] patrol picked: {p.S.Road - _player.Distance:0} m ahead, lane {p.S.Lane:0.0} m, {p.S.Speed * 3.6f:0} km/h " +
                                   $"({_patrols.Count}/{max} patrols, next in ~{_nextPatrolAt - _player.Distance:0} m)");
        }

        // ------------------------------------------------------------------ chase

        private void StartChase(Patrol p, string reason)
        {
            GameApi.ReadChase(p.Pf, out p.SavedRubber, out p.SavedMax);
            p.Written = float.NaN; p.Offset = 0f;
            p.Chasing = true;
            _chaser = p;
            _bar = 50f; _chaseTime = 0f;
            _barShownPct = -1;
            ApplyChase(p);
            Toast("POLICE!  Lose them", Bad);
            if (Plugin.LogEvents.Value)
                Plugin.Log.LogInfo($"[Police] noticed: {reason}. Chase on (you {_player.Speed * 3.6f:0} km/h, patrol {p.S.Speed * 3.6f:0} km/h, " +
                                   $"its top speed {p.SavedMax * 3.6f:0} -> {p.Written * 3.6f:0} km/h, rubber banding {p.SavedRubber} -> False)");
        }

        /// <summary>
        /// Rubber banding off, MaxSpeed = SpeedFactor x the player's top speed. Re-applied every tick. The game itself only
        /// moves MaxSpeed for slow motion (-/+ 0.2 x the car's base speed): any change we didn't write is kept as an
        /// offset, applied on top of ours and given back on restore, so slow motion still slows the chaser and restores exactly.
        /// </summary>
        private void ApplyChase(Patrol p)
        {
            float cur = GameApi.ReadMaxSpeed(p.Pf);
            if (!float.IsNaN(p.Written) && Mathf.Abs(cur - p.Written) > 0.01f) p.Offset += cur - p.Written;
            float target = _player.TopSpeed > 1f ? Mathf.Clamp(Plugin.SpeedFactor.Value, 0.5f, 0.99f) * _player.TopSpeed : p.SavedMax;
            float v = Mathf.Max(1f, target + p.Offset);
            GameApi.WriteChase(p.Pf, false, v);
            p.Written = v;
        }

        /// <summary>Gives the car its own values back. writeMax false = only the serialized rubber-banding switch (the game reset MaxSpeed).</summary>
        private static void RestoreChase(Patrol p, bool writeMax)
        {
            if (!p.Chasing) return;
            p.Chasing = false;
            try
            {
                if (p.Pf != null) GameApi.WriteChase(p.Pf, p.SavedRubber, writeMax && !float.IsNaN(p.SavedMax) ? p.SavedMax + p.Offset : float.NaN);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Police] restoring a patrol car's AI values failed: {e.Message}"); }
        }

        private void StepChase(float dt, int hits, int nears)
        {
            var p = _chaser;
            ApplyChase(p);
            _chaseTime += dt;
            float gap = _player.Distance - p.S.Road;   // + = you're ahead of the chaser

            float rate;
            if (gap > CloseGap) rate = RisePerSecond * Mathf.Clamp01((gap - CloseGap) / GapSpan);
            else rate = -FallPerSecond;                 // within 20 m, or it's alongside / ahead of you
            if (_player.TopSpeed > 1f)
            {
                float frac = _player.Speed / _player.TopSpeed;
                if (frac > 0.8f) rate += FastBonus;
                else if (frac < 0.6f) rate -= SlowPenalty;
            }
            _bar = Mathf.Clamp(_bar + rate * dt + nears * NearMissBonus - hits * CrashPenalty, 0f, 100f);

            float duration = Mathf.Clamp(Plugin.Duration.Value, 10f, 300f);
            if (gap > EscapeBehind) EndChase(Outcome.Escaped, $"left it {gap:0} m behind");
            else if (_bar >= 100f) EndChase(Outcome.Escaped, "lead bar full");
            else if (_bar <= 0f) EndChase(Outcome.Caught, "lead bar empty");
            else if (_chaseTime >= duration) EndChase(_bar > 50f ? Outcome.Escaped : Outcome.Caught, $"time up at {_bar:0}%");
        }

        private void EndChase(Outcome outcome, string reason)
        {
            var p = _chaser;
            _chaser = null;
            if (p == null) return;
            RestoreChase(p, true);   // no-op if a reuse already restored the switch
            if (outcome != Outcome.None) _cooldownUntil = Time.time + Mathf.Clamp(Plugin.Cooldown.Value, 0f, 300f);

            string penalty = "";
            if (outcome == Outcome.Escaped) Toast("ESCAPED", Good);
            else if (outcome == Outcome.Caught)
            {
                float taken = 0f; string note = null;
                float want = Mathf.Clamp(Plugin.CaughtPenaltySeconds.Value, 0f, 30f);
                if (want > 0f && GameApi.TimerOk && !_penaltyOff)
                {
                    try { taken = GameApi.TakeTime(want, PenaltyKeepSeconds, out note); }
                    catch (Exception e) { _penaltyOff = true; note = "penalty switched off after an error: " + e.Message; Plugin.Log.LogWarning($"[Police] {note}"); }
                }
                else note = want <= 0f ? "penalty off" : !GameApi.TimerOk ? "timer not found (game check)" : "penalty switched off";
                Toast(taken > 0f ? $"CAUGHT  -{taken:0.#} s" : "CAUGHT", Bad);
                penalty = taken > 0f ? $", -{taken:0.#} s" : $", no time penalty ({note})";
            }
            if (Plugin.LogEvents.Value)
                Plugin.Log.LogInfo($"[Police] chase over: {(outcome == Outcome.None ? "cancelled" : outcome.ToString().ToUpperInvariant())} ({reason}) after {_chaseTime:0.0} s, " +
                                   $"lead {_bar:0}%{penalty}; patrol AI values restored");
            Release(p, "chase over");
            float spacing = Mathf.Clamp(Plugin.PatrolSpacing.Value, 300f, 10000f);
            if (!float.IsNaN(_nextPatrolAt)) _nextPatrolAt = Mathf.Max(_nextPatrolAt, _player.Distance + 0.5f * spacing);
        }

        // ------------------------------------------------------------------ patrol lifetime

        private void Release(Patrol p, string reason)
        {
            if (p == _chaser) { EndChase(Outcome.None, reason); return; }   // EndChase restores, then releases
            RestoreChase(p, true);
            if (p.Bar != null) { p.Bar.Destroy(); p.Bar = null; }
            _patrols.Remove(p);
            _patrolPtrs.Remove(p.Ptr);
            if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] patrol released: {reason} ({_patrols.Count} left)");
        }

        private void ReleaseAll(string reason)
        {
            if (_chaser != null) EndChase(Outcome.None, reason);
            for (int i = _patrols.Count - 1; i >= 0; i--) Release(_patrols[i], reason);
            _patrols.Clear();
            _patrolPtrs.Clear();
        }

        /// <summary>Restores every car and destroys everything we created. Never throws.</summary>
        private void Shutdown(string reason)
        {
            try { ReleaseAll(reason); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Police] shutdown cleanup: {e.Message}");
                foreach (var p in _patrols) { try { RestoreChase(p, true); p.Bar?.Destroy(); } catch { /* the scene takes it */ } }
                _patrols.Clear(); _patrolPtrs.Clear(); _chaser = null;
            }
        }

        private void DestroyLightbars()
        {
            foreach (var p in _patrols)
            {
                try { p.Bar?.Destroy(); } catch { /* the scene takes it */ }
                p.Bar = null;
            }
        }

        // ------------------------------------------------------------------ visuals (every frame)

        private void Visuals()
        {
            var look = ((int)(Time.time * 4f) & 1) == 0 ? Lightbar.Look.FlashRed : Lightbar.Look.FlashBlue;   // 2 Hz red/blue
            float now = Time.unscaledTime;
            for (int i = 0; i < _patrols.Count; i++)
            {
                var p = _patrols[i];
                if (p.T == null) { p.HasRoof = false; continue; }   // destroyed with the scene; released at the next tick
                Vector3 pos = p.T.position;
                if (now >= p.NextHeight)
                {
                    p.NextHeight = now + 1f;
                    if (p.Col != null && p.Col.enabled)
                    {
                        float top = p.Col.bounds.max.y - pos.y;
                        if (top > 0.8f && top < 5f) p.RoofHeight = top + 0.08f;
                    }
                }
                Vector3 up = p.T.up;
                p.Roof = pos + up * p.RoofHeight;
                p.HasRoof = true;
                if (p.Bar != null)
                {
                    p.Bar.Place(p.Roof, p.T.rotation, p.T.right);
                    p.Bar.Show(p.Chasing ? look : Lightbar.Look.Idle);
                }
            }
        }

        // ------------------------------------------------------------------ HUD (IMGUI; GUI.Box / GUI.Label only: GUI.DrawTexture is stripped)

        private void Hud()
        {
            if (_white == null)
            {
                _white = new GUIStyle();
                _white.normal.background = Texture2D.whiteTexture;
                _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                _label.normal.textColor = Color.white;
                _none = GUIContent.none;
                _barContent = new GUIContent();
                _toastContent = new GUIContent();
                if (_toast != null) _toastContent.text = _toast;
            }
            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            var old = GUI.color;
            try
            {
                DrawMarkers(s);
                if (_chaser != null) DrawLeadBar(s);
                if (_toast != null && Time.unscaledTime <= _toastUntil) DrawToast(s);
            }
            finally { GUI.color = old; }
        }

        private void DrawMarkers(float s)
        {
            float now = Time.unscaledTime;
            if (now >= _nextCamFetch) { _nextCamFetch = now + 2f; _cam = Camera.main; }   // at most every 2 s, also while there is none
            if (_cam == null) return;
            for (int i = 0; i < _patrols.Count; i++)
            {
                var p = _patrols[i];
                if (!p.HasRoof) continue;
                Vector3 sp = _cam.WorldToScreenPoint(new Vector3(p.Roof.x, p.Roof.y + 1.1f, p.Roof.z));
                if (sp.z < 1f || sp.z > 900f) continue;
                float px = Mathf.Clamp(700f / sp.z, 7f, 20f) * s;
                float x = sp.x - px * 0.5f, y = Screen.height - sp.y - px;
                GUI.color = Shade;
                GUI.Box(new Rect(x - 2f * s, y - 2f * s, px + 4f * s, px + 4f * s), _none, _white);
                GUI.color = p.Chasing ? MarkerChase : p.InZone ? MarkerZone : MarkerIdle;
                GUI.Box(new Rect(x, y, px, px), _none, _white);
            }
        }

        private void DrawLeadBar(float s)
        {
            float w = 340f * s, h = 24f * s, x = (Screen.width - w) * 0.5f, y = 112f * s;   // below TrafficDensity's toast (y 70-106)
            GUI.color = Shade;
            GUI.Box(new Rect(x, y, w, h), _none, _white);
            float pad = 3f * s;
            GUI.color = _bar >= 70f ? Good : _bar <= 30f ? Bad : Warn;
            GUI.Box(new Rect(x + pad, y + pad, (w - 2f * pad) * _bar / 100f, h - 2f * pad), _none, _white);

            int pct = Mathf.RoundToInt(_bar);
            int sec = Mathf.CeilToInt(Mathf.Max(0f, Mathf.Clamp(Plugin.Duration.Value, 10f, 300f) - _chaseTime));
            if (pct != _barShownPct || sec != _barShownSec)
            {
                _barShownPct = pct; _barShownSec = sec;
                _barText = $"POLICE   lead {pct}%   {sec} s";
                _barContent.text = _barText;
            }
            SetFont(Mathf.RoundToInt(14f * s));
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, w, h), _barContent, _label);
        }

        private void DrawToast(float s)
        {
            float w = 360f * s, h = 34f * s, x = (Screen.width - w) * 0.5f, y = 144f * s;
            GUI.color = Shade;
            GUI.Box(new Rect(x, y, w, h), _none, _white);
            SetFont(Mathf.RoundToInt(18f * s));
            GUI.color = _toastColor;
            GUI.Label(new Rect(x, y, w, h), _toastContent, _label);
        }

        private void SetFont(int size)
        {
            if (size == _labelFont) return;
            _labelFont = size;
            _label.fontSize = size;
        }

        private void Toast(string text, Color color)
        {
            _toast = text;
            _toastColor = color;
            _toastUntil = Time.unscaledTime + 2.5f;
            if (_toastContent != null) _toastContent.text = text;   // before the first OnGUI: set when the HUD is created
        }

        // ------------------------------------------------------------------ breakers

        /// <summary>Runs one feature; an exception switches just that feature off (logged once) and counts toward the plugin breaker.</summary>
        private void Guard(ref bool off, string feature, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                off = true;
                Plugin.Log.LogWarning($"[Police] {feature} switched off for this session after an error: {e.Message}");
                if (feature == "patrols") Shutdown("patrols off");
                else if (feature == "lightbars") DestroyLightbars();
                Fault(e);
            }
        }

        private void Fault(Exception e)
        {
            float now = Time.unscaledTime;
            _errors.Enqueue(now);
            while (_errors.Count > 0 && now - _errors.Peek() > 10f) _errors.Dequeue();
            if (_errors.Count >= 5)
            {
                _broken = true;
                Shutdown("plugin switched off");
                Plugin.Log.LogError($"[Police] switched off for this session after repeated errors; patrol cars restored, our objects removed. Last error: {e}");
            }
            else Plugin.Log.LogWarning($"[Police] error ({_errors.Count}/5 in 10 s): {e.Message}");
        }
    }
}
