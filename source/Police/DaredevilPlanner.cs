using System;
using UnityEngine;

namespace Police
{
    /// <summary>What a rival knows this frame (filled by Daredevils.Steer).</summary>
    internal struct PlanInput
    {
        public IntPtr Self;
        public float S, V, Offset, HalfW, HalfL;   // road distance, speed (m/s), lateral offset (+ = right), half size
        public float LineTarget;                    // where the racing line (with wander / defence) wants it
        public float PrevTarget;                    // last frame's plan (hysteresis), NaN at first
        public float Limit, Rate;                   // how far either side it may go; sideways speed (m/s)
        public float Vmax;                          // its own pace here (profile, top speed, pace factor)
        public float SinceSnap;                     // seconds since the traffic snapshot (cars move on at their speed)
        public bool YouValid;
        public float YouRoad, YouLane, YouLo, YouHi, YouSpeed;   // you: road distance, lane, band you may occupy soon, road speed
        /// <summary>
        /// 0.8.0 multiplayer: every other player, each kept to the same never-hit rules as you (null / 0 in single-player,
        /// so the plan is exactly the single-player one).
        /// </summary>
        public PlanOther[] Others;
        public int OthersN;
    }

    /// <summary>
    /// Another player the plan must never hit (0.8.0). A remote player's numbers are honest estimates: Road is its synced
    /// distance moved on by its speed for the time since the update and half a round trip; ViewLag is how far behind
    /// the host's real cars that player's screen shows them (half a round trip + the interpolation buffer), so its
    /// effective place against this car is Road + this car's speed x ViewLag. Margin widens its band sideways, ExtraGap
    /// lengthens every gap to it. All zero = exactly the rules for you.
    /// </summary>
    internal struct PlanOther
    {
        public float Road, Lo, Hi, Speed;   // road distance, band it may occupy soon (m, + = right), road speed (m/s)
        public float ViewLag, Margin, ExtraGap;
    }

    internal struct PlanOutput
    {
        public float Target;      // lateral offset to steer to
        public float Cap;         // speed it can carry (m/s): its pace, or less for a car it can't get round in time
        public float Instant;     // running speed to cut to at once (too close), +inf if none
        public bool Tow;          // lined up close behind a car: slipstream
        public bool Evading;      // inside your band (you moved over): get out faster
    }

    /// <summary>
    /// The rivals' traffic planner (doc "Daredevil Rival AI", follow-up from the 2026-10-04 playtest: rivals averaged
    /// 88 km/h against your 138 near you because they braked for every car in their lane, even ones they were already
    /// swerving round, and planned round one car at a time in dense traffic). Plain maths on the snapshot, no game calls.
    ///
    /// Each frame, per rival:
    /// - Obstacles: every snapshot car near its road position (moved on at its own speed since the snapshot), plus you.
    ///   Each car occupies a sideways band (LaneLo..LaneHi: where it is and where it's heading) widened by both half widths
    ///   and a margin (0.9 m for traffic, 1.6 m for you). Its braking envelope = the fastest the rival may go and still
    ///   slow to that car's speed before a safe gap (5 m + 0.2 s behind traffic, 8 m + 0.35 s behind you), allowing for
    ///   the game reaching a new speed about Smoothness s late on the closing speed.
    /// - Gap finding: candidate offsets every 0.5 m across the road (plus the line's own). A candidate carries the lowest
    ///   envelope of the cars whose band covers it (its "free speed"). A candidate is out if it is inside the band of a car
    ///   alongside, if getting there means crossing a car's band within its safe gap (until that car is fully past), or
    ///   (with you alongside or close) being on your far side or inside your band. The
    ///   pick maximises free speed, then stays close to the racing line, then moves least, with a little hysteresis so it
    ///   doesn't flip between equal gaps.
    /// - Braking: the free speed of the chosen offset, plus the envelope of any car covering its current offset. A traffic
    ///   car it will swerve clear of before reaching that car's safe gap (at its full pace, + 0.15 s) no longer brakes it;
    ///   you always keep the full envelope. The look-ahead is never shorter than the distance needed to stop for a
    ///   standing car. Inside a car's safe gap and closing, the running speed is cut at once (unchanged).
    /// </summary>
    internal static class DaredevilPlanner
    {
        private const float GapMarginTraffic = 0.9f, GapMarginYou = 1.6f;
        private const float SafeGapTraffic = 5f, SafeTimeTraffic = 0.2f, SafeGapYou = 8f, SafeTimeYou = 0.35f;
        private const float YouClearAhead = 12f, YouClearSeconds = 1.5f;
        private const float Braking = 10f, Smoothness = 0.5f;
        private const float Step = 0.5f, ClearSlack = 0.15f;
        private const float TowGap = 30f, TowLane = 1.5f;
        // 0.7.0 (review 2026-10-04: 81% of rivals crashed): the side margin to traffic grows with the closing speed
        // (0.9 m standing, +0.02 m per m/s closing, at most +1 m), and a car coming up from behind within RearSeconds is
        // a hard limit like a car alongside (the planner used to look ahead only)
        private const float MarginPerClosing = 0.02f, MarginClosingMax = 1f, RearSeconds = 1.5f, RearRange = 40f;

