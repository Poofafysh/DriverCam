using System;
using System.Collections.Generic;

namespace RacingLine
{
    /// <summary>Every tuning number of the v2 rules (design doc "Racing Line v2 - Scoring Algorithm"). All in config.</summary>
    internal sealed class ScoreSettings
    {
        public float LineFull = 2.5f, LineZero = 8f, LineFloor = 0.25f;   // metres from the line: full credit / floor credit
        public float StraightnessGain = 1.3f;
        public float WSpeed = 0.45f, WGrip = 0.25f, WPedal = 0.30f;
        public float Base = 0.22f;                                        // points per metre at q = 1 (tidy race ~6-7k, normal ~2k)
        public float TickInterval = 0.5f, TickMinQ = 0.3f;                // combo ticks
        public float DriftFactor = 0.5f;                                  // running points while drifting
        public float TrafficGrace = 1.5f;                                 // seconds the position term is held after a near miss
        public float ExitWeight = 0.5f, CleanBonus = 1.15f, GripBonus = 2f;
        public float CoastPerSecond = 0.15f, CoastFloor = 0.6f;
        public float FullThrottle = 0.9f, CoastThrottle = 0.1f, CoastBrake = 0.05f;
        public float Gold = 0.8f, Silver = 0.6f, Bronze = 0.4f;
        public float StreakStep = 0.15f, StreakMax = 2f, StreakKeepQ = 0.45f, StreakBreakQ = 0.3f;
        public float PaceMin = 0.8f, PaceRange = 0.4f;
    }

    /// <summary>What one frame looks like to the scorer.</summary>
    internal struct ScoreInput
    {
        public bool Active;          // grounded, in control, race running
        public float Distance;       // along the centre line, metres
        public float Offset;         // sideways from the centre, metres, + = right
        public float Speed;          // m/s (the game's logical speed)
        public float Dt;             // seconds
        public float Throttle, Brake;
        public float CarCurvature;   // |1/m| of the car's own path
        public float Grip;           // the car's cornering limit estimate, m/s^2
        public bool Drifting, Hit, NearMiss;
    }

    internal sealed class CornerResult
    {
        public int Index;
        public char Type;
        public double Bonus;        // paid now, at the exit (running ticks were paid along the way)
        public double Total;        // everything this corner earned, ticks included
        public float MeanQ, Exit, SecondsToFullThrottle, CoastAfterApex;
        public bool Clean, Grip;
        public string Grade;        // GOLD / SILVER / BRONZE / null
        public float Units;         // coin action units
        public string Label => $"{(Grade ?? "corner")}{(Grip ? " GRIP LINE" : "")} q {MeanQ:0.00}";
    }

    internal struct StepResult { public double Tick; public CornerResult Corner; }

    /// <summary>
    /// Racing Line v2 rules, plain .NET (tested outside the game). Every frame in a corner zone gets a quality
    ///   q = Pos x (0.45 S + 0.25 U + 0.30 P)
    /// Pos = max(line closeness, path straightness); S = speed vs the reference profile; U = grip used;
    /// P = pedals (1 before the apex: brake straight / lift / light throttle are all fine, because braking while turning
    /// starts a drift in this game; after the apex 0.3 + 0.7 x throttle).
    /// Running points (Base x q per metre, halved while drifting) are paid as combo ticks every 0.5 s; at the zone exit a
    /// bonus pays the rest: x(1 + 0.5 x exit) x coasting x clean x grip line. A streak and the run's pace scale everything.
    /// Nothing is pass/fail.
    /// </summary>
    internal sealed class LineScorer
    {
        private readonly ScoreSettings _cfg;
        private readonly Line _line;
        private readonly List<Corner> _corners;
        private readonly float[] _lineCurvature;
        private float[] _vref;

        // run state
        private int _cur = -1, _nextCorner, _doneUpTo = -1, _streak;
        private float _lastDistance = float.NaN, _time, _paceSum, _paceDist;
        // corner state
        private double _running, _paid, _tickPending;
        private float _qDist, _dist, _tickQ, _tickT, _coast, _apexTime, _tFull, _lastSpeed, _graceUntil, _posHeld;
        private bool _apexSeen, _drifted, _hit;

        public double TotalPoints { get; private set; }
        public float TimeOnLine { get; private set; }
        public float Units { get; private set; }
        public int CornersDone { get; private set; }
        public int Gold { get; private set; }
        public int Silver { get; private set; }
        public int Bronze { get; private set; }
        public int GripCorners { get; private set; }
        public int CornerCount => _corners.Count;
        public float LastQ { get; private set; }
        public float LastS { get; private set; }
        public float LastP { get; private set; }
        public float LastPos { get; private set; }
        public float LastError { get; private set; }
        public float Pace => _paceDist > 1f ? _paceSum / _paceDist : 0.75f;
        public float StreakMultiplier => Math.Min(_cfg.StreakMax, 1f + _cfg.StreakStep * _streak);
        public float PaceMultiplier => _cfg.PaceMin + _cfg.PaceRange * Math.Max(0f, Math.Min(1f, Pace));
        public bool HasReference => _vref != null;
        /// <summary>Readout text, formatted only when read (the F5 readout); the numbers are kept from the last corner frame.</summary>
        public string State => !_stateInCorner ? "straight"
            : $"corner {_stateCorner}{_stateType}: q {LastQ:0.00} (pos {LastPos:0.00} speed {LastS:0.00} pedals {LastP:0.00}){(_stateDrifted ? " drifted" : "")}{(_stateHit ? " hit" : "")}";
        private bool _stateInCorner, _stateDrifted, _stateHit;
        private int _stateCorner;
        private char _stateType;

