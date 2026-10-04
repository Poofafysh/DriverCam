using System;

namespace RacingLine
{
    /// <summary>One NPC traffic car in the racing line's own frame (filled by GameApi.ReadTraffic).</summary>
    internal struct TrafficCar
    {
        public long Id;            // identity across snapshots (the car's native object)
        public float Road;         // centre, metres along the run's path (same scale as the player's distance)
        public float Lane;         // centre, metres from the centre line, + = right (same frame as Line.E)
        public float HalfWidth, HalfLength;
        public float Speed;        // m/s along the path: + = the player's direction, - = oncoming (reverse path)
    }

    /// <summary>Tuning for the traffic-aware line ([Traffic] in the config).</summary>
    internal sealed class TrafficSettings
    {
        public float Margin = 0.5f;            // extra gap between the player's car and a traffic car, metres
        public float PlayerHalfWidth = 1f;
        public float MinLeadIn = 15f, MaxLeadIn = 60f, LeadInSeconds = 1.2f;
        public float LeadOut = 15f;
        public float GapAlong = 2f;            // two cars this close (bumper to bumper) share a stretch of road
    }

    /// <summary>
    /// The traffic-aware racing line, plain .NET (tested outside the game). The global line E (Line.E) ignores traffic;
    /// if a traffic car sits on it, that stretch can't be the perfect line. Each snapshot (0.1 s), Rebuild:
    /// - a car BLOCKS the line where |lane - E(road)| &lt; car half-width + player half-width + margin, and only while the
    ///   player is catching it (closing speed &gt; 0.5 m/s; oncoming cars always close at player speed + their speed);
    /// - its passing offset is the closer of lane ± that clearance to E(road), inside ±Line.Limit and not taken by
    ///   another car alongside it (overlapping along the road). Neither side free = a blocked stretch (counts as perfect position);
    /// - e*(s) = E(s) + w(s) (pass - E(s)), w a cosine ramp from D_in = clamp(LeadInSeconds x closing, MinLeadIn,
    ///   MaxLeadIn) before the car, 1 along it, back to 0 over LeadOut after it;
    /// - cars alongside each other share one way round (a car adopts an alongside car's pass when it clears it too, and a
    ///   car with no free side of its own is freed by one), so the line never flips sides beside a group;
    /// - overlapping detours: the largest shift right plus the largest shift left at each s (same-way detours don't add
    ///   up; opposite ones cross over smoothly, never a jump).
    /// The scorer also gets the detour's car side and clearance (Deviation overload), so driving through the car's space
    /// is never "on the line" even though it may be within LineFull of e*.
    /// Detours are kept relative to their car, which moves on at its speed between snapshots (SetTime), so the line never
    /// lags the traffic by more than one frame. No allocations after construction.
    /// </summary>
    internal sealed class TrafficLine
    {
        public const int Capacity = 48;
        private const float ClosingMin = 0.5f;

        /// <summary>Snapshot buffer: GameApi.ReadTraffic fills Cars[0..CarCount).</summary>
        public readonly TrafficCar[] Cars = new TrafficCar[Capacity];
        public int CarCount;

        // detours (one per blocking car) as of the last snapshot
        private readonly float[] _c0 = new float[Capacity], _v = new float[Capacity], _h = new float[Capacity];
        private readonly float[] _pass = new float[Capacity], _in = new float[Capacity], _out = new float[Capacity];
        private readonly float[] _clear = new float[Capacity], _toCar = new float[Capacity];   // clearance; +1/-1 = the car is right/left of the pass
        private readonly float[] _lane = new float[Capacity];
        private readonly bool[] _blocked = new bool[Capacity];
        private readonly long[] _id = new long[Capacity];
        private int _n;
        private float _snapTime, _elapsed;

        // cars the player is passing around a detour: counted as a clean pass once behind the player with no hit since
        private readonly long[] _pendId = new long[Capacity];
        private readonly bool[] _pendHit = new bool[Capacity];
        private int _pendN;

        private Line _line;

        /// <summary>Cars the line currently routes around (as of the last snapshot).</summary>
        public int ShiftedCars { get; private set; }
        /// <summary>Cars currently blocking the line with no free side.</summary>
        public int BlockedCars { get; private set; }
        /// <summary>Detoured cars passed this race with no collision during the pass.</summary>
        public int CleanPasses { get; private set; }

        public bool IsFor(Line line) => line != null && ReferenceEquals(_line, line);

        /// <summary>A new line (new race, or the same race's longer path): detours are rebuilt on the next snapshot.</summary>
        public void Attach(Line line) { _line = line; Clear(); }

        /// <summary>A new race: counters start from zero.</summary>
        public void ResetRace() { CleanPasses = 0; _pendN = 0; Clear(); }

