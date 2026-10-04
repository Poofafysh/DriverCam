using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;

namespace DriverCam;

/// <summary>
/// Parsed cockpit model exported from Blender (Assets/cockpit.blend -> cockpit.dcm). Plain text, one
/// triangle per line, already in Unity axes with the driver's eye at the origin.
/// </summary>
internal sealed class CockpitModel
{
    public sealed class Part
    {
        public string Name;
        public string Group = "Misc";
        public bool HasPivot;
        public UnityEngine.Vector3 PivotPos, PivotForward, PivotUp;
        public readonly Dictionary<string, (List<UnityEngine.Vector3> verts, List<UnityEngine.Vector3> normals, List<UnityEngine.Vector2> uvs)> ByTag = new();
        /// <summary>Working gauges (pivot parts only): needle ranges (`n`), digit quads (`dg`) and bar quads (`db`).</summary>
        public readonly List<Needle> Needles = new();
        public readonly List<DigitSpec> Digits = new();
        public readonly List<BarSpec> Bars = new();
        /// <summary>Quads claimed so far by `dg` / `db` lines (they take the part's first mesh's quads in order).</summary>
        public int QuadCursor;
        public bool IsGauge => Needles.Count > 0 || Digits.Count > 0 || Bars.Count > 0;
    }

    /// <summary>
    /// `n source unit|- lo hi a0 a1 [red]`: the part is a needle built at rest pointing at angle a0 (maths degrees in the
    /// dial plane, counter-clockwise as the driver sees it); value v turns it to a0 + (a1 - a0) * (v - lo) / (hi - lo).
    /// </summary>
    public sealed class Needle
    {
        public string Source, Unit;
        public float Lo, Hi, A0, A1, Red = float.NaN;
    }

    /// <summary>`dg source unit|- count u0 v0 du`: `count` quads are digits (most significant first); glyph k is du * k to the right of glyph 0.</summary>
    public sealed class DigitSpec
    {
        public string Source, Unit;
        public int Count, FirstQuad;
        public float U0, V0, Du;
    }

    /// <summary>`db source count lo hi red dlu dlv dru drv`: `count` quads are bars; bar i lights at lo + (i + 1) / count * (hi - lo).</summary>
    public sealed class BarSpec
    {
        public string Source;
        public int Count, FirstQuad;
        public float Lo, Hi, Red;
        public UnityEngine.Vector2 Lit, RedLit;
    }

    /// <summary>How far left of the car's center line the driver sits, in model meters.</summary>
    public float DriverSide;
    /// <summary>Car width the model was built for, used to scale it to each car.</summary>
    public float ReferenceWidth = 1.8f;
    /// <summary>True when the model is fitted to one car in that car's own body coordinates (no scaling).</summary>
    public bool BodyFrame;
    /// <summary>Driver's eye in body coordinates (body-frame models only), before the seat offsets.</summary>
    public UnityEngine.Vector3 Eye;
    public string Source;
    /// <summary>
    /// Interior layout version from a `layout N` line. 2 = the modelled interiors, built where the old tuned dash sat,
    /// so a saved Part.Interior offset from layout 1 must not be applied again (CarPresets migrates it once).
    /// </summary>
    public int Layout = 1;
    public string ModelFolder;
    /// <summary>Material tag -> texture file (next to the .dcm); textured tags use alpha cutout for window holes.</summary>
    public readonly Dictionary<string, string> TagTextures = new();
    /// <summary>
    /// Material tag -> the model's own colour ("mat tag r g b smoothness metallic glow" lines). Tags listed here use these
    /// instead of DriverCam's built-in neon palette, so a model can carry a realistic cabin (leather, wood, chrome...).
    /// </summary>
    public readonly Dictionary<string, (UnityEngine.Color color, float smoothness, float metallic, float glow)> TagColors = new();
    public readonly List<Part> Parts = new();

    static readonly Dictionary<string, CockpitModel> _cache = new();

    static string Folder => Path.Combine(Paths.PluginPath, "DriverCam");

    /// <summary>The cockpit fitted to this car (cockpit_CarId.dcm) if there is one, else the generic cockpit.dcm.</summary>
    public static CockpitModel LoadFor(string carId)
    {
        var fitted = string.IsNullOrEmpty(carId) ? null : Path.Combine(Folder, $"cockpit_{carId}.dcm");
        return fitted != null && File.Exists(fitted) ? LoadFile(fitted) : LoadFile(Path.Combine(Folder, "cockpit.dcm"));
    }

