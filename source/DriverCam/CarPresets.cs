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
        MigrateInterior(car, path, values);
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

    /// <summary>
    /// The Part.Interior offsets the shipped setups had for the layout-1 cockpits. A layout-2 cockpit (modelled interior)
    /// is already built at that tuned place, so applying the old offset again would shift it twice.
    /// </summary>
    static readonly Dictionary<string, float[]> OldInterior = new()
    {
        ["Bond"] = new[] { -0.0088f, 0.1546f, 0.2691f, 0f, -3.03f, 0f, 0.86f },
        ["Centaur"] = new[] { -0.0293f, 0.1279f, 0.39f, 0f, -1.81f, 0f, 0.88f },
        ["Centipede"] = new[] { 0.0462f, 0.1808f, 0.5221f, 0f, -2.39f, 0f, 0.86f },
        ["Delivery"] = new[] { 0.0205f, 0.1557f, 0.4437f, 0f, -2.17f, 0f, 0.84f },
        ["Justice"] = new[] { -0.0032f, 0.1367f, 0.5766f, 0f, -2.38f, 0f, 0.785f },
        ["Phoenix"] = new[] { 0.0101f, 0.1619f, 0.3822f, 0f, -2.75f, 0f, 0.84f },
        ["Rotary"] = new[] { 0.017f, 0.2234f, 0.1503f, 0f, -2.62f, 0f, 0.82f },
        ["Saber"] = new[] { 0.0177f, 0.1079f, 0.2901f, 0f, 0.05f, 0f, 0.88f },
        ["Shadow"] = new[] { -0.0548f, 0.2136f, 0.2302f, 0f, -0.16f, 0f, 0.82f },
        ["Vektor"] = new[] { 0.0248f, 0.1737f, 0.0916f, 0f, -1.18f, 0f, 0.84f },
    };

    /// <summary>
    /// Once, for a layout-2 cockpit: a saved Part.Interior that is still (within 6 cm / 1.5 deg / 0.05 scale) the old
    /// shipped offset for this car is reset to identity and the file saved. A value tuned for the new interior never
    /// resembles those, so it is never touched.
    /// </summary>
    static void MigrateInterior(string car, string path, Dictionary<string, string> values)
    {
        if (Cockpit.Current == null || Cockpit.Current.Layout < 2) return;
        if (!OldInterior.TryGetValue(car, out var old) || !values.TryGetValue("Part.Interior", out var v)) return;
        var p = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 7) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < 7; i++)
        {
            if (!float.TryParse(p[i], System.Globalization.NumberStyles.Float, inv, out var f)) return;
            float tol = i < 3 ? 0.06f : i < 6 ? 1.5f : 0.05f;
            if (Math.Abs(f - old[i]) > tol) return;
        }
        values["Part.Interior"] = "0 0 0 0 0 0 1";
        Plugin.Logger.LogInfo($"{car}: the modelled interior is already built at your tuned dash position; reset the old Part.Interior offset ({v}) in {Path.GetFileName(path)}.");
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
