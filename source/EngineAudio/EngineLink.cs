using System;
using UnityEngine;

namespace EngineAudio
{
    /// <summary>
    /// Publishes the simulated engine for other plugins as AppDomain data "rogue.engineaudio" (DriverCam's working
    /// tachometer reads it, so the needle shows the RPM you hear). No assembly reference either way: readers look the
    /// array up by name and treat it as live only when [4] is 1 and [5] is less than about 0.25 s old.
    ///   [0] rpm   [1] idle rpm   [2] redline rpm   [3] gear (0 = first)   [4] 1 = live, 0 = off
    ///   [5] Time.unscaledTime of the last write   [6] throttle 0-1 (eased)   [7] 1 = bouncing off the limiter
    /// Written once a frame while EngineAudio drives the engine (re-stamped unchanged while the game is paused or has
    /// stopped its engine sound); set off on every hand-back (F1, disabled, no car, new car, errors) and removed on unload.
    /// </summary>
    internal static class EngineLink
    {
        internal const string Key = "rogue.engineaudio";
        internal static readonly float[] Shared = new float[8];

        internal static void Install() => AppDomain.CurrentDomain.SetData(Key, Shared);

        internal static void Publish(EngineModel m, float throttle)
        {
            Shared[0] = m.Rpm;
            Shared[1] = m.Idle;
            Shared[2] = m.Redline;
            Shared[3] = m.Gear;
            Shared[6] = throttle;
            Shared[7] = m.OnLimiter ? 1f : 0f;
            Stamp();
        }

        /// <summary>Still live, same values (paused, or the game's engine sound is stopped for a moment).</summary>
        internal static void Stamp()
        {
            Shared[5] = Time.unscaledTime;
            Shared[4] = 1f;
        }

        internal static void Off() { Shared[4] = 0f; }

        internal static void Uninstall()
        {
            Off();
            try { AppDomain.CurrentDomain.SetData(Key, null); } catch (Exception) { /* unloading */ }
        }
    }
}