        /// <param name="carry">the scorer for the same race before the path grew: its totals carry over</param>
        public LineScorer(Line line, List<Corner> corners, float[] lineCurvature, ScoreSettings cfg, LineScorer carry = null)
        {
            _line = line; _corners = corners; _lineCurvature = lineCurvature; _cfg = cfg;
            if (carry != null)
            {
                TotalPoints = carry.TotalPoints; TimeOnLine = carry.TimeOnLine; Units = carry.Units; CornersDone = carry.CornersDone;
                Gold = carry.Gold; Silver = carry.Silver; Bronze = carry.Bronze; GripCorners = carry.GripCorners;
                _streak = carry._streak; _paceSum = carry._paceSum; _paceDist = carry._paceDist; _time = carry._time;
                _lastDistance = carry._lastDistance; _doneUpTo = carry._doneUpTo; _nextCorner = carry._nextCorner;
            }
        }

        /// <summary>Reference speeds per sample (SpeedProfile). Until set, S counts as 1.</summary>
        public void SetReference(float[] vref) { if (vref != null && vref.Length == _line.N) _vref = vref; }

        public float LineOffsetAt(float d)
        {
            float f = d / _line.Step;
            if (f <= 0f) return _line.E[0];
            int i = (int)f;
            if (i >= _line.N - 1) return _line.E[_line.N - 1];
            return _line.E[i] + (_line.E[i + 1] - _line.E[i]) * (f - i);
        }

        public StepResult Step(ScoreInput f)
        {
            var result = default(StepResult);
            if (f.Dt <= 0f || float.IsNaN(f.Distance) || float.IsNaN(f.Offset) || float.IsNaN(f.Speed)) return result;

            // respawn, reset or teleport: forget the corner in progress (Safety rule 10). Distance is tracked even while
            // inactive (airborne, not in control), so a long jump is not mistaken for a teleport; only active frames score.
            float travelled = float.IsNaN(_lastDistance) ? 0f : f.Distance - _lastDistance;
            _lastDistance = f.Distance;
            if (travelled < -5f || travelled > 50f) { Abandon(); Resync(f.Distance); return result; }
            if (!f.Active) return result;
            if (travelled < 0f) travelled = 0f;
            _time += f.Dt;

            int sample = Math.Max(0, Math.Min(_line.N - 1, (int)(f.Distance / _line.Step)));
            float vref = _vref != null ? Math.Max(1f, _vref[sample]) : Math.Max(1f, f.Speed);
            float ratio = Math.Min(1.1f, f.Speed / vref);
            _paceSum += ratio * travelled; _paceDist += travelled;

            if (_cur < 0)
            {
                while (_nextCorner < _corners.Count && _corners[_nextCorner].ZoneEnd < sample) _nextCorner++;   // skipped past
                if (_nextCorner < _corners.Count && sample >= _corners[_nextCorner].ZoneStart) Begin(_nextCorner);
            }
            if (_cur < 0) { _stateInCorner = false; return result; }

            var c = _corners[_cur];
            if (sample > c.ZoneEnd) { result.Corner = Finish(c); return result; }

            // ---- quality this frame
            float d = Math.Abs(f.Offset - LineOffsetAt(f.Distance));
            LastError = d;
            float L = d <= _cfg.LineFull ? 1f
                    : d >= _cfg.LineZero ? _cfg.LineFloor
                    : _cfg.LineFloor + (1f - _cfg.LineFloor) * 0.5f * (1f + (float)Math.Cos(Math.PI * (d - _cfg.LineFull) / (_cfg.LineZero - _cfg.LineFull)));
            float kLine = Math.Abs(_lineCurvature[sample]);
            float St = Clamp01(_cfg.StraightnessGain * kLine / Math.Max(f.CarCurvature, 1e-4f));
            float pos = Math.Max(L, St);
            if (f.NearMiss) { _graceUntil = _time + _cfg.TrafficGrace; }
            if (_time < _graceUntil) pos = Math.Max(pos, _posHeld); else _posHeld = pos;   // dodging traffic isn't punished

            float S = Math.Max(0f, ratio);
            float U = Clamp01((f.Speed * f.Speed * f.CarCurvature / Math.Max(1f, f.Grip) - 0.3f) / 0.55f);
            bool afterApex = sample >= c.Apex;
            float P = afterApex ? 0.3f + 0.7f * Clamp01(f.Throttle) : 1f;
            float q = Clamp01(pos * (_cfg.WSpeed * S + _cfg.WGrip * U + _cfg.WPedal * P));
            LastQ = q; LastS = S; LastP = P; LastPos = pos;

            if (f.Drifting) _drifted = true;
            if (f.Hit) _hit = true;
            if (afterApex)
            {
                if (!_apexSeen) { _apexSeen = true; _apexTime = _time; }
                if (float.IsNaN(_tFull) && f.Throttle >= _cfg.FullThrottle) _tFull = _time - _apexTime;
                if (f.Throttle < _cfg.CoastThrottle && f.Brake < _cfg.CoastBrake) _coast += f.Dt;   // coasting out of the apex wastes time
            }
            _lastSpeed = f.Speed;
            if (pos >= 0.75f) TimeOnLine += f.Dt;

            double pts = _cfg.Base * q * travelled * (f.Drifting ? _cfg.DriftFactor : 1f);
            _running += pts; _tickPending += pts;
            _qDist += q * travelled; _dist += travelled;
            _tickQ += q * f.Dt; _tickT += f.Dt;

            // ---- combo tick every TickInterval while the corner is driven well
            if (_tickT >= _cfg.TickInterval)
            {
                float windowQ = _tickQ / _tickT;
                if (windowQ >= _cfg.TickMinQ && _tickPending > 0)
                {
                    double tick = _tickPending * StreakMultiplier * PaceMultiplier;
                    _paid += _tickPending; _tickPending = 0;
                    TotalPoints += tick;
                    result.Tick = tick;
                }
                _tickQ = 0f; _tickT = 0f;
            }
            _stateInCorner = true; _stateCorner = _cur + 1; _stateType = c.Type; _stateDrifted = _drifted; _stateHit = _hit;   // State formats these on demand
            return result;
        }

