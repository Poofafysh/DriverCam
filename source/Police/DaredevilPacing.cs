using System;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// Daredevils' race pacing: a multiplier on a rival's top speed and speed profile from where it is against you, so
    /// rivals spend their race near you instead of far up the road.
    /// - 0.7.0 (review 2026-10-04: 44 of 52 rivals started 160-760 m ahead and none ever raced you, because 85% of a
    ///   rival's top speed was still faster than your real average in traffic): ahead of you, the pace is also capped by
    ///   YOUR real road speed (<paramref name="youShare"/> = your road speed / its top speed), so you catch it up:
    ///   more than <see cref="FarAhead"/> m ahead at most <see cref="FarAheadFactor"/> and 90% of your speed; from
    ///   <see cref="HonestAhead"/> to 250 m ahead at most <see cref="EaseFactor"/> and 95% of your speed;
    /// - more than <see cref="NearBand"/> m behind: pushes, from +3% at 150 m up to <see cref="FarBehindFactor"/> at
    ///   <see cref="FarBehindFull"/> m behind; +3% beyond 120 m behind (PushFactor);
    /// - from 120 m behind to <see cref="HonestAhead"/> m ahead it races honestly (1);
    /// - never below <see cref="MinFactor"/>; always smooth: the multiplier moves towards its target exponentially at
    ///   0.5 per second (no step changes).
    /// Pure function: the caller keeps the current value per rival (Rival.Push) and passes it back in.
    /// </summary>
    internal static class DaredevilPacing
    {
        internal const float FarAhead = 250f, HonestAhead = 100f, NearBand = 150f, FarBehindFull = 300f;
        internal const float FarAheadFactor = 0.85f, FarBehindFactor = 1.10f, MinFactor = 0.5f;
        internal const float PushBehind = 120f, PushFactor = 1.03f, EaseFactor = 0.98f;
        internal const float FarYouShare = 0.90f, NearYouShare = 0.95f;
        internal const float Rate = 0.5f;    // 1/s

        /// <summary>
        /// The pace multiplier after this frame. <paramref name="relToPlayer"/> = rival road distance - yours (m, + = it
        /// is ahead), <paramref name="dt"/> = frame time (s), <paramref name="current"/> = last frame's multiplier
        /// (NaN or out of range starts from 1), <paramref name="youShare"/> = your road speed / the rival's top speed
        /// (NaN = unknown: no cap from your speed).
        /// </summary>
        internal static float Factor(float relToPlayer, float dt, float current, float youShare = float.NaN)
        {
            if (float.IsNaN(current) || current < 0.5f || current > 1.5f) current = 1f;
            if (float.IsNaN(relToPlayer) || !(dt > 0f)) return current;
            bool share = !float.IsNaN(youShare) && youShare > 0f;
            float target;
            if (relToPlayer > FarAhead) target = share ? Mathf.Min(FarAheadFactor, FarYouShare * youShare) : FarAheadFactor;
            else if (relToPlayer > HonestAhead) target = share ? Mathf.Min(EaseFactor, NearYouShare * youShare) : EaseFactor;
            else if (relToPlayer < -NearBand)
                target = Mathf.Lerp(PushFactor, FarBehindFactor, Mathf.InverseLerp(NearBand, FarBehindFull, -relToPlayer));
            else if (relToPlayer < -PushBehind) target = PushFactor;
            else target = 1f;
            target = Mathf.Max(MinFactor, target);
            return current + (target - current) * (1f - Mathf.Exp(-Mathf.Min(dt, 1f) * Rate));
        }
    }

    /// <summary>
    /// Daredevils' per-race tally for the race-over summary: every rival let go during a race adds itself (crashed or
    /// not, overtakes both ways, closest gap). Keyed by your car, so a new race (a new player car) starts a new tally.
    /// </summary>
    internal static class DaredevilTally
    {
        private static IntPtr _car;
        private static int _rivals, _crashed, _passes, _passed, _contacts;
        private static float _closest = float.PositiveInfinity;

        internal static int Crashed => _crashed;

        internal static void Add(IntPtr raceCar, bool crashed, int passes, int passed, float closest, int contacts = 0)
        {
            if (raceCar != _car) Reset(raceCar);
            _rivals++;
            if (crashed) _crashed++;
            _passes += passes;
            _passed += passed;
            _contacts += contacts;
            if (closest < _closest) _closest = closest;
        }

        /// <summary>The summary line for this race (null if no rival was let go in it); the tally then starts over.</summary>
        internal static string TakeSummary(IntPtr raceCar)
        {
            if (raceCar != _car || _rivals == 0) return null;
            string s = $"[Police] daredevils race summary: rivals {_rivals}, crashed {_crashed}, passed you {_passes}, you passed them {_passed}, " +
                       $"closest {(float.IsInfinity(_closest) ? "-" : _closest.ToString("0.0") + " m")}, contacts with you {_contacts}";
            Reset(raceCar);
            return s;
        }

        private static void Reset(IntPtr raceCar)
        {
            _car = raceCar;
            _rivals = _crashed = _passes = _passed = _contacts = 0;
            _closest = float.PositiveInfinity;
        }
    }
}
