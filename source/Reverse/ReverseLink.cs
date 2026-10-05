using System;
using UnityEngine;

namespace Reverse
{
    /// <summary>
    /// Publishes the reverse state for other plugins as AppDomain data "rogue.reverse" (a float[3]; no assembly
    /// reference either way). Readers treat it as live only when [2] is less than about 0.25 s old.
    ///   [0] 1 = reversing (the car is being pushed backwards), 0 = not   [1] reverse speed, m/s (0 or more)
    ///   [2] Time.unscaledTime of the last write
    /// Written once a frame by Runner; set to 0 on every stop and removed on unload. RacingLine reads it (no scoring while reversing, nor until the car is past its furthest point again). EngineAudio
    /// (reverse whine) and DriverCam's gauges ("R") could.
    /// </summary>
    internal static class ReverseLink
    {
        internal const string Key = "rogue.reverse";
        internal static readonly float[] Shared = new float[3];

        internal static void Install() => AppDomain.CurrentDomain.SetData(Key, Shared);

        internal static void Publish(bool reversing, float speed)
        {
            Shared[0] = reversing ? 1f : 0f;
            Shared[1] = reversing ? speed : 0f;
            Shared[2] = Time.unscaledTime;
        }

        internal static void Uninstall()
        {
            Shared[0] = 0f; Shared[1] = 0f;
            try { AppDomain.CurrentDomain.SetData(Key, null); } catch (Exception) { /* unloading */ }
        }
    }
}
