using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;

namespace DriverCam;

/// <summary>
/// Per-part placement tweaks (Parts tab / Edit mode). They are saved in each car's own settings file
/// (see CarPresets) as "Part.&lt;group&gt; = posX posY posZ rotX rotY rotZ scale". Positions are in cockpit-model
/// meters (before the per-car scale), rotations in degrees. The old shared DriverCam_parts.txt is read once
/// so earlier tweaks carry over.
/// </summary>
internal static class PartLayout
{
    public struct Entry
    {
        public Vector3 Position;
        public Vector3 Rotation;
        public float Scale;

        public static Entry Default => new() { Scale = 1f };
    }

    /// <summary>"CarId/" for the car being driven, so each car keeps its own tweaks.</summary>
    public static string Prefix = "";

    static readonly Dictionary<string, Entry> _entries = new();
    static bool _loaded;
    static bool _dirty;

    static string LegacyFile => Path.Combine(Paths.ConfigPath, "DriverCam_parts.txt");
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Entry Get(string group)
    {
        EnsureLoaded();
        return _entries.TryGetValue(Prefix + group, out var e) ? e : Entry.Default;
    }

    public static void Set(string group, Entry entry)
    {
        SetDeferred(group, entry);
        Flush();
    }

    /// <summary>Change a part without writing the file yet (for continuous stick edits); call Flush when done.</summary>
    public static void SetDeferred(string group, Entry entry)
    {
        EnsureLoaded();
        _entries[Prefix + group] = entry;
        _dirty = true;
    }

    public static void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        CarPresets.Save();
    }

    public static void Reset(string group)
    {
        EnsureLoaded();
        _entries.Remove(Prefix + group);
        _dirty = true;
        Flush();
    }

    /// <summary>The "Part.x = ..." lines for one car's settings file.</summary>
    public static IEnumerable<string> LinesFor(string car)
    {
        EnsureLoaded();
        var prefix = car + "/";
        var lines = new List<string>();
        foreach (var (key, e) in _entries)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                lines.Add(string.Format(Inv, "Part.{0} = {1:0.####} {2:0.####} {3:0.####} {4:0.##} {5:0.##} {6:0.##} {7:0.####}",
                    key.Substring(prefix.Length), e.Position.x, e.Position.y, e.Position.z, e.Rotation.x, e.Rotation.y, e.Rotation.z, e.Scale));
        return lines;
    }

    /// <summary>Takes the part positions from a car's settings file (replacing what was known for that car).</summary>
    public static void LoadCar(string car, Dictionary<string, string> values)
    {
        EnsureLoaded();
        var parsed = new Dictionary<string, Entry>();
        foreach (var (key, value) in values)
        {
            if (!key.StartsWith("Part.", StringComparison.Ordinal)) continue;
            if (TryParse(value.Split(' ', StringSplitOptions.RemoveEmptyEntries), 0, out var e)) parsed[key.Substring(5)] = e;
            else Plugin.Logger.LogWarning($"Skipping bad part line for {car}: {key} = {value}");
        }
        if (parsed.Count == 0) return;   // older file without parts: keep the ones from DriverCam_parts.txt

        var prefix = car + "/";
        var stale = new List<string>();
        foreach (var key in _entries.Keys) if (key.StartsWith(prefix, StringComparison.Ordinal)) stale.Add(key);
        foreach (var key in stale) _entries.Remove(key);
        foreach (var (group, e) in parsed) _entries[prefix + group] = e;
    }

    static bool TryParse(string[] t, int start, out Entry e)
    {
        e = Entry.Default;
        if (t.Length < start + 7) return false;
        try
        {
            float F(int i) => float.Parse(t[start + i], Inv);
            e = new Entry { Position = new Vector3(F(0), F(1), F(2)), Rotation = new Vector3(F(3), F(4), F(5)), Scale = F(6) };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(LegacyFile)) return;

        foreach (var line in File.ReadAllLines(LegacyFile))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 8 && TryParse(t, 1, out var e)) _entries[t[0]] = e;
        }
        Plugin.Logger.LogInfo($"Read {_entries.Count} part positions from the old DriverCam_parts.txt.");
    }
}