    static CockpitModel LoadFile(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        _cache[path] = null;
        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"Cockpit model not found at {path}; using the basic built-in cockpit.");
            return null;
        }
        CockpitModel _cached;

        try
        {
            _cached = Parse(File.ReadAllLines(path));
            _cached.Source = Path.GetFileName(path);
            _cached.ModelFolder = Path.GetDirectoryName(path);
            int tris = 0;
            foreach (var p in _cached.Parts)
                foreach (var t in p.ByTag.Values) tris += t.verts.Count / 3;
            Plugin.Logger.LogInfo($"Loaded cockpit {_cached.Source}: {_cached.Parts.Count} parts, {tris} triangles, " + (_cached.BodyFrame ? $"fitted to the car, eye at {_cached.Eye}." : $"generic, driver {_cached.DriverSide:0.00} m left of center."));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Failed to read cockpit model {path}: {e}");
            _cached = null;
        }
        _cache[path] = _cached;
        return _cached;
    }

    static CockpitModel Parse(string[] lines)
    {
        var model = new CockpitModel();
        Part part = null;
        string tag = "interior_dark";
        var inv = CultureInfo.InvariantCulture;

        foreach (var raw in lines)
        {
            if (raw.Length < 2 || raw[0] == '#') continue;
            var tok = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float F(int i) => float.Parse(tok[i], inv);
            // built from fields: `new Vector3(...)` / `new Vector2(...)` are interop calls, and a cockpit has ~15k corners
            UnityEngine.Vector3 V(int i) => RogueShared.FastMath.V3(F(i), F(i + 1), F(i + 2));

            switch (tok[0])
            {
                case "s": model.DriverSide = F(1); break;
                case "w": model.ReferenceWidth = F(1); break;
                case "frame": model.BodyFrame = tok[1] == "body"; break;
                case "layout": model.Layout = int.Parse(tok[1], inv); break;
                case "tex": model.TagTextures[tok[1]] = tok[2]; break;
                case "mat":
                    if (tok.Length >= 8) model.TagColors[tok[1]] = (new UnityEngine.Color(F(2), F(3), F(4)), F(5), F(6), F(7));
                    break;
                case "e": model.Eye = V(1); break;
                case "o":
                    part = new Part { Name = tok[1] };
                    model.Parts.Add(part);
                    tag = "interior_dark";
                    break;
                case "p":
                    part.HasPivot = true;
                    part.PivotPos = V(1);
                    part.PivotForward = V(4);
                    part.PivotUp = V(7);
                    break;
                case "n":
                case "dg":
                case "db":
                    // working-gauge lines belong to a pivot part (after its `p` line); a bad or short line is skipped
                    if (part != null && part.HasPivot && !ParseGauge(part, tok)) Plugin.Logger.LogWarning($"Cockpit model: ignored gauge line '{raw}'.");
                    break;
                case "g": part.Group = tok[1]; break;
                case "m": tag = tok[1]; break;
                case "f":
                case "u":
                    if (!part.ByTag.TryGetValue(tag, out var lists))
                    {
                        lists = (new List<UnityEngine.Vector3>(), new List<UnityEngine.Vector3>(), new List<UnityEngine.Vector2>());
                        part.ByTag[tag] = lists;
                    }
                    bool hasUv = tok[0] == "u";
                    int stride = hasUv ? 8 : 6;
                    for (int k = 0; k < 3; k++)
                    {
                        lists.verts.Add(V(1 + k * stride));
                        lists.normals.Add(V(4 + k * stride));
                        lists.uvs.Add(hasUv ? RogueShared.FastMath.V2(F(7 + k * stride), F(8 + k * stride)) : default);
                    }
                    break;
            }
        }
        return model;
    }

    static bool TryF(string[] tok, int i, out float v)
    {
        v = 0f;
        return i < tok.Length && float.TryParse(tok[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v);
    }

    static bool TryI(string[] tok, int i, out int v)
    {
        v = 0;
        return i < tok.Length && int.TryParse(tok[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>One `n` / `dg` / `db` line into the part; false (nothing added) when it is short or malformed.</summary>
    static bool ParseGauge(Part part, string[] tok)
    {
        switch (tok[0])
        {
            case "n":
            {
                if (tok.Length < 7 || !TryF(tok, 3, out var lo) || !TryF(tok, 4, out var hi) || !TryF(tok, 5, out var a0) || !TryF(tok, 6, out var a1) || hi == lo) return false;
                part.Needles.Add(new Needle { Source = tok[1], Unit = tok[2], Lo = lo, Hi = hi, A0 = a0, A1 = a1, Red = TryF(tok, 7, out var red) ? red : float.NaN });
                return true;
            }
            case "dg":
            {
                if (tok.Length < 7 || !TryI(tok, 3, out var count) || count < 1 || count > 9 || !TryF(tok, 4, out var u0) || !TryF(tok, 5, out var v0) || !TryF(tok, 6, out var du)) return false;
                part.Digits.Add(new DigitSpec { Source = tok[1], Unit = tok[2], Count = count, FirstQuad = part.QuadCursor, U0 = u0, V0 = v0, Du = du });
                part.QuadCursor += count;
                return true;
            }
            case "db":
            {
                if (tok.Length < 10 || !TryI(tok, 2, out var count) || count < 1 || count > 64 || !TryF(tok, 3, out var lo) || !TryF(tok, 4, out var hi) || hi == lo
                    || !TryF(tok, 5, out var red) || !TryF(tok, 6, out var dlu) || !TryF(tok, 7, out var dlv) || !TryF(tok, 8, out var dru) || !TryF(tok, 9, out var drv)) return false;
                part.Bars.Add(new BarSpec { Source = tok[1], Count = count, FirstQuad = part.QuadCursor, Lo = lo, Hi = hi, Red = red,
                                            Lit = new UnityEngine.Vector2(dlu, dlv), RedLit = new UnityEngine.Vector2(dru, drv) });
                part.QuadCursor += count;
                return true;
            }
        }
        return false;
    }
}





