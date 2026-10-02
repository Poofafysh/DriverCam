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
    public string ModelFolder;
    /// <summary>Material tag -> texture file (next to the .dcm); textured tags use alpha cutout for window holes.</summary>
    public readonly Dictionary<string, string> TagTextures = new();
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
            UnityEngine.Vector3 V(int i) => new(F(i), F(i + 1), F(i + 2));

            switch (tok[0])
            {
                case "s": model.DriverSide = F(1); break;
                case "w": model.ReferenceWidth = F(1); break;
                case "frame": model.BodyFrame = tok[1] == "body"; break;
                case "tex": model.TagTextures[tok[1]] = tok[2]; break;
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
                        lists.uvs.Add(hasUv ? new UnityEngine.Vector2(F(7 + k * stride), F(8 + k * stride)) : UnityEngine.Vector2.zero);
                    }
                    break;
            }
        }
        return model;
    }
}





