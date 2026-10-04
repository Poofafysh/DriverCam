using System;
using System.Collections.Generic;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// Daredevils' traffic lane tracker: remembers each traffic car's lane offset between avoidance snapshots (by its
    /// pointer) and turns it into a smoothed sideways speed, then widens the snapshot's RoadCar.LaneLo / LaneHi to cover
    /// the car's lane now plus where it is heading in <see cref="Predict"/> s (a lane change in progress), the same way
    /// the player's band is predicted. Cars not seen for <see cref="ForgetSeconds"/> s are dropped; the table is bounded
    /// (<see cref="MaxCars"/>) and reuses its own storage, so a snapshot allocates nothing once it has warmed up.
    /// A jump sideways faster than <see cref="MaxLaneSpeed"/> (a pooled car reused elsewhere) starts that car over.
    /// </summary>
    internal static class TrafficTracker
    {
        internal const float Predict = 0.6f;          // s ahead, like the player's band (Daredevils.YouPredict)
        private const float ForgetSeconds = 2f, PruneSeconds = 0.5f;
        private const float MaxGap = 0.5f;            // s between sightings still used for a speed
        private const float MaxLaneSpeed = 15f;       // m/s sideways: more is a reused car, not a lane change
        private const float LaneSpeedCap = 8f;        // m/s used for the prediction (as for the player)
        private const float Smoothing = 8f;           // 1/s
        private const int MaxCars = 512;

        private struct Track { public float Lane, Vel, Seen; }

        private static readonly Dictionary<IntPtr, Track> Tracks = new Dictionary<IntPtr, Track>(MaxCars);
        private static readonly List<IntPtr> Stale = new List<IntPtr>(MaxCars);
        private static float _nextPrune;

        /// <summary>
        /// Updates the tracks from the first <paramref name="n"/> cars of a fresh snapshot taken at <paramref name="now"/>
        /// (game time) and widens each car's LaneLo / LaneHi in place. Call right after GameApi.ReadRoad.
        /// </summary>
        internal static void Update(RoadCar[] road, int n, float now)
        {
            if (road == null) return;
            if (n > road.Length) n = road.Length;
            for (int i = 0; i < n; i++)
            {
                float lane = road[i].Lane;
                if (float.IsNaN(lane)) continue;
                IntPtr key = road[i].Ptr;
                float vel = 0f;
                if (Tracks.TryGetValue(key, out var t))
                {
                    float dt = now - t.Seen;
                    if (dt <= 1e-4f) vel = t.Vel;                                   // same snapshot time: nothing new
                    else if (dt < MaxGap)
                    {
                        float raw = (lane - t.Lane) / dt;
                        if (Mathf.Abs(raw) <= MaxLaneSpeed) vel = t.Vel + (raw - t.Vel) * (1f - Mathf.Exp(-dt * Smoothing));
                    }
                }
                else if (Tracks.Count >= MaxCars) continue;                         // full: no prediction for this one
                Tracks[key] = new Track { Lane = lane, Vel = vel, Seen = now };
                float soon = lane + Mathf.Clamp(vel, -LaneSpeedCap, LaneSpeedCap) * Predict;
                road[i].LaneLo = Mathf.Min(road[i].LaneLo, Mathf.Min(lane, soon));
                road[i].LaneHi = Mathf.Max(road[i].LaneHi, Mathf.Max(lane, soon));
            }
            if (now >= _nextPrune || now < _nextPrune - 2f * PruneSeconds) Prune(now);   // (time went back: a new scene)
        }

        /// <summary>Forgets every car (nothing to give back; the game owns the cars).</summary>
        internal static void Clear()
        {
            Tracks.Clear();
            Stale.Clear();
            _nextPrune = 0f;
        }

        private static void Prune(float now)
        {
            _nextPrune = now + PruneSeconds;
            Stale.Clear();
            foreach (var kv in Tracks)
                if (now - kv.Value.Seen > ForgetSeconds || kv.Value.Seen > now) Stale.Add(kv.Key);
            for (int i = 0; i < Stale.Count; i++) Tracks.Remove(Stale[i]);
            Stale.Clear();
        }
    }
}
