using UnityEngine;

namespace CurbFeel
{
    /// <summary>Live counters shown on the status panel.</summary>
    internal static class Stats
    {
        // A. Hull
        public static int HullCars;
        public static int HullCapsules;
        public static float PlayerBodyHalfWidth;

        // B/D. Walls + ramps (current, for loaded tiles)
        public static int WallPairs;
        public static int Ramps;
        public static float AvgWallShift;
        public static float AvgOverCurb;
        public static float CappedShare;
        public static int UnpairedWalls;

        // C. Wall contacts (this session)
        public static int SoftScrapes;
        public static int HardWallHits;

        // E. Traffic
        public static int TrafficCars;
        public static int TrafficResized;
        public static int SideSwipes;
        public static int TrafficHits;
        public static string Lanes = "";
        public static float NearMissRange = -1f;

        // last event, flashed on the panel
        public static string LastEvent = "";
        public static float LastEventTime = -100f;

        public static void Event(string what)
        {
            LastEvent = what;
            LastEventTime = Time.unscaledTime;
        }

        public static void ResetApplied()
        {
            HullCars = HullCapsules = 0;
            WallPairs = Ramps = UnpairedWalls = 0;
            AvgWallShift = 0f;
            TrafficResized = 0;
        }
    }
}