        private void Begin(int index)
        {
            _cur = index;
            _running = 0; _paid = 0; _tickPending = 0;
            _qDist = 0; _dist = 0; _tickQ = 0; _tickT = 0; _coast = 0; _apexTime = 0; _tFull = float.NaN; _lastSpeed = 0;
            _apexSeen = false; _drifted = false; _hit = false;
        }

        private CornerResult Finish(Corner c)
        {
            var r = new CornerResult { Index = _cur, Type = c.Type };
            r.MeanQ = _dist > 0f ? _qDist / _dist : 0f;
            float vrefExit = _vref != null ? Math.Max(1f, _vref[c.ZoneEnd]) : Math.Max(1f, _lastSpeed);
            float throttlePart = float.IsNaN(_tFull) ? 0f : Clamp01(1f - (_tFull - 0.5f) / 2.5f);
            r.Exit = Math.Min(1.25f, (0.5f * throttlePart + 0.5f * Clamp01(_lastSpeed / vrefExit)) * c.ExitFactor);
            r.SecondsToFullThrottle = _tFull;
            r.CoastAfterApex = _coast;
            r.Clean = !_hit;
            r.Grip = !_drifted;
            float coast = Math.Max(_cfg.CoastFloor, 1f - _cfg.CoastPerSecond * _coast);
            double total = _running * (1f + _cfg.ExitWeight * r.Exit) * coast * (r.Clean ? _cfg.CleanBonus : 1f) * (r.Grip ? _cfg.GripBonus : 1f);
            double mult = StreakMultiplier * PaceMultiplier;
            r.Bonus = Math.Round(Math.Max(0, total - _paid) * mult);
            r.Total = Math.Round(total * mult);
            TotalPoints += r.Bonus;

            r.Grade = r.MeanQ >= _cfg.Gold ? "GOLD" : r.MeanQ >= _cfg.Silver ? "SILVER" : r.MeanQ >= _cfg.Bronze ? "BRONZE" : null;
            float units = r.Grade == "GOLD" ? 1f : r.Grade == "SILVER" ? 0.6f : r.Grade == "BRONZE" ? 0.3f : 0f;
            r.Units = units * (r.Grip && units > 0 ? 1.5f : 1f);
            Units += r.Units;
            if (r.Grade == "GOLD") Gold++; else if (r.Grade == "SILVER") Silver++; else if (r.Grade == "BRONZE") Bronze++;
            if (r.Grip && r.Grade != null) GripCorners++;

            // streak: we only know "a collision happened", not wall vs traffic, so any hit drops two steps
            if (_hit) _streak = Math.Max(0, _streak - 2);
            else if (r.MeanQ < _cfg.StreakBreakQ) _streak = 0;
            else if (r.MeanQ >= _cfg.StreakKeepQ && StreakMultiplier < _cfg.StreakMax) _streak++;

            CornersDone++;
            _doneUpTo = Math.Max(_doneUpTo, _cur);
            _nextCorner = _cur + 1;
            _cur = -1;
            _stateInCorner = false;
            return r;
        }

        private void Abandon() { _cur = -1; _running = 0; _tickPending = 0; }

        private void Resync(float distance)
        {
            int sample = (int)(distance / _line.Step);
            _nextCorner = _doneUpTo + 1;
            while (_nextCorner < _corners.Count && _corners[_nextCorner].ZoneEnd < sample) _nextCorner++;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