        private struct Ob
        {
            public float Gap, Lo, Hi, Speed, Closing, Allowed, Safe;
            public bool You, Ahead;   // Ahead: not yet past its centre (its braking envelope still matters)
            public bool Beside;       // not yet fully past it: its band is a hard limit for gap finding
        }

        private static readonly Ob[] s_obs = new Ob[160];

        public static PlanOutput Plan(in PlanInput p, RoadCar[] road, int n)
        {
            var o = new PlanOutput { Target = p.Offset, Cap = p.Vmax, Instant = float.PositiveInfinity };
            // never shorter than the distance needed to stop for a standing car behind the safe gap
            float look = Mathf.Max(Mathf.Max(60f, p.V * 3.5f), p.V * p.V / (2f * Braking) + SafeGapYou + (SafeTimeYou + Smoothness) * p.V);
            int m = 0;
            int gn = 0;   // guards: you (first) and every other player alongside or about to be (0.8.0)

            // ---- obstacles
            if (p.YouValid)
            {
                float gap = (p.YouRoad - 2.4f) - (p.S + p.HalfL);
                float past = 2f * 2.4f + 2f * p.HalfL + YouClearAhead + YouClearSeconds * Mathf.Max(0f, p.YouSpeed - p.V);
                if (gap >= -past && gap <= look)
                {
                    float clear = 1.0f + p.HalfW + GapMarginYou;
                    Add(ref m, gap, p.YouLo - clear, p.YouHi + clear, p.YouSpeed, p, true, 2.4f);
                    if (gap < 6f) { s_gLo[gn] = p.YouLo - clear; s_gHi[gn] = p.YouHi + clear; gn++; }   // alongside or about to be
                }
            }
            // 0.8.0: every other player gets the same rules (with their latency margins)
            for (int q = 0; p.Others != null && q < p.OthersN && q < p.Others.Length && m < s_obs.Length; q++)
            {
                var ot = p.Others[q];
                float otherRoad = ot.Road + p.V * ot.ViewLag;
                float gap = (otherRoad - 2.4f) - (p.S + p.HalfL);
                float past = 2f * 2.4f + 2f * p.HalfL + YouClearAhead + YouClearSeconds * Mathf.Max(0f, ot.Speed - p.V) + ot.ExtraGap;
                if (gap >= -past && gap <= look + ot.ExtraGap)
                {
                    float clear = 1.0f + p.HalfW + GapMarginYou + ot.Margin;
                    Add(ref m, gap, ot.Lo - clear, ot.Hi + clear, ot.Speed, p, true, 2.4f, false, ot.ExtraGap);
                    if (gap < 6f + ot.ExtraGap && gn < s_gLo.Length) { s_gLo[gn] = ot.Lo - clear; s_gHi[gn] = ot.Hi + clear; gn++; }
                }
            }
            bool guard = gn > 0;
            for (int j = 0; j < n && m < s_obs.Length; j++)
            {
                var c = road[j];
                if (c.Ptr == p.Self) continue;
                float carRoad = c.Road + c.Speed * p.SinceSnap;
                float gap = (carRoad - c.HalfLength) - (p.S + p.HalfL);
                float lo = Mathf.Min(c.LaneLo, c.Lane), hi = Mathf.Max(c.LaneHi, c.Lane);
                if (gap < -(2f * c.HalfLength + 2f * p.HalfL))
                {
                    // behind us: only a car closing in fast enough to matter, as a hard limit (never move into its band)
                    float behind = -(gap + 2f * c.HalfLength + 2f * p.HalfL), closingUp = c.Speed - p.V;
                    if (behind > RearRange || closingUp <= 0.5f || behind / closingUp > RearSeconds) continue;
                    float rclear = c.HalfWidth + p.HalfW + GapMarginTraffic;
                    Add(ref m, gap, lo - rclear, hi + rclear, c.Speed, p, false, c.HalfLength, true);
                    continue;
                }
                if (gap > look) continue;   // too far to matter
                float clear = c.HalfWidth + p.HalfW + GapMarginTraffic + Mathf.Min(MarginClosingMax, MarginPerClosing * Mathf.Max(0f, p.V - c.Speed));
                Add(ref m, gap, lo - clear, hi + clear, c.Speed, p, false, c.HalfLength);
            }

            // ---- gap finding
            float lim = p.Limit + (guard ? 0.5f : 0f);   // an escape from you may use the road's last half metre
            float bestCost = float.PositiveInfinity, best = float.NaN, bestFree = p.Vmax;
            int steps = Mathf.Max(1, Mathf.FloorToInt(2f * lim / Step));
            for (int k = -2; k <= steps; k++)
            {
                float x = k == -2 ? Mathf.Clamp(p.LineTarget, -lim, lim) : k == -1 ? Mathf.Clamp(p.Offset, -lim, lim) : -lim + k * Step;
                if (guard && Guarded(x, p.Offset, gn)) continue;   // never across you (or another player), never inside a band
                float free = p.Vmax;
                bool ok = true;
                for (int i = 0; i < m && ok; i++)
                {
                    ref var b = ref s_obs[i];
                    if (!b.Beside) continue;
                    bool inBand = x > b.Lo && x < b.Hi;
                    bool coversNow = p.Offset > b.Lo && p.Offset < b.Hi;
                    if (inBand)
                    {
                        if (b.Gap < 0f && !coversNow) { ok = false; continue; }   // alongside it: never move into it
                        // alongside it and already inside its band (it moved over, or we were squeezed): only a way OUT is
                        // allowed, never deeper toward its middle (0.7.1, from the crash forensics: rivals and chasers swung
                        // into a car beside them, side clearance below zero, because any spot inside the band passed)
                        if (b.Gap < 0f && coversNow)
                        {
                            float midB = (b.Lo + b.Hi) * 0.5f;
                            if (Math.Abs(x - midB) < Math.Abs(p.Offset - midB) - 0.05f) { ok = false; continue; }
                        }
                        if (b.Ahead && b.Allowed < free) free = b.Allowed;
                        continue;
                    }
                    // getting from here to x crosses this car's band: only well before we reach it (its safe gap)
                    float a = Mathf.Min(p.Offset, x), z = Mathf.Max(p.Offset, x);
                    if (z <= b.Lo || a >= b.Hi) continue;
                    if (coversNow) continue;   // already in its band: moving out of it is the point
                    float tOut = (x > p.Offset ? b.Hi - p.Offset : p.Offset - b.Lo) / Mathf.Max(0.5f, p.Rate);
                    if (b.Gap - Mathf.Max(0f, Mathf.Max(b.Closing, p.Vmax - b.Speed)) * tOut < b.Safe) ok = false;
                }
                if (!ok) continue;
                float cost = -free + 0.6f * Mathf.Abs(x - p.LineTarget) + 0.25f * Mathf.Abs(x - p.Offset)
                           + (!float.IsNaN(p.PrevTarget) && Mathf.Abs(x - p.PrevTarget) > 1.5f ? 0.8f : 0f);
                if (cost < bestCost) { bestCost = cost; best = x; bestFree = free; }
            }
            if (!float.IsNaN(best)) { o.Target = best; o.Cap = Mathf.Min(o.Cap, bestFree); }
            else if (guard)
            {
                // boxed in: at least keep off you (and every other player alongside), each pushing it out on its own side
                float t = p.Offset;
                for (int g = 0; g < gn; g++)
                {
                    float gm = (s_gLo[g] + s_gHi[g]) * 0.5f;
                    t = p.Offset <= gm ? Mathf.Min(t, s_gLo[g]) : Mathf.Max(t, s_gHi[g]);
                }
                o.Target = Mathf.Clamp(t, -lim, lim);
            }

            // ---- braking for what covers us now and won't be cleared in time; instant cut; slipstream
            for (int i = 0; i < m; i++)
            {
                ref var b = ref s_obs[i];
                if (!b.Ahead) continue;
                bool covers = p.Offset > b.Lo && p.Offset < b.Hi;
                if (covers)
                {
                    // traffic only (you always keep the full envelope): out of its band before reaching its safe gap,
                    // counting on the rival speeding up to its full pace once the cap lifts
                    bool clearsInTime = false;
                    if (!b.You && !(o.Target > b.Lo && o.Target < b.Hi) && b.Gap > b.Safe)
                    {
                        float tLat = (o.Target > p.Offset ? b.Hi - p.Offset : p.Offset - b.Lo) / Mathf.Max(0.5f, p.Rate);
                        float closeFast = Mathf.Max(b.Closing, p.Vmax - b.Speed);
                        float tReach = closeFast > 0.1f ? (b.Gap - b.Safe) / closeFast : float.PositiveInfinity;
                        clearsInTime = tLat + ClearSlack < tReach;
                    }
                    if (!clearsInTime && b.Allowed < o.Cap) o.Cap = b.Allowed;
                    if (b.Gap < b.Safe && b.Closing > 0f)
                        o.Instant = Mathf.Min(o.Instant, Mathf.Max(0f, b.Speed - (b.Gap < b.Safe * 0.5f ? 2f : 0.5f)));
                }
                float mid2 = (b.Lo + b.Hi) * 0.5f;
                if (b.Gap > 0f && b.Gap < TowGap && b.Speed > 15f && Mathf.Abs(p.Offset - mid2) < TowLane) o.Tow = true;
            }
            bool evading = false;
            for (int g = 0; g < gn && !evading; g++) evading = p.Offset > s_gLo[g] && p.Offset < s_gHi[g];
            o.Evading = evading;
            return o;
        }

