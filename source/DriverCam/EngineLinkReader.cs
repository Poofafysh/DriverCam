using System;

namespace DriverCam;

/// <summary>
/// Optional link to the EngineAudio plugin (Poofafysh): while its engine runs it publishes AppDomain data
/// "rogue.engineaudio" = float[8] { rpm, idle, redline, gear (0 = first), 1 = live, Time.unscaledTime of the last write,
/// throttle 0-1, 1 = on the limiter }, so the tachometer shows the same RPM you hear. No assembly reference: without
/// EngineAudio (or with F1 off, or a stale value) the gauges use their own GaugeRpm model.
/// </summary>
static class EngineLinkReader
{
    const string Key = "rogue.engineaudio";
    const float StaleAfter = 0.25f;   // seconds without a write = EngineAudio isn't driving the engine now
    static float[] _shared;
    static float _nextLookup;

    public static bool TryGet(out float rpm, out float redline)
    {
        rpm = 0f; redline = 0f;
        float now = UnityEngine.Time.unscaledTime;
        if (_shared == null)
        {
            if (now < _nextLookup) return false;
            _nextLookup = now + 2f;   // looked up at most every 2 s until EngineAudio appears
            _shared = AppDomain.CurrentDomain.GetData(Key) as float[];
            if (_shared == null || _shared.Length < 8) { _shared = null; return false; }
        }
        var s = _shared;
        if (s[4] < 0.5f || now - s[5] > StaleAfter || now < s[5] - 1f)
        {
            // not live: EngineAudio may have been unloaded and loaded again (HotReload) with a new array, so look it up
            // again (throttled like the first lookup) instead of reading the old one forever
            if (now >= _nextLookup)
            {
                _nextLookup = now + 2f;
                if (AppDomain.CurrentDomain.GetData(Key) is float[] fresh && fresh.Length >= 8) _shared = fresh;
            }
            return false;
        }
        rpm = s[0]; redline = s[2];
        if (float.IsNaN(rpm) || float.IsInfinity(rpm) || float.IsNaN(redline) || redline < 1000f) return false;
        return true;
    }
}
