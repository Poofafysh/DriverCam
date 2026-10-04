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
        public float QualityPower = 1.5f;                                 // live points use q^1.5: clean driving pulls further ahead
        public float LiveScale = 1.25f;                                   // keeps totals where v2's whole-corner exit bonus had them
        public float LiveMinQ = 0.3f, LiveGrace = 0.6f;                   // live counting needs this q; below it for LiveGrace s ends the action
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
        public double Total;        // everything this corner earned (all paid live, while it was driven)
        public float MeanQ, Exit, SecondsToFullThrottle, CoastAfterApex;
        public bool Clean, Grip;
        public bool TrafficShifted; // traffic moved (or blocked) the line somewhere in this corner
        public string Grade;        // GOLD / SILVER / BRONZE / null
        public float Units;         // coin action units
        public string Label => $"{(Grade ?? "corner")}{(Grip ? " GRIP LINE" : "")} q {MeanQ:0.00}";
    }

    /// <summary>
    /// One frame's outcome. Live = points earned this frame (already multiplied: streak, pace, grip, clean, exit, coasting),
    /// counted straight into the live total. Open = the live action should be running (inside a corner zone, driven
    /// well or within LiveGrace of it). Lost = a collision in this corner: its live points are forfeited (the action is
    /// cancelled, like the game's own categories on a crash). Corner = the zone just ended (grade, coins, streak; no points).
    /// </summary>
    internal struct StepResult { public double Live; public bool Open, Lost; public CornerResult Corner; }

    /// <summary>
    /// Racing Line v2 rules, plain .NET (tested outside the game). Every frame in a corner zone gets a quality
    ///   q = Pos x (0.45 S + 0.25 U + 0.30 P)
    /// Pos = max(line closeness, path straightness); S = speed vs the reference profile; U = grip used;
    /// P = pedals (1 before the apex: brake straight / lift / light throttle are all fine, because braking while turning
    /// starts a drift in this game; after the apex 0.3 + 0.7 x throttle).
    /// Points are LIVE, like the game's own categories: every frame with q >= LiveMinQ earns Base x 1.25 x q^1.5 per metre (halved
    /// while drifting), multiplied by everything that is true right now: streak x pace x Grip line (no drift yet in this
    /// corner) x Clean (no hit yet) x exit (after the apex: 1 + ExitWeight x throttle x speed ratio) x coasting. Nothing is
    /// paid at the end of a corner; the zone exit only grades it (coins, streak). A hit forfeits the corner's live points.
    /// With a TrafficLine attached, "the line" is the traffic-aware line e* (routed around NPC cars), and position counts as
    /// perfect where traffic leaves no free side; without one (or before its first snapshot) it is the global line E.
    /// Inside a detour the full-credit band doesn't reach towards the car: the space it occupies scores like being far off.
    /// </summary>
    internal sealed class LineScorer
    {
        private const float TrafficShiftCounts = 0.5f;   // metres e* must differ from E for a corner to count as traffic-shifted

        private readonly ScoreSettings _cfg;
        private readonly Line _line;
        private readonly List<Corner> _corners;
        private readonly float[] _lineCurvature;
        private float[] _vref;

        // run state
        private int _cur = -1, _nextCorner, _doneUpTo = -1, _streak;
        private float _lastDistance = float.NaN, _time, _paceSum, _paceDist;
        // corner state
        private double _cornerPts, _banked;   // _banked: this corner's points already handed to the combo (a LiveGrace gap ended an action)
        private float _qDist, _dist, _lowQ, _coast, _apexTime, _tFull, _lastSpeed, _graceUntil, _posHeld;
        private bool _apexSeen, _drifted, _hit, _trafficShifted;

        /// <summary>The traffic-aware line (set by the Runner); null = score against the global line only.</summary>
        public TrafficLine Traffic;

        public double TotalPoints { get; private set; }
        /// <summary>Corners in which traffic shifted (or blocked) the line under the player.</summary>
        public int TrafficCorners { get; private set; }
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
        /// <summary>Signed distance from the line last frame, metres (+ = you're right of it); NaN outside corners.</summary>
        public float LastSigned { get; private set; } = float.NaN;
        /// <summary>Live points earned in the corner being driven (0 between corners).</summary>
        public double CornerPoints => _cornerPts;
        public bool InCorner => _cur >= 0;
        /// <summary>Reference speed at a sample (m/s), NaN until the speed profile is known.</summary>
        public float RefSpeed(int sample) => _vref == null || sample < 0 || sample >= _vref.Length ? float.NaN : _vref[sample];
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
                Gold = carry.Gold; Silver = carry.Silver; Bronze = carry.Bronze; GripCorners = carry.GripCorners; TrafficCorners = carry.TrafficCorners;
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
            if (_cur < 0) { _stateInCorner = false; LastSigned = float.NaN; return result; }

            var c = _corners[_cur];
            if (sample > c.ZoneEnd) { result.Corner = Finish(c); return result; }

            // ---- quality this frame: distance from the traffic-aware line e* (E where no traffic is in the way)
            float target = LineOffsetAt(f.Distance);
            bool blocked = false;
            float carSide = 0f, clearance = 0f;
            var traffic = Traffic;
            if (traffic != null && traffic.IsFor(_line))
            {
                float dev = traffic.Deviation(f.Distance, out carSide, out clearance);
                target += dev;
                blocked = traffic.IsBlocked(f.Distance);   // no free side past the traffic: no line is possible here
                if (blocked || Math.Abs(dev) > TrafficShiftCounts) _trafficShifted = true;
            }
            float err = f.Offset - target;
            LastSigned = err;
            float d = Math.Abs(err);
            // towards the car from e*, the error climbs from LineFull (just clear) to LineZero (the car's centre line),
            // faded in with the detour: the space the car occupies is never "on the line", whatever LineFull is
            if (carSide != 0f && clearance > 0f && err * carSide > 0f)
            {
                float steep = _cfg.LineFull + d * (_cfg.LineZero - _cfg.LineFull) / clearance;
                if (steep > d) d += Math.Abs(carSide) * (steep - d);
            }
            LastError = d;
            float L = blocked || d <= _cfg.LineFull ? 1f
                    : d >= _cfg.LineZero ? _cfg.LineFloor
                    : _cfg.LineFloor + (1f - _cfg.LineFloor) * 0.5f * (1f + (float)Math.Cos(Math.PI * (d - _cfg.LineFull) / (_cfg.LineZero - _cfg.LineFull)));
            float kLine = Math.Abs(_lineCurvature[sample]);
            float St = Clamp01(_cfg.StraightnessGain * kLine / Math.Max(f.CarCurvature, 1e-4f));
            St *= 1f - Math.Abs(carSide);   // around traffic, a path as straight as E goes through the car: no credit for it
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
            if (f.Hit && !_hit)
            {
                _hit = true;
                TotalPoints -= _cornerPts - _banked;   // a crash cancels the live action: its points are lost (banked ones stay)
                _cornerPts = _banked;
                result.Lost = true;
            }
            if (afterApex)
            {
                if (!_apexSeen) { _apexSeen = true; _apexTime = _time; }
                if (float.IsNaN(_tFull) && f.Throttle >= _cfg.FullThrottle) _tFull = _time - _apexTime;
                if (f.Throttle < _cfg.CoastThrottle && f.Brake < _cfg.CoastBrake) _coast += f.Dt;   // coasting out of the apex wastes time
            }
            _lastSpeed = f.Speed;
            if (pos >= 0.75f) TimeOnLine += f.Dt;

            _qDist += q * travelled; _dist += travelled;

            // ---- live points: everything that is true right now multiplies this frame's points
            if (q >= _cfg.LiveMinQ)
            {
                _lowQ = 0f;
                float exit = afterApex ? 1f + _cfg.ExitWeight * Clamp01(f.Throttle) * Clamp01(f.Speed / vref) * c.ExitFactor : 1f;
                float coast = Math.Max(_cfg.CoastFloor, 1f - _cfg.CoastPerSecond * _coast);
                double mult = StreakMultiplier * PaceMultiplier * (_drifted ? 1f : _cfg.GripBonus) * (_hit ? 1f : _cfg.CleanBonus) * exit * coast;
                double pts = _cfg.Base * _cfg.LiveScale * Math.Pow(q, _cfg.QualityPower) * travelled * (f.Drifting ? _cfg.DriftFactor : 1f) * mult;
                if (pts > 0) { _cornerPts += pts; TotalPoints += pts; result.Live = pts; }
            }
            else _lowQ += f.Dt;
            result.Open = _lowQ < _cfg.LiveGrace;
            if (!result.Open) _banked = _cornerPts;   // the Runner ends (banks) the action now
            _stateInCorner = true; _stateCorner = _cur + 1; _stateType = c.Type; _stateDrifted = _drifted; _stateHit = _hit;   // State formats these on demand
            return result;
        }

        private void Begin(int index)
        {
            _cur = index;
            _cornerPts = 0; _banked = 0;
            _qDist = 0; _dist = 0; _lowQ = 0; _coast = 0; _apexTime = 0; _tFull = float.NaN; _lastSpeed = 0;
            _apexSeen = false; _drifted = false; _hit = false; _trafficShifted = false;
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
            r.TrafficShifted = _trafficShifted;
            if (_trafficShifted) TrafficCorners++;
            r.Total = Math.Round(_cornerPts);   // already paid, live

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

        private void Abandon() { _cur = -1; _cornerPts = 0; _banked = 0; }

        private void Resync(float distance)
        {
            int sample = (int)(distance / _line.Step);
            _nextCorner = _doneUpTo + 1;
            while (_nextCorner < _corners.Count && _corners[_nextCorner].ZoneEnd < sample) _nextCorner++;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
