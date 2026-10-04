using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Police
{
    /// <summary>
    /// Patrols, noticing and the chase.
    ///
    /// - Tick (every 0.15 s): read the player and our patrols; release patrols that are gone, back in the pool or left
    ///   150 m behind; notice; step the chase and its lead bar; pick a new patrol (a traffic car 300-700 m ahead) about
    ///   once per PatrolSpacing metres.
    /// - Noticing (0.1, strict): inside a patrol's range (NoticeRange along the road, either side): a crash, a near miss,
    ///   drifting (half range), speeding above SpeedingKmh for SpeedingSeconds, or passing it OverspeedKmh faster.
    ///   During a chase every patrol you come near joins as backup (up to MaxChasers), costing BackupPenalty lead.
    /// - The chase uses the traffic AI itself, on each chaser's AIPathFollower: rubber banding off; MaxSpeed =
    ///   SpeedFactor x your car's CURRENT top speed (upgrades and boosts included: they inherit your stats);
    ///   speedSmoothness lowered to Acceleration (quicker to reach it); behindDistanceDespawn raised past the escape
    ///   distance (the pool no longer takes a chaser away mid-chase). A chaser more than 60 m behind you gets up to CatchUp
    ///   (15%) more top speed, in full at 200 m (the traffic AI loses ground fast). All captured first and restored when
    ///   the chase ends, when the car goes back to the pool (at once: pooled cars keep serialized fields) and when the
    ///   plugin switches off.
    /// - Chase driving (0.7.0, Chase.Drive; review 2026-10-04: units fell 200 m behind in 6 s because the game's traffic
    ///   braking stops them behind any car in their lane): each chaser is driven every frame like a daredevil, by
    ///   DaredevilPlanner on a traffic snapshot (every 0.1 s): it heads for your lane, passes traffic round the gaps,
    ///   takes corners at full speed (the game's curve slow-down held off) and only brakes for a car it can't get round;
    ///   it never steers into you (the planner's never-hit rules). Handed back to its home lane when the chase ends.
    /// - Ends: lead bar full / empty, time up (at or past the middle = escaped), every chaser more than 200 m behind for
    ///   3 s (only with the lead bar at or past the middle and not in the first 12 s), every unit lost (despawned, or 260 m
    ///   behind: "lost them" below the middle), the last unit wrecked ("they crashed"; cancelled in the first 3 s), or the
    ///   race ending (at or past the middle = escaped at the finish, else its points are banked).
    /// - Fairness (0.7.0): a wrecked unit leaves the chase at once and can't start one; a collision only costs lead when
    ///   a unit is near enough to see it, and the bar only empties (BUSTED) with a unit on you. At 80%+ of your top
    ///   speed a unit on your tail no longer drains the bar: busted means caught slow or boxed in.
    /// - Looks (LateUpdate): each patrol is drawn as one of the plugin's own police car models (PoliceModels, cycled), or
    ///   a boss's car in a police livery (Look.CarModels); the traffic car's own model is never drawn meanwhile
    ///   (PoliceCar). Plus a lightbar (Lightbar) and a 3D marker (Marker); none of them parented to the pooled car.
    /// - HUD: PursuitHud (uGUI panel + banners); the old IMGUI drawing stays as the fallback if it can't be built.
    /// - Score: PursuitScore (the PURSUIT category: live points during a chase, escape bonus, results and Victory rows)
    ///   is told when a chase starts, ticks and ends; every exit path of a chase ends its live action.
    /// - Circuit breakers: patrols, visuals, HUD and the caught penalty each switch themselves off after an error;
    ///   5 errors in 10 s switch the whole plugin off for the session (everything restored and destroyed first).
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private const float TickSeconds = 0.15f;
        private const float PickMinAhead = 300f, PickMaxAhead = 700f, PickLaneClear = 100f;
        private const float ReleaseBehind = 150f, EscapeBehind = 200f, ChaseDespawnBehind = 260f;
        private const float CatchUpFrom = 60f, CatchUpFull = 200f;   // a chaser further back than this drives up to CatchUp faster (in full at CatchUpFull)
        private const float EscapeLead = 50f, MinEscapeSeconds = 12f; // a distance escape needs the lead bar at or past the middle, and not in a chase's first 12 s (0.7.0: was 6)
        private const float OutOfSightSeconds = 3f;                   // ... and every unit more than EscapeBehind back for this long (0.7.0)
        private const float SeeCrashBehind = 80f, SeeCrashAhead = 40f; // a collision costs lead only with a unit this close (behind / ahead of you) (0.7.0)
        private const float LaunchFactor = 0.85f;   // a unit that joins a chase launches to this share of your speed (it isn't left crawling at traffic pace)
        // BUSTED <-> EVADE meter (0.1.1, from live logs: 6 of 6 chases were "busted" in 2-8 s just for overtaking,
        // because a patrol ahead of or alongside you counted as closing in). Now only a chaser BEHIND you (or level
        // with you) can bust you, and mostly when you're slow; at speed it only nibbles. A patrol still ahead of you is
        // neutral: you haven't passed it yet.
        private const float CloseGap = 25f, GapSpan = 60f, AlongsideAhead = 4f;
        private const float RisePerSecond = 3f;                               // EVADE %/s at GapSpan past CloseGap
        private const float BustAtSpeed = 1.5f, BustWhenSlow = 9.5f;          // BUSTED %/s with a chaser on you: at >= 60% top speed / stopped
        private const float FastBonus = 1f, StuckPenalty = 2f;              // %/s: above 80% of top speed / below 60% stuck behind a patrol
        private const float SafeShare = 0.8f, SlowShare = 0.6f;              // a unit on you: no drain at 80%+ of your top speed, BustAtSpeed at 60%, more below
        private const float NearMissBonus = 4f, CrashPenalty = 12f;           // % per event
        private const float StartGrace = 3f;                                  // seconds at the start of a chase in which the meter can't fall
        private const float PenaltyKeepSeconds = 3f;
        // chase driving (0.7.0)
        private const float DriveSnapSeconds = 0.1f, DriveLimit = 7.5f, DriveRate = 4.5f, DriveLookAhead = 260f;

        private enum Outcome { None, Escaped, Caught }
        private const string RaceOver = "race over";   // the one cancel reason that banks a chase's pending points

        private sealed class Patrol
        {
            public MonoBehaviour Car, Pf;      // AIVehicleController / AIPathFollower, untyped (see GameApi)
            public IntPtr Ptr;
            public Transform T;                // the car's transform, fetched once (no wrapper per frame)
            public Collider Col;
            public Vector3 BoxC, BoxS;         // the car's box collider (car space)
            public Lightbar Bar;
            public PoliceCar Look;
            public GameObject Skin;            // the traffic model we hid (Look != null)
            public Marker Mark;
            public CarState S;
            public float LastRel = float.NaN;  // player distance - car distance at the last tick (+ = player ahead)
            public float LastTravelled = float.NaN;
            public bool InZone;
            public float PassedAt = -100f;     // game time of the last pass of this patrol
            public bool PassJudged;            // that pass already started (or joined) a chase
            public float RoofHeight = 1.6f, NextHeight;
            public Vector3 Roof;
            public bool HasRoof;
            public float CamDist2;             // squared distance roof - camera this frame (which chasers get a real light)
            public bool Lit;                   // its lightbar's real light is allowed (near the camera, hysteresis)
            // chase
            public bool Chasing;
            public bool SavedRubber;
            public float SavedMax = float.NaN, Written = float.NaN, Offset, SavedSmooth = float.NaN, SavedDespawn = float.NaN;
            public float ChaseTop = float.NaN;  // the chase top speed of the last tick (before the planner's cap)
            // chase driving (0.7.0)
            public MonoBehaviour Lane;         // AIVehicleLaneHandler, untyped
            public int HomeLane;
            public bool Driving;
            public float LatOffset, PlanTarget = float.NaN, Yaw;
            public bool Reverse;               // on the oncoming path: never driven by us
        }

        private readonly List<Patrol> _patrols = new List<Patrol>();
        private readonly List<Patrol> _chasers = new List<Patrol>();
        private readonly List<Patrol> _scratch = new List<Patrol>();
        private readonly HashSet<IntPtr> _patrolPtrs = new HashSet<IntPtr>();
        private readonly List<MonoBehaviour> _candidates = new List<MonoBehaviour>();
        private readonly System.Random _rng = new System.Random();
        private PlayerState _player;
        private IntPtr _raceCar, _raceSpawner;
        private float _nextPatrolAt = float.NaN, _nextPickTry, _cooldownUntil, _lastTickTime = -1f, _nextTick;
        private int _lastHits = -1, _lastNear = -1;
        private float _bar, _chaseTime;
        private float _maxGap, _catchUpTime;   // this chase: farthest any unit fell behind you, seconds a unit drove with catch-up (log)
        private float _farTime;                // this chase: seconds every unit has been more than EscapeBehind back (out of sight)
        // this chase, what drained the bar (log): seconds with a unit on you (and slow), collisions counted / unseen, backup
        private float _onYouTime, _slowOnYouTime, _crashLoss, _backupLoss;
        private int _crashesSeen, _crashesUnseen;
        // chase driving (0.7.0)
        private readonly RoadCar[] _road = new RoadCar[128];
        private int _roadN;
        private float _snapTime, _nextSnap = -1f;
        private PlayerState _dp;               // you, read each frame while chasers are driven
        private float _dpTime = -1f, _dpLaneVel, _dpLastLane = float.NaN;
        private Action _drive;
        private bool _driveOff;
        private bool _sessionOn = true;
        private string _state = "", _offReason;

        // boss looks
        private readonly List<BossModel> _models = new List<BossModel>();
        private float _nextModelLoad;
        private int _modelCursor, _lookFailures, _policeCursor = -1;
        private bool _looksOff, _modelsLogged;

        // breakers
        private readonly Queue<float> _errors = new Queue<float>();
        private bool _broken, _tickOff, _visualsOff, _hudOff, _penaltyOff, _markersOff;
        private Action _tick, _visuals, _hud;
        private PursuitScore _pursuit;   // created on the first Update

        // input, camera (refreshed every 2 s, not fetched every frame)
        private Keyboard _kb;
        private UnityEngine.InputSystem.Controls.KeyControl _f3;
        private float _nextKbFetch;
        private Camera _cam;
        private Transform _camT;               // kept with _cam: no transform wrapper per frame
        private float _nextCamFetch;
        // real lights (0.6.0 perf): only the chasers nearest the camera light the scene; the halo carries the look further
        private const float LightOnDist = 70f, LightOffDist = 80f;
        private const int MaxLitChasers = 2;

        // HUD (new uGUI one, else the IMGUI fallback below)
        private PursuitHud _phud;
        private bool _phudTried;
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

        private bool Chasing => _chasers.Count > 0;

        // ------------------------------------------------------------------ Unity entry points

        private void Update()
        {
            if (_broken) { if (_pursuit != null) _pursuit.CleanupRows(); return; }   // rows still go once their screen has closed
            using var perf = RogueShared.Perf.Scope("Police.Update");   // shared timing overlay (TrafficDensity [Perf]); free when off
            try
            {
                if (_tick == null)
                {
                    _tick = Tick; _visuals = Visuals; _hud = Hud; _drive = Drive; _pursuit = new PursuitScore(Fault);   // created once: no delegate per frame
                    try { useGUILayout = false; } catch { /* only skips the IMGUI layout pass; GUI.Box / GUI.Label need none */ }
                }
                ReadHotkey();
                float now = Time.unscaledTime;
                if (now >= _nextTick)
                {
                    _nextTick = now + TickSeconds;
                    if (!_tickOff) Guard(ref _tickOff, "patrols", _tick);
                    // after the patrol tick: a chase cut short by a new race has already ended its live action
                    if (!_broken) _pursuit.Tick(!_tickOff && WhyOff() == null);
                }
                if (!_broken) _pursuit.Frame();   // PURSUIT rows on the results and Victory screens
            }
            catch (Exception e) { Fault(e); }
        }

        private void LateUpdate()
        {
            if (_broken) return;
            using var perf = RogueShared.Perf.Scope("Police.LateUpdate");
            if (!_driveOff && !_tickOff && _chasers.Count > 0) Guard(ref _driveOff, "chase driving", _drive);
            if (!_visualsOff && _patrols.Count > 0) Guard(ref _visualsOff, "car looks / lightbars", _visuals);
            if (!_hudOff) PursuitHudTick();
        }

        private void PursuitHudTick()
        {
            try
            {
                if (_phud == null)
                {
                    if (_phudTried || !Chasing && (_toast == null || Time.unscaledTime > _toastUntil)) return;   // built on first need
                    _phudTried = true;
                    _phud = PursuitHud.Create();
                    if (!_phud.Ok) { _phud = null; return; }
                    if (_toast != null && Time.unscaledTime <= _toastUntil) _phud.Banner(_toast, _toastColor);
                }
                int secLeft = Mathf.CeilToInt(Mathf.Max(0f, Mathf.Clamp(Plugin.Duration.Value, 10f, 300f) - _chaseTime));
                _phud.Tick(Chasing, _bar, _chasers.Count, secLeft);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Police] new HUD switched off for this session (simple HUD stays): {e.Message}");
                try { _phud?.Destroy(); } catch { /* scene takes it */ }
                _phud = null;
                Fault(e);
            }
        }

        private void OnGUI()
        {
            if (_broken || _hudOff) return;
            // cheap checks first, before Event.current (a wrapper per call): most of the time there is nothing to draw
            bool markers = _markersOff || !Plugin.Markers.Value;           // the 3D markers can't be used: draw the flat ones
            // the flat HUD only once the new one has failed: until its first need it is built in LateUpdate (before
            // OnGUI) whenever there is a chase or banner, so the flat one never had anything to draw then
            bool fallbackHud = _phud == null && _phudTried;
            if (!markers && !fallbackHud) return;
            if (_patrols.Count == 0 && !Chasing && (_toast == null || Time.unscaledTime > _toastUntil)) return;
            var ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;
            using var perf = RogueShared.Perf.Scope("Police.OnGUI");
            _drawMarkers = markers; _drawHud = fallbackHud;
            Guard(ref _hudOff, "HUD", _hud);
        }

        private bool _drawMarkers, _drawHud;

        private void OnDestroy()
        {
            Shutdown("plugin unloaded");
            DestroyShared();
            try { if (_pursuit != null) _pursuit.Destroy(); } catch { /* shutting down */ }
        }

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
            if (_f3 == null || Time.timeScale <= 0f || !_f3.wasPressedThisFrame) return;   // no input (and no chase cancelled) while paused
            _sessionOn = !_sessionOn;
            Toast(_sessionOn ? "POLICE ON" : "POLICE OFF", _sessionOn ? Good : Warn);
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
                // load the police car models now (race start), not when the first patrol is picked mid-race; once per session
                if (string.Equals(Plugin.CarModels.Value, "Police", StringComparison.OrdinalIgnoreCase))
                {
                    try { PoliceModels.EnsureLoaded(); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Police] police car models: {e.Message}"); }
                }
            }
            if (_player.LevelEnded)
            {
                // 0.7.0: at or past the middle when the race ends = escaped at the finish (the time-up rule); else banked
                if (_chaseLive && _bar >= EscapeLead) EndChase(Outcome.Escaped, $"ahead at the finish at {_bar:0}%", true);
                if (_patrols.Count > 0) ReleaseAll(RaceOver);   // a running chase's pending PURSUIT points are banked
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
            bool wasChasing = Chasing;
            if (dt > 0f) Notice(hits, nears, now, dt);
            // a chase that just started doesn't also pay for the event that started it; paused (dt 0): no chase step, no writes
            if (Chasing && wasChasing && dt > 0f) StepChase(dt, hits, nears);
            if (_broken || _tickOff) return;   // a breaker tripped inside the chase step: pick nothing new (nothing would restore it)
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
                    // why (diagnostic): crashed (wrecks are cleared), or how far from you along the road
                    Release(p, ok ? $"returned to the pool ({(p.S.WasHit ? "wrecked" : "not wrecked")}, {p.S.Road - _player.Distance:+0;-0} m from you)" : "gone", true);
                    continue;
                }
                // a big jump in its own distance means the pool reused it between two ticks: it is a different car now
                if (!float.IsNaN(p.LastTravelled))
                {
                    float moved = p.S.Travelled - p.LastTravelled;
                    float limit = Mathf.Max(60f, Mathf.Abs(p.S.Speed) * rawDt * 2f + 20f);
                    if (moved < -5f || moved > limit)
                    {
                        // SetVehicle has already written a fresh MaxSpeed: give back only the serialized fields
                        RestoreChase(p, false);
                        Release(p, "reused by the pool", true);
                        continue;
                    }
                }
                p.LastTravelled = p.S.Travelled;
                if (p.Chasing && p.S.WasHit)
                {
                    // 0.7.0: a wrecked unit leaves the chase at once (it used to stay until the pool took it at 260 m,
                    // draining the bar while you were slow beside it, then "escaping" you with a close-call bonus)
                    float at = p.S.Road - _player.Distance;
                    Release(p, $"wrecked ({at:+0;-0} m from you)", false);
                    if (_chaseLive)
                    {
                        if (_chasers.Count > 0) Toast("UNIT WRECKED", Good);
                        else if (_chaseTime < StartGrace) EndChase(Outcome.None, "the unit crashed at the start");   // you hit it as it noticed you: no chase, no points
                        else EndChase(Outcome.Escaped, "they crashed: the last unit was wrecked");
                    }
                    continue;
                }
                float rel = _player.Distance - p.S.Road;
                if (!p.Chasing && rel > ReleaseBehind) { Release(p, "left behind", false); continue; }
                p.InZone = Mathf.Abs(rel) <= range;
                if (p.Look != null && p.Skin != null) p.Look.HideTraffic(p.Skin);   // undo anything that switched the traffic model back on
            }
            if (!Chasing && _chaseLive) EndChase(Outcome.Escaped, LostAll("every unit was despawned"));
        }

        private bool _chaseLive;   // a chase is running (set by StartChase, cleared by EndChase)

        /// <summary>Strict noticing; during a chase, patrols you come near join as backup.</summary>
        private void Notice(int hits, int nears, float now, float dt)
        {
            // police engage ONLY on a reckless pass (the player's rule, 2026-10-03): the moment you go by a patrol, and
            // only if that pass was reckless. Nothing else (speeding nearby, drifting nearby) starts a chase.
            if (hits > 0) _lastCrash = now;
            if (nears > 0) _lastNearMiss = now;
            if (_player.Drifting) _lastDrift = now;
            float overspeed = Mathf.Clamp(Plugin.OverspeedKmh.Value, 10f, 300f) / 3.6f;
            float closeLane = Mathf.Clamp(Plugin.CloseLaneMetres.Value, 0f, 10f);
            float window = Mathf.Clamp(Plugin.PassWindow.Value, 0.2f, 5f);
            bool normal = IsMode("Normal");
            bool canNotice = !Chasing && now >= _cooldownUntil && normal;
            int maxChasers = Mathf.Clamp(Plugin.MaxChasers.Value, 1, 4);
            Patrol witness = null; string reason = null;
            _scratch.Clear();
            for (int i = 0; i < _patrols.Count; i++)
            {
                var p = _patrols[i];
                float rel = _player.Distance - p.S.Road;
                if (!p.Chasing && normal)
                {
                    // a wrecked patrol can't chase (0.7.0: ramming a patrol used to start a chase that paid an escape)
                    bool watching = !p.S.WasHit && (Chasing ? _chasers.Count + _scratch.Count < maxChasers : canNotice);
                    // the pass itself: you were behind it at the last tick and are level with or ahead of it now
                    bool passing = !float.IsNaN(p.LastRel) && p.LastRel < 0f && rel >= 0f;
                    if (passing) p.PassedAt = now;
                    string why = null;
                    if (watching && passing) why = Reckless(p, now, now, overspeed, closeLane, window);
                    // a crash, near miss or drift just after the pass (it is still right behind you) counts too
                    else if (watching && now - p.PassedAt <= window && rel >= 0f && rel < 40f && !p.PassJudged) why = Reckless(p, p.PassedAt, now, float.MaxValue, -1f, window);
                    if (why != null)
                    {
                        p.PassJudged = true;
                        if (Chasing) _scratch.Add(p);                  // backup: you blew past it while fleeing
                        else if (witness == null) { witness = p; reason = why; }
                    }
                    else if (passing) p.PassJudged = false;
                }
                p.LastRel = rel;
            }
            if (witness != null) StartChase(witness, reason);
            foreach (var b in _scratch) JoinChase(b);
            _scratch.Clear();
        }

        private float _lastCrash = -100f, _lastNearMiss = -100f, _lastDrift = -100f;

        /// <summary>
        /// Why a pass of p was reckless, or null if it wasn't: much faster than it (overspeed), cutting close at speed
        /// (closer than closeLane across), or a crash / near miss / drift within the pass window.
        /// </summary>
        private string Reckless(Patrol p, float passedAt, float now, float overspeed, float closeLane, float window)
        {
            float faster = _player.Speed - p.S.Speed;
            float across = Mathf.Abs(_player.Lane - p.S.Lane);
            if (Mathf.Abs(_lastCrash - passedAt) <= window && _lastCrash <= now) return "crashed while passing it";
            if (Plugin.NoticeNearMiss.Value && Mathf.Abs(_lastNearMiss - passedAt) <= window && _lastNearMiss <= now) return "near miss while passing it";
            if (Plugin.NoticeDrift.Value && Mathf.Abs(_lastDrift - passedAt) <= window && _lastDrift <= now) return "drifted past it";
            if (faster > overspeed) return $"passed it {faster * 3.6f:0} km/h faster";
            if (closeLane > 0f && across < closeLane && faster > 15f / 3.6f) return $"cut past it {across:0.0} m away at {faster * 3.6f:0} km/h faster";
            return null;
        }

        private void MaybePick()
        {
            int max = Mathf.Clamp(Plugin.MaxPatrols.Value, 0, 4);
            float spacing = Mathf.Clamp(Plugin.PatrolSpacing.Value, 300f, 10000f);
            // the first patrol of a race comes early (5-30% of the spacing), so every race has police to meet
            if (float.IsNaN(_nextPatrolAt)) _nextPatrolAt = _player.Distance + spacing * (0.05f + 0.25f * (float)_rng.NextDouble());
            if (_patrols.Count >= max || _player.Distance < _nextPatrolAt || Time.unscaledTime < _nextPickTry) return;
            _nextPickTry = Time.unscaledTime + 1f;   // no candidate: look again in a second, never every tick

            GameApi.FindCandidates(_player.Distance, _player.Lane, PickMinAhead, PickMaxAhead, PickLaneClear, _patrolPtrs, _candidates);
            if (_candidates.Count == 0) return;
            // prefer cars long enough to carry a boss car's shape (>= 4.3 m box); any car if none
            MonoBehaviour car = null;
            int longOnes = 0;
            for (int i = 0; i < _candidates.Count; i++) { GameApi.BoxOf(_candidates[i], out _, out var sz); if (sz.z >= 4.3f) longOnes++; }
            if (longOnes > 0)
            {
                int pick = _rng.Next(longOnes);
                for (int i = 0; i < _candidates.Count && car == null; i++) { GameApi.BoxOf(_candidates[i], out _, out var sz); if (sz.z >= 4.3f && pick-- == 0) car = _candidates[i]; }
            }
            if (car == null) car = _candidates[_rng.Next(_candidates.Count)];
            _candidates.Clear();   // drop the other wrappers

            var pf = GameApi.PathFollowerOf(car);
            if (pf == null) return;
            var p = new Patrol { Car = car, Pf = pf, Ptr = car.Pointer, T = car.transform, Col = GameApi.ColliderOf(car) };
            if (!GameApi.ReadCar(p.Car, p.Pf, ref p.S)) return;
            GameApi.BoxOf(car, out p.BoxC, out p.BoxS);
            p.LastTravelled = p.S.Travelled;
            p.LastRel = _player.Distance - p.S.Road;
            if (!_visualsOff) MakeVisuals(p);
            _patrols.Add(p);
            _patrolPtrs.Add(p.Ptr);
            _nextPatrolAt = _player.Distance + spacing * (0.6f + 0.8f * (float)_rng.NextDouble());
            if (Plugin.LogEvents.Value)
                Plugin.Log.LogInfo($"[Police] patrol picked: {p.S.Road - _player.Distance:0} m ahead, lane {p.S.Lane:0.0} m, {p.S.Speed * 3.6f:0} km/h, " +
                                   $"{(p.Look != null ? p.Look.Name : "traffic look")} ({_patrols.Count}/{max} patrols, next in ~{_nextPatrolAt - _player.Distance:0} m)");
        }

        /// <summary>Boss-car look, lightbar and marker for a new patrol. Each part fails on its own (the patrol still works).</summary>
        private void MakeVisuals(Patrol p)
        {
            string models = Plugin.CarModels.Value ?? "Police";
            if (!string.Equals(models, "Traffic", StringComparison.OrdinalIgnoreCase) && !_looksOff && GameApi.SkinOk)
            {
                try
                {
                    var skin = GameApi.SkinOf(p.Car);
                    if (skin != null)
                    {
                        string livery = Plugin.Livery.Value ?? "Classic";
                        PoliceCar look = null;
                        // our own models first; the boss cars when asked for, or when the model files are missing
                        if (string.Equals(models, "Police", StringComparison.OrdinalIgnoreCase) && PoliceModels.EnsureLoaded() > 0)
                        {
                            if (_policeCursor < 0) _policeCursor = _rng.Next(PoliceModels.Count);
                            look = PoliceCar.Build(PoliceModels.Get(_policeCursor++), livery);
                        }
                        else if (GameApi.BossOk)
                        {
                            var model = NextModel();
                            if (model != null) look = PoliceCar.Build(model, livery);
                        }
                        if (look != null)
                        {
                            p.Look = look;
                            p.Skin = skin;
                            look.HideTraffic(skin);
                        }
                    }
                }
                catch (Exception e)
                {
                    try { p.Look?.Destroy(); } catch { /* scene takes it */ }
                    p.Look = null; p.Skin = null;
                    if (++_lookFailures >= 3) { _looksOff = true; Plugin.Log.LogWarning($"[Police] police car looks switched off for this session after 3 failures (lightbars stay): {e.Message}"); }
                    else Plugin.Log.LogWarning($"[Police] police car look failed for one patrol ({_lookFailures}/3): {e.Message}");
                }
            }
            try { p.Bar = Lightbar.Create(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Police] lightbar failed for one patrol: {e.Message}"); }
            if (Plugin.Markers.Value && !_markersOff)
            {
                try { p.Mark = Marker.Create(); if (p.Mark == null) _markersOff = true; }
                catch (Exception e) { _markersOff = true; Plugin.Log.LogWarning($"[Police] 3D markers switched off for this session (flat markers instead): {e.Message}"); }
            }
        }

        /// <summary>The boss models (loaded once the boss list is available), handed out in a shuffled cycle.</summary>
        private BossModel NextModel()
        {
            if (_models.Count == 0)
            {
                if (Time.unscaledTime < _nextModelLoad) return null;
                _nextModelLoad = Time.unscaledTime + 10f;
                GameApi.LoadBossModels(_models, w => Plugin.Log.LogWarning($"[Police] {w}"));
                for (int i = _models.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); (_models[i], _models[j]) = (_models[j], _models[i]); }
                if (!_modelsLogged && _models.Count > 0)
                {
                    _modelsLogged = true;
                    var names = new List<string>();
                    foreach (var m in _models) names.Add($"{m.Boss}: {m.Car}{(m.ForceOn.Count > 0 ? $" (+{m.ForceOn.Count} kit parts)" : "")}");
                    Plugin.Log.LogInfo($"[Police] {_models.Count} boss cars for police looks: {string.Join(", ", names)}");
                }
                if (_models.Count == 0) return null;
            }
            var model = _models[_modelCursor % _models.Count];
            _modelCursor++;
            if (model.Prefab == null) { _models.Clear(); return null; }   // unloaded: reload next time
            return model;
        }

        // ------------------------------------------------------------------ chase

        private void StartChase(Patrol p, string reason)
        {
            _bar = 50f; _chaseTime = 0f; _chaseLive = true;
            _maxGap = 0f; _catchUpTime = 0f; _farTime = 0f;
            _onYouTime = _slowOnYouTime = _crashLoss = _backupLoss = 0f; _crashesSeen = _crashesUnseen = 0;
            _barShownPct = -1;
            AddChaser(p);
            if (_pursuit != null) _pursuit.ChaseStart(_chasers.Count);   // PURSUIT live action
            Toast("POLICE PURSUIT", Bad);
            if (Plugin.LogEvents.Value)
                Plugin.Log.LogInfo($"[Police] noticed: {reason}. Chase on (you {_player.Speed * 3.6f:0} km/h, your top speed now {Basis() * 3.6f:0} km/h, patrol {p.S.Speed * 3.6f:0} km/h, " +
                                   $"its top speed {p.SavedMax * 3.6f:0} -> {p.Written * 3.6f:0} km/h, acceleration smoothing {p.SavedSmooth:0.00} -> {Smooth(p):0.00} s, rubber banding {p.SavedRubber} -> False, " +
                                   $"driving: {(Driven && p.Lane != null && !p.Reverse ? "own (passes traffic)" : "game traffic")})");
        }

        private void JoinChase(Patrol p)
        {
            AddChaser(p);
            float before = _bar;
            _bar = Mathf.Max(1f, _bar - Mathf.Clamp(Plugin.BackupPenalty.Value, 0f, 50f));
            _backupLoss += before - _bar;
            Toast($"BACKUP JOINED  ({_chasers.Count} UNITS)", Bad);
            if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] backup joined: {_chasers.Count} units, lead {_bar:0}%");
        }

        private void AddChaser(Patrol p)
        {
            GameApi.ReadChase(p.Pf, out p.SavedRubber, out p.SavedMax, out p.SavedSmooth, out p.SavedDespawn);
            p.Written = float.NaN; p.Offset = 0f;
            p.Driving = false; p.PlanTarget = float.NaN; p.Yaw = 0f;
            if (p.Lane == null && GameApi.DaredevilOk)
            {
                p.Lane = GameApi.LaneHandlerOf(p.Car);
                if (p.Lane != null) p.HomeLane = GameApi.ReadHomeLane(p.Lane);
                p.Reverse = GameApi.IsReversePath(p.Car);   // an oncoming patrol keeps the game's driving (the steering frame is verified for your direction only)
            }
            p.Chasing = true;
            _chasers.Add(p);
            ApplyChase(p);
            if (!Driven || p.Lane == null || p.Reverse) GameApi.Launch(p.Pf, LaunchFactor * _player.Speed);   // driven: Drive launches it once the way is clear
        }

        /// <summary>Your car's current top speed (upgrades and boosts), or its base top speed if unknown.</summary>
        private float Basis() => !float.IsNaN(_player.MaxNow) ? _player.MaxNow : _player.TopSpeed;

        private static float Smooth(Patrol p)
        {
            float want = Mathf.Clamp(Plugin.ChaseSmoothness.Value, 0.2f, 5f);
            return float.IsNaN(p.SavedSmooth) ? want : Mathf.Min(p.SavedSmooth, want);   // never slower than its own
        }

        /// <summary>
        /// Rubber banding off, MaxSpeed = SpeedFactor x your current top speed, quicker acceleration, no despawn behind
        /// you. Re-applied every tick. A chaser more than CatchUpFrom behind you (behind = your distance - its distance)
        /// gets up to +CatchUp on that, in full at CatchUpFull, none again within CatchUpFrom. The game itself only moves
        /// MaxSpeed for slow motion (-/+ 0.2 x the car's base speed): any change we didn't write is kept as an offset,
        /// applied on top of ours and given back on restore. Returns the catch-up share used (0 = none).
        /// </summary>
        private float ApplyChase(Patrol p, float behind = 0f)
        {
            float cur = GameApi.ReadMaxSpeed(p.Pf);
            if (!p.Driving && !float.IsNaN(p.Written) && Mathf.Abs(cur - p.Written) > 0.01f) p.Offset += cur - p.Written;   // driven: Drive folds it (never twice)
            float basis = Basis();
            float boost = 0f;
            float target = p.SavedMax;
            if (basis > 1f)
            {
                boost = Mathf.Clamp(Plugin.CatchUp.Value, 0f, 0.5f) * Mathf.Clamp01((behind - CatchUpFrom) / (CatchUpFull - CatchUpFrom));
                target = Mathf.Clamp(Plugin.SpeedFactor.Value, 0.5f, 1.1f) * basis * (1f + boost);
            }
            float v = Mathf.Max(1f, target + p.Offset);
            p.ChaseTop = v;   // the driving (each frame) caps it for traffic it can't get round and writes MaxSpeed itself
            GameApi.WriteChase(p.Pf, false, p.Driving ? float.NaN : v);
            GameApi.WriteChaseExtras(p.Pf, Smooth(p), float.IsNaN(p.SavedDespawn) ? ChaseDespawnBehind : Mathf.Max(p.SavedDespawn, ChaseDespawnBehind));
            if (!p.Driving) p.Written = v;   // driven: Drive keeps Written on what it wrote
            return boost;
        }

        /// <summary>Gives the car its own values back. writeMax false = only the serialized fields (the game reset MaxSpeed).</summary>
        private static void RestoreChase(Patrol p, bool writeMax)
        {
            if (!p.Chasing) return;
            p.Chasing = false;
            bool driving = p.Driving;
            p.Driving = false; p.Yaw = 0f;
            try
            {
                if (p.Pf != null)
                {
                    GameApi.WriteChase(p.Pf, p.SavedRubber, writeMax && !float.IsNaN(p.SavedMax) ? p.SavedMax + p.Offset : float.NaN);
                    GameApi.WriteChaseExtras(p.Pf, p.SavedSmooth, p.SavedDespawn);
                }
                // driven (0.7.0): home lane back and the game's own eased lane change into it (not for a wreck or a reused car)
                if (driving && writeMax && p.Lane != null && p.S.Active && !p.S.WasHit) GameApi.HandBack(p.Lane, p.HomeLane);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Police] restoring a patrol car's AI values failed: {e.Message}"); }
        }

        private void StepChase(float dt, int hits, int nears)
        {
            float gap = float.MaxValue;        // to the nearest chaser either way (+ = you're ahead of it)
            float behind = float.MaxValue;     // to the nearest chaser behind you or level with you
            int onYou = 0;                     // chasers within CloseGap behind / level
            float lastLost = float.NaN;        // the last unit fell past the hold distance (ends the chase below)
            bool catchUp = false;
            bool sees = false;                 // a unit close enough to see a collision of yours
            // distance counts only once earned: the lead bar at or past the middle, after the first seconds of the chase
            bool distanceCounts = _bar >= EscapeLead && _chaseTime >= MinEscapeSeconds;
            for (int i = _chasers.Count - 1; i >= 0; i--)
            {
                var c = _chasers[i];
                if (c.S.WasHit) continue;          // a wreck never counts (it is released at the next read)
                float g = _player.Distance - c.S.Road;
                _maxGap = Mathf.Max(_maxGap, g);
                bool lost = g > ChaseDespawnBehind || (distanceCounts && g > EscapeBehind);
                if (lost && _chasers.Count > 1)
                {
                    Release(c, $"lost you ({g:0} m behind)", false);   // one unit dropped off; the others keep going
                    Toast("UNIT LOST", Good);
                    continue;
                }
                if (g > ChaseDespawnBehind) lastLost = g;
                if (ApplyChase(c, g) > 0f) catchUp = true;
                gap = Mathf.Min(gap, g);
                if (g >= -AlongsideAhead) { behind = Mathf.Min(behind, g); if (g <= CloseGap) onYou++; }
                if (g >= -SeeCrashAhead && g <= SeeCrashBehind) sees = true;
            }
            if (!Chasing) return;
            _chaseTime += dt;
            if (catchUp) _catchUpTime += dt;
            _farTime = gap > EscapeBehind ? _farTime + dt : 0f;
            // a collision only costs lead when a unit is close enough to see it (0.7.0)
            int counted = sees ? hits : 0;
            _crashesSeen += counted; _crashesUnseen += hits - counted;

            float top = Basis();
            float frac = top > 1f ? _player.Speed / top : 1f;
            float rate;
            if (behind == float.MaxValue)
                rate = frac < 0.6f ? -StuckPenalty : 0f;   // every chaser is still ahead of you: neutral unless you're stuck behind it
            else if (behind > CloseGap)
                rate = RisePerSecond * Mathf.Clamp01((behind - CloseGap) / GapSpan) + (frac > 0.8f ? FastBonus : 0f);
            else
            {
                // a chaser on you: at 80%+ of your top speed it can't gain (0.7.0: units now keep up, and a unit on your
                // tail at full speed isn't a bust); from 80% down to 60% it nibbles up to BustAtSpeed; slowed down or
                // stopped next to it, you're busted quickly
                float slow = Mathf.Clamp01(1f - frac / SlowShare);
                float nibble = Mathf.Clamp01((SafeShare - frac) / (SafeShare - SlowShare));
                float close = 1f - Mathf.Clamp01(Mathf.Max(0f, behind) / CloseGap);
                rate = -(BustAtSpeed * nibble + (BustWhenSlow - BustAtSpeed) * slow) * (0.5f + 0.5f * close) * (1f + 0.25f * (onYou - 1));
                if (_chaseTime >= StartGrace) { _onYouTime += dt; if (frac < SlowShare) _slowOnYouTime += dt; }
            }
            if (_chaseTime < StartGrace && rate < 0f) rate = 0f;   // nobody is busted in the first seconds of a chase
            float before = _bar;
            float after = _bar + rate * dt + nears * NearMissBonus - counted * CrashPenalty;
            if (onYou == 0) after = Mathf.Max(after, Mathf.Min(_bar, 1f));   // the bar only empties with a unit on you (0.7.0)
            _crashLoss += counted * CrashPenalty;
            _bar = Mathf.Clamp(after, 0f, 100f);
            if (_pursuit != null) _pursuit.ChaseStep(before, _bar, dt, top > 1f && frac >= PursuitScore.FastShare, _chasers.Count);

            float duration = Mathf.Clamp(Plugin.Duration.Value, 10f, 300f);
            if (!float.IsNaN(lastLost)) EndChase(Outcome.Escaped, LostAll($"the last unit fell {lastLost:0} m behind"));
            else if (_farTime >= OutOfSightSeconds && _bar >= EscapeLead && _chaseTime >= MinEscapeSeconds) EndChase(Outcome.Escaped, $"left it {gap:0} m behind");
            else if (_bar >= 100f) EndChase(Outcome.Escaped, "lead bar full");
            else if (_bar <= 0f) EndChase(Outcome.Caught, "lead bar empty");
            else if (_chaseTime >= duration) EndChase(_bar >= EscapeLead ? Outcome.Escaped : Outcome.Caught, $"time up at {_bar:0}%", true);   // 0.7.0: the middle counts as escaped
        }

        /// <summary>The reason for a chase that ended because every unit was lost: "lost them" below the middle of the bar.</summary>
        private string LostAll(string detail) => _bar < EscapeLead ? "lost them: " + detail : detail;

        private void EndChase(Outcome outcome, string reason, bool timeUp = false)
        {
            if (!_chaseLive) return;
            _chaseLive = false;
            int units = _chasers.Count;
            if (outcome != Outcome.None) _cooldownUntil = Time.time + Mathf.Clamp(Plugin.Cooldown.Value, 0f, 300f);

            // PURSUIT: escaped = into the combo with the escape bonus, caught = lost, race over = banked, else cancelled
            double pts = 0;
            if (_pursuit != null)
            {
                var kind = outcome == Outcome.Escaped ? (timeUp ? PursuitScore.End.TimeUp : PursuitScore.End.Escaped)
                         : outcome == Outcome.Caught ? PursuitScore.End.Caught
                         : string.Equals(reason, RaceOver, StringComparison.Ordinal) ? PursuitScore.End.Bank : PursuitScore.End.Cancel;
                pts = _pursuit.ChaseEnd(kind, _chaseTime, Mathf.Clamp(Plugin.Duration.Value, 10f, 300f), units, reason);
            }

            string penalty = "";
            if (outcome == Outcome.Escaped)
                Toast(pts >= 1 ? "ESCAPED  +" + Math.Round(pts).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "ESCAPED", Good);
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
                Toast(taken > 0f ? $"BUSTED  -{taken:0.#} s" : "BUSTED", Bad);
                penalty = taken > 0f ? $", -{taken:0.#} s" : $", no time penalty ({note})";
            }
            if (Plugin.LogEvents.Value)
                Plugin.Log.LogInfo($"[Police] chase over: {(outcome == Outcome.None ? "cancelled" : outcome.ToString().ToUpperInvariant())} ({reason}) after {_chaseTime:0.0} s, " +
                                   $"lead {_bar:0}%, {units} unit(s){penalty}, max gap {_maxGap:0} m, catch-up used {_catchUpTime:0.0} s; " +
                                   $"drain: unit on you {_onYouTime:0.0} s (slow {_slowOnYouTime:0.0} s), collisions {_crashesSeen} (-{_crashLoss:0}%), unseen {_crashesUnseen}, backup -{_backupLoss:0}%; patrol AI values restored");
            for (int i = _chasers.Count - 1; i >= 0; i--) Release(_chasers[i], "chase over", false);
            _chasers.Clear();
            float spacing = Mathf.Clamp(Plugin.PatrolSpacing.Value, 300f, 10000f);
            if (!float.IsNaN(_nextPatrolAt)) _nextPatrolAt = Mathf.Max(_nextPatrolAt, _player.Distance + 0.5f * spacing);
        }

        // ------------------------------------------------------------------ patrol lifetime

        /// <summary>Restores the car and destroys our objects. lostMidChase: the car vanished; the chase goes on if others remain.</summary>
        private void Release(Patrol p, string reason, bool lostMidChase)
        {
            bool wasChaser = _chasers.Remove(p);
            RestoreChase(p, true);
            if (p.Bar != null) { try { p.Bar.Destroy(); } catch { /* scene */ } p.Bar = null; }
            if (p.Look != null) { try { p.Look.Destroy(); } catch { /* scene */ } p.Look = null; p.Skin = null; }
            if (p.Mark != null) { try { p.Mark.Destroy(); } catch { /* scene */ } p.Mark = null; }
            _patrols.Remove(p);
            _patrolPtrs.Remove(p.Ptr);
            if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] patrol released: {reason} ({_patrols.Count} left{(wasChaser ? $", {_chasers.Count} chasing" : "")})");
            if (wasChaser && lostMidChase && _chaseLive)
            {
                if (_chasers.Count == 0) EndChase(Outcome.Escaped, LostAll($"the last unit was despawned ({reason})"));
                else Toast("UNIT LOST", Good);   // one unit despawned; the others keep going
            }
        }

        private void ReleaseAll(string reason)
        {
            if (_chaseLive) EndChase(Outcome.None, reason);
            for (int i = _patrols.Count - 1; i >= 0; i--) Release(_patrols[i], reason, false);
            _patrols.Clear(); _chasers.Clear();
            _patrolPtrs.Clear();
        }

        /// <summary>Restores every car and destroys everything we created. Never throws.</summary>
        private void Shutdown(string reason)
        {
            try { ReleaseAll(reason); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Police] shutdown cleanup: {e.Message}");
                foreach (var p in _patrols)
                {
                    try { RestoreChase(p, true); } catch { /* scene */ }
                    try { p.Bar?.Destroy(); p.Look?.Destroy(); p.Mark?.Destroy(); } catch { /* the scene takes it */ }
                }
                _patrols.Clear(); _patrolPtrs.Clear(); _chasers.Clear(); _chaseLive = false;
            }
            if (_pursuit != null) _pursuit.Close(false);   // never leave the game's live action open (never throws)
            try { _phud?.Destroy(); } catch { /* scene */ }
            _phud = null;
        }

        private static void DestroyShared()
        {
            try { Lightbar.DestroyShared(); } catch { /* scene */ }
            try { Marker.DestroyShared(); } catch { /* scene */ }
            try { PoliceCar.DestroyShared(); } catch { /* scene */ }
            try { PoliceModels.DestroyAll(); } catch { /* scene */ }
        }

        private void DestroyVisuals()
        {
            foreach (var p in _patrols)
            {
                try { p.Bar?.Destroy(); } catch { /* the scene takes it */ }
                try { p.Look?.Destroy(); } catch { /* the scene takes it */ }
                try { p.Mark?.Destroy(); } catch { /* the scene takes it */ }
                p.Bar = null; p.Look = null; p.Skin = null; p.Mark = null;
            }
        }

        // ------------------------------------------------------------------ chase driving (every frame, 0.7.0)

        /// <summary>Chasers are driven by us: Chase.Drive on, and the lane fields verified (the daredevils' game check).</summary>
        private static bool DriveOn => Plugin.ChaseDrive.Value && GameApi.DaredevilOk;
        /// <summary>DriveOn and the chase-driving breaker hasn't tripped.</summary>
        private bool Driven => DriveOn && !_driveOff;

        /// <summary>
        /// Drives every chaser for the next physics steps like a daredevil: the gap planner on a traffic snapshot (every
        /// 0.1 s) picks its offset across the road (towards your lane, round traffic, never into you) and caps its speed
        /// for a car it can't get round; GameApi.Steer writes the offset, holds the game's curve slow-down and (unless the
        /// snapshot was full) its obstruction braking off, and writes the capped top speed. Nothing while paused.
        /// </summary>
        private void Drive()
        {
            float now = Time.time, dt = Time.deltaTime;
            if (dt <= 0f || Time.timeScale <= 0f) return;   // nothing is written to the game while paused (a switch-off waits too)
            if (!DriveOn)
            {
                // switched off mid-chase: back to the game's driving (home lane, game factors)
                foreach (var c in _chasers) if (c.Driving) StopDriving(c);
                return;
            }
            if (!GameApi.ReadPlayer(ref _dp, false)) return;
            if (_dpTime >= 0f && now - _dpTime > 1e-4f && now - _dpTime < 0.5f && !float.IsNaN(_dpLastLane) && !float.IsNaN(_dp.Lane))
                _dpLaneVel += ((_dp.Lane - _dpLastLane) / (now - _dpTime) - _dpLaneVel) * (1f - Mathf.Exp(-(now - _dpTime) * 8f));
            else if (_dpTime < 0f || now - _dpTime >= 0.5f) _dpLaneVel = 0f;
            _dpLastLane = _dp.Lane; _dpTime = now;

            if (now >= _nextSnap || now < _snapTime)
            {
                _nextSnap = now + DriveSnapSeconds;
                float lo = -60f, hi = 60f;
                foreach (var c in _chasers) { float rel = c.S.Road - _dp.Distance; lo = Mathf.Min(lo, rel - 40f); hi = Mathf.Max(hi, rel + DriveLookAhead); }
                _roadN = GameApi.ReadRoad(_dp.Distance, -lo, hi, _road);
                TrafficTracker.Update(_road, _roadN, now);
                _snapTime = now;
            }
            float youV = Mathf.Max(0f, _dp.Speed);
            float soon = _dp.Lane + Mathf.Clamp(_dpLaneVel, -8f, 8f) * 0.6f;
            for (int i = 0; i < _chasers.Count; i++)
            {
                var c = _chasers[i];
                if (c.Lane == null || c.T == null || c.Reverse) continue;
                if (!GameApi.ReadCar(c.Car, c.Pf, ref c.S) || !c.S.Active || c.S.WasHit) continue;   // the tick releases it
                if (float.IsNaN(c.ChaseTop)) continue;
                // reused by the pool since the last tick (a different car now): never steer it; the tick releases it
                float moved = c.S.Travelled - c.LastTravelled;
                if (!float.IsNaN(c.LastTravelled) && (moved < -5f || moved > Mathf.Max(60f, Mathf.Abs(c.S.Speed) * 0.5f + 20f))) continue;
                // the game's slow-motion change to MaxSpeed (-/+0.2 x base) since our last write: kept as the offset, as in ApplyChase
                float cur = GameApi.ReadMaxSpeed(c.Pf);
                if (!float.IsNaN(c.Written) && Mathf.Abs(cur - c.Written) > 0.01f) { c.Offset += cur - c.Written; c.ChaseTop += cur - c.Written; }
                float v = Mathf.Max(0f, c.S.Speed);
                bool start = !c.Driving;
                if (start)
                {
                    c.Driving = true;
                    c.LatOffset = c.S.Lane;
                    c.PlanTarget = float.NaN;
                    GameApi.ClampSpeed(c.Pf, v);   // from what it really drives: no jump when the game's slow-down factors go to 1
                }
                float halfW = Mathf.Clamp(c.BoxS.x * 0.5f, 0.8f, 1.3f), halfL = Mathf.Clamp(c.BoxS.z * 0.5f, 1.8f, 3.2f);
                var plan = DaredevilPlanner.Plan(new PlanInput
                {
                    Self = c.Ptr, S = c.S.Road, V = v, Offset = c.LatOffset, HalfW = halfW, HalfL = halfL,
                    LineTarget = Mathf.Clamp(_dp.Lane, -DriveLimit, DriveLimit), PrevTarget = c.PlanTarget,
                    Limit = DriveLimit, Rate = DriveRate, Vmax = c.ChaseTop, SinceSnap = now - _snapTime,
                    YouValid = true, YouRoad = _dp.Distance, YouLane = _dp.Lane,
                    YouLo = Mathf.Min(_dp.Lane, soon), YouHi = Mathf.Max(_dp.Lane, soon), YouSpeed = youV,
                }, _road, _roadN);
                c.PlanTarget = plan.Target;
                float before = c.LatOffset;
                c.LatOffset = Mathf.MoveTowards(c.LatOffset, plan.Target, DriveRate * (plan.Evading ? 1.6f : 1f) * dt);
                float sideways = (c.LatOffset - before) / dt;
                c.Yaw = v > 1f ? Mathf.Atan2(sideways, v) * Mathf.Rad2Deg : 0f;   // the look points where it goes
                float top = Mathf.Max(1f, Mathf.Min(c.ChaseTop, plan.Cap));
                GameApi.Steer(c.Pf, c.Lane, c.LatOffset, top, _roadN < _road.Length);   // a full snapshot: keep the game's braking too
                c.Written = top;   // ours: ApplyChase doesn't take it for a slow-motion change
                if (plan.Instant < v) GameApi.ClampSpeed(c.Pf, plan.Instant);
                else if (start) GameApi.Launch(c.Pf, Mathf.Min(LaunchFactor * youV, top));   // the launch, now that the way is known to be clear
            }
        }

        /// <summary>Gives a driven chaser back to the game's driving (still chasing: its chase values stay).</summary>
        private static void StopDriving(Patrol c)
        {
            c.Driving = false; c.Yaw = 0f;
            try { if (c.Lane != null && c.S.Active && !c.S.WasHit) GameApi.HandBack(c.Lane, c.HomeLane); }
            catch { /* destroyed with the scene */ }
        }

        // ------------------------------------------------------------------ visuals (every frame)

        private void Visuals()
        {
            var look = ((int)(Time.time * 4f) & 1) == 0 ? Lightbar.Look.FlashRed : Lightbar.Look.FlashBlue;   // 2 Hz red/blue
            float now = Time.unscaledTime, dt = Time.deltaTime, t = Time.time;
            if (now >= _nextCamFetch || _cam == null || _camT == null) FetchCamera(now);
            Vector3 camPos = _camT != null ? _camT.position : Vector3.zero;
            // which chasers may light the scene: within LightOnDist of the camera (off again past LightOffDist), the
            // MaxLitChasers nearest (from last frame's roof: one frame late is invisible at 2 Hz flashing)
            for (int i = 0; i < _patrols.Count; i++)
            {
                var p = _patrols[i];
                bool near = p.Chasing && p.HasRoof && _camT != null && p.CamDist2 < (p.Lit ? LightOffDist * LightOffDist : LightOnDist * LightOnDist);
                if (near)
                {
                    int closer = 0;
                    for (int j = 0; j < _patrols.Count; j++)
                    {
                        var q = _patrols[j];
                        if (j != i && q.Chasing && q.HasRoof && (q.CamDist2 < p.CamDist2 || q.CamDist2 == p.CamDist2 && j < i)) closer++;
                    }
                    near = closer < MaxLitChasers;
                }
                p.Lit = near;
            }
            for (int i = 0; i < _patrols.Count; i++)
            {
                var p = _patrols[i];
                if (p.T == null) { p.HasRoof = false; continue; }   // destroyed with the scene; released at the next tick
                Vector3 pos = p.T.position;
                Vector3 up = p.T.up;
                if (p.Look != null)
                {
                    p.Look.Place(p.T, p.BoxC, p.BoxS, p.S.Speed, dt, p.Yaw);
                    p.Roof = p.Look.Placed ? p.Look.Roof : pos + up * p.RoofHeight;
                }
                else
                {
                    if (now >= p.NextHeight)
                    {
                        p.NextHeight = now + 1f;
                        if (p.Col != null && p.Col.enabled)
                        {
                            float top = p.Col.bounds.max.y - pos.y;
                            if (top > 0.8f && top < 5f) p.RoofHeight = top + 0.02f;
                        }
                    }
                    p.Roof = pos + up * p.RoofHeight;
                }
                p.HasRoof = true;
                p.CamDist2 = (p.Roof - camPos).sqrMagnitude;
                if (p.Bar != null)
                {
                    p.Bar.Show(p.Chasing ? look : Lightbar.Look.Idle, p.Lit);   // before Place: a halo that comes on is placed this frame
                    p.Bar.Place(p.Roof, p.T.rotation, p.T.right, up, camPos);
                }
                if (p.Mark != null && _cam != null)
                {
                    p.Mark.Set(p.Chasing ? Marker.State.Chase : p.InZone ? Marker.State.Zone : Marker.State.Idle);
                    p.Mark.Place(p.Roof, camPos, t);
                }
            }
        }

        /// <summary>The main camera and its transform, refreshed every 2 s (not fetched every frame).</summary>
        private void FetchCamera(float now)
        {
            _nextCamFetch = now + 2f;
            var cam = Camera.main;
            if (cam == null) { _cam = null; _camT = null; return; }
            if (_cam == null || _camT == null || _cam.Pointer != cam.Pointer) { _cam = cam; _camT = cam.transform; }
        }

        // ------------------------------------------------------------------ fallback HUD (IMGUI; GUI.Box / GUI.Label only: GUI.DrawTexture is stripped)

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
                if (_drawMarkers) DrawMarkers(s);
                if (_drawHud)
                {
                    if (Chasing) DrawLeadBar(s);
                    if (_toast != null && Time.unscaledTime <= _toastUntil) DrawToast(s);
                }
            }
            finally { GUI.color = old; }
        }

        private GUIContent _policeTag;

        private void DrawMarkers(float s)
        {
            float now = Time.unscaledTime;
            if (now >= _nextCamFetch || _cam == null || _camT == null) FetchCamera(now);
            if (_cam == null) return;
            for (int i = 0; i < _patrols.Count; i++)
            {
                var p = _patrols[i];
                if (!p.HasRoof) continue;
                Vector3 sp = _cam.WorldToScreenPoint(new Vector3(p.Roof.x, p.Roof.y + 1.1f, p.Roof.z));
                if (sp.z < 1f || sp.z > 900f) continue;
                float px = Mathf.Clamp(1400f / sp.z, 12f, 30f) * s;
                float x = sp.x - px * 0.5f, y = Screen.height - sp.y - px;
                GUI.color = Shade;
                GUI.Box(new Rect(x - 2f * s, y - 2f * s, px + 4f * s, px + 4f * s), _none, _white);
                var c = p.Chasing ? MarkerChase : p.InZone ? MarkerZone : MarkerIdle;
                GUI.color = c;
                GUI.Box(new Rect(x, y, px, px), _none, _white);
                if (sp.z < 450f)
                {
                    if (_policeTag == null) _policeTag = new GUIContent("POLICE");
                    SetFont(Mathf.RoundToInt(13f * s));
                    float w = 70f * s, h = 18f * s;
                    GUI.color = Shade;
                    GUI.Box(new Rect(sp.x - w * 0.5f, y - h - 3f * s, w, h), _none, _white);
                    GUI.color = c;
                    GUI.Label(new Rect(sp.x - w * 0.5f, y - h - 3f * s, w, h), _policeTag, _label);
                }
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
                _barText = $"POLICE   lead {pct}%   {sec} s   {_chasers.Count} unit(s)";
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
            try { _phud?.Banner(text, color); } catch { /* HUD breaker handles it in LateUpdate */ }
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
                else if (feature == "car looks / lightbars") DestroyVisuals();
                else if (feature == "chase driving") foreach (var c in _chasers) { if (c.Driving) StopDriving(c); }
                else if (feature == "HUD") { try { _phud?.Destroy(); } catch { /* scene */ } _phud = null; }   // nothing frozen on screen
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
                DestroyShared();
                Plugin.Log.LogError($"[Police] switched off for this session after repeated errors; patrol cars restored, our objects removed. Last error: {e}");
            }
            else Plugin.Log.LogWarning($"[Police] error ({_errors.Count}/5 in 10 s): {e.Message}");
        }
    }
}
