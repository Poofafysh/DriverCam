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
    /// - Each frame: the results screen gets its Racing Line row while it is open.
    /// - OnGUI: the line preview (dots) and a status/readout line.
    /// - Circuit breakers (Safety rule 6): scoring, the native category and the results row each switch themselves off
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
        private bool _scoringOff, _nativeOff, _resultsOff, _victoryOff;   // per-feature breakers
        private Func<GameApi.ResultsData> _resultsData;
        private Func<GameApi.VictoryData> _victoryData;
        private Action<string> _log;
        // created once (LoadIcons) so Update/Score don't allocate a delegate or closure every frame
        private Action _resultsTick, _victoryTick, _score, _ensureNative, _awardTick, _payCorner;
        // run total for the Victory screen: each race's Racing Line score is added once (session only)
        private double _runTotal;
        private LineScorer _countedScorer;
        private double _pendingTick;          // read by _awardTick
        private CornerResult _pendingCorner;  // read by _payCorner
        private int _carChanges = -1;         // GameApi.CarChanges when the grip estimate was started

        private readonly Queue<float> _errors = new Queue<float>();
        private bool _broken;
        private string _status = "starting";

        private static readonly Color LineNear = new Color(0.25f, 1f, 0.45f, 0.95f);
        private static readonly Color LineEdge = new Color(1f, 0.85f, 0.2f, 0.95f);
        private static readonly Color EdgeDot = new Color(1f, 1f, 1f, 0.35f);

        private void Update()
        {
            // our results row is only ever removed once the results screen has closed (removing it mid-animation would
            // leave the player without a Continue button), and that cleanup keeps running when we're off or broken
            if (_broken || !Plugin.Enabled.Value) { CleanupRowQuietly(); CleanupVictoryQuietly(); return; }
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

                if (_line != null && _scorer != null && GameApi.PlayerOk && !_scoringOff) Guard(ref _scoringOff, "scoring", _score);
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
            _ensureNative = EnsureNative;
            _awardTick = () => GameApi.AwardNative(_pendingTick, Plugin.TickPopups.Value);
            _payCorner = () =>
            {
                if (_pendingCorner.Bonus > 0) GameApi.AwardNative(_pendingCorner.Bonus, true);
                GameApi.AddCoinUnits(_pendingCorner.Units);
            };
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
                _scorer = new LineScorer(_line, _corners, _lineCurvature, Settings(), previous != null && previous.PathPtr == _line.PathPtr ? _scorer : null);
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
            TickInterval = Mathf.Clamp(Plugin.TickInterval.Value, 0.1f, 0.8f), TickMinQ = Plugin.TickMinQ.Value,
            DriftFactor = Plugin.DriftFactor.Value, TrafficGrace = Plugin.TrafficGrace.Value,
            ExitWeight = Plugin.ExitWeight.Value, CleanBonus = Plugin.CleanBonus.Value, GripBonus = Plugin.GripBonus.Value,
            CoastPerSecond = Plugin.CoastPerSecond.Value, CoastFloor = Plugin.CoastFloor.Value,
            Gold = Plugin.Gold.Value, Silver = Plugin.Silver.Value, Bronze = Plugin.Bronze.Value,
            StreakStep = Plugin.StreakStep.Value, StreakMax = Mathf.Max(1f, Plugin.StreakMax.Value),
        };

        /// <summary>One frame of scoring: read the player, smooth the inputs, step the scorer, pay ticks and corner bonuses.</summary>
        private void Score()
        {
            if (!GameApi.ReadPlayer(ref _player)) { _lastHits = -1; _lastNearMisses = -1; _heading = float.NaN; return; }
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

            var r = _scorer.Step(new ScoreInput
            {
                Active = _player.InControl && _player.Grounded,
                Distance = _player.Distance, Offset = _player.Offset, Speed = _player.Speed, Dt = dt,
                Throttle = _throttle, Brake = _brake, CarCurvature = _carCurvature, Grip = _grip.Value,
                Drifting = _player.Drifting, Hit = hit, NearMiss = nearMiss,
            });

            bool native = GameApi.NativeActive && !_nativeOff;
            if (r.Tick > 0 && native) { _pendingTick = r.Tick; Guard(ref _nativeOff, "native category", _awardTick); }
            var c = r.Corner;
            if (c == null) return;
            if (native) { _pendingCorner = c; Guard(ref _nativeOff, "native category", _payCorner); _pendingCorner = null; }
            _lastResult = $"corner {c.Index + 1}{c.Type}: {c.Label} +{c.Total:0}{(native ? "" : " (display only)")}";
            _lastResultUntil = Time.unscaledTime + 4f;
            if (Plugin.LogCorners.Value)
                Plugin.Log.LogInfo($"[RacingLine] corner {c.Index + 1}{c.Type}: {c.Grade ?? "-"}{(c.Grip ? " grip" : " drifted")}{(c.Clean ? "" : " hit")} q {c.MeanQ:0.00} " +
                                   $"exit {c.Exit:0.00} full-throttle {(float.IsNaN(c.SecondsToFullThrottle) ? "never" : c.SecondsToFullThrottle.ToString("0.0") + " s")} " +
                                   $"coast {c.CoastAfterApex:0.0} s -> {c.Total:0} pts (bonus {c.Bonus:0}), units {c.Units:0.0}, streak x{_scorer.StreakMultiplier:0.00}, pace {_scorer.Pace:0.00}");
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
                _line = null; _builder = null; _watchPtr = IntPtr.Zero; _failedPtr = IntPtr.Zero;
                // _scorer is kept: its totals feed the results row, which opens after the race
                _status = "waiting for a run";
                return;
            }

            if (ptr != _watchPtr)
            {
                // a new run: whatever was built or building belongs to the old path, drop it before anything else
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

        private void OnGUI()
        {
            if (_broken || !Plugin.Enabled.Value || !Plugin.ShowLine.Value) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            using var perf = RogueShared.Perf.Scope("RacingLine.OnGUI");
            try { Draw(); }
            catch (Exception e) { Fault(e); }
        }

        private void Draw()
        {
            var line = _line;
            var cam = Camera.main;
            if (line != null && cam != null)
            {
                int idx = Nearest(line, cam.transform.position);
                if (idx >= 0)
                {
                    int ahead = Mathf.CeilToInt(Mathf.Clamp(Plugin.DrawAhead.Value, 20f, 400f) / line.Step);
                    int end = Mathf.Min(line.N, idx + ahead);
                    float pxPerMetre = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                    var old = GUI.color;
                    for (int i = Mathf.Max(0, idx - 4); i < end; i++)
                    {
                        if (i % 4 == 0)
                        {
                            Dot(cam, line.Edge(i, -line.HalfWidth, 0.15f), 0.25f, pxPerMetre, EdgeDot);
                            Dot(cam, line.Edge(i, line.HalfWidth, 0.15f), 0.25f, pxPerMetre, EdgeDot);
                        }
                        float t = line.Limit > 0f ? Mathf.Abs(line.E[i]) / line.Limit : 0f;
                        Dot(cam, line.Point(i, 0.15f), 0.45f, pxPerMetre, Color.Lerp(LineNear, LineEdge, t));
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
            try { Icons.Destroy(); } catch { /* shutting down: the scene takes our row with it */ }
        }
    }
}
