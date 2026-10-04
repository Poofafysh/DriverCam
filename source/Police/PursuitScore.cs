using System;
using System.Globalization;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// The PURSUIT score category's logic (design doc "Police Pursuit - v1 Concept": Scoring; game glue in
    /// GameApi.Score.cs). A plain class owned by Runner, which calls the chase hooks; it never touches the chase itself.
    ///
    /// - Live points while a chase runs (the game's temporary score, counted up on the HUD and kept in the combo):
    ///   8 pts per 1% the lead bar rises, plus 10 pts/s while you're at 80%+ of your top speed.
    /// - ESCAPED: escape bonus = 250 + 25/s x min(chase seconds, Duration) + 150 x (units - 1)
    ///   + 300 x clamp01((50 - lowest lead) / 50); then the action goes into the combo and one coin unit is added.
    ///   An escape at time up (lead above 50%) gets a flat 100 instead.
    /// - CAUGHT: the chase's pending points are lost (the 5 s timer penalty stays Runner's).
    /// - Cancelled because the race is over: banked. Any other exit (plugin, Pursuit.Enabled or F3 off, new race, quit,
    ///   breaker, unload): cancelled. Pursuit.Enabled switched off mid-chase cancels that chase's action (the chase goes
    ///   on, scoring nothing); switched back on, scoring starts again from the next chase. If the game ended the action itself (a crash failed the combo), the next points start a new one.
    /// - All numbers x Pursuit.PointsScale, before the game's own multipliers (cards, combo).
    /// - While paused (timeScale 0) nothing is added, started or ensured (no escape bonus, no coin unit); closing an action
    ///   is always allowed.
    /// - Rows: PURSUIT on the per-race results screen and the end-of-run Victory screen (RogueShared.ModScoreRows,
    ///   rank 2: after RacingLine's RACING LINE). Each race's provider score is added to a session run total once. Rows and
    ///   the run total follow whether the category is in this level (not the settings), so points already banked keep
    ///   their row when Pursuit or the plugin is switched off. The first race of a new run (stage 0, race 0) takes the
    ///   previous run's total off; the run position is read again on later ticks until a read succeeds for that race.
    /// - Breaker: any error switches PURSUIT off for the session (its action cancelled) and counts toward Runner's
    ///   plugin breaker. Rows are still removed once their screen has closed.
    /// </summary>
    internal sealed class PursuitScore
    {
        internal enum End { Escaped, TimeUp, Caught, Bank, Cancel }

        internal const float PtsPerLeadPct = 8f, PtsPerSecondFast = 10f, FastShare = 0.8f;
        internal const float EscapeBase = 250f, EscapePerSecond = 25f, EscapePerExtraUnit = 150f, EscapeCloseness = 300f, TimeUpBonus = 100f;
        private const float CoinTargetEscapes = 2f;   // two escapes in a race pay the full CoinReward

        private readonly Action<Exception> _fault;
        private readonly RogueShared.ModScoreRows _rows;
        private bool _off, _resultsOff, _victoryOff, _nativeLogged, _iconsLoaded;
        private float _nextNativeTry;

        // the live action
        private bool _inChase, _open, _activated;
        private double _pending, _lostToCombo, _stepPoints;
        private float _lowestBar;
        private int _peakUnits;

        // per race / per run (session only)
        private IntPtr _raceCar, _raceSpawner;
        private int _raceNo, _countedRace = -1, _escapes, _chases;
        private double _runTotal;
        private bool _runCheckPending;   // this race's run position (new run?) hasn't been read yet
        private double _runTotalBefore;  // the run total when this race began (all of it belongs to an earlier race)

        // created once: no delegate per call
        private readonly Action _resultsTick, _victoryTick, _ensure, _begin, _step;
        private readonly Func<RogueShared.ModScoreRows.ResultsData> _resultsData;
        private readonly Func<RogueShared.ModScoreRows.VictoryData> _victoryData;

        internal PursuitScore(Action<Exception> fault)
        {
            _fault = fault;
            _rows = new RogueShared.ModScoreRows(GameApi.PursuitId, GameApi.PursuitName, 2, s => Plugin.Log.LogInfo("[Police] " + s));
            _resultsData = ResultsData;
            _victoryData = VictoryData;
            _resultsTick = () => _rows.ResultsTick(_resultsData);
            _victoryTick = () => _rows.VictoryTick(_victoryData);
            _ensure = Ensure;
            _begin = Begin;
            _step = Step;
        }

        private static bool Wanted => Plugin.Enabled.Value && Plugin.PursuitEnabled.Value;
        private bool Counts => Wanted && !_off && GameApi.PursuitOk && GameApi.PursuitActive;
        /// <summary>The PURSUIT category is in this level's score list (whatever the settings say now): rows and run total.</summary>
        private bool InLevel => !_off && GameApi.PursuitOk && GameApi.PursuitActive;
        private static float Scale => Mathf.Clamp(Plugin.PursuitPointsScale.Value, 0f, 3f);

        // ------------------------------------------------------------------ Runner entry points

        /// <summary>Every frame: the rows on the results and Victory screens. Never throws.</summary>
        internal void Frame()
        {
            if (!_off && (Wanted || InLevel || _runTotal > 0))   // points banked before a switch-off keep their rows
            {
                if (GameApi.ResultsOk && !_resultsOff) Guard(ref _resultsOff, "pursuit results row", _resultsTick);
                if (GameApi.VictoryOk && !_victoryOff) Guard(ref _victoryOff, "pursuit victory row", _victoryTick);
            }
            CleanupRows();
        }

        /// <summary>Removes rows whose screen has closed (also while off or broken). Never throws.</summary>
        internal void CleanupRows()
        {
            try { _rows.CleanupWhenClosed(GameApi.ResultsOk, GameApi.VictoryOk); }
            catch { /* the rows go with the scene at worst */ }
        }

        /// <summary>
        /// Every Runner tick, AFTER the patrol tick (so a chase cut short by a new race is ended first): notices a new
        /// race and adds the category to the level. policeOn = patrols can run (config, mode, F3, single-player).
        /// </summary>
        internal void Tick(bool policeOn)
        {
            if (_off || !GameApi.PursuitOk || !GameApi.PlayerOk) return;
            if (!Wanted && (_inChase || _open)) SwitchedOff();   // Pursuit.Enabled / Enabled off mid-chase (e.g. from RogueHub)
            try
            {
                IntPtr car = GameApi.PlayerCar();
                IntPtr spawner = GameApi.TrafficOk ? GameApi.Spawner() : IntPtr.Zero;
                // like Runner.Tick: no spawner (loading) is not a new race
                if (car != IntPtr.Zero && spawner != IntPtr.Zero && (car != _raceCar || spawner != _raceSpawner)) NewRace(car, spawner);
                if (_runCheckPending) CheckNewRun();
                if (policeOn && Wanted && car != IntPtr.Zero && Time.timeScale > 0f && Time.unscaledTime >= _nextNativeTry)
                {
                    _nextNativeTry = Time.unscaledTime + 2f;
                    _ensure();
                }
            }
            catch (Exception e) { Off("pursuit score", e); }
        }

        /// <summary>A chase started with this many units.</summary>
        internal void ChaseStart(int units)
        {
            if (_open) Close(false);   // never two actions: a leftover one is cancelled (never throws)
            _inChase = true; _activated = false;
            _pending = 0; _lostToCombo = 0; _lowestBar = 50f; _peakUnits = Math.Max(1, units);
            _chases++;
            if (!Counts || Time.timeScale <= 0f) return;
            Safe(_begin);
        }

        /// <summary>One chase tick: the lead bar before and after it, game seconds, whether you were at 80%+ of top speed.</summary>
        internal void ChaseStep(float barBefore, float barAfter, float dt, bool fast, int units)
        {
            if (!_inChase) return;
            _lowestBar = Mathf.Min(_lowestBar, barAfter);
            _peakUnits = Math.Max(_peakUnits, units);
            if (dt <= 0f || Time.timeScale <= 0f || !Counts) return;
            _stepPoints = (PtsPerLeadPct * Mathf.Max(0f, barAfter - barBefore) + (fast ? PtsPerSecondFast * dt : 0f)) * Scale;
            Safe(_step);
        }

        /// <summary>
        /// The chase is over. Returns the points it earned (before the game's multipliers; 0 if none) for the banner.
        /// Never throws.
        /// </summary>
        internal double ChaseEnd(End kind, float chaseSeconds, float duration, int units, string reason)
        {
            if (!_inChase) return 0;
            _inChase = false;
            int peak = Math.Max(_peakUnits, Math.Max(1, units));
            if (kind == End.Escaped || kind == End.TimeUp) _escapes++;
            if (!Counts) { Close(false); return 0; }

            double awarded = 0, bonus = 0, live = 0;
            string what;
            try
            {
                Refresh();
                live = _pending;
                switch (kind)
                {
                    case End.Escaped:
                    case End.TimeUp:
                        // while paused (a unit despawned): no new points, no coin unit; the open action is still closed
                        bool paused = Time.timeScale <= 0f;
                        bonus = paused ? 0 : kind == End.TimeUp ? TimeUpBonus * Scale : EscapeBonus(chaseSeconds, duration, peak, _lowestBar) * Scale;
                        if (!_open && bonus > 0)
                        {
                            GameApi.PursuitBegin(!_activated);
                            _activated = true;
                            _open = GameApi.PursuitLiveRunning();
                        }
                        if (_open && bonus > 0) { GameApi.PursuitAdd(bonus); _pending += bonus; }
                        else bonus = 0;
                        if (_open) GameApi.PursuitEnd(true);
                        _open = false;
                        if (!paused) GameApi.PursuitCoinUnits(1f);
                        awarded = _pending;
                        what = kind == End.TimeUp
                            ? $"escaped at time up: {live:0} live + {bonus:0} bonus"
                            : $"escaped after {chaseSeconds:0.0} s, {peak} unit(s), lowest lead {_lowestBar:0}%: {live:0} live + {bonus:0} escape bonus";
                        if (paused) what += " (while paused: no bonus, no coins)";
                        break;
                    case End.Caught:
                        Close(false);
                        what = $"caught: {live:0} pending pts lost";
                        break;
                    case End.Bank:
                        Close(true);
                        awarded = live;
                        what = $"race over: {live:0} pending pts banked";
                        break;
                    default:
                        Close(false);
                        what = $"cancelled ({reason}): {live:0} pending pts dropped";
                        break;
                }
            }
            catch (Exception e)
            {
                Off("pursuit score", e);
                return 0;
            }
            _pending = 0;
            Plugin.Log.LogInfo($"[Police] pursuit: +{awarded:0} pts ({what}{(_lostToCombo > 0 ? $"; {_lostToCombo:0} lost earlier to a failed combo" : "")}; before card multipliers)");
            return awarded;
        }

        /// <summary>Escape bonus before PointsScale: 250 + 25/s x min(t, Duration) + 150 x (units - 1) + 300 x closeness.</summary>
        internal static double EscapeBonus(float chaseSeconds, float duration, int units, float lowestBar)
            => EscapeBase + EscapePerSecond * Mathf.Min(Mathf.Max(0f, chaseSeconds), duration) + EscapePerExtraUnit * Math.Max(0, units - 1)
             + EscapeCloseness * Mathf.Clamp01((50f - lowestBar) / 50f);

        /// <summary>Ends a running live action (completed = into the combo). Never throws, never counts as a fault.</summary>
        internal void Close(bool completed)
        {
            bool open = _open;
            _open = false;
            try
            {
                // also an action we didn't record as open (PursuitBegin threw half-way): ask the game
                if (GameApi.PursuitOk && (open || (GameApi.PursuitActive && GameApi.PursuitLiveRunning()))) GameApi.PursuitEnd(completed);
            }
            catch { /* the level is going away */ }
        }

        /// <summary>Pursuit.Enabled (or Police.Enabled) switched off mid-chase: cancel the action; the chase scores nothing more. Never throws.</summary>
        private void SwitchedOff()
        {
            bool chase = _inChase;
            _inChase = false;
            Close(false);
            if (chase || _pending > 0) Plugin.Log.LogInfo($"[Police] pursuit: cancelled (Pursuit switched off): {_pending:0} pending pts dropped");
            _pending = 0;
        }

        /// <summary>Plugin unload: cancel the action, remove the rows, free the icons. Never throws.</summary>
        internal void Destroy()
        {
            _inChase = false;
            Close(false);
            try { _rows.DestroyAll(GameApi.ResultsOk, GameApi.VictoryOk); } catch { /* shutting down */ }
            try { PursuitIcons.Destroy(); } catch { /* shutting down */ }
        }

        // ------------------------------------------------------------------ inside

        private void NewRace(IntPtr car, IntPtr spawner)
        {
            Close(false);   // normally closed already by the chase's end
            _inChase = false;
            _raceCar = car; _raceSpawner = spawner;
            _raceNo++;
            _escapes = 0; _chases = 0;
            GameApi.ForgetPursuit();   // re-check the score list (EnsurePursuit adopts our copy if it's still there)
            _nextNativeTry = 0f;
            // the first race of a new run (stage 0, race 0) starts the Victory screen's run total from zero; the read is
            // retried on later ticks (it is throttled and fails while the world manager isn't found yet)
            if (_runCheckPending) Plugin.Log.LogInfo("[Police] run position never read for the last race: pursuit run total kept");
            _runTotalBefore = _runTotal;
            _runCheckPending = GameApi.RunOk;
            if (_runCheckPending) CheckNewRun();
        }

        /// <summary>Reads the run position once for this race; on the first race of a new run takes the previous run's total off.</summary>
        private void CheckNewRun()
        {
            if (!GameApi.RunPosition(out int stage, out int race)) return;   // try again next tick
            _runCheckPending = false;
            if (stage != 0 || race != 0 || _runTotalBefore <= 0) return;
            // only what was there when this race began: this race's own points (if already counted) stay
            Plugin.Log.LogInfo($"[Police] new run: pursuit run total reset (previous run {_runTotalBefore:0})");
            _runTotal = Math.Max(0, _runTotal - _runTotalBefore);
            _runTotalBefore = 0;
        }

        private void Ensure()
        {
            if (!_iconsLoaded) { _iconsLoaded = true; PursuitIcons.Load(); }
            bool ok = GameApi.EnsurePursuit(PursuitIcons.Hud, PursuitIcons.Stat, Mathf.Clamp(Plugin.PursuitCoinReward.Value, 0, 1000), out string log);
            if (log != null && (!_nativeLogged || ok)) { Plugin.Log.LogInfo(log); _nativeLogged = ok; }
            if (ok) GameApi.PursuitCoinTarget(CoinTargetEscapes);
        }

        private void Begin()
        {
            GameApi.PursuitBegin(true);
            _activated = true;
            _open = GameApi.PursuitLiveRunning();
        }

        /// <summary>The game failed the combo and ended our action (it cleared the pending points): note it.</summary>
        private void Refresh()
        {
            if (_open && !GameApi.PursuitLiveRunning())
            {
                _open = false;
                _lostToCombo += _pending;
                _pending = 0;
            }
        }

        private void Step()
        {
            Refresh();
            double pts = _stepPoints;
            if (pts <= 0) return;
            if (!_open)
            {
                GameApi.PursuitBegin(!_activated);   // the game's activation once per chase, not again after a failed combo
                _activated = true;
                _open = GameApi.PursuitLiveRunning();
            }
            if (_open) { GameApi.PursuitAdd(pts); _pending += pts; }
        }

        /// <summary>Adds this race's PURSUIT score to the run total, once per race.</summary>
        private void CountRace()
        {
            if (_raceNo == _countedRace || !InLevel) return;
            _countedRace = _raceNo;
            _runTotal += GameApi.PursuitScoreNow();   // the game's own number for this race (card multipliers included)
        }

        private RogueShared.ModScoreRows.ResultsData ResultsData()
        {
            CountRace();
            if (!InLevel) return new RogueShared.ModScoreRows.ResultsData { Skip = true };   // no category this race: no row
            return new RogueShared.ModScoreRows.ResultsData
            {
                Skip = false,
                Amount = $"{_escapes}/{_chases}",   // escapes / chases this race
                Counts = true,
                Score = GameApi.PursuitScoreNow(),
                Coins = GameApi.PursuitCoins(),
                Icon = PursuitIcons.Stat,
            };
        }

        private RogueShared.ModScoreRows.VictoryData VictoryData()
        {
            CountRace();   // in case the Victory screen comes before the last race's results screen
            bool show = _runTotal > 0 || InLevel;
            bool record = _runTotal > 0 && _runTotal > Plugin.PursuitBestRunTotal.Value;
            if (record) Plugin.PursuitBestRunTotal.Value = Math.Round(_runTotal);   // saved to rogue.police.cfg
            return new RogueShared.ModScoreRows.VictoryData
            {
                Show = show,
                Why = show ? null : "no PURSUIT category this run (run total 0)",
                Value = Math.Round(_runTotal).ToString("N0", CultureInfo.InvariantCulture),
                NewRecord = record,
                Icon = PursuitIcons.Stat,
            };
        }

        private void Safe(Action body)
        {
            try { body(); }
            catch (Exception e) { Off("pursuit score", e); }
        }

        private void Guard(ref bool off, string feature, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                off = true;
                Plugin.Log.LogWarning($"[Police] {feature} switched off for this session after an error: {e.Message}");
                _fault?.Invoke(e);
            }
        }

        /// <summary>The score category's breaker: cancel the action, stop for the session, report to Runner's breaker.</summary>
        private void Off(string feature, Exception e)
        {
            if (_off) return;
            _off = true;
            _inChase = false;
            Close(false);
            Plugin.Log.LogWarning($"[Police] {feature} switched off for this session after an error (patrols and chases go on): {e.Message}");
            _fault?.Invoke(e);
        }
    }
}