        /// <summary>No traffic information (feature off, no spawner): the line falls back to E everywhere.</summary>
        public void Clear() { _n = 0; CarCount = 0; ShiftedCars = 0; BlockedCars = 0; _elapsed = 0f; }

        /// <summary>The current game time, for moving the detours on with their cars between snapshots.</summary>
        public void SetTime(float now)
        {
            float e = now - _snapTime;
            _elapsed = e < 0f || float.IsNaN(e) ? 0f : e > 0.3f ? 0.3f : e;   // a stalled snapshot never extrapolates far
        }

        /// <summary>The global line's offset at distance s (linear between samples).</summary>
        public float BaseOffset(float s)
        {
            var l = _line;
            if (l == null || l.N == 0) return 0f;
            float f = s / l.Step;
            if (!(f > 0f)) return l.E[0];
            int i = (int)f;
            if (i >= l.N - 1) return l.E[l.N - 1];
            return l.E[i] + (l.E[i + 1] - l.E[i]) * (f - i);
        }

        /// <summary>Rebuilds the detours from Cars[0..CarCount) (call right after each snapshot).</summary>
        public void Rebuild(float now, float playerDist, float playerSpeed, TrafficSettings cfg)
        {
            _snapTime = now; _elapsed = 0f; _n = 0;
            int shifted = 0, blocked = 0;
            if (_line == null || cfg == null || float.IsNaN(playerDist) || float.IsNaN(playerSpeed)) { ShiftedCars = 0; BlockedCars = 0; return; }
            float limit = _line.Limit;
            for (int i = 0; i < CarCount && _n < Capacity; i++)
            {
                ref TrafficCar car = ref Cars[i];
                float e = BaseOffset(car.Road);
                float clear = car.HalfWidth + cfg.PlayerHalfWidth + cfg.Margin;
                if (Math.Abs(car.Lane - e) >= clear) continue;                 // not on the line
                float closing = playerSpeed - car.Speed;                         // oncoming: player speed + its speed
                if (!(closing > ClosingMin)) continue;                           // never reached (or pulling away / catching up from behind)

                float pass;
                bool free;
                int mate = AlongsideDetour(car.Road, car.HalfLength, car.Lane, clear, cfg);
                if (mate >= 0 && !Occupied(i, _pass[mate], cfg)) { pass = _pass[mate]; free = true; }   // one way round a group
                else
                {
                    float left = car.Lane - clear, right = car.Lane + clear;
                    bool leftOk = left >= -limit && !Occupied(i, left, cfg);
                    bool rightOk = right <= limit && !Occupied(i, right, cfg);
                    if (leftOk && rightOk)
                    {
                        float dl = Math.Abs(left - e), dr = Math.Abs(right - e);
                        pass = dl < dr ? left : dr < dl ? right : Math.Abs(left) <= Math.Abs(right) ? left : right;   // tie: nearer the centre
                    }
                    else pass = leftOk ? left : right;   // only used when one side is free
                    free = leftOk || rightOk;
                }

                int k = _n++;
                _id[k] = car.Id; _c0[k] = car.Road; _v[k] = car.Speed; _h[k] = car.HalfLength; _pass[k] = pass; _lane[k] = car.Lane;
                _clear[k] = clear; _toCar[k] = car.Lane >= pass ? 1f : -1f;
                float dIn = cfg.LeadInSeconds * closing;
                _in[k] = Math.Max(1f, dIn < cfg.MinLeadIn ? cfg.MinLeadIn : dIn > cfg.MaxLeadIn ? cfg.MaxLeadIn : dIn);
                _out[k] = Math.Max(1f, cfg.LeadOut);
                _blocked[k] = !free;
            }

            // a car with no free side of its own can still be passed on the far side of a car alongside it
            for (int k = 0; k < _n; k++)
            {
                if (!_blocked[k]) continue;
                int mate = AlongsideDetour(_c0[k], _h[k], _lane[k], _clear[k], cfg);
                if (mate < 0) continue;
                _pass[k] = _pass[mate]; _toCar[k] = _lane[k] >= _pass[k] ? 1f : -1f; _blocked[k] = false;
            }
            for (int k = 0; k < _n; k++) { if (_blocked[k]) blocked++; else shifted++; }
            ShiftedCars = shifted; BlockedCars = blocked;
            TrackPasses(playerDist);
        }

        /// <summary>A free detour of a car alongside (road, halfLength) whose pass also clears a car at lane, or -1.</summary>
        private int AlongsideDetour(float road, float halfLength, float lane, float clear, TrafficSettings cfg)
        {
            for (int k = 0; k < _n; k++)
            {
                if (_blocked[k] || Math.Abs(_c0[k] - road) >= _h[k] + halfLength + cfg.GapAlong) continue;
                if (Math.Abs(_pass[k] - lane) >= clear) return k;
            }
            return -1;
        }

