using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace DriverCam;

/// <summary>
/// Working gauges: turns the cockpit's needles and rewrites the digital readout from the car's real speed (in the game
/// HUD's unit, with its numbers) and the engine RPM (EngineAudio's, else GaugeRpm). Fed by the gauge parts of the
/// .dcm (`n` / `dg` / `db` lines, Cockpit.BuildFromModel registers them) and run from Cockpit.UpdatePose, once a
/// frame, only while the driver view shows the modelled cockpit (0.11.3: or, in a Bikes car model, the W8 readout CarCabin
/// builds on Bikes' cluster socket and registers here, run from CarCabin.Pose). Per frame: a few transform rotations; the digital
/// panel's UVs are written only when the shown number or the lit bar count changes, into an array made at build time.
/// Read only towards the game. 3 errors switch it off for the session with every needle back at rest.
/// </summary>
internal static class Gauges
{
    // the HUD's own multipliers (MetricEnumExtensions.GetMultiplierNonLogical): it shows the real speed x 1.1
    const float KmhPerMs = 3.6f * 1.1f, MphPerMs = 2.237f * 1.1f;
    const float OvertravelDeg = 2f;   // a pegged needle goes this far past the end of its scale
    const float IdleRpm = 900f;       // the built-in engine's idle (EngineAudio's default)
    const float Response = 12f;       // needle smoothing rate (1/s)

    sealed class NeedleRig
    {
        public Transform Spin;
        public CockpitModel.Needle Kmh, Mph, Other;
        public float Applied;
    }

    sealed class QuadRig
    {
        public Mesh Mesh;
        public Vector2[] BaseUv;
        public Il2CppStructArray<Vector2> Work;
        public float[] OffU, OffV;   // each quad's current UV offset from its baked UVs (only changed quads are rewritten)
        public bool Dirty;
        public CockpitModel.DigitSpec[] Digits;
        public CockpitModel.BarSpec[] Bars;
        public int Quads;
        public int LastValue = int.MinValue, LastLit = int.MinValue;
    }

    static readonly List<NeedleRig> _needles = new();
    static readonly List<QuadRig> _quads = new();
    static readonly GaugeRpm _engine = new();
    static GaugeRead _read;
    static bool _haveRead;
    static float _dialRed = float.NaN;   // the tachometer's red line (dial units): EngineAudio's redline lands on it
    static float _speedMs, _rpm, _nextUnitPoll, _lastErrorLog = -999f;
    static int _frame = -1, _errors, _unit = 1;   // the game's unit: 0 km/h, 1 mph (its default)
    static bool _off, _atRest = true, _faceMph, _faceApplied, _hasMphFace;
    static int _rpmSource = -1;          // 1 EngineAudio, 0 built-in, -1 not logged yet
    static string _loggedCar;

    /// <summary>True while the gauges show mph (the face material is built with that atlas).</summary>
    internal static bool FaceMph => _faceMph;

    /// <summary>Forgets the old cockpit's gauge parts (rebuild, car change). The new model's gauges register next.</summary>
    internal static void Clear(CockpitModel model)
    {
        foreach (var q in _quads) Keep.Release(q.Mesh);
        _needles.Clear();
        _quads.Clear();
        _dialRed = float.NaN;
        _faceApplied = false;
        _atRest = true;
        _hasMphFace = Cockpit.HasMphFace(model);   // same check as the face Cockpit loads, so needles match the face
    }

    /// <summary>A gauge part was built: its spinning transform and (digits / bars) its first mesh with that mesh's baked UVs.</summary>
    internal static void Register(Transform spin, CockpitModel.Part part, Mesh quadMesh, List<Vector2> quadUvs)
    {
        if (part.Needles.Count > 0 && spin != null)
        {
            var rig = new NeedleRig { Spin = spin };
            foreach (var n in part.Needles)
            {
                if (n.Source == "speed" && n.Unit == "kmh") { if (rig.Kmh == null) rig.Kmh = n; }
                else if (n.Source == "speed" && n.Unit == "mph") { if (rig.Mph == null) rig.Mph = n; }
                else if (rig.Other == null) rig.Other = n;
                if (n.Source == "rpm") NoteRed(n.Red, n.Hi);
            }
            _needles.Add(rig);
        }
        if (part.Digits.Count == 0 && part.Bars.Count == 0) return;
        int verts = part.QuadCursor * 6;   // two triangles = six corners per quad, in file order
        if (quadMesh == null || quadUvs == null || quadUvs.Count < verts)
        {
            Plugin.Logger.LogWarning($"Gauges: {part.Name} has {part.QuadCursor} digit/bar quads but its first mesh has only {(quadUvs == null ? 0 : quadUvs.Count / 6)}; left as built.");
            return;
        }
        var baseUv = quadUvs.ToArray();
        var work = new Il2CppStructArray<Vector2>(baseUv.Length);
        for (int i = 0; i < baseUv.Length; i++) work[i] = baseUv[i];
        foreach (var b in part.Bars) if (b.Source == "rpm") NoteRed(b.Red, b.Hi);
        _quads.Add(new QuadRig { Mesh = Keep.Hold(quadMesh), BaseUv = baseUv, Work = work, OffU = new float[part.QuadCursor], OffV = new float[part.QuadCursor], Digits = part.Digits.ToArray(), Bars = part.Bars.ToArray(), Quads = part.QuadCursor });
    }

