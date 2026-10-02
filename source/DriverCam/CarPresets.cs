using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace DriverCam;

/// <summary>
/// Per-car presets: the driver-view settings (seat, view, cockpit look, mirrors) are saved separately for every
/// car in BepInEx/config/DriverCam_cars/&lt;Car&gt;.cfg and loaded when that car is driven. A car without a preset
/// starts from the current values.
/// </summary>
internal static class CarPresets
{
    static readonly List<ConfigEntryBase> _entries = new();
    static string _car;
    static bool _loading;

    /// <summary>While true, changes aren't written straight away (Edit mode writes once the stick is released).</summary>
    public static bool Suspended;

    static string Folder => Path.Combine(Paths.ConfigPath, "DriverCam_cars");
    static string FileFor(string car) => Path.Combine(Folder, car + ".cfg");
    static string Key(ConfigEntryBase e) => e.Definition.Section + "." + e.Definition.Key;

    static readonly HashSet<ConfigFile> _watched = new();

    public static void Track(IEnumerable<ConfigEntryBase> entries)
    {
        foreach (var e in entries)
        {
            if (e == null || _entries.Contains(e)) continue;
            _entries.Add(e);
            if (_watched.Add(e.ConfigFile)) e.ConfigFile.SettingChanged += OnSettingChanged;
        }
    }

    static void OnSettingChanged(object sender, SettingChangedEventArgs args)
    {
        if (!_loading && !Suspended && _entries.Contains(args.ChangedSetting)) Save();
    }

    /// <summary>Switches presets when the driven car changes.</summary>
    public static void SelectCar(string car)
    {
        if (string.IsNullOrEmpty(car) || car == _car) return;
        _car = car;
        var path = FileFor(car);
        if (!File.Exists(path))
        {
            Save();
            Plugin.Logger.LogInfo($"New DriverCam preset for {car} (started from the current settings).");
            return;
        }

        var values = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (line.StartsWith("#") || eq <= 0) continue;
            values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        _loading = true;
        try
        {
            foreach (var e in _entries)
                if (values.TryGetValue(Key(e), out var v))
                {
                    try { e.SetSerializedValue(v); }
                    catch (Exception) { Plugin.Logger.LogWarning($"Bad value for {Key(e)} in {path}: {v}"); }
                }
        }
        finally
        {
            _loading = false;
        }
        Plugin.SettingsVersion++;
        DriverMode.Apply();
        Plugin.Logger.LogInfo($"Loaded DriverCam preset for {car}.");
    }

    public static void Save()
    {
        if (_car == null) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var lines = new List<string> { $"# DriverCam preset for {_car}" };
            foreach (var e in _entries) lines.Add($"{Key(e)} = {e.GetSerializedValue()}");
            File.WriteAllLines(FileFor(_car), lines);
        }
        catch (IOException ex)
        {
            Plugin.Logger.LogError($"Couldn't save the {_car} preset: {ex.Message}");
        }
    }
}
