using System;
using System.Collections.Generic;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// Daredevils: the game's red "devil" traffic cars (they come up behind you fast, the devil icon shows while one is
    /// within 100 m behind) become rivals: drawn as the game's boss cars (their own paint and body kit; the traffic model
    /// is never drawn meanwhile, as for patrols) and racing the optimal racing line published by the RacingLine plugin
    /// (AppDomain "rogue.racingline", see RacingLine/LineShare.cs). Single-player only; independent of patrols (Mode, F3).
    /// Design doc: "Daredevil Rival AI" (claude.ai artifact YW9wyNs6gWWz1w9kte5GjT).
    ///
    /// Pace (0.5.0): rivals drive YOUR car's numbers scaled by their skill, so the fight is fair and some are quicker than
    /// you, some slower. Each boss has a fixed skill, spread from SkillMin to SkillMax over the game's boss list order.
    /// - top speed = your current top speed (smoothed over 3 s: a boost isn't copied the instant you fire it) x skill;
    /// - cornering = your measured cornering x skill^2 (x DriftCornerFactor for drift cars): the 90th percentile of
    ///   v^2 x |curvature| at your spot on the racing line, learned live while you drive (starts at 16 m/s^2);
    /// - braking 10 x skill, acceleration 5 x skill (m/s^2): a weaker driver brakes gentler, so earlier;
    /// - a per-rival two-pass speed profile of the line from those, read 0.6 s ahead (the game smooth-damps towards it).
    /// Racecraft: slipstream (lined up within 1.5 m, within 30 m behind a car: +6% top speed); passes in the run-up to a
    /// corner take its inside; defending (you 25-60 m behind and closing, a corner within 150 m: one move to cover the
    /// inside, held through the corner); push / conserve (more than 120 m behind you: +3%; more than 250 m ahead: -2%).
    /// Precision by skill: weaker rivals wander off the line (a slow drift of up to 0.9 m at skill 0.90; none from 1.05).
    ///
    /// Driving (LateUpdate, written for the next physics steps; GameApi.Steer):
    /// - lateral: the line's offset E(s) at the car's road distance, with detours round traffic and the player: a car
    ///   ahead that sits on the line within its width + ours + a margin is passed on the inside of the coming corner (or
    ///   the side nearer to us on a straight), blended in with a cosine ramp over a lead-in that grows with the closing
    ///   speed; with neither side free the braking envelope queues it. The offset moves at most 4.5 m/s sideways (5.5
    ///   for drift cars, 1.6x when evading).
    /// - never into you: while you are alongside or close, the side of you it is on is a hard limit (your lane now and
    ///   where you're heading in 0.6 s, plus both widths + 1.6 m); it only swings back in front of you once it is
    ///   12 m + 1.5 s x your extra speed ahead; anything in line ahead caps its speed by a braking envelope (it can always
    ///   slow to that car's real road speed before a safe gap: 8 m + 0.35 s behind you, 5 m + 0.2 s behind traffic,
    ///   allowing for the game's speed smoothing on the closing speed) and inside that gap its running speed is cut at once.
    /// - style: grip cars point where they go (yaw = their sideways motion); drift cars slide: the look is turned into
    ///   the corner by up to MaxSlipAngle as the cornering load rises. Front wheels steer with the corner (line curvature
    ///   over the wheelbase); drift cars counter-steer. The physics car itself is never turned.
    /// The game's own obstruction braking (pedalFactor: a gentle, long-range slow-down) is held off while a rival races
    /// the line (its braking envelope replaces it), except when the traffic snapshot was full (a car could be missing from
    /// it); the game's despawn distances are raised (to at least 350 m behind / 1,400 m ahead) while a daredevil is a
    /// rival, so one that drops back for a moment stays in the race. Without a racing line (RacingLine not installed or still building) rivals keep the
    /// game's own driving. When we let a car go it gets back MaxSpeed, speedSmoothness, rubber banding, its despawn
    /// distances, its home lane (the game's own lane change takes it into a lane) and its own model; a car the pool has
    /// already reused only gets its serialized fields (speedSmoothness, despawn distances) back.
    /// </summary>
    public class Daredevils : MonoBehaviour
    {
        public Daredevils(IntPtr ptr) : base(ptr) { }

        private const float TickSeconds = 0.25f, SnapSeconds = 0.1f;
        private const float FindBehind = 160f, FindAhead = 850f, ReleaseBehind = 340f;
        private const float RaceBehindDespawn = 350f, RaceAheadDespawn = 1400f;          // while it is a rival (the game's: logged once)
        private const float SteerAhead = 1000f, SteerAheadOff = 1050f;                    // further ahead: the game drives it (hysteresis)
        private const float SnapBehind = 220f, SnapAhead = 420f, SnapMargin = 140f;
        private const int MaxRivals = 6;
        private const float LeadSeconds = 0.6f, Smoothness = 0.5f, ProfileTop = 120f;
        private const float BaseBraking = 10f, BaseAccel = 5f;                              // x skill
        private const float GripStart = 16f, GripMin = 8f, GripMax = 45f, GripRate = 3f;    // your cornering estimate (m/s^2)
        private const float TowGap = 30f, TowLane = 1.5f, TowBoost = 1.06f;
        private const float PushBehind = 120f, PushFactor = 1.03f, EaseAhead = 250f, EaseFactor = 0.98f;
        private const float GapMargin = 0.9f, SafeGap = 5f, SafeTime = 0.2f;                                                     // traffic
        private const float YouMargin = 1.6f, YouSafeGap = 8f, YouSafeTime = 0.35f, YouClearAhead = 12f, YouClearSeconds = 1.5f, YouPredict = 0.6f;   // you
        private const float YouHalfWidth = 1.0f, YouHalfLength = 2.4f;
        private const float Wheelbase = 2.7f;

        private sealed class Rival
        {
            public MonoBehaviour Car, Pf, Lane;   // AIVehicleController / AIPathFollower / AIVehicleLaneHandler, untyped (see GameApi)
            public IntPtr Ptr;
            public Transform T;
            public Vector3 BoxC, BoxS;
            public PoliceCar Look;
            public GameObject Skin;
            public bool Drift;
            public string Name = "daredevil";
            public CarState S;
            public float LastTravelled = float.NaN;
            public float SavedMax = float.NaN, SavedSmooth = float.NaN, SavedBehind = float.NaN, SavedAhead = float.NaN;
            public int HomeLane;
            public float Offset, Slip, Yaw, Steer;
            public bool Steering, Saved, SavedRubber, HitLogged, Far;
            // pace
            public float Skill = 1f, Top, Corner;
            public float[] Profile;
            public object ProfileLine;            // the line data the profile was built on
            public float ProfileCorner, NextProfile;
            public float Push = 1f;
            // precision and racecraft
            public float Wander, WanderTarget, NextWander, DefendCorner = float.NaN, DefendSide;
            // race report
            public float NearTime, SpeedSum, YouSum, SpeedTime, Closest = float.PositiveInfinity;
            public int Passes, Passed, RelSign;
        }

        private readonly List<Rival> _rivals = new List<Rival>();
        private readonly HashSet<IntPtr> _ptrs = new HashSet<IntPtr>();
        private readonly List<MonoBehaviour> _found = new List<MonoBehaviour>();
        private readonly RoadCar[] _road = new RoadCar[128];
        private int _roadN;
        private float _snapTime, _nextSnap, _nextTick;
        private PlayerState _player;
        private float _playerTime, _playerLaneVel, _lastPlayerLane = float.NaN;
        private float _playerRoadVel = float.NaN, _lastPlayerDist = float.NaN;
        private float _youTop = float.NaN, _youGrip = GripStart;
        private IntPtr _raceCar;
        private bool _raceReported, _despawnLogged;

        // the racing line (RacingLine's LineShare)
        private object[] _lineData;
        private float _step, _limit;
        private int _n;
        private float[] _e, _k;
        private bool _lineLogged;

        // boss looks
        private readonly List<BossModel> _models = new List<BossModel>();
        private readonly System.Random _rng = new System.Random();
        private int _cursor;
        private float _nextModelLoad;
        private int _lookFailures;
        private bool _looksOff;

        // breakers
        private readonly Queue<float> _errors = new Queue<float>();
        private bool _broken, _tickOff, _driveOff;
        private string _state = "";
        private Action _tick, _drive;

        /// <summary>Daredevils we drive (Police never picks these as patrols).</summary>
        internal static readonly HashSet<IntPtr> Owned = new HashSet<IntPtr>();

        // ------------------------------------------------------------------ Unity entry points

        private void Update()
        {
            if (_broken) return;
            using var perf = RogueShared.Perf.Scope("Police.Daredevils");
            try
            {
                if (_tick == null) { _tick = Tick; _drive = Drive; }   // created once: no delegate per frame
                float now = Time.unscaledTime;
                if (now >= _nextTick)
                {
                    _nextTick = now + TickSeconds;
                    if (!_tickOff) Guard(ref _tickOff, "daredevils", _tick);
                }
            }
            catch (Exception e) { Fault(e); }
        }

        private void LateUpdate()
        {
            if (_broken || _rivals.Count == 0) return;
            using var perf = RogueShared.Perf.Scope("Police.Daredevils.Drive");
            if (!_driveOff) Guard(ref _driveOff, "daredevil driving", _drive);
        }

        private void OnDestroy() => ReleaseAll("plugin unloaded");

        // ------------------------------------------------------------------ tick: find, check, let go

        private string WhyOff()
        {
            if (!Plugin.Enabled.Value) return "disabled in config (General.Enabled)";
            if (!Plugin.DareEnabled.Value) return "disabled in config";
            if (!GameApi.DaredevilOk || !GameApi.PlayerOk) return "game check failed (see log)";
            if (GameApi.IsMultiplayer()) return "multiplayer (single-player only)";
            return null;
        }

        private void SetState(string s)
        {
            if (s == _state) return;
            _state = s;
            Plugin.Log.LogInfo($"[Police] daredevils: {s}");
        }

        private void Tick()
        {
            string off = WhyOff();
            if (off != null) { if (_rivals.Count > 0) ReleaseAll(off); SetState("idle: " + off); return; }
            if (!GameApi.ReadPlayer(ref _player) || GameApi.Spawner() == IntPtr.Zero)
            {
                if (_rivals.Count > 0) ReleaseAll("no race");
                _raceCar = IntPtr.Zero;
                SetState("waiting for a race");
                return;
            }
            if (_player.Car != _raceCar)
            {
                if (_rivals.Count > 0) ReleaseAll("new race");
                _raceCar = _player.Car;
                _lastPlayerLane = float.NaN; _playerLaneVel = 0f; _lastPlayerDist = float.NaN; _playerRoadVel = float.NaN;
                _youTop = float.NaN; _raceReported = false;   // your cornering estimate carries over: usually the same car
            }
            NotePlayer(Time.time);
            if (_player.LevelEnded)
            {
                if (_rivals.Count > 0) ReleaseAll("race over");
                if (!_raceReported && _e != null) { _raceReported = true; Plugin.Log.LogInfo($"[Police] daredevils: race over, your cornering {_youGrip:0} m/s^2, your top speed {_youTop * 3.6f:0} km/h"); }
                SetState("race over");
                return;
            }
            ReadLine();
            SetState(_e != null && Plugin.DareRaceLine.Value ? "active (racing the line)" : "active (no racing line: game driving)");

            // check the ones we have
            for (int i = _rivals.Count - 1; i >= 0; i--)
            {
                var r = _rivals[i];
                if (!GameApi.ReadCar(r.Car, r.Pf, ref r.S) || !r.S.Active) { Release(r, "gone (despawned)"); continue; }
                if (!float.IsNaN(r.LastTravelled) && (r.S.Travelled - r.LastTravelled < -5f || r.S.Travelled - r.LastTravelled > 80f))
                { Release(r, "reused by the pool", true); continue; }
                r.LastTravelled = r.S.Travelled;
                if (_player.Distance - r.S.Road > ReleaseBehind) { Release(r, "left behind"); continue; }
                if (r.S.WasHit && r.Steering)
                {
                    StopSteering(r, false);   // physics has it now; the look stays on it
                    if (!r.HitLogged && Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Police] daredevil {r.Name} crashed");
                    r.HitLogged = true;
                }
                if (r.Look != null && r.Skin != null) r.Look.HideTraffic(r.Skin);
            }

            // new ones
            if (_rivals.Count < MaxRivals)
            {
                GameApi.FindDaredevils(_player.Distance, FindBehind, FindAhead, _ptrs, _found);
                for (int i = 0; i < _found.Count && _rivals.Count < MaxRivals; i++) Adopt(_found[i]);
            }
        }

        private void Adopt(MonoBehaviour car)
        {
            var r = new Rival { Car = car, Ptr = car.Pointer, Pf = GameApi.PathFollowerOf(car), Lane = GameApi.LaneHandlerOf(car) };
            if (r.Pf == null || r.Lane == null || !GameApi.ReadCar(r.Car, r.Pf, ref r.S)) return;
            r.T = car.transform;
            GameApi.BoxOf(car, out r.BoxC, out r.BoxS);
            r.LastTravelled = r.S.Travelled;
            r.Offset = r.S.Lane;
            GameApi.ReadChase(r.Pf, out r.SavedRubber, out r.SavedMax, out r.SavedSmooth, out _);
            GameApi.ReadDespawn(r.Pf, out r.SavedBehind, out r.SavedAhead);
            if (!_despawnLogged) { _despawnLogged = true; Plugin.Log.LogInfo($"[Police] daredevils: the game despawns traffic {r.SavedBehind:0} m behind / {r.SavedAhead:0} m ahead of you; rivals use at least {RaceBehindDespawn:0} / {RaceAheadDespawn:0} m"); }
            r.HomeLane = GameApi.ReadHomeLane(r.Lane);
            r.Saved = true;
            int rank = MakeLook(r);
            r.Skill = SkillFor(rank);
            r.RelSign = Math.Sign(r.S.Road - _player.Distance);
            GameApi.WriteDespawn(r.Pf, Mathf.Max(r.SavedBehind, RaceBehindDespawn), Mathf.Max(r.SavedAhead, RaceAheadDespawn));   // stays in the race
            UpdatePace(r);
            _rivals.Add(r);
            _ptrs.Add(r.Ptr);
            Owned.Add(r.Ptr);
            if (Plugin.LogEvents.Value)
                Plugin.Log.LogInfo($"[Police] daredevil {r.Name}: {r.S.Road - _player.Distance:+0;-0} m from you, skill {r.Skill:0.00}, top {r.Top * 3.6f:0} km/h, " +
                                   $"cornering {r.Corner:0} m/s^2, {(r.Drift ? "drift" : "grip")} style ({_rivals.Count} driving)");
        }

        /// <summary>Skill for a boss's rank in the game's list (spread SkillMin..SkillMax); a random one without a boss look.</summary>
        private float SkillFor(int rank)
        {
            float lo = Mathf.Clamp(Plugin.DareSkillMin.Value, 0.5f, 1.5f), hi = Mathf.Clamp(Plugin.DareSkillMax.Value, 0.5f, 1.5f);
            if (hi < lo) (lo, hi) = (hi, lo);
            int count = _models.Count;
            float t = rank >= 0 && count > 1 ? (float)rank / (count - 1) : (float)_rng.NextDouble();
            return lo + (hi - lo) * Mathf.Clamp01(t);
        }

        /// <summary>The boss look; returns the boss's rank in the game's list (-1 without a boss look).</summary>
        private int MakeLook(Rival r)
        {
            if (!Plugin.DareBossLooks.Value || _looksOff || !GameApi.BossOk || !GameApi.SkinOk) { r.Drift = false; return -1; }
            try
            {
                var model = NextModel();
                var skin = model != null ? GameApi.SkinOf(r.Car) : null;
                if (model == null || skin == null) return -1;
                r.Look = PoliceCar.Build(model, "Boss");
                r.Skin = skin;
                r.Look.HideTraffic(skin);
                r.Name = $"{model.Car} ({model.Boss})";
                r.Drift = IsDriftCar(model.Car) || IsDriftCar(model.Prefab != null ? model.Prefab.name : null);
                return model.Rank;
            }
            catch (Exception e)
            {
                try { r.Look?.Destroy(); } catch { /* scene takes it */ }
                r.Look = null; r.Skin = null;
                if (++_lookFailures >= 3) { _looksOff = true; Plugin.Log.LogWarning($"[Police] daredevil boss looks switched off for this session after 3 failures: {e.Message}"); }
                else Plugin.Log.LogWarning($"[Police] daredevil boss look failed ({_lookFailures}/3): {e.Message}");
                return -1;
            }
        }

        private static bool IsDriftCar(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var part in (Plugin.DareDriftCars.Value ?? "").Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0 && name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>The boss models (loaded once the boss list is available), handed out in a shuffled cycle.</summary>
        private BossModel NextModel()
        {
            if (_models.Count == 0)
            {
                if (Time.unscaledTime < _nextModelLoad) return null;
                _nextModelLoad = Time.unscaledTime + 10f;
                GameApi.LoadBossModels(_models, w => Plugin.Log.LogWarning($"[Police] {w}"));
                if (_models.Count == 0) return null;
                var names = new List<string>();
                foreach (var m in _models) names.Add($"{m.Car} ({m.Boss}, skill {SkillFor(m.Rank):0.00}, {(IsDriftCar(m.Car) ? "drift" : "grip")})");
                Plugin.Log.LogInfo($"[Police] daredevils: {_models.Count} boss cars: {string.Join(", ", names)}");
                for (int i = _models.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); (_models[i], _models[j]) = (_models[j], _models[i]); }
            }
            // skip a model another rival is already wearing when there is a choice
            for (int tries = 0; tries < _models.Count; tries++)
            {
                var m = _models[_cursor++ % _models.Count];
                if (m.Prefab == null) { _models.Clear(); return null; }   // unloaded: reload next time
                bool used = false;
                foreach (var r in _rivals) if (r.Look != null && r.Name.StartsWith(m.Car + " (", StringComparison.Ordinal)) { used = true; break; }
                if (!used || tries == _models.Count - 1) return m;
            }
            return null;
        }

        // ------------------------------------------------------------------ the racing line and pace

        private void ReadLine()
        {
            var d = AppDomain.CurrentDomain.GetData("rogue.racingline") as object[];
            if (ReferenceEquals(d, _lineData)) return;
            _lineData = d;
            _e = null; _k = null;
            if (d == null) { if (_lineLogged) Plugin.Log.LogInfo("[Police] daredevils: racing line gone"); _lineLogged = false; return; }
            try
            {
                // object[] { int version, long pathPtr, float step, int n, float limit, float[] e, float[] curvature }
                _step = (float)d[2]; _n = (int)d[3]; _limit = (float)d[4];
                var e = (float[])d[5]; var k = (float[])d[6];
                if (_n < 3 || e.Length < _n || k.Length < _n || !(_step > 0f)) throw new InvalidOperationException("bad sizes");
                _e = e; _k = k;
                Plugin.Log.LogInfo($"[Police] daredevils: racing line v{d[0]} ({_n * _step / 1000f:0.0} km, every {_step:0.#} m, ±{_limit:0.0} m)");
                _lineLogged = true;
            }
            catch (Exception ex)
            {
                _e = null;   // _lineData keeps the bad object: it isn't parsed (and reported) again every tick
                Plugin.Log.LogWarning($"[Police] daredevils: racing line data not understood ({ex.Message}); daredevils keep the game's driving");
            }
        }

        /// <summary>
        /// A rival's top speed and cornering from your car x its skill; its speed profile is rebuilt when the line changes
        /// or its cornering moved more than 4% (at most once a second).
        /// </summary>
        private void UpdatePace(Rival r)
        {
            float factor = Mathf.Clamp(Plugin.DareSpeedFactor.Value, 0.5f, 1.5f);
            float youTop = float.IsNaN(_youTop) ? FallbackTop(r) : _youTop;
            r.Top = youTop * r.Skill * factor;
            r.Corner = Mathf.Clamp(_youGrip * r.Skill * r.Skill * (r.Drift ? Mathf.Clamp(Plugin.DareDriftCornerFactor.Value, 0.6f, 1.2f) : 1f), GripMin * 0.8f, GripMax * 1.3f);
            if (_e == null) { r.Profile = null; return; }
            float now = Time.unscaledTime;
            bool stale = r.Profile == null || !ReferenceEquals(r.ProfileLine, _lineData) || Mathf.Abs(r.Corner - r.ProfileCorner) > 0.04f * r.ProfileCorner;
            if (!stale || now < r.NextProfile && r.Profile != null && ReferenceEquals(r.ProfileLine, _lineData)) return;
            r.Profile = Profile(_k, _n, _step, r.Corner, BaseBraking * r.Skill, BaseAccel * r.Skill, r.Profile);
            r.ProfileLine = _lineData; r.ProfileCorner = r.Corner;
            r.NextProfile = now + 1f + 0.5f * (float)_rng.NextDouble();   // staggered: rivals don't all rebuild in one frame
        }

        /// <summary>Before your top speed is known: the car's own game top speed (SetVehicle's value).</summary>
        private static float FallbackTop(Rival r)
        {
            float top = GameApi.ReadOriginalMaxSpeed(r.Pf);
            return top > 1f ? top : (r.SavedMax > 1f ? r.SavedMax : 45f);
        }

        /// <summary>
        /// Corner-limited speed along the line (same two-pass method as RacingLine's SpeedProfile), uncapped at the top;
        /// rebuilt in place when the previous array has the right size.
        /// </summary>
        private static float[] Profile(float[] k, int n, float step, float corner, float braking, float accel, float[] reuse)
        {
            var v = reuse != null && reuse.Length == n ? reuse : new float[n];
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.Abs(k[i]);
                v[i] = a < 1e-5f ? ProfileTop : Mathf.Min(ProfileTop, Mathf.Sqrt(corner / a));
            }
            for (int i = n - 2; i >= 0; i--) v[i] = Mathf.Min(v[i], Mathf.Sqrt(v[i + 1] * v[i + 1] + 2f * braking * step));
            for (int i = 1; i < n; i++) v[i] = Mathf.Min(v[i], Mathf.Sqrt(v[i - 1] * v[i - 1] + 2f * accel * step));
            return v;
        }

        /// <summary>After each read of the player: time stamp, smoothed sideways and road speed, top speed.</summary>
        private void NotePlayer(float now)
        {
            float dtp = now - _playerTime;
            if (dtp <= 1e-4f && !float.IsNaN(_lastPlayerLane)) return;   // read twice in one frame (tick + drive): nothing new
            if (dtp < 0.5f && !float.IsNaN(_lastPlayerLane) && !float.IsNaN(_player.Lane))
            {
                float a = 1f - Mathf.Exp(-dtp * 8f);
                _playerLaneVel += ((_player.Lane - _lastPlayerLane) / dtp - _playerLaneVel) * a;
                float rv = (_player.Distance - _lastPlayerDist) / dtp;   // your real progress along the road (less when sliding)
                if (!float.IsNaN(rv) && rv > -5f && rv < 150f) _playerRoadVel = float.IsNaN(_playerRoadVel) ? rv : _playerRoadVel + (rv - _playerRoadVel) * a;
            }
            else { _playerLaneVel = 0f; _playerRoadVel = float.NaN; }    // a real gap (pause, loading): start over
            float top = !float.IsNaN(_player.MaxNow) && _player.MaxNow > 1f ? _player.MaxNow : _player.TopSpeed;
            if (top > 1f && !float.IsNaN(top))
                _youTop = float.IsNaN(_youTop) ? top : _youTop + (top - _youTop) * (1f - Mathf.Exp(-Mathf.Clamp(dtp, 0f, 0.5f) / 3f));
            _lastPlayerLane = _player.Lane;
            _lastPlayerDist = _player.Distance;
            _playerTime = now;
        }

        /// <summary>Your speed along the road for the daredevils' braking: the lower of the speedometer and real progress.</summary>
        private float YouRoadSpeed => float.IsNaN(_playerRoadVel) ? _player.Speed : Mathf.Min(_player.Speed, Mathf.Max(0f, _playerRoadVel));

        /// <summary>
        /// Learns your cornering: the 90th percentile of v^2 x |line curvature| at your spot (in corners tighter than a
        /// 400 m radius, above 12 m/s). Each frame nudges the estimate up by 0.9 x rate or down by 0.1 x rate.
        /// </summary>
        private void LearnGrip(float dt)
        {
            if (_e == null || float.IsNaN(_playerRoadVel)) return;
            float s = _player.Distance;
            if (!(s >= 0f) || s > (_n - 1) * _step) return;
            float ks = Mathf.Abs(Sample(_k, s, _step, _n)), v = YouRoadSpeed;
            if (ks < 1f / 400f || v < 12f) return;
            float a = v * v * ks, stepUp = GripRate * dt;
            _youGrip = Mathf.Clamp(_youGrip + (a > _youGrip ? 0.9f * stepUp : -0.1f * stepUp), GripMin, GripMax);
        }

        /// <summary>Distance from x to the range [lo, hi] (0 inside).</summary>
        private static float Dist(float x, float lo, float hi) => x < lo ? lo - x : x > hi ? x - hi : 0f;

        private static float Sample(float[] a, float s, float step, int n)
        {
            float x = s / step;
            if (!(x > 0f)) return a[0];
            int i = (int)x;
            if (i >= n - 1) return a[n - 1];
            float t = x - i;
            return a[i] + (a[i + 1] - a[i]) * t;
        }

        // ------------------------------------------------------------------ per frame: drive and draw

        private void Drive()
        {
            float now = Time.time, dt = Time.deltaTime;
            bool paused = dt <= 0f || Time.timeScale <= 0f;   // nothing is written to the game while paused
            bool line = _e != null && Plugin.DareRaceLine.Value;
            if (line && !paused)
            {
                // you, every frame (passing you is what they do most); traffic every 0.1 s, covering every rival
                if (GameApi.ReadPlayer(ref _player)) NotePlayer(now);
                LearnGrip(dt);
                if (now >= _nextSnap)
                {
                    _nextSnap = now + SnapSeconds;
                    float lo = -SnapBehind, hi = SnapAhead;
                    foreach (var r in _rivals)
                    {
                        if (r.Far || r.S.WasHit) continue;
                        float rel = r.S.Road - _player.Distance;
                        lo = Mathf.Min(lo, rel - SnapMargin); hi = Mathf.Max(hi, rel + SnapMargin);
                    }
                    _roadN = GameApi.ReadRoad(_player.Distance, -lo, hi, _road);
                    _snapTime = now;
                }
            }
            for (int i = 0; i < _rivals.Count; i++)
            {
                var r = _rivals[i];
                if (r.T == null) continue;
                if (!GameApi.ReadCar(r.Car, r.Pf, ref r.S) || !r.S.Active) continue;   // the tick lets it go
                float moved = r.S.Travelled - r.LastTravelled;
                if (moved < -5f || moved > 80f) continue;                                // reused by the pool: the tick lets it go
                float ahead = r.S.Road - _player.Distance;
                r.Far = r.Far ? ahead > SteerAhead : ahead > SteerAheadOff;
                bool steer = line && !r.S.WasHit && !r.Far && r.S.Road >= 0f && r.S.Road < (_n - 2) * _step;
                if (paused) { if (r.Look != null) r.Look.Place(r.T, r.BoxC, r.BoxS, 0f, 0f, r.Yaw, r.Steer); continue; }
                if (steer) { UpdatePace(r); Steer(r, now, dt); Report(r, dt); }
                else
                {
                    if (r.Steering) StopSteering(r, true);
                    r.Slip = 0f;
                    r.Yaw *= Mathf.Exp(-dt * 4f);   // a slide eases out, it doesn't snap
                    r.Steer *= Mathf.Exp(-dt * 6f);
                }
                if (r.Look != null) r.Look.Place(r.T, r.BoxC, r.BoxS, r.S.Speed, dt, r.Yaw, r.Steer);
            }
        }

        private void Steer(Rival r, float now, float dt)
        {
            if (!r.Steering)
            {
                r.Steering = true;
                r.Offset = r.S.Lane;
                GameApi.WriteChaseExtras(r.Pf, Mathf.Min(Smoothness, r.SavedSmooth), float.NaN);
            }
            float s = r.S.Road, v = Mathf.Max(0f, r.S.Speed);
            float rel = s - _player.Distance;   // + = ahead of you
            float cap = float.PositiveInfinity;

            // the line, with a weaker driver's slow wander off it
            float target = Sample(_e, s, _step, _n);
            float wanderAmp = 0.9f * Mathf.Clamp01((1.05f - r.Skill) / 0.15f);
            if (wanderAmp > 0f)
            {
                if (now >= r.NextWander) { r.NextWander = now + 1.5f + 1.5f * (float)_rng.NextDouble(); r.WanderTarget = (float)_rng.NextDouble() * 2f - 1f; }
                r.Wander += (r.WanderTarget - r.Wander) * (1f - Mathf.Exp(-dt * 0.6f));
                target += r.Wander * wanderAmp;
            }

            // defending: you 25-60 m behind and closing, a corner within 150 m: one move to cover its inside, held through it
            float youV = YouRoadSpeed;
            if (Plugin.DareDefend.Value)
            {
                if (!float.IsNaN(r.DefendCorner) && s > r.DefendCorner + 20f) r.DefendCorner = float.NaN;   // corner done
                float behindYou = -rel;   // < 0: you are behind us
                if (float.IsNaN(r.DefendCorner) && behindYou < -25f && behindYou > -60f && youV - v > 0.5f)
                {
                    for (float d = 30f; d <= 150f; d += 15f)
                    {
                        float kc = Sample(_k, s + d, _step, _n);
                        if (Mathf.Abs(kc) > 1f / 200f) { r.DefendCorner = s + d; r.DefendSide = Mathf.Sign(kc); break; }
                    }
                }
                if (!float.IsNaN(r.DefendCorner)) target = r.DefendSide * Mathf.Max(Mathf.Abs(target), 0.6f * _limit);
            }

            // Skilled drivers who don't want damage. For every car ahead (traffic and you):
            // - braking envelope: a car in line with us (any part of where it is, or for you where you're heading in the
            //   next 0.6 s, within both widths + margin) caps our speed so we can always slow to its speed before a safe gap
            //   (a distance + a time gap), allowing for the game reaching a new speed about Smoothness s late on the closing
            //   speed; inside that gap our running speed is cut at once;
            // - detour: a car on the line (or on us) is passed on the inside of the coming corner, or on a straight the side
            //   nearer to us, blended in over a lead-in that grows with the closing speed; with no way past, the envelope
            //   queues us. You get a wider berth, and a pass of you only swings back once we're well clear ahead of you;
            // - slipstream: lined up close behind a car, a little more top speed.
            float myHalfW = Mathf.Clamp(r.BoxS.x * 0.5f, 0.8f, 1.3f), myHalfL = Mathf.Clamp(r.BoxS.z * 0.5f, 1.8f, 3.2f);
            float bestW = 0f, bestPass = target, instant = float.PositiveInfinity;
            float sinceSnap = now - _snapTime;
            bool tow = false;
            bool youGuard = false; float youLo = 0f, youHi = 0f;   // the band beside you we must not enter (while alongside / close)
            for (int j = -1; j < _roadN; j++)                      // you first: on a tie your detour wins
            {
                RoadCar o;
                bool you = j < 0;
                float laneLo, laneHi;
                if (!you)
                {
                    o = _road[j];
                    if (o.Ptr == r.Ptr) continue;
                    o.Road += o.Speed * sinceSnap;
                    laneLo = laneHi = o.Lane;
                }
                else
                {
                    o = new RoadCar { Road = _player.Distance + youV * (now - _playerTime), Lane = _player.Lane, HalfWidth = YouHalfWidth, HalfLength = YouHalfLength, Speed = youV };
                    float soon = o.Lane + Mathf.Clamp(_playerLaneVel, -8f, 8f) * YouPredict;   // where you're heading
                    laneLo = Mathf.Min(o.Lane, soon); laneHi = Mathf.Max(o.Lane, soon);
                }
                float gap = (o.Road - o.HalfLength) - (s + myHalfL);
                float past = 2f * o.HalfLength + 2f * myHalfL + (you ? YouClearAhead + YouClearSeconds * Mathf.Max(0f, o.Speed - v) : 0f);
                if (gap < -past) continue;                                            // behind us (well clear, for you)
                float clear = o.HalfWidth + myHalfW + (you ? YouMargin : GapMargin);
                float closing = v - o.Speed;
                bool inLine = Dist(r.Offset, laneLo, laneHi) < clear;
                if (you && gap < 6f) { youGuard = true; youLo = laneLo - clear; youHi = laneHi + clear; }   // alongside or about to be
                if (inLine && gap > -(o.HalfLength + myHalfL))
                {
                    float slowTo = Mathf.Max(0f, o.Speed);
                    float safe = you ? YouSafeGap + YouSafeTime * v : SafeGap + SafeTime * v;
                    float room = gap - safe - Mathf.Max(0f, closing) * Smoothness;
                    cap = Mathf.Min(cap, room > 0f ? Mathf.Sqrt(slowTo * slowTo + 2f * BaseBraking * room) : Mathf.Max(0f, slowTo - 1f));
                    if (gap < safe && closing > 0f) instant = Mathf.Min(instant, Mathf.Max(0f, slowTo - (gap < safe * 0.5f ? 2f : 0.5f)));
                }
                if (gap > 0f && gap < TowGap && o.Speed > 15f && Dist(r.Offset, laneLo, laneHi) < TowLane) tow = true;
                float lead = Mathf.Clamp(closing * 2.5f, you ? 25f : 15f, 120f);
                if (gap > lead) continue;                                             // too far ahead to plan round yet
                if (gap > 0f && closing < 0.5f) continue;                             // not catching it
                if (Dist(Sample(_e, o.Road, _step, _n), laneLo, laneHi) >= clear && !inLine) continue;   // the line (and we) go by it
                float left = laneLo - clear, right = laneHi + clear;
                bool okL = left >= -_limit, okR = right <= _limit;
                float pass;
                if (okL && okR)
                {
                    float kc = Sample(_k, o.Road + 25f, _step, _n);   // a corner coming up: take its inside (+ curvature = right turn)
                    if (Mathf.Abs(kc) > 1f / 250f) pass = kc > 0f ? right : left;
                    else pass = Mathf.Abs(left - r.Offset) <= Mathf.Abs(right - r.Offset) ? left : right;
                }
                else if (okL) pass = left;
                else if (okR) pass = right;
                else continue;                                                        // no way past: the envelope queues us
                float w = gap <= 4f ? 1f : 0.5f * (1f + Mathf.Cos(Mathf.PI * Mathf.Clamp01((gap - 4f) / Mathf.Max(1f, lead - 4f))));
                if (w > bestW) { bestW = w; bestPass = pass; }
            }
            if (bestW > 0f) target = Mathf.Lerp(target, bestPass, bestW);
            target = Mathf.Clamp(target, -_limit, _limit);
            // hard rule: never across you. Whatever the line, a defence or a traffic detour wants, it stays on its side of
            // you; if it is in your band already (you moved over), it gets out on its side, faster. Traffic it then can't
            // pass, it queues behind (braking envelope above).
            bool evading = false;
            if (youGuard)
            {
                float mid = (youLo + youHi) * 0.5f;
                if (r.Offset <= mid) { if (target > youLo) target = youLo; evading = r.Offset > youLo; }
                else { if (target < youHi) target = youHi; evading = r.Offset < youHi; }
                target = Mathf.Clamp(target, -_limit - 0.5f, _limit + 0.5f);   // an escape may use the road's last half metre
            }

            float rate = (r.Drift ? 5.5f : 4.5f) * (evading ? 1.6f : 1f);
            float before = r.Offset;
            r.Offset = Mathf.MoveTowards(r.Offset, target, rate * dt);

            // look: grip = pointing where it goes; drift = sliding into the corner; front wheels steer with the corner
            float sideways = dt > 0f ? (r.Offset - before) / dt : 0f;
            float heading = v > 1f ? Mathf.Atan2(sideways, v) * Mathf.Rad2Deg : 0f;
            float k = Sample(_k, s, _step, _n), load = v * v * k;   // signed cornering load, + = turning right
            float maxSlip = Mathf.Clamp(Plugin.DareMaxSlip.Value, 0f, 50f);
            float want = r.Drift ? Mathf.Sign(load) * maxSlip * Mathf.Clamp01((Mathf.Abs(load) - 2.5f) / 5.5f)
                                 : Mathf.Sign(load) * 2.5f * Mathf.Clamp01(Mathf.Abs(load) / 10f);
            r.Slip += (want - r.Slip) * (1f - Mathf.Exp(-dt * (r.Drift ? 3f : 6f)));
            r.Yaw = heading + r.Slip;
            float steerWant = Mathf.Atan(Wheelbase * k) * Mathf.Rad2Deg + 0.5f * heading - (r.Drift ? 0.85f * r.Slip : 0f);   // drift: counter-steer
            r.Steer += (Mathf.Clamp(steerWant, -32f, 32f) - r.Steer) * (1f - Mathf.Exp(-dt * 8f));

            // speed: the rival's own profile (its skill on your car's numbers), push / conserve, slipstream
            float pushWant = rel < -PushBehind ? PushFactor : rel > EaseAhead ? EaseFactor : 1f;
            r.Push += (pushWant - r.Push) * (1f - Mathf.Exp(-dt * 0.5f));
            float top = r.Top * r.Push * (tow && Plugin.DareSlipstream.Value ? TowBoost : 1f);
            float vt = r.Profile != null ? Mathf.Min(top, Sample(r.Profile, s + v * LeadSeconds, _step, _n) * r.Push) : top;
            GameApi.Steer(r.Pf, r.Lane, r.Offset, Mathf.Min(vt, cap), _roadN < _road.Length);   // a full snapshot: keep the game's braking too
            if (instant < v) GameApi.ClampSpeed(r.Pf, instant);   // too close: slow now, not after the game's smoothing
        }

        /// <summary>The race report: time near you, overtakes both ways, closest gap, average speeds (logged when let go).</summary>
        private void Report(Rival r, float dt)
        {
            float rel = r.S.Road - _player.Distance;
            float myHalfL = Mathf.Clamp(r.BoxS.z * 0.5f, 1.8f, 3.2f), myHalfW = Mathf.Clamp(r.BoxS.x * 0.5f, 0.8f, 1.3f);
            if (Mathf.Abs(rel) < 100f) r.NearTime += dt;
            if (Mathf.Abs(rel) < 200f) { r.SpeedSum += Mathf.Max(0f, r.S.Speed) * dt; r.YouSum += YouRoadSpeed * dt; r.SpeedTime += dt; }
            int sign = rel > 3f ? 1 : rel < -3f ? -1 : r.RelSign;
            if (r.RelSign < 0 && sign > 0) r.Passes++;        // it got past you
            else if (r.RelSign > 0 && sign < 0) r.Passed++;   // you got past it
            r.RelSign = sign;
            float gap = Mathf.Max(Mathf.Abs(rel) - (myHalfL + YouHalfLength), Mathf.Abs(r.Offset - _player.Lane) - (myHalfW + YouHalfWidth));
            if (gap < r.Closest) r.Closest = gap;
        }

        /// <summary>
        /// Gives the game its car back: top speed, speed smoothing, rubber banding, home lane; with backToLane (a car still
        /// driving, not crashed) the game's own lane change takes it from our offset into its lane.
        /// </summary>
        private void StopSteering(Rival r, bool backToLane)
        {
            r.Steering = false;
            try
            {
                if (r.Pf != null && r.Saved) { GameApi.WriteChase(r.Pf, r.SavedRubber, r.SavedMax); GameApi.WriteChaseExtras(r.Pf, r.SavedSmooth, float.NaN); }
                if (r.Lane != null && r.Saved && backToLane) GameApi.HandBack(r.Lane, r.HomeLane);
            }
            catch { /* destroyed with the scene */ }
        }

        // ------------------------------------------------------------------ letting go

        private void Release(Rival r, string reason, bool reused = false)
        {
            // a pooled car keeps its serialized fields (speedSmoothness, despawn distances) into its next life: always given
            // back; MaxSpeed and the lane only while it still drives (SetVehicle writes fresh ones when the pool reuses it)
            if (r.Steering && r.S.Active && !reused) StopSteering(r, !r.S.WasHit);
            r.Steering = false;
            if (r.Saved)
            {
                try
                {
                    if (r.Pf != null)
                    {
                        GameApi.WriteChaseExtras(r.Pf, r.SavedSmooth, float.NaN);
                        GameApi.WriteDespawn(r.Pf, r.SavedBehind, r.SavedAhead);
                    }
                }
                catch { /* destroyed with the scene */ }
            }
            if (r.Look != null) { try { r.Look.Destroy(); } catch { /* scene */ } r.Look = null; r.Skin = null; }
            _rivals.Remove(r);
            _ptrs.Remove(r.Ptr);
            Owned.Remove(r.Ptr);
            if (Plugin.LogEvents.Value)
            {
                string report = r.SpeedTime > 1f
                    ? $"; skill {r.Skill:0.00}, {r.NearTime:0} s within 100 m of you, passed you {r.Passes}x, you passed it {r.Passed}x, " +
                      $"closest {(float.IsInfinity(r.Closest) ? "-" : r.Closest.ToString("0.0") + " m")}, its average {r.SpeedSum / r.SpeedTime * 3.6f:0} km/h vs your {r.YouSum / r.SpeedTime * 3.6f:0}"
                    : $"; skill {r.Skill:0.00}";
                Plugin.Log.LogInfo($"[Police] daredevil {r.Name} let go: {reason} ({_rivals.Count} driving){report}");
            }
        }

        private void ReleaseAll(string reason)
        {
            for (int i = _rivals.Count - 1; i >= 0; i--)
            {
                try { Release(_rivals[i], reason); }
                catch (Exception e)
                {
                    var r = _rivals[i];
                    try { r.Look?.Destroy(); } catch { /* scene */ }
                    _rivals.RemoveAt(i); _ptrs.Remove(r.Ptr); Owned.Remove(r.Ptr);
                    Plugin.Log.LogWarning($"[Police] daredevil cleanup: {e.Message}");
                }
            }
            _rivals.Clear(); _ptrs.Clear(); Owned.Clear();
        }

        // ------------------------------------------------------------------ breakers

        private void Guard(ref bool off, string feature, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                off = true;
                // either half failing switches both off: a look that is no longer placed (or a car no longer driven) must not stay
                _tickOff = true; _driveOff = true;
                Plugin.Log.LogWarning($"[Police] daredevils switched off for this session after an error in {feature}: {e.Message}");
                ReleaseAll(feature + " off");
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
                ReleaseAll("daredevils switched off");
                Plugin.Log.LogError($"[Police] daredevils switched off for this session after repeated errors; cars restored, our objects removed. Last error: {e}");
            }
            else Plugin.Log.LogWarning($"[Police] daredevils error ({_errors.Count}/5 in 10 s): {e.Message}");
        }
    }
}