    static void NoteRed(float red, float hi)
    {
        if (!float.IsNaN(_dialRed)) return;
        _dialRed = !float.IsNaN(red) && red > 0f ? red : hi * 0.85f;
    }

    /// <summary>Once a frame (Cockpit.UpdatePose runs twice: after LateUpdate and before rendering).</summary>
    internal static void Update()
    {
        if (_off || (_needles.Count == 0 && _quads.Count == 0)) return;
        int frame = Time.frameCount;
        if (frame == _frame) return;
        _frame = frame;
        try { Step(); }
        catch (Exception e) { Fault(e); }
    }

    static void Step()
    {
        if (!Plugin.WorkingGauges.Value)
        {
            if (!_atRest) Rest();
            return;
        }

        // the game's unit (it can change in the pause menu): the face, the needles' scale and the digits follow it
        float now = Time.unscaledTime;
        if (now >= _nextUnitPoll || !_faceApplied)
        {
            _nextUnitPoll = now + 1f;
            int u = GaugeGame.Unit();
            if (u == 0 || u == 1) _unit = u;
            bool mph = _unit == 1 && _hasMphFace;
            if (mph != _faceMph || !_faceApplied)
            {
                _faceMph = mph;
                _faceApplied = true;
                Cockpit.SetGaugeFace(mph);
                CarCabin.SetGaugeFace(mph);   // a Bikes car model's readout (no-op otherwise)
                foreach (var q in _quads) q.LastValue = int.MinValue;
            }
        }

        // paused: no reads, the needles hold (a unit change still shows)
        float dt = Time.deltaTime;
        if (Time.timeScale > 0f && dt > 0f)
        {
            _haveRead = GaugeGame.Read(ref _read);
            float targetMs = _haveRead ? _read.Speed : 0f;
            float targetRpm = 0f;
            if (!float.IsNaN(_dialRed))
            {
                int source;
                if (EngineLinkReader.TryGet(out float rpm, out float redline))
                {
                    source = 1;
                    targetRpm = rpm * _dialRed / redline;   // EngineAudio's red line on the dial's red line
                    _engine.Sync(targetRpm);
                }
                else
                {
                    source = 0;
                    targetRpm = _haveRead ? _engine.Step(in _read, IdleRpm, _dialRed, dt) : 0f;
                }
                if (source != _rpmSource) { _rpmSource = source; LogSetup(); }
            }
            else if (_rpmSource < 0) { _rpmSource = 0; LogSetup(); }
            float k = 1f - MathF.Exp(-Response * dt);
            _speedMs += (targetMs - _speedMs) * k;
            _rpm += (targetRpm - _rpm) * k;
        }
        if (Cockpit.CarId != _loggedCar) LogSetup();

        _atRest = false;
        for (int i = 0; i < _needles.Count; i++) Turn(_needles[i]);
        for (int i = 0; i < _quads.Count; i++) Write(_quads[i], false);
    }

    /// <summary>A gauge source's value in a scale's unit; NaN for sources the gauges don't drive (the needle stays at rest).</summary>
    static float Value(string source, string unit)
    {
        switch (source)
        {
            case "speed":
                bool mph = unit == "mph" || (unit != "kmh" && _faceMph);
                return _speedMs * (mph ? MphPerMs : KmhPerMs);
            case "rpm":
                return _rpm;
            default:
                return float.NaN;
        }
    }

    static void Turn(NeedleRig rig)
    {
        if (rig.Spin == null) return;
        var n = _faceMph ? (rig.Mph != null ? rig.Mph : rig.Kmh) : (rig.Kmh != null ? rig.Kmh : rig.Mph);
        if (n == null) n = rig.Other;
        if (n == null) return;
        float v = Value(n.Source, n.Unit);
        float angle = 0f;
        if (!float.IsNaN(v))
        {
            float sweep = n.A1 - n.A0;
            float t = (v - n.Lo) / (n.Hi - n.Lo);
            float absSweep = MathF.Abs(sweep);
            float over = absSweep > 1f ? OvertravelDeg / absSweep : 0f;
            angle = sweep * FM.Clamp(t, 0f, 1f + over);
        }
        if (MathF.Abs(angle - rig.Applied) < 0.01f) return;
        rig.Applied = angle;
        rig.Spin.localRotation = Quaternion.Euler(0f, 0f, angle);   // +angle turns +x towards +y: counter-clockwise for the driver
    }

    /// <summary>The HUD's number: floor(|CurrentSpeed| x the unit's multiplier), unsmoothed.</summary>
    static float HudSpeed(string unit)
    {
        bool mph = unit == "mph" || (unit != "kmh" && _faceMph);
        return (_haveRead ? _read.Speed : 0f) * (mph ? MphPerMs : KmhPerMs);
    }