        private static readonly float[] s_gLo = new float[6], s_gHi = new float[6];

        /// <summary>x is out for a guard: on the far side of that player from where the car is now, or inside its band.</summary>
        private static bool Guarded(float x, float offset, int gn)
        {
            for (int g = 0; g < gn; g++)
            {
                float gm = (s_gLo[g] + s_gHi[g]) * 0.5f;
                if (offset <= gm ? x > s_gLo[g] : x < s_gHi[g]) return true;
            }
            return false;
        }

        private static void Add(ref int m, float gap, float lo, float hi, float speed, in PlanInput p, bool you, float halfLength, bool rear = false, float extraSafe = 0f)
        {
            float closing = p.V - speed;
            float safe = (you ? SafeGapYou + SafeTimeYou * p.V : SafeGapTraffic + SafeTimeTraffic * p.V) + extraSafe;
            float room = gap - safe - Mathf.Max(0f, closing) * Smoothness;
            float slowTo = Mathf.Max(0f, speed);
            s_obs[m++] = new Ob
            {
                Gap = gap, Lo = lo, Hi = hi, Speed = speed, Closing = closing, Safe = safe, You = you,
                Allowed = room > 0f ? Mathf.Sqrt(slowTo * slowTo + 2f * Braking * room) : Mathf.Max(0f, slowTo - 1f),
                Ahead = gap > -(halfLength + p.HalfL),
                Beside = rear || gap > -(2f * halfLength + 2f * p.HalfL),
            };
        }
    }
}
