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
        public float PrevAge;                       // 0.9.0: seconds since that gap was chosen (commit time)
        public float Limit, Rate;                   // how far either side it may go; sideways speed (m/s)
        public float LatAccel;                      // 0.9.0: sideways acceleration it may use (m/s^2; 0 = instant, the old timing)
        public float LatJerk;                       // 0.9.0: ... and how fast that acceleration builds (m/s^3; 0 = at once)
        public float LatVel;                        // 0.9.0: its sideways speed now (m/s, + = right)
        public float Vmax;                          // its own pace here (profile, top speed, pace factor)
        public float SinceSnap;                     // seconds since the traffic snapshot (cars move on at their speed)
        public bool YouValid;
        public float YouRoad, YouLane, YouLo, YouHi, YouSpeed;   // you: road distance, lane, band you may occupy soon, road speed
        public float YouLaneVel;                    // 0.9.0: your sideways speed (m/s, + = right; capped by the caller)
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
        // 0.9.0 (replaces the instant cut): the hardest braking a car in its way needs to stop closing before contact
        // (1.5 m behind traffic, 3 m behind a player), with that car's room (m), closing speed and speed; 0 = none
        public float NeedDecel, NeedRoom, NeedClosing, NeedSpeed;
        public bool NeedEscapes;  // ... but it is swerving clear of that car in time (no cut, no slowing to its speed)
        public bool NeedAlongside;// ... that car is alongside (no cut: braking only drops it back)
        public bool NewGap;       // the target is a different gap from last frame's (more than CommitMetres away)
        public bool Committed;    // kept last frame's gap (commit time) though another scored a little better
        public bool Tow;          // lined up close behind a car: slipstream
        public bool Evading;      // inside your band (you moved over): get out faster
        public bool Threat;       // 0.9.0: a player alongside / close and within ThreatMargin of its band: full evade limits at once
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
    ///   doesn't flip between equal gaps. 0.9.0: a chosen gap is kept for at least CommitSeconds (0.8 s) unless it
    ///   becomes unsafe (a hard limit rules it out) or another is more than CommitSlack (3 m/s) faster; a move of more
    ///   than 0.5 m costs 0.8 (was: only moves over 1.5 m); the sideways timing counts the car's sideways acceleration
    ///   and the game's 0.1 s position smoothing (LatTime), not an instant sideways speed.
    /// - Braking: the free speed of the chosen offset, plus the envelope of any car covering its current offset. A traffic
    ///   car it will swerve clear of before reaching that car's safe gap (at its full pace, + 0.15 s) no longer brakes it;
    ///   you always keep the full envelope. The look-ahead is never shorter than the distance needed to stop for a
    ///   standing car. 0.9.0: no instant cut any more; for a car covering its offset that it is closing on (inside its
    ///   safe gap, or not cleared in time) the plan reports the deceleration needed to stop closing before contact
    ///   (NeedDecel): the caller brakes smoothly up to DriveFeel.BrakeHard and cuts only when that can't be enough.
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
        // 0.9.0
        private const float CommitSeconds = 0.8f, CommitMetres = 0.5f, CommitSlack = 3f, MovePenalty = 0.8f;
        private const float LatLag = 0.1f;                          // s: the game's 0.1 s SmoothDamp of the car's position
        private const float ContactTraffic = 1.5f, ContactYou = 3f; // m: the room left when the needed braking is worked out
        private const float EscapeBrake = 9f;                       // m/s^2: the most braking asked for while swerving clear in time
        private const float AlongsideBrake = 8f;                    // m/s^2: braking to drop back from a car alongside in its band
        private const float CoverSlack = 0.15f;                     // m: braking hysteresis at a traffic car's band edge
        private const float PinnedDrop = 8f, PinnedBrake = 10f;     // squeezed by a player: drop to their speed - 8 m/s, braking at 10 m/s^2
        // 0.9.0 contacts fix (toward players only, safety beats smoothness)
        private const float PinnedMargin = 2.5f;                    // m: a player's band counts this much wider for "pinned"
        private const float ThreatMargin = 2f;                      // m: this close to a player's band = full evade limits
        private const float ArriveMin = 0.7f, ArriveMax = 1.5f;     // s: how far ahead a closing player's sideways speed is followed
        private const float CrossFactor = 1.5f, CrossSlack = 0.5f;  // crossing ahead of a player behind: this much time to spare
        private const float TailRange = 15f;                        // m nose to tail: a player this close behind is "on its tail"

        private struct Ob
        {
            public float Gap, Lo, Hi, Speed, Closing, Allowed, Safe;
            public bool You, Ahead;   // Ahead: not yet past its centre (its braking envelope still matters)
            public bool Beside;       // not yet fully past it: its band is a hard limit for gap finding
        }

        private static readonly Ob[] s_obs = new Ob[160];

        public static PlanOutput Plan(in PlanInput p, RoadCar[] road, int n)
        {
            var o = new PlanOutput { Target = p.Offset, Cap = p.Vmax };
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
                    // 0.9.0: you behind and closing: your band also covers where your sideways speed takes you by the time
                    // you get here (0.7-1.5 s), so it isn't chased out to the road edge in front of you
                    float behind = (p.S - p.HalfL) - (p.YouRoad + 2.4f), arrive = Arrive(behind, p.YouSpeed - p.V);
                    float ylo = p.YouLo, yhi = p.YouHi;
                    if (behind > 0f && p.YouSpeed > p.V)
                    {
                        float ext = p.YouLane + p.YouLaneVel * Mathf.Clamp(arrive, ArriveMin, ArriveMax);
                        ylo = Mathf.Min(ylo, ext); yhi = Mathf.Max(yhi, ext);
                    }
                    Add(ref m, gap, ylo - clear, yhi + clear, p.YouSpeed, p, true, 2.4f);
                    if (gap < 6f) { s_gLo[gn] = ylo - clear; s_gHi[gn] = yhi + clear; s_gRoad[gn] = p.YouRoad; s_gSpeed[gn] = p.YouSpeed; s_gBehind[gn] = behind; s_gArrive[gn] = arrive; gn++; }   // alongside or about to be
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
                    float obehind = (p.S - p.HalfL) - (otherRoad + 2.4f) - ot.ExtraGap;
                    if (gap < 6f + ot.ExtraGap && gn < s_gLo.Length) { s_gLo[gn] = ot.Lo - clear; s_gHi[gn] = ot.Hi + clear; s_gRoad[gn] = otherRoad; s_gSpeed[gn] = ot.Speed; s_gBehind[gn] = obehind; s_gArrive[gn] = Arrive(obehind, ot.Speed - p.V); gn++; }
                }
            }
            bool guard = gn > 0;
            // 0.9.0: a player close on its tail (or level): inside a car's safe gap it matches that car's speed instead of
            // dropping 1 m/s below it (the player following the same car would creep into it)
            s_tail = false;
            for (int g = 0; g < gn; g++) if (s_gBehind[g] > -2.4f - p.HalfL && s_gBehind[g] < TailRange) s_tail = true;
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
            bool hasPrev = !float.IsNaN(p.PrevTarget);
            float nearCost = float.PositiveInfinity, near = float.NaN, nearFree = 0f;   // the best spot within CommitMetres of last frame's
            int steps = Mathf.Max(1, Mathf.FloorToInt(2f * lim / Step));
            for (int k = hasPrev ? -3 : -2; k <= steps; k++)
            {
                float x = k == -3 ? Mathf.Clamp(p.PrevTarget, -lim, lim) : k == -2 ? Mathf.Clamp(p.LineTarget, -lim, lim) : k == -1 ? Mathf.Clamp(p.Offset, -lim, lim) : -lim + k * Step;
                if (guard && Guarded(x, p, gn)) continue;   // never inside a player's band; across one only well ahead of them
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
                    float tOut = LatTime(x > p.Offset ? b.Hi - p.Offset : p.Offset - b.Lo, x > p.Offset ? 1f : -1f, p);
                    if (b.Gap - Mathf.Max(0f, Mathf.Max(b.Closing, p.Vmax - b.Speed)) * tOut < b.Safe) ok = false;
                }
                if (!ok) continue;
                bool nearPrev = hasPrev && Mathf.Abs(x - p.PrevTarget) <= CommitMetres;
                float cost = -free + 0.6f * Mathf.Abs(x - p.LineTarget) + 0.25f * Mathf.Abs(x - p.Offset)
                           + (hasPrev && !nearPrev ? MovePenalty : 0f);
                if (cost < bestCost) { bestCost = cost; best = x; bestFree = free; }
                if (nearPrev && cost < nearCost) { nearCost = cost; near = x; nearFree = free; }
            }
            // commit: last frame's gap stays for CommitSeconds while it is still allowed (every hard limit passed) and
            // nearly as fast as the best
            if (!float.IsNaN(near) && !float.IsNaN(best) && p.PrevAge < CommitSeconds && nearFree >= bestFree - CommitSlack
                && Mathf.Abs(best - near) > 1e-3f)
            { best = near; bestFree = nearFree; o.Committed = true; }
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

            // ---- braking for what covers us now and won't be cleared in time; the braking it needs; slipstream
            for (int i = 0; i < m; i++)
            {
                ref var b = ref s_obs[i];
                if (!b.Ahead) continue;
                // 0.9.0: braking counts a car only once the offset is CoverSlack inside its band: the band's edge moves with
                // the closing speed, so a car sitting on the edge used to brake, slow, uncover, speed up and brake again
                float slack = b.You ? 0f : CoverSlack;
                bool covers = p.Offset > b.Lo + slack && p.Offset < b.Hi - slack;
                if (covers)
                {
                    // traffic only (you always keep the full envelope): out of its band before reaching its safe gap,
                    // counting on the rival speeding up to its full pace once the cap lifts
                    bool clearsInTime = false;
                    if (!b.You && !(o.Target > b.Lo && o.Target < b.Hi) && b.Gap > b.Safe)
                    {
                        float tLat = LatTime(o.Target > p.Offset ? b.Hi - p.Offset : p.Offset - b.Lo, o.Target > p.Offset ? 1f : -1f, p);
                        float closeFast = Mathf.Max(b.Closing, p.Vmax - b.Speed);
                        float tReach = closeFast > 0.1f ? (b.Gap - b.Safe) / closeFast : float.PositiveInfinity;
                        clearsInTime = tLat + ClearSlack < tReach;
                    }
                    if (!clearsInTime && b.Allowed < o.Cap) o.Cap = b.Allowed;
                    if (b.Closing > 0f && (b.Gap < b.Safe || !clearsInTime))
                    {
                        float room = b.Gap - (b.You ? ContactYou : ContactTraffic);
                        float need = room > 0.05f ? b.Closing * b.Closing / (2f * room) : b.Closing > 1f ? 1000f : AlongsideBrake;   // at the contact distance, barely closing: firm braking
                        // a traffic car it is already swerving clear of before contact (its real sideways timing): brake
                        // firmly at most, never cut, never down to that car's speed (you never get this credit)
                        bool escapes = false;
                        if (!b.You && room > 0.05f && !(o.Target > b.Lo && o.Target < b.Hi))
                            escapes = LatTime(o.Target > p.Offset ? b.Hi - p.Offset : p.Offset - b.Lo, o.Target > p.Offset ? 1f : -1f, p) < room / b.Closing;
                        if (escapes) need = Mathf.Min(need, EscapeBrake);
                        // alongside it (overlapping lengthwise): no nose-to-tail distance to save, so no cut (it can't
                        // prevent a touch from the side); firm braking drops it back behind that car
                        bool alongside = !b.You && b.Gap <= 0f;   // a player alongside keeps the cut (never hitting beats smoothness)
                        if (alongside) need = Mathf.Min(need, AlongsideBrake);
                        if (need > o.NeedDecel)
                        {
                            o.NeedDecel = need; o.NeedRoom = room; o.NeedClosing = b.Closing; o.NeedSpeed = Mathf.Max(0f, b.Speed);
                            o.NeedEscapes = escapes; o.NeedAlongside = alongside;
                        }
                    }
                }
                float mid2 = (b.Lo + b.Hi) * 0.5f;
                if (b.Gap > 0f && b.Gap < TowGap && b.Speed > 15f && Mathf.Abs(p.Offset - mid2) < TowLane) o.Tow = true;
            }
            bool evading = false;
            for (int g = 0; g < gn && !evading; g++) evading = p.Offset > s_gLo[g] && p.Offset < s_gHi[g];
            o.Evading = evading;
            // 0.9.0: pinned inside a player's band with no sideways way out (its target is still inside it: the road
            // edge), behind that player: drop back behind them (firm braking, no cut), as a driver backs out of a squeeze
            // (0.9.0 contacts fix: with PinnedMargin, so it drops back before the squeeze, not once already pinned)
            bool threat = false;
            for (int g = 0; g < gn; g++)
            {
                if (p.Offset > s_gLo[g] - ThreatMargin && p.Offset < s_gHi[g] + ThreatMargin) threat = true;
                bool inside = p.Offset > s_gLo[g] - PinnedMargin && p.Offset < s_gHi[g] + PinnedMargin;
                bool stuck = o.Target > s_gLo[g] - PinnedMargin && o.Target < s_gHi[g] + PinnedMargin;
                if (!inside || !stuck || p.S >= s_gRoad[g] + 2.4f + p.HalfL) continue;   // behind that player or overlapping alongside
                float want = Mathf.Max(0f, s_gSpeed[g] - PinnedDrop);
                o.Cap = Mathf.Min(o.Cap, want);
                if (PinnedBrake > o.NeedDecel)
                {
                    o.NeedDecel = PinnedBrake; o.NeedRoom = float.PositiveInfinity; o.NeedClosing = 0f; o.NeedSpeed = want;
                    o.NeedEscapes = false; o.NeedAlongside = true;
                }
            }
            o.Threat = threat || evading;
            o.NewGap = !hasPrev || Mathf.Abs(o.Target - p.PrevTarget) > CommitMetres;
            return o;
        }

        /// <summary>0.9.0: seconds until a player <paramref name="behind"/> m behind (nose to tail) reaches it at that closing speed; 0 = alongside or ahead.</summary>
        private static float Arrive(float behind, float closing)
            => behind <= 0f ? 0f : behind / Mathf.Max(0.5f, closing);

        /// <summary>
        /// 0.9.0: seconds to move <paramref name="d"/> m sideways in direction <paramref name="dir"/> (+1 = right): from
        /// standing, accelerating at LatAccel up to Rate; plus stopping a sideways motion the wrong way first (LatVel),
        /// building the acceleration up and down at LatJerk, and the game's position smoothing. LatAccel 0 = the old
        /// timing (d / Rate).
        /// </summary>
        private static float LatTime(float d, float dir, in PlanInput p)
        {
            float v = Mathf.Max(0.5f, p.Rate);
            if (!(p.LatAccel > 0f)) return d / v;
            float a = p.LatAccel;
            d = Mathf.Max(0f, d);
            float t = d < v * v / (2f * a) ? Mathf.Sqrt(2f * d / a) : d / v + v / (2f * a);
            float wrong = -p.LatVel * dir;
            if (wrong > 0f) t += 2f * wrong / a;
            if (p.LatJerk > 0f) t += 0.5f * a / p.LatJerk;   // the acceleration building up (it needn't stop at the band edge)
            return t + LatLag;
        }

        private static readonly float[] s_gLo = new float[6], s_gHi = new float[6], s_gRoad = new float[6], s_gSpeed = new float[6];
        private static readonly float[] s_gBehind = new float[6], s_gArrive = new float[6];
        private static bool s_tail;

        /// <summary>
        /// x is out for a guard: inside that player's band, or on the far side of it from where the car is now. 0.9.0: the
        /// far side is allowed while that player is still behind (at least SafeGapYou nose to tail) and won't arrive
        /// before the car is across with time to spare (its real sideways timing x CrossFactor + CrossSlack): a car held
        /// up ahead of a fast player was chased out to the road edge, into the lane that player then took.
        /// </summary>
        private static bool Guarded(float x, in PlanInput p, int gn)
        {
            for (int g = 0; g < gn; g++)
            {
                if (x > s_gLo[g] && x < s_gHi[g]) return true;
                float gm = (s_gLo[g] + s_gHi[g]) * 0.5f;
                bool far = p.Offset <= gm ? x >= s_gHi[g] : x <= s_gLo[g];
                if (!far) continue;
                if (s_gBehind[g] >= SafeGapYou && LatTime(Mathf.Abs(x - p.Offset), x > p.Offset ? 1f : -1f, p) * CrossFactor + CrossSlack < s_gArrive[g]) continue;
                return true;
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
                Allowed = room > 0f ? Mathf.Sqrt(slowTo * slowTo + 2f * Braking * room) : Mathf.Max(0f, slowTo - (s_tail && !you ? 0f : 1f)),
                Ahead = gap > -(halfLength + p.HalfL),
                Beside = rear || gap > -(2f * halfLength + 2f * p.HalfL),
            };
        }
    }
}
