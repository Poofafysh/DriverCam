using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RacingLine
{
    /// <summary>
    /// Builds the racing line for each run, previews it, scores corners and feeds the Racing Line score category.
    ///
    /// - Every 0.5 s: has the run's path changed (new object) or grown (same object, longer)? Either one logs and rebuilds.
    /// - Each frame while building: LineBuilder.Step within Line.FrameBudgetMs. When the line is ready: find its corners.
    /// - Each frame with a line: read the player, step the LineScorer; a finished corner pays out through the native
    ///   category (single-player) or is shown as display-only (multiplayer / fallback).
    /// - Every 0.1 s (game time) with a line: snapshot the NPC traffic near the player and rebuild the traffic-aware line
    ///   (TrafficLine), which the scorer and the preview use instead of the plain line.
    /// - Each frame: the results screen gets its Racing Line row while it is open.
    /// - OnGUI: the line preview (dots) and a status/readout line.
    /// - Circuit breakers (Safety rule 6): scoring, the traffic line, the native category and the results row each switch themselves off
    ///   after an error; 5 errors in 10 s anywhere switches the whole plugin off for the session. The game keeps running.
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private LineBuilder _builder;
        private Line _line;
        private IntPtr _watchPtr, _failedPtr;
        private float _failedLength;
        private float _nextWatch;
        private bool _waitingLogged;
        private int _nearest = -1;
        private float _nextFullScan;

        private LineScorer _scorer;
        private List<Corner> _corners;
        private float[] _lineCurvature;
        private PlayerState _player;
        private int _lastHits = -1, _lastNearMisses = -1;
        private float _heading = float.NaN, _carCurvature, _throttle, _brake;   // smoothed over ~0.2 s
        private SpeedProfile.GripEstimate _grip;
        private float _profileTop, _profileGrip, _nextProfile;
        private string _lastResult = "";
        private float _lastResultUntil;
        private float _nextNativeTry;
        private bool _nativeLogged, _iconsLoaded;
        private bool _scoringOff, _nativeOff, _resultsOff, _victoryOff, _trafficOff;   // per-feature breakers
        private Func<GameApi.ResultsData> _resultsData;
        private Func<GameApi.VictoryData> _victoryData;
        private Action<string> _log;
        // created once (LoadIcons) so Update/Score don't allocate a delegate or closure every frame
        private Action _resultsTick, _victoryTick, _score, _ensureNative, _liveStep, _trafficTick;
        // traffic-aware line: one object for the session (preallocated buffers), re-attached to each new line
        private readonly TrafficLine _traffic = new TrafficLine();
        private readonly TrafficSettings _trafficCfg = new TrafficSettings();
        private float _nextTraffic;
        private bool _playerValid;            // _player was read this frame
        private bool _trafficSeenLogged;      // this race's first traffic snapshot was logged
        private LineScorer _trafficLoggedFor; // the race whose traffic summary was logged
        // run total for the Victory screen: each race's Racing Line score is added once (session only)
        private double _runTotal;
        private LineScorer _countedScorer;
        private StepResult _pendingStep;      // read by _liveStep
        private bool _liveOpen;               // our live action is running in the game's score system
        private (LineScorer, int) _activatedCorner = (null, -1);
        private int _liveActions;             // live actions begun this race (log)
        private LineScorer _liveLoggedFor;
        private int _carChanges = -1;         // GameApi.CarChanges when the grip estimate was started

        // ground-projected line + HUD card (created on first use; null/!Ok = fall back to the IMGUI dots / text)
        private GroundLine _ground;
        private LineHud _hud;
        private bool _visualsTried, _visualsOff;
        private string _hudGrade;
        private bool _hudGrip;
        private float _hudGradeUntil;
        private Camera _cam;
        private float _nextCamFetch;

        private readonly Queue<float> _errors = new Queue<float>();
        private bool _broken;
        private string _status = "starting";

        private static readonly Color LineNear = new Color(0.25f, 1f, 0.45f, 0.95f);
        private static readonly Color LineEdge = new Color(1f, 0.85f, 0.2f, 0.95f);
        private static readonly Color EdgeDot = new Color(1f, 1f, 1f, 0.35f);
        private static readonly Color LineDetour = new Color(1f, 0.55f, 0.1f, 0.95f);   // routed around a traffic car
        private static readonly Color LineBlocked = new Color(1f, 0.2f, 0.2f, 0.4f);    // no way past: counts as perfect position

        private void Update()
        {
            // our results row is only ever removed once the results screen has closed (removing it mid-animation would
            // leave the player without a Continue button), and that cleanup keeps running when we're off or broken
            if (_broken || !Plugin.Enabled.Value) { CloseLive(true); CleanupRowQuietly(); CleanupVictoryQuietly(); LineShare.Sync(null, null); return; }
            using var perf = RogueShared.Perf.Scope("RacingLine.Update");   // shared timing overlay (TrafficDensity [Perf]); free when off
            try
            {
                if (!_iconsLoaded) LoadIcons();
                var kb = Keyboard.current;
                if (kb != null && kb.f5Key.wasPressedThisFrame) Plugin.ShowLine.Value = !Plugin.ShowLine.Value;

                if (GameApi.ResultsOk && !_resultsOff) Guard(ref _resultsOff, "results row", _resultsTick);
                else CleanupRowQuietly();
                if (GameApi.VictoryOk && !_victoryOff) Guard(ref _victoryOff, "victory row", _victoryTick);
                else CleanupVictoryQuietly();

                if (!GameApi.PathOk) { _status = "game check failed (see log)"; return; }
                if (Time.unscaledTime >= _nextWatch) { _nextWatch = Time.unscaledTime + 0.5f; Watch(); }
                StepBuilder();
                LineShare.Sync(_line, _lineCurvature);   // other plugins (Police daredevils) race the same line

                bool traffic = _line != null && GameApi.TrafficOk && Plugin.TrafficEnabled.Value && !_trafficOff;
                if (traffic) _traffic.SetTime(Time.time);   // detours move on with their cars between snapshots
                else _traffic.Clear();                       // plain line everywhere

                _playerValid = false;
                if (_line != null && _scorer != null && GameApi.PlayerOk && !_scoringOff) Guard(ref _scoringOff, "scoring", _score);
                if (traffic && _playerValid && Time.time >= _nextTraffic)
                {
                    _nextTraffic = Time.time + 0.1f;   // game time: no snapshots while paused
                    // a multiplayer client's traffic copies are driven by the host: their path data is unverified, so the
                    // plain line (multiplayer is display mode anyway)
                    if (Net.Role() == NetRole.Client) _traffic.Clear();
                    else Guard(ref _trafficOff, "traffic line", _trafficTick);
                }
                if (_line != null && GameApi.ScoreOk && !_nativeOff && Plugin.NativeScoring.Value && Time.unscaledTime >= _nextNativeTry)
                {
                    _nextNativeTry = Time.unscaledTime + 2f;
                    Guard(ref _nativeOff, "native category", _ensureNative);
                }
            }
            catch (Exception e) { Fault(e); }
        }

        private void LoadIcons()
        {
            _iconsLoaded = true;
            _resultsData = ResultsData;
            _log = s => Plugin.Log.LogInfo(s);
            _resultsTick = () => GameApi.ResultsTick(_resultsData, _log);
            _victoryData = VictoryData;
            _victoryTick = () => GameApi.VictoryTick(_victoryData, _log);
            _score = Score;
            _trafficTick = TrafficTick;
            _ensureNative = EnsureNative;
            _liveStep = LiveStep;
            string folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "RacingLine");
            Icons.Load(folder);
            Plugin.Log.LogInfo($"[RacingLine] icons: {Icons.Source} ({folder})");
        }

        private void StepBuilder()
        {
            if (_builder == null || !_builder.Step(Mathf.Clamp(Plugin.FrameBudgetMs.Value, 0.5f, 10f))) return;
            if (_builder.Result != null)
            {
                var previous = _line;
                _line = _builder.Result;
                _nearest = -1;
                // the first race of a new run (stage 0, race 0) starts the Victory screen's run total from zero
                if (previous == null && GameApi.WidthOk && GameApi.RunPosition(out int stage, out int race) && stage == 0 && race == 0 && _runTotal > 0)
                {
                    Plugin.Log.LogInfo($"[RacingLine] new run: run total reset (previous run {_runTotal:0})");
                    _runTotal = 0; _countedScorer = null;
                }
                // corners come from the road's centre line; quality and the speed profile use the racing line's own curvature
                var centre = Corners.Curvature(_line.Px, _line.Pz, _line.N, _line.Step);
                _corners = Corners.Find(centre, _line.N, _line.Step, 1f / Mathf.Max(50f, Plugin.CornerMinRadius.Value));
                Corners.AssignZones(_corners, _line.N, _line.Step);
                var qx = new float[_line.N]; var qz = new float[_line.N];
                for (int i = 0; i < _line.N; i++) { qx[i] = _line.Px[i] + _line.Nx[i] * _line.E[i]; qz[i] = _line.Pz[i] + _line.Nz[i] * _line.E[i]; }
                _lineCurvature = Corners.Curvature(qx, qz, _line.N, _line.Step);
                // same race, longer path: totals carry over; a new race starts from zero
                bool sameRace = previous != null && previous.PathPtr == _line.PathPtr;
                _scorer = new LineScorer(_line, _corners, _lineCurvature, Settings(), sameRace ? _scorer : null);
                if (!sameRace) { _traffic.ResetRace(); _trafficSeenLogged = false; }
                _traffic.Attach(_line);
                _scorer.Traffic = _traffic;
                _nextTraffic = 0f;
                _profileTop = 0f; _nextProfile = 0f;   // the speed profile is (re)built once the car's top speed is known
                if (GameApi.NativeActive) GameApi.SetCoinTarget(_corners.Count * Plugin.CoinTargetPerCorner.Value);
                int a = 0, c = 0; foreach (var k in _corners) { if (k.Type == 'A') a++; else if (k.Type == 'C') c++; }
                _status = $"line ready, {_line.Length / 1000f:0.0} km, {_corners.Count} corners";
                Plugin.Log.LogInfo($"[RacingLine] line built: {_builder.Summary()}; {_corners.Count} corners ({a} onto straights, {c} linked)");
            }
            else
            {
                _failedPtr = _builder.PathPtr; _failedLength = _builder.PathLength;   // retried only when the path changes or grows
                _status = "build failed (see log)";
                Plugin.Log.LogWarning($"[RacingLine] line build failed: {_builder.FailReason}");
            }
            _builder = null;
        }

        private static ScoreSettings Settings() => new ScoreSettings
        {
            LineFull = Plugin.LineFull.Value, LineZero = Mathf.Max(Plugin.LineFull.Value + 0.5f, Plugin.LineZero.Value), LineFloor = Plugin.LineFloor.Value,
            WSpeed = Plugin.WSpeed.Value, WGrip = Plugin.WGrip.Value, WPedal = Plugin.WPedal.Value, Base = Plugin.Base.Value,
            LiveMinQ = Plugin.TickMinQ.Value, LiveGrace = Mathf.Clamp(Plugin.LiveGrace.Value, 0.1f, 0.8f),
            DriftFactor = Plugin.DriftFactor.Value, TrafficGrace = Plugin.TrafficGrace.Value,
            ExitWeight = Plugin.ExitWeight.Value, CleanBonus = Plugin.CleanBonus.Value, GripBonus = Plugin.GripBonus.Value,
            CoastPerSecond = Plugin.CoastPerSecond.Value, CoastFloor = Plugin.CoastFloor.Value,
            Gold = Plugin.Gold.Value, Silver = Plugin.Silver.Value, Bronze = Plugin.Bronze.Value,
            StreakStep = Plugin.StreakStep.Value, StreakMax = Mathf.Max(1f, Plugin.StreakMax.Value),
        };

        /// <summary>One frame of scoring: read the player, smooth the inputs, step the scorer, pay ticks and corner bonuses.</summary>
        private void Score()
        {
            if (!GameApi.ReadPlayer(ref _player)) { _lastHits = -1; _lastNearMisses = -1; _heading = float.NaN; CloseLive(true); return; }
            if (_player.LevelEnded) CloseLive(true);   // the game's FinishLevel already banked the temporary score
            _playerValid = true;
            float dt = Time.deltaTime;   // game time: 0 while paused, so nothing scores
            if (dt <= 0f) return;

            // the car's own path curvature from its heading change per metre, and pedals, smoothed over ~0.2 s
            float hSpeed = (float)Math.Sqrt(_player.Vx * _player.Vx + _player.Vz * _player.Vz);
            float a = dt / (0.2f + dt);
            if (hSpeed > 2f)
            {
                float heading = (float)Math.Atan2(_player.Vx, _player.Vz);
                if (!float.IsNaN(_heading))
                {
                    float dh = heading - _heading;
                    if (dh > Mathf.PI) dh -= 2f * Mathf.PI; else if (dh < -Mathf.PI) dh += 2f * Mathf.PI;
                    _carCurvature += a * (Math.Abs(dh) / Math.Max(0.05f, hSpeed * dt) - _carCurvature);
                }
                _heading = heading;
            }
            else _heading = float.NaN;
            _throttle += a * (_player.Throttle - _throttle);
            _brake += a * (_player.Brake - _brake);

            // a different player car (or a new level's car object): learn its grip from the start and rebuild the speed profile
            if (GameApi.CarChanges != _carChanges)
            {
                _carChanges = GameApi.CarChanges;
                _grip = null; _profileTop = 0f; _profileGrip = 0f; _nextProfile = 0f;
            }
            // learn this car's cornering limit (95th percentile while gripping) and keep the reference speeds current
            if (_grip == null) _grip = new SpeedProfile.GripEstimate(Plugin.GripStart.Value);
            if (_player.Grounded && !_player.Drifting && _player.Speed > 10f) _grip.Add(_player.Speed * _player.Speed * _carCurvature);
            if (_player.TopSpeed > 1f && Time.unscaledTime >= _nextProfile &&
                (Math.Abs(_player.TopSpeed - _profileTop) > 0.5f || Math.Abs(_grip.Value - _profileGrip) > 0.5f))
            {
                _nextProfile = Time.unscaledTime + 5f;
                _profileTop = _player.TopSpeed; _profileGrip = _grip.Value;
                _scorer.SetReference(SpeedProfile.Build(_lineCurvature, _line.N, _line.Step, _profileTop, _profileGrip, Plugin.BrakeDecel.Value, Plugin.AccelRate.Value));
            }

            bool hit = _player.Hits >= 0 && _lastHits >= 0 && _player.Hits > _lastHits;
            bool nearMiss = _player.NearMisses >= 0 && _lastNearMisses >= 0 && _player.NearMisses > _lastNearMisses;
            _lastHits = _player.Hits; _lastNearMisses = _player.NearMisses;
            if (hit) _traffic.NoteHit();   // a pass in progress isn't clean any more

            var r = _scorer.Step(new ScoreInput
            {
                Active = _player.InControl && _player.Grounded,
                Distance = _player.Distance, Offset = _player.Offset, Speed = _player.Speed, Dt = dt,
                Throttle = _throttle, Brake = _brake, CarCurvature = _carCurvature, Grip = _grip.Value,
                Drifting = _player.Drifting, Hit = hit, NearMiss = nearMiss,
            });

            bool native = GameApi.NativeActive && !_nativeOff;
            if (native) { _pendingStep = r; Guard(ref _nativeOff, "native category", _liveStep); _pendingStep = default; }
            else CloseLive(true);   // native scoring went away mid-action: never leave the game's action open
            var c = r.Corner;
            if (c == null) return;
            _lastResult = $"corner {c.Index + 1}{c.Type}: {c.Label} +{c.Total:0}{(native ? "" : " (display only)")}";
            _lastResultUntil = Time.unscaledTime + 4f;
            _hudGrade = c.Grade; _hudGrip = c.Grip; _hudGradeUntil = Time.unscaledTime + 4f;
            if (Plugin.LogCorners.Value)
                Plugin.Log.LogInfo($"[RacingLine] corner {c.Index + 1}{c.Type}: {c.Grade ?? "-"}{(c.Grip ? " grip" : " drifted")}{(c.Clean ? "" : " hit")}{(c.TrafficShifted ? " traffic" : "")} q {c.MeanQ:0.00} " +
                                   $"exit {c.Exit:0.00} full-throttle {(float.IsNaN(c.SecondsToFullThrottle) ? "never" : c.SecondsToFullThrottle.ToString("0.0") + " s")} " +
                                   $"coast {c.CoastAfterApex:0.0} s -> {c.Total:0} pts (all live), units {c.Units:0.0}, streak x{_scorer.StreakMultiplier:0.00}, pace {_scorer.Pace:0.00}");
        }

        /// <summary>
        /// Feeds one frame into the game's live action: begin when points start flowing, add them every frame, end it
        /// (into the combo) when the corner zone ends or the line is lost for LiveGrace, cancel it on a crash.
        /// If the game ended it itself (combo failed), the next points start a new one.
        /// </summary>
        private void LiveStep()
        {
            var r = _pendingStep;
            if (_liveOpen && !GameApi.LiveRunning()) _liveOpen = false;
            if (r.Lost && _liveOpen) { GameApi.EndLive(false); _liveOpen = false; }
            if (r.Live > 0 && !_player.LevelEnded)
            {
                if (!_liveOpen)
                {
                    // the game's activation (counters, activation bonus) once per corner, not again after a LiveGrace gap
                    var corner = (_scorer, _scorer?.CornersDone ?? -1);
                    bool activate = !corner.Equals(_activatedCorner);
                    _activatedCorner = corner;
                    GameApi.BeginLive(activate);
                    _liveOpen = GameApi.LiveRunning();
                    if (_liveOpen) _liveActions++;
                }
                if (_liveOpen) GameApi.AddLive(r.Live);
            }
            if (!r.Open && _liveOpen) { GameApi.EndLive(true); _liveOpen = false; }
            if (r.Corner != null && r.Corner.Units > 0) GameApi.AddCoinUnits(r.Corner.Units);
        }

        /// <summary>Ends a running live action (completed = banked into the combo). Never throws, never counts as a fault.</summary>
        private void CloseLive(bool completed)
        {
            bool open = _liveOpen;
            _liveOpen = false;
            try
            {
                // also an action we didn't record as open (BeginLive threw half-way): ask the game
                if (open || (GameApi.ScoreOk && GameApi.NativeActive && GameApi.LiveRunning())) GameApi.EndLive(completed);
            }
            catch { /* the level is going away */ }
        }

        /// <summary>One traffic snapshot (every 0.1 s): the cars near the player, then the traffic-aware line around them.</summary>
        private void TrafficTick()
        {
            using var perf = RogueShared.Perf.Scope("RacingLine.Traffic");
            if (!_traffic.IsFor(_line)) _traffic.Attach(_line);
            _trafficCfg.Margin = Mathf.Clamp(Plugin.TrafficMargin.Value, 0f, 5f);
            _trafficCfg.PlayerHalfWidth = Mathf.Clamp(Plugin.TrafficPlayerHalfWidth.Value, 0.3f, 3f);
            _trafficCfg.MinLeadIn = Mathf.Clamp(Plugin.TrafficMinLeadIn.Value, 1f, 200f);
            _trafficCfg.MaxLeadIn = Mathf.Max(_trafficCfg.MinLeadIn, Mathf.Min(200f, Plugin.TrafficMaxLeadIn.Value));
            _trafficCfg.LeadInSeconds = Mathf.Clamp(Plugin.TrafficLeadInSeconds.Value, 0f, 10f);
            _trafficCfg.LeadOut = Mathf.Clamp(Plugin.TrafficLeadOut.Value, 1f, 200f);
            float ahead = Mathf.Clamp(Plugin.TrafficLookAhead.Value, 30f, 400f);
            _traffic.CarCount = GameApi.ReadTraffic(_player.Distance, 15f, ahead, _traffic.Cars);
            _traffic.Rebuild(Time.time, _player.Distance, _player.Speed, _trafficCfg);
            if (!_trafficSeenLogged && _traffic.CarCount > 0) LogFirstTraffic();
        }

        /// <summary>Once per race: the nearest traffic car next to the player's own numbers, to confirm the frames in the log.</summary>
        private void LogFirstTraffic()
        {
            _trafficSeenLogged = true;
            int best = 0;
            for (int i = 1; i < _traffic.CarCount; i++)
                if (Mathf.Abs(_traffic.Cars[i].Road - _player.Distance) < Mathf.Abs(_traffic.Cars[best].Road - _player.Distance)) best = i;
            var c = _traffic.Cars[best];
            Plugin.Log.LogInfo($"[RacingLine] traffic: {_traffic.CarCount} cars near the player; nearest {c.Road - _player.Distance:+0;-0} m along the road, lane {c.Lane:0.0} m " +
                               $"(player lane {_player.Offset:0.0} m, + = right), {c.HalfWidth * 2f:0.0} x {c.HalfLength * 2f:0.0} m, {c.Speed:0} m/s{(c.Speed < 0f ? " (oncoming)" : "")}; " +
                               $"line shifted around {_traffic.ShiftedCars}, {_traffic.BlockedCars} with no way past");
        }

        private void EnsureNative()
        {
            bool ok = GameApi.EnsureNative(Icons.Hud, Icons.Stat, Plugin.CoinReward.Value, out string log);
            if (log != null && (!_nativeLogged || ok)) { Plugin.Log.LogInfo(log); _nativeLogged = ok; }
            if (ok && _corners != null) GameApi.SetCoinTarget(_corners.Count * Plugin.CoinTargetPerCorner.Value);
        }

        private GameApi.ResultsData ResultsData()
        {
            float t = _scorer?.TimeOnLine ?? 0f;
            bool counts = GameApi.NativeActive && !_nativeOff;
            CountRace();
            if (_scorer != null && !ReferenceEquals(_scorer, _trafficLoggedFor) && GameApi.TrafficOk && Plugin.TrafficEnabled.Value)
            {
                _trafficLoggedFor = _scorer;
                Plugin.Log.LogInfo($"[RacingLine] traffic: line shifted in {_scorer.TrafficCorners} of {_scorer.CornersDone} corners, {_traffic.CleanPasses} clean passes{(_trafficOff ? " (traffic line switched off after an error)" : "")}");
            }
            if (_scorer != null && !ReferenceEquals(_scorer, _liveLoggedFor))
            {
                _liveLoggedFor = _scorer;
                Plugin.Log.LogInfo($"[RacingLine] live scoring: {_liveActions} live actions, {_scorer.TotalPoints:0} pts counted live ({(counts ? $"game total {GameApi.NativeScore():0}, includes card multipliers" : "display mode")}); " +
                                   $"{_scorer.CornersDone} corners: gold {_scorer.Gold}, silver {_scorer.Silver}, bronze {_scorer.Bronze}, grip {_scorer.GripCorners}");
                _liveActions = 0;
            }
            return new GameApi.ResultsData
            {
                Amount = $"{(int)(t / 60):00}:{(int)(t % 60):00}",
                Counts = counts,
                Score = counts ? GameApi.NativeScore() : 0,
                Coins = counts ? GameApi.NativeCoins() : 0,
                Icon = Icons.Stat,
            };
        }

        /// <summary>Adds this race's Racing Line score to the run total, once per race (the scorer is the race's identity).</summary>
        private void CountRace()
        {
            if (_scorer == null || ReferenceEquals(_scorer, _countedScorer)) return;
            if (!(GameApi.NativeActive && !_nativeOff)) return;   // display mode: nothing counted, no Victory row
            _countedScorer = _scorer;
            _runTotal += GameApi.NativeScore();   // the game's own number for this race (card multipliers included)
        }

        private GameApi.VictoryData VictoryData()
        {
            CountRace();   // in case the Victory screen comes before the last race's results screen
            bool show = _runTotal > 0 || (GameApi.NativeActive && !_nativeOff);
            bool record = _runTotal > 0 && _runTotal > Plugin.BestRunTotal.Value;
            if (record) Plugin.BestRunTotal.Value = Math.Round(_runTotal);   // saved to rogue.racingline.cfg
            return new GameApi.VictoryData
            {
                Show = show,
                Value = Math.Round(_runTotal).ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                NewRecord = record,
                Icon = Icons.Stat,
            };
        }

        private static void CleanupVictoryQuietly()
        {
            try { if (GameApi.VictoryOk) GameApi.CleanupVictoryRowWhenClosed(); }
            catch { /* the row goes with the scene at worst */ }
        }

        /// <summary>Notice a new or grown path and (re)build. Bad input means idle, not guess (Safety rule 10).</summary>
        private void Watch()
        {
            if (!GameApi.RefreshPath(out IntPtr ptr, out float length))
            {
                if (_line != null || _builder != null) Plugin.Log.LogInfo("[RacingLine] path gone (menu or loading): line dropped");
                CloseLive(true);
                _line = null; _builder = null; _watchPtr = IntPtr.Zero; _failedPtr = IntPtr.Zero;
                // _scorer is kept: its totals feed the results row, which opens after the race
                _status = "waiting for a run";
                return;
            }

            if (ptr != _watchPtr)
            {
                // a new run: whatever was built or building belongs to the old path, drop it before anything else
                CloseLive(true);
                _liveActions = 0;   // a race quit before its results screen doesn't carry its count over
                _builder = null; _line = null; _scorer = null; _nearest = -1; _lastHits = -1;
                _watchPtr = ptr;
                Plugin.Log.LogInfo($"[RacingLine] new path: {length:0} m");
                GameApi.ForgetScene();    // find the world manager again
                GameApi.ForgetNative();   // re-check the score list (EnsureNative adopts our provider if it's still there)
                _nextNativeTry = 0f;
            }

            float have = _builder != null ? _builder.PathLength : _line != null ? _line.Length : float.NaN;
            if (!float.IsNaN(have) && Mathf.Abs(length - have) <= 1f) return;              // up to date, or building it
            if (ptr == _failedPtr && Mathf.Abs(length - _failedLength) <= 1f) return;      // already failed on exactly this path

            if (length < 100f) { _status = "path too short"; return; }
            float width = GameApi.WidthOk ? GameApi.RoadWidth() : -1f;
            if (width <= 0f || float.IsNaN(width) || float.IsInfinity(width))
            {
                if (!_waitingLogged) { _waitingLogged = true; Plugin.Log.LogInfo("[RacingLine] road width not known yet, waiting"); }
                _status = "waiting for road width";
                return;   // retried next tick
            }
            _waitingLogged = false;

            if (!float.IsNaN(have))
                Plugin.Log.LogInfo($"[RacingLine] path grew in place: {have:0} m -> {length:0} m (same object), rebuilding; the current line stays up until then");
            _builder = new LineBuilder(ptr, length, width, Plugin.Margin.Value, Plugin.SampleStep.Value);   // _line kept until replaced
            _status = _line != null ? "line ready, rebuilding for the longer path" : "building line";
        }

        // ------------------------------------------------------------------ the line on the road + the HUD card

        private static readonly Color PaceGo = new Color(0.2f, 1f, 0.45f, 0.85f);
        private static readonly Color PaceLift = new Color(1f, 0.82f, 0.15f, 0.9f);
        private static readonly Color PaceBrake = new Color(1f, 0.18f, 0.12f, 0.92f);
        private static readonly Color Detour = new Color(1f, 0.55f, 0.1f, 0.9f);
        private static readonly Color NoWay = new Color(1f, 1f, 1f, 0.12f);

        /// <summary>After the car has moved this frame: lay the ground line and update the card (or hide both).</summary>
        private void LateUpdate()
        {
            if (_broken || _visualsOff) return;
            bool show = Plugin.Enabled.Value && Plugin.ShowLine.Value && _line != null;
            if (!show && !_visualsTried) return;   // nothing created yet, nothing to hide
            using var perf = RogueShared.Perf.Scope("RacingLine.Visuals");
            try
            {
                if (!_visualsTried)
                {
                    _visualsTried = true;
                    _ground = GroundLine.Create();
                    _hud = LineHud.Create();
                }
                if (!show) { _ground?.Hide(); _hud?.SetVisible(false); return; }
                DrawGround();
                if (_hud != null && _hud.Ok)
                {
                    var sc = _scorer;
                    _hud.SetVisible(sc != null);
                    if (sc != null)
                    {
                        bool recent = Time.unscaledTime < _hudGradeUntil;
                        _hud.Show(sc.InCorner ? sc.CornerPoints : sc.TotalPoints, sc.InCorner, recent ? _hudGrade : null, _hudGrip,
                                  sc.StreakMultiplier, sc.LastSigned, Plugin.LineFull.Value, Mathf.Max(Plugin.LineZero.Value, Plugin.LineFull.Value + 0.5f));
                    }
                }
            }
            catch (Exception e)
            {
                _visualsOff = true;
                Plugin.Log.LogWarning($"[RacingLine] ground line / HUD card switched off for this session after an error (dots stay): {e.Message}");
                DestroyVisuals();
                Fault(e);
            }
        }

        private void DrawGround()
        {
            var g = _ground;
            var line = _line;
            if (g == null || !g.Ok || line == null) return;
            float now = Time.unscaledTime;
            if (now >= _nextCamFetch || _cam == null) { _nextCamFetch = now + 2f; _cam = Camera.main; }
            // start just ahead of the car: from the player's own road distance when known, else nearest to the camera
            int from;
            if (_playerValid) from = Mathf.Clamp(Mathf.FloorToInt(_player.Distance / line.Step), 0, line.N - 1);
            else if (_cam != null) from = Nearest(line, _cam.transform.position);
            else from = -1;
            if (from < 0) { g.Hide(); return; }
            int start = Mathf.Min(line.N - 1, from + 1);
            int count = Mathf.Min(line.N - start, Mathf.Min(GroundLine.MaxSamples, Mathf.CeilToInt(Mathf.Clamp(Plugin.DrawAhead.Value, 20f, 400f) / line.Step)));
            var traffic = _traffic.IsFor(line) ? _traffic : null;
            var sc = _scorer;
            float v = _playerValid ? _player.Speed : float.NaN;
            float brake = Mathf.Max(1f, Plugin.BrakeDecel.Value);
            for (int k = 0; k < count; k++)
            {
                int i = start + k;
                float along = i * line.Step;
                float dev = traffic != null ? traffic.Deviation(along) : 0f;
                g.Offset[k] = line.E[i] + dev;
                Color c;
                if (traffic != null && traffic.IsBlocked(along)) c = NoWay;
                else
                {
                    // what to do at this point at the current speed: can you slow to its reference speed in the distance left?
                    float vr = sc != null ? sc.RefSpeed(i) : float.NaN;
                    if (float.IsNaN(vr) || float.IsNaN(v)) c = PaceGo;
                    else
                    {
                        float dist = Mathf.Max(3f, (i - from) * line.Step);
                        float need = (v * v - vr * vr) / (2f * dist) / brake;   // fraction of full braking needed
                        c = need < 0.3f ? PaceGo
                          : need < 0.65f ? Color.Lerp(PaceGo, PaceLift, (need - 0.3f) / 0.35f)
                          : Color.Lerp(PaceLift, PaceBrake, Mathf.Clamp01((need - 0.65f) / 0.35f));
                    }
                    if (Mathf.Abs(dev) > 0.05f) c = Color.Lerp(c, Detour, 0.65f);
                }
                g.Tint[k] = c;
            }
            g.Commit(line, start, count, Mathf.Clamp(Plugin.LineWidth.Value, 0.3f, 3f));
        }

        private void DestroyVisuals()
        {
            try { _ground?.Destroy(); } catch { /* scene takes it */ }
            try { _hud?.Destroy(); } catch { /* scene takes it */ }
            _ground = null; _hud = null;
        }

        private void OnGUI()
        {
            if (_broken || !Plugin.Enabled.Value || !Plugin.ShowLine.Value) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            bool ground = _ground != null && _ground.Ok && !_visualsOff;
            if (ground && !Plugin.DebugText.Value) return;   // the line is on the road and the card shows the numbers
            using var perf = RogueShared.Perf.Scope("RacingLine.OnGUI");
            try { Draw(!ground); }
            catch (Exception e) { Fault(e); }
        }

        private void Draw(bool dots)
        {
            var line = _line;
            var cam = Camera.main;
            if (dots && line != null && cam != null)
            {
                int idx = Nearest(line, cam.transform.position);
                if (idx >= 0)
                {
                    int ahead = Mathf.CeilToInt(Mathf.Clamp(Plugin.DrawAhead.Value, 20f, 400f) / line.Step);
                    int end = Mathf.Min(line.N, idx + ahead);
                    float pxPerMetre = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                    var traffic = _traffic.IsFor(line) ? _traffic : null;   // draws e*, the line the scorer uses
                    var old = GUI.color;
                    for (int i = Mathf.Max(0, idx - 4); i < end; i++)
                    {
                        if (i % 4 == 0)
                        {
                            Dot(cam, line.Edge(i, -line.HalfWidth, 0.15f), 0.25f, pxPerMetre, EdgeDot);
                            Dot(cam, line.Edge(i, line.HalfWidth, 0.15f), 0.25f, pxPerMetre, EdgeDot);
                        }
                        float along = i * line.Step;
                        float dev = traffic != null ? traffic.Deviation(along) : 0f;
                        Color c;
                        if (traffic != null && traffic.IsBlocked(along)) c = LineBlocked;
                        else if (Mathf.Abs(dev) > 0.05f) c = LineDetour;
                        else c = Color.Lerp(LineNear, LineEdge, line.Limit > 0f ? Mathf.Abs(line.E[i]) / line.Limit : 0f);
                        Dot(cam, line.Edge(i, line.E[i] + dev, 0.15f), 0.45f, pxPerMetre, c);
                    }
                    GUI.color = old;
                }
            }

            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            int oldSize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = Mathf.RoundToInt(13 * s);
            string mode = GameApi.NativeActive && !_nativeOff ? "native" : "display";
            GUI.Label(new Rect(12 * s, Screen.height - 28 * s, 900 * s, 24 * s),
                      $"RacingLine {Plugin.Version} (preview) · {Net.Role()} · {mode} · {_status} · F5 hide");
            var sc = _scorer;
            if (sc != null)
            {
                string last = Time.unscaledTime < _lastResultUntil ? $" · last: {_lastResult}" : "";
                GUI.Label(new Rect(12 * s, Screen.height - 48 * s, 900 * s, 24 * s),
                          $"{sc.State} · off line {sc.LastError:0.0} m · corners {sc.CornersDone}/{sc.CornerCount} (gold {sc.Gold} silver {sc.Silver} bronze {sc.Bronze}, grip {sc.GripCorners}) · " +
                          $"streak x{sc.StreakMultiplier:0.00} pace {sc.Pace:0.00}{(sc.HasReference ? "" : " (no speed ref yet)")} · {sc.TotalPoints:0} pts{last}");
                if (GameApi.TrafficOk && Plugin.TrafficEnabled.Value)
                {
                    string traffic = _trafficOff ? "traffic: line switched off after an error (plain line)"
                        : $"traffic: line shifted around {_traffic.ShiftedCars} cars{(_traffic.BlockedCars > 0 ? $", {_traffic.BlockedCars} with no way past" : "")} · " +
                          $"corners shifted {sc.TrafficCorners} · clean passes {_traffic.CleanPasses}";
                    GUI.Label(new Rect(12 * s, Screen.height - 68 * s, 900 * s, 24 * s), traffic);
                }
            }
            GUI.skin.label.fontSize = oldSize;
        }

        // GUI.DrawTexture is stripped from this game's build ("Method unstripping failed"), so dots are GUI.Box calls with
        // a plain white style, tinted by GUI.color. Only members present in dump.cs: GUIStyle(), normal.background,
        // Texture2D.whiteTexture, GUI.Box(Rect, GUIContent, GUIStyle), GUIContent.none.
        private static GUIStyle _dotStyle;

        private static void Dot(Camera cam, Vector3 world, float size, float pxPerMetre, Color c)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0.5f) return;
            if (_dotStyle == null) { _dotStyle = new GUIStyle(); _dotStyle.normal.background = Texture2D.whiteTexture; }
            float px = Mathf.Clamp(size * pxPerMetre / sp.z, 2f, 18f);
            GUI.color = c;
            GUI.Box(new Rect(sp.x - px * 0.5f, Screen.height - sp.y - px * 0.5f, px, px), GUIContent.none, _dotStyle);
        }

        /// <summary>Nearest sample to a position: a local search around the last answer, a full scan at most twice a second.</summary>
        private int Nearest(Line line, Vector3 pos)
        {
            int best = -1; float bestD = float.MaxValue;
            if (_nearest >= 0 && _nearest < line.N)
            {
                int lo = Mathf.Max(0, _nearest - 80), hi = Mathf.Min(line.N, _nearest + 80);
                for (int i = lo; i < hi; i++) { float d = Sq(line, i, pos); if (d < bestD) { bestD = d; best = i; } }
            }
            if ((best < 0 || bestD > 30f * 30f) && Time.unscaledTime >= _nextFullScan)
            {
                _nextFullScan = Time.unscaledTime + 0.5f;
                for (int i = 0; i < line.N; i++) { float d = Sq(line, i, pos); if (d < bestD) { bestD = d; best = i; } }
            }
            if (bestD > 60f * 60f) best = -1;   // not near the road (menus, spectating)
            _nearest = best;
            return best;
        }

        private static float Sq(Line l, int i, Vector3 p) { float dx = l.Px[i] - p.x, dz = l.Pz[i] - p.z; return dx * dx + dz * dz; }

        /// <summary>Runs one feature; an exception switches just that feature off (logged once) and counts toward the plugin breaker.</summary>
        private void Guard(ref bool off, string feature, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                off = true;
                Plugin.Log.LogWarning($"[RacingLine] {feature} switched off for this session after an error: {e.Message}");
                if (off && (feature == "native category" || feature == "scoring")) CloseLive(true);   // never leave the game's live action open
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
                CloseLive(true);
                DestroyVisuals();
                _line = null; _builder = null; _scorer = null;   // a results row still showing is removed once the screen closes
                Plugin.Log.LogError($"[RacingLine] switched off for this session after repeated errors; the game is unaffected. Last error: {e}");
            }
            else Plugin.Log.LogWarning($"[RacingLine] error ({_errors.Count}/5 in 10 s): {e.Message}");
        }

        /// <summary>Removes a leftover results row once the screen is closed. Never throws, never counts as a fault.</summary>
        private static void CleanupRowQuietly()
        {
            try { if (GameApi.ResultsOk) GameApi.CleanupRowWhenClosed(); }
            catch { /* the row goes with the scene at worst */ }
        }

        private void OnDestroy()
        {
            CloseLive(true);
            try { LineShare.Sync(null, null); } catch { /* shutting down */ }
            try { Icons.Destroy(); } catch { /* shutting down: the scene takes our row with it */ }
            DestroyVisuals();
        }
    }
}
