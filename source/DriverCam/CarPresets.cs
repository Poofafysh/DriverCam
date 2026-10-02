using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace DriverCam;

/// <summary>
/// One settings file per car: BepInEx/config/DriverCam_cars/&lt;Car&gt;.cfg holds that car's driver-view settings
/// (seat, view, cockpit look, mirrors) and its cockpit part positions. It is loaded when that car is driven.
/// Every car with a fitted cockpit gets a file at startup, starting from the default settings.
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

    /// <summary>Writes a default settings file for every car that has a fitted cockpit and no file yet.</summary>
    public static void CreateMissing(IEnumerable<string> cars)
    {
        var created = new List<string>();
        foreach (var car in cars)
        {
            if (File.Exists(FileFor(car))) continue;
            var lines = Header(car);
            foreach (var e in _entries) lines.Add($"{Key(e)} = {TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType)}");
            lines.AddRange(PartLayout.LinesFor(car));
            if (Write(car, lines)) created.Add(car);
        }
        if (created.Count > 0) Plugin.Logger.LogInfo($"Created DriverCam settings files for: {string.Join(", ", created)}.");
    }

    /// <summary>Switches to a car's settings when the driven car changes.</summary>
    public static void SelectCar(string car)
    {
        if (string.IsNullOrEmpty(car) || car == _car) return;
        _car = car;
        var path = FileFor(car);
        if (!File.Exists(path))
        {
            Save();
            Plugin.Logger.LogInfo($"New DriverCam settings file for {car} (started from the current settings).");
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
            PartLayout.LoadCar(car, values);
        }
        finally
        {
            _loading = false;
        }
        Save();   // brings older files up to date (part positions, new settings)
        Plugin.SettingsVersion++;
        DriverMode.Apply();
        Plugin.Logger.LogInfo($"Loaded DriverCam settings for {car}.");
    }

    /// <summary>Writes the current car's file.</summary>
    public static void Save()
    {
        if (_car == null) return;
        var lines = Header(_car);
        foreach (var e in _entries) lines.Add($"{Key(e)} = {e.GetSerializedValue()}");
        lines.AddRange(PartLayout.LinesFor(_car));
        Write(_car, lines);
    }

    static List<string> Header(string car) => new()
    {
        $"# DriverCam settings for {car}",
        "# Part.<name> = move x y z   turn x y z   size  (cockpit part positions)",
    };

    static bool Write(string car, List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(FileFor(car), lines);
            return true;
        }
        catch (IOException ex)
        {
            Plugin.Logger.LogError($"Couldn't save the {car} settings: {ex.Message}");
            return false;
        }
    }
}