        /// <summary>Is another car alongside car i taking the passing offset x?</summary>
        private bool Occupied(int i, float x, TrafficSettings cfg)
        {
            ref TrafficCar a = ref Cars[i];
            for (int j = 0; j < CarCount; j++)
            {
                if (j == i) continue;
                ref TrafficCar b = ref Cars[j];
                if (Math.Abs(b.Road - a.Road) >= a.HalfLength + b.HalfLength + cfg.GapAlong) continue;   // not alongside
                if (Math.Abs(b.Lane - x) < b.HalfWidth + cfg.PlayerHalfWidth + cfg.Margin) return true;
            }
            return false;
        }

        /// <summary>How far e* is from E at s (0 = no detour there). Positive = to the right.</summary>
        public float Deviation(float s) => Deviation(s, out _, out _);

        /// <summary>
        /// As Deviation(s), plus where the detouring car is: carSide = w x (+1 if the car is to the right of e*, -1 if left),
        /// 0 where no detour applies; clearance = the car-to-player centre gap the detour keeps. The scorer uses these so
        /// that the space the car occupies never counts as being on the line.
        /// </summary>
        public float Deviation(float s, out float carSide, out float clearance)
        {
            carSide = 0f; clearance = 0f;
            if (_n == 0) return 0f;
            // the largest shift right plus the largest shift left: detours the same way don't add up, opposite ones
            // (a slalom) cross over smoothly instead of jumping from one side to the other
            float e = BaseOffset(s), right = 0f, left = 0f, best = -1f;
            for (int k = 0; k < _n; k++)
            {
                if (_blocked[k]) continue;
                float w = Weight(k, s);
                if (w <= 0f) continue;
                float dev = w * (_pass[k] - e);
                if (dev > right) right = dev; else if (dev < left) left = dev;
                if (Math.Abs(dev) > best) { best = Math.Abs(dev); carSide = w * _toCar[k]; clearance = _clear[k]; }
            }
            return right + left;
        }

        /// <summary>The line the player is scored against at s: E routed around traffic.</summary>
        public float EffectiveOffset(float s) => BaseOffset(s) + Deviation(s);

        /// <summary>True where traffic leaves no free side to pass: no line is possible there, so position counts as perfect.</summary>
        public bool IsBlocked(float s)
        {
            for (int k = 0; k < _n; k++)
            {
                if (!_blocked[k]) continue;
                float c = _c0[k] + _v[k] * _elapsed;
                if (s >= c - _h[k] - _in[k] && s <= c + _h[k] + _out[k]) return true;
            }
            return false;
        }

        /// <summary>A collision happened: passes in progress are no longer clean.</summary>
        public void NoteHit()
        {
            for (int p = 0; p < _pendN; p++) _pendHit[p] = true;
        }

        private float Weight(int k, float s)
        {
            float c = _c0[k] + _v[k] * _elapsed;
            float front = c - _h[k], back = c + _h[k];
            float dIn = _in[k], dOut = _out[k];
            if (s <= front - dIn || s >= back + dOut) return 0f;
            if (s < front) return 0.5f - 0.5f * (float)Math.Cos(Math.PI * (s - (front - dIn)) / dIn);
            if (s <= back) return 1f;
            return 0.5f + 0.5f * (float)Math.Cos(Math.PI * (s - back) / dOut);
        }

        private void TrackPasses(float playerDist)
        {
            // start tracking a car once the player is on its detour (ramp in or alongside)
            for (int k = 0; k < _n; k++)
            {
                if (_blocked[k] || playerDist < _c0[k] - _h[k] - _in[k] || playerDist > _c0[k] + _h[k]) continue;
                bool known = false;
                for (int p = 0; p < _pendN; p++) if (_pendId[p] == _id[k]) { known = true; break; }
                if (!known && _pendN < Capacity) { _pendId[_pendN] = _id[k]; _pendHit[_pendN] = false; _pendN++; }
            }
            // a tracked car now behind the player is passed; one no longer in the snapshot is dropped
            for (int p = _pendN - 1; p >= 0; p--)
            {
                int i = -1;
                for (int j = 0; j < CarCount; j++) if (Cars[j].Id == _pendId[p]) { i = j; break; }
                if (i >= 0 && playerDist <= Cars[i].Road + Cars[i].HalfLength) continue;   // still beside or ahead
                if (i >= 0 && !_pendHit[p]) CleanPasses++;
                _pendN--;
                _pendId[p] = _pendId[_pendN]; _pendHit[p] = _pendHit[_pendN];
            }
        }
    }
}
