using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;

namespace DriverCam;

/// <summary>
/// Per-part placement tweaks made in the in-game Parts editor, saved to BepInEx/config/DriverCam_parts.txt.
/// Positions are in cockpit-model meters (before the per-car scale), rotations in degrees.
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

    /// <summary>"CarId/" for cockpits fitted to one car, so each car keeps its own tweaks.</summary>
    public static string Prefix = "";

    static readonly Dictionary<string, Entry> _entries = new();
    static bool _loaded;

    static string FilePath => Path.Combine(Paths.ConfigPath, "DriverCam_parts.txt");

    public static Entry Get(string group)
    {
        EnsureLoaded();
        return _entries.TryGetValue(Prefix + group, out var e) ? e : Entry.Default;
    }

    public static void Set(string group, Entry entry)
    {
        EnsureLoaded();
        _entries[Prefix + group] = entry;
        Save();
    }

    static bool _dirty;

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
        Save();
    }

    public static void Reset(string group)
    {
        EnsureLoaded();
        _entries.Remove(Prefix + group);
        Save();
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(FilePath)) return;

        var inv = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(FilePath))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 8) continue;
            try
            {
                float F(int i) => float.Parse(t[i], inv);
                _entries[t[0]] = new Entry
                {
                    Position = new Vector3(F(1), F(2), F(3)),
                    Rotation = new Vector3(F(4), F(5), F(6)),
                    Scale = F(7),
                };
            }
            catch (FormatException)
            {
                Plugin.Logger.LogWarning($"Skipping bad line in {FilePath}: {line}");
            }
        }
        Plugin.Logger.LogInfo($"Loaded saved positions for {_entries.Count} cockpit parts.");
    }

    static void Save()
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string> { "# DriverCam cockpit part placement: group  posX posY posZ  rotX rotY rotZ  scale" };
        foreach (var (group, e) in _entries)
            lines.Add(string.Format(inv, "{0} {1:0.####} {2:0.####} {3:0.####} {4:0.##} {5:0.##} {6:0.##} {7:0.####}",
                group, e.Position.x, e.Position.y, e.Position.z, e.Rotation.x, e.Rotation.y, e.Rotation.z, e.Scale));
        try
        {
            File.WriteAllLines(FilePath, lines);
        }
        catch (IOException ex)
        {
            Plugin.Logger.LogError($"Couldn't save cockpit part positions: {ex.Message}");
        }
    }
}

