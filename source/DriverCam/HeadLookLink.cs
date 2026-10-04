using System;

namespace DriverCam;

/// <summary>
/// Optional link to the HeadLook plugin (Poofafysh): when it is installed it publishes the player's head turn (right
/// stick / right mouse) as AppDomain data "rogue.headlook" = float[3] { yaw degrees (+ right), pitch degrees (+ up),
/// 1 = running }. The driver view adds it to the eye direction. No assembly reference: without HeadLook this is (0, 0).
/// </summary>
static class HeadLookLink
{
    static float[] _shared;
    static float _nextLookup;

    public static void Get(out float yaw, out float pitch)
    {
        yaw = 0f; pitch = 0f;
        if (_shared == null)
        {
            if (UnityEngine.Time.unscaledTime < _nextLookup) return;
            _nextLookup = UnityEngine.Time.unscaledTime + 2f;   // looked up at most every 2 s until HeadLook appears
            _shared = AppDomain.CurrentDomain.GetData("rogue.headlook") as float[];
            if (_shared == null || _shared.Length < 3) { _shared = null; return; }
        }
        if (_shared[2] < 0.5f) return;
        yaw = _shared[0]; pitch = _shared[1];
        if (float.IsNaN(yaw) || float.IsNaN(pitch)) { yaw = 0f; pitch = 0f; }
    }
}