    /// <summary>Digits (floor of the speed in the shown unit, like the HUD, leading zeros blank) and lit bars.</summary>
    static void Write(QuadRig q, bool rest)
    {
        if (q.Mesh == null) return;
        int value = 0, lit = 0;
        if (!rest)
        {
            foreach (var d in q.Digits)
            {
                float v = d.Source == "speed" ? HudSpeed(d.Unit) : Value(d.Source, d.Unit);
                value = float.IsNaN(v) ? 0 : (int)FM.Clamp(MathF.Floor(v), 0f, 999999f);
                break;   // one readout per part
            }
            foreach (var b in q.Bars)
            {
                float v = Value(b.Source, "-");
                if (float.IsNaN(v)) break;
                lit = FM.Clamp(FM.FloorToInt((v - b.Lo) / (b.Hi - b.Lo) * b.Count + 1e-3f), 0, b.Count);
                break;
            }
        }
        else { value = -1; lit = 0; }
        if (value == q.LastValue && lit == q.LastLit) return;
        q.LastValue = value; q.LastLit = lit;

        foreach (var d in q.Digits)
        {
            int shown = rest ? 0 : Math.Min(value, Pow10(d.Count) - 1);   // over range holds 999
            for (int i = d.Count - 1, rem = shown; i >= 0; i--, rem /= 10)   // the last quad is the units digit
            {
                int glyph = !rest && rem == 0 && i < d.Count - 1 ? 10 : rem % 10;   // leading zeros blank (glyph 10)
                Shift(q, d.FirstQuad + i, glyph * d.Du, 0f);
            }
        }
        foreach (var b in q.Bars)
            for (int i = 0; i < b.Count; i++)
            {
                if (i < lit)
                {
                    float threshold = b.Lo + (i + 1f) / b.Count * (b.Hi - b.Lo);
                    var o = threshold >= b.Red ? b.RedLit : b.Lit;
                    Shift(q, b.FirstQuad + i, o.x, o.y);
                }
                else Shift(q, b.FirstQuad + i, 0f, 0f);
            }
        if (!q.Dirty) return;
        q.Dirty = false;
        q.Mesh.uv = q.Work;
    }

    static int Pow10(int n) { int p = 1; for (int i = 0; i < n && p < 100000000; i++) p *= 10; return p; }

    /// <summary>Moves one quad's UVs to its baked UVs + (du, dv); skipped when the quad already shows that glyph / state.</summary>
    static void Shift(QuadRig q, int quad, float du, float dv)
    {
        if ((uint)quad >= (uint)q.OffU.Length || (q.OffU[quad] == du && q.OffV[quad] == dv)) return;
        q.OffU[quad] = du; q.OffV[quad] = dv;
        q.Dirty = true;
        var work = q.Work;
        var baseUv = q.BaseUv;
        int s = quad * 6, e = Math.Min(s + 6, baseUv.Length);
        for (int k = s; k < e; k++) work[k] = FM.V2(baseUv[k].x + du, baseUv[k].y + dv);   // field writes: new Vector2 is an interop call
    }

    /// <summary>Every needle back at its zero, the readout back to how it was built.</summary>
    static void Rest()
    {
        _atRest = true;
        foreach (var rig in _needles)
        {
            if (rig.Spin == null) continue;
            rig.Spin.localRotation = Quaternion.identity;
            rig.Applied = 0f;
        }
        foreach (var q in _quads) Write(q, true);
    }

    static void LogSetup()
    {
        _loggedCar = Cockpit.CarId;
        string speedo = "no speedometer", tach = "no tachometer";
        foreach (var rig in _needles)
        {
            var s = _faceMph ? (rig.Mph != null ? rig.Mph : rig.Kmh) : (rig.Kmh != null ? rig.Kmh : rig.Mph);
            if (s != null) speedo = $"speedometer 0-{s.Hi:0} {(s.Unit == "kmh" ? "km/h" : s.Unit)}";
            if (rig.Other != null && rig.Other.Source == "rpm") tach = $"tachometer 0-{rig.Other.Hi:0} (red {_dialRed:0})";
        }
        foreach (var q in _quads)
        {
            if (q.Digits.Length > 0) speedo = $"digital speed readout in {(_faceMph ? "mph" : "km/h")}";
            if (q.Bars.Length > 0) tach = $"{q.Bars[0].Count} rpm bars 0-{q.Bars[0].Hi:0} (red {q.Bars[0].Red:0})";
        }
        Plugin.Logger.LogInfo($"Gauges for {Cockpit.CarId}: {speedo}, {tach}, rpm from {(_rpmSource == 1 ? "EngineAudio" : "the built-in engine model")}.");
    }

    static void Fault(Exception e)
    {
        _errors++;
        if (Time.unscaledTime - _lastErrorLog > 5f || _errors >= 3)
        {
            _lastErrorLog = Time.unscaledTime;
            Plugin.Logger.LogWarning($"Gauges: error ({_errors}/3): {e.Message}");
        }
        if (_errors < 3) return;
        _off = true;
        try { Rest(); } catch (Exception) { /* the cockpit is gone */ }
        Plugin.Logger.LogError($"Gauges switched off for this session; the dials stay at rest. Last error: {e}");
    }
}
