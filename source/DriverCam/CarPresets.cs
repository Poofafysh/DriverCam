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
    /// <summary>Tuned setups that ship with the mod (plugins/DriverCam/cars), used for cars that haven't been set up yet.</summary>
    static string SharedFor(string car) => Path.Combine(Paths.PluginPath, "DriverCam", "cars", car + ".cfg");
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

    /// <summary>
    /// Gives every car a settings file: the shared tuned setup that ships with the mod if there is one (also
    /// replacing a file that still only has the untouched defaults), otherwise the default settings.
    /// </summary>
    public static void CreateMissing(IEnumerable<string> cars)
    {
        var created = new List<string>();
        var shared = new List<string>();
        foreach (var car in cars)
        {
            bool exists = File.Exists(FileFor(car));
            if (File.Exists(SharedFor(car)) && (!exists || IsUntouched(Read(FileFor(car)))))
            {
                if (CopyShared(car)) shared.Add(car);
                continue;
            }
            if (exists) continue;
            var lines = Header(car);
            foreach (var e in _entries) lines.Add($"{Key(e)} = {TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType)}");
            lines.AddRange(PartLayout.LinesFor(car));
            if (Write(car, lines)) created.Add(car);
        }
        if (shared.Count > 0) Plugin.Logger.LogInfo($"Using the shared tuned setup for: {string.Join(", ", shared)}.");
        if (created.Count > 0) Plugin.Logger.LogInfo($"Created default DriverCam settings files for: {string.Join(", ", created)}.");
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

        var values = Read(path);
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

    public static bool HasShared => _car != null && File.Exists(SharedFor(_car));

    /// <summary>Replaces the current car's settings with the shared tuned setup and loads it.</summary>
    public static void UseShared()
    {
        var car = _car;
        if (car == null || !CopyShared(car)) return;
        _car = null;
        SelectCar(car);
        Plugin.Logger.LogInfo($"{car}: switched to the shared tuned setup.");
    }

    static bool CopyShared(string car)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.Copy(SharedFor(car), FileFor(car), true);
            return true;
        }
        catch (IOException ex)
        {
            Plugin.Logger.LogError($"Couldn't copy the shared setup for {car}: {ex.Message}");
            return false;
        }
    }

    static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (line.StartsWith("#") || eq <= 0) continue;
            values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return values;
    }

    /// <summary>True for a file nobody has tuned yet: no part positions and every setting at its default.</summary>
    static bool IsUntouched(Dictionary<string, string> values)
    {
        foreach (var key in values.Keys)
            if (key.StartsWith("Part.", StringComparison.Ordinal)) return false;
        foreach (var e in _entries)
            if (values.TryGetValue(Key(e), out var v) && v != TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType)) return false;
        return true;
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
