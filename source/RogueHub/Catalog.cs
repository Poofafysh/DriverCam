using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using RogueShared;

namespace RogueHub
{
    internal enum Kind { Bool, Number, Choice, KeyBind, Text, Action, ReadOnly }

    /// <summary>One row of the hub: a plugin's config entry, or a HubLink button.</summary>
    internal sealed class Setting
    {
        internal Module Module;
        internal ConfigEntryBase Entry;          // null for an action
        internal string Id;                      // "guid|Section|Key" or "act:guid|id" (quick-menu favourites)
        internal string Label, Description, Section;
        internal Kind Kind;
        internal bool Advanced, IsInt;
        internal double Min = double.NaN, Max = double.NaN, Step = 1, Scale = 1, Game = double.NaN, GameShows = double.NaN;
        internal string Unit = "", Applies = "", GameLabel = "GAME";
        internal string[] Choices;               // Kind.Choice
        internal bool KeyIsString;               // Kind.KeyBind stored as a key name string (CurbFeel)
        internal Func<string> Run;               // Kind.Action

        internal bool HasRange => !double.IsNaN(Min) && !double.IsNaN(Max) && Max > Min;
        internal bool HasGame => !double.IsNaN(Game);
        internal Type Type => Entry?.SettingType;

        internal double Num
        {
            get
            {
                try { return Convert.ToDouble(Entry.BoxedValue, CultureInfo.InvariantCulture); }
                catch { return 0; }
            }
        }

        internal bool AtGame => HasGame && Math.Abs(Num - Game) < 1e-6;

        /// <summary>Value differs from the plugin's default (the orange dot).</summary>
        internal bool Changed
        {
            get
            {
                if (Entry == null) return false;
                try
                {
                    if (Kind == Kind.Number) return Math.Abs(Num - Convert.ToDouble(Entry.DefaultValue, CultureInfo.InvariantCulture)) > 1e-6;
                    return !Equals(Entry.BoxedValue, Entry.DefaultValue);
                }
                catch { return false; }
            }
        }
    }

    /// <summary>One plugin: its settings (grouped by config section), its HubLink buttons and status line.</summary>
    internal sealed class Module
    {
        internal string Guid, Name, Version, Area;
        internal ConfigFile Config;
        internal readonly List<Setting> Settings = new List<Setting>();   // in config order, actions first
        internal Setting Enabled;                                         // the plugin's master switch, if any
        internal Func<string> Status;
        internal int AdvancedCount;
    }

    /// <summary>
    /// Every loaded BepInEx plugin and its settings, read from the chainloader (no plugin needs to know about the hub).
    /// Optional "hub:" ConfigDescription tags (HubLink.Meta) give labels, slider ranges, units and GAME / AUTO values;
    /// HubLink's registry gives buttons and status lines. Built when the hub first opens, rebuilt if plugins or
    /// HubLink buttons change.
    /// </summary>
    internal static class Catalog
    {
        internal static readonly string[] Areas = { "FAVES", "DRIVING", "CAMERA", "WORLD", "SCORE", "AUDIO", "SYSTEM" };

        private static readonly Dictionary<string, string> AreaOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rogue.curbfeel"] = "DRIVING", ["rogue.pitstop"] = "DRIVING",
            ["drivingrogue.drivercam"] = "CAMERA", ["rogue.headlook"] = "CAMERA",
            ["rogue.trafficdensity"] = "WORLD", ["rogue.police"] = "WORLD",
            ["rogue.racingline"] = "SCORE", ["rogue.engineaudio"] = "AUDIO",
            // newer plugins (were falling into SYSTEM, where their settings were hard to find)
            ["rogue.sandbox"] = "WORLD", ["rogue.declutter"] = "WORLD",
            ["rogue.bikes"] = "DRIVING", ["rogue.carskins"] = "DRIVING", ["rogue.reverse"] = "DRIVING",
            ["rogue.driver"] = "CAMERA", ["rogue.unreallink"] = "CAMERA",
        };

        /// <summary>Entries a plugin writes itself (never shown). Plugins with HubLink tags mark these with hidden=1.</summary>
        private static readonly HashSet<string> HiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ConfigVersion", "LayoutVersion", "DriverViewSelected", "BestRunTotal", "RefilledThisRun",
        };

        internal static readonly List<Module> Modules = new List<Module>();
        internal static readonly Dictionary<string, Setting> ById = new Dictionary<string, Setting>();
        private static int _pluginCount = -1, _actionCount = -1;

        /// <summary>Rebuild when a plugin or a HubLink button appeared since the last build.</summary>
        internal static void Refresh()
        {
            int plugins = IL2CPPChainloader.Instance?.Plugins?.Count ?? 0;
            int actions = Actions().Count;
            if (plugins == _pluginCount && actions == _actionCount && Modules.Count > 0) return;
            _pluginCount = plugins; _actionCount = actions;
            Build();
        }

        private static List<object[]> Actions()
        {
            try { return (List<object[]>)HubLink.Registry()["actions"]; }
            catch { return new List<object[]>(); }
        }

        private static Dictionary<string, Func<string>> Statuses()
        {
            try { return (Dictionary<string, Func<string>>)HubLink.Registry()["status"]; }
            catch { return new Dictionary<string, Func<string>>(); }
        }

        private static void Build()
        {
            Modules.Clear();
            ById.Clear();
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins == null) return;
            var actions = Actions();
            var statuses = Statuses();
            foreach (var kv in plugins)
            {
                try
                {
                    var info = kv.Value;
                    var plugin = info?.Instance as BasePlugin;
                    if (plugin == null) continue;
                    var meta = info.Metadata;
                    var m = new Module
                    {
                        Guid = meta.GUID,
                        Name = Words(meta.Name).ToUpperInvariant(),
                        Version = Convert.ToString(meta.GetType().GetProperty("Version")?.GetValue(meta), CultureInfo.InvariantCulture) ?? "",   // SemanticVersioning type, read by name
                        Area = AreaOf.TryGetValue(meta.GUID, out var a) ? a : "SYSTEM",
                        Config = plugin.Config,
                    };
                    statuses.TryGetValue(m.Guid, out m.Status);
                    foreach (var act in actions)
                    {
                        if ((string)act[0] != m.Guid) continue;
                        var s = new Setting
                        {
                            Module = m, Kind = Kind.Action, Id = $"act:{m.Guid}|{act[1]}", Label = (string)act[2],
                            Description = (string)act[3], Section = "ACTIONS", Run = act[4] as Func<string>,
                        };
                        m.Settings.Add(s);
                        ById[s.Id] = s;
                    }
                    if (m.Config != null)
                        foreach (var ckv in m.Config)
                        {
                            var s = FromEntry(m, ckv.Value);
                            if (s == null) continue;
                            m.Settings.Add(s);
                            ById[s.Id] = s;
                            if (s.Kind == Kind.Bool && m.Enabled == null && s.Entry.Definition.Key == "Enabled" &&
                                (s.Entry.Definition.Section == "General" || s.Entry.Definition.Section == "Settings"))
                                m.Enabled = s;
                        }
                    // advanced rows go last (shown under a collapsed ADVANCED group)
                    var normal = m.Settings.Where(x => !x.Advanced).ToList();
                    var adv = m.Settings.Where(x => x.Advanced).ToList();
                    m.Settings.Clear();
                    m.Settings.AddRange(normal);
                    m.Settings.AddRange(adv);
                    m.AdvancedCount = adv.Count;
                    if (m.Settings.Count > 0) Modules.Add(m);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] skipped plugin {kv.Key}: {e.Message}"); }
            }
            Modules.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            Plugin.Log.LogInfo($"[RogueHub] catalog: {Modules.Count} plugins, {ById.Count} settings and buttons");
        }

        private static Setting FromEntry(Module m, ConfigEntryBase e)
        {
            if (e == null) return null;
            var def = e.Definition;
            var tag = Tag(e);
            if (tag.ContainsKey("hidden") || HiddenKeys.Contains(def.Key)) return null;
            var s = new Setting
            {
                Module = m, Entry = e, Id = $"{m.Guid}|{def.Section}|{def.Key}",
                Label = tag.TryGetValue("label", out var l) ? l : Sentence(def.Key),
                Description = e.Description?.Description ?? "",
                Section = SectionName(def.Section),
                Advanced = tag.ContainsKey("advanced") || IsDebug(def),
                Applies = tag.TryGetValue("applies", out var ap) ? ap : "",
                Unit = tag.TryGetValue("unit", out var u) ? (u == "deg" ? "°" : u) : "",
            };
            if (tag.TryGetValue("gameLabel", out var gl)) s.GameLabel = gl;
            var t = e.SettingType;
            if (t == typeof(bool)) s.Kind = Kind.Bool;
            else if (t == typeof(UnityEngine.InputSystem.Key) || t == typeof(UnityEngine.KeyCode)) s.Kind = Kind.KeyBind;
            else if (t.IsEnum) { s.Kind = Kind.Choice; s.Choices = Enum.GetNames(t); }
            else if (IsNumber(t)) SetupNumber(s, e, tag);
            else if (t == typeof(string))
            {
                var list = ListValues(e);
                string v = e.BoxedValue as string ?? "";
                if (list != null) { s.Kind = Kind.Choice; s.Choices = list; }
                else if (def.Key.EndsWith("Key", StringComparison.Ordinal) && Enum.TryParse(v, true, out UnityEngine.InputSystem.Key _))
                { s.Kind = Kind.KeyBind; s.KeyIsString = true; }
                else s.Kind = Kind.Text;
            }
            else s.Kind = Kind.ReadOnly;
            return s;
        }

        private static void SetupNumber(Setting s, ConfigEntryBase e, Dictionary<string, string> tag)
        {
            var t = e.SettingType;
            s.Kind = Kind.Number;
            s.IsInt = t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte);
            // range: BepInEx AcceptableValueRange first, then the hub tag
            var av = e.Description?.AcceptableValues;
            if (av != null)
            {
                try
                {
                    var min = av.GetType().GetProperty("MinValue")?.GetValue(av);
                    var max = av.GetType().GetProperty("MaxValue")?.GetValue(av);
                    if (min != null && max != null)
                    {
                        s.Min = Convert.ToDouble(min, CultureInfo.InvariantCulture);
                        s.Max = Convert.ToDouble(max, CultureInfo.InvariantCulture);
                    }
                }
                catch { /* not a range */ }
            }
            if (tag.TryGetValue("min", out var mn) && TryNum(mn, out double dmn)) s.Min = dmn;
            if (tag.TryGetValue("max", out var mx) && TryNum(mx, out double dmx)) s.Max = dmx;
            if (tag.TryGetValue("scale", out var sc) && TryNum(sc, out double dsc) && dsc != 0) s.Scale = dsc;
            if (tag.TryGetValue("game", out var g) && TryNum(g, out double dg)) s.Game = dg;
            if (tag.TryGetValue("gameShows", out var gs) && TryNum(gs, out double dgs)) s.GameShows = dgs;
            if (tag.TryGetValue("step", out var st) && TryNum(st, out double dst) && dst > 0) s.Step = dst;
            else if (s.IsInt) s.Step = 1;
            else if (s.HasRange) s.Step = Nice((s.Max - s.Min) / 100.0);
            else
            {
                double d = Math.Abs(SafeNum(e.DefaultValue));
                s.Step = d >= 100 ? 5 : d >= 10 ? 1 : d >= 1 ? 0.1 : 0.01;
            }
            if (s.IsInt) s.Step = Math.Max(1, Math.Round(s.Step));
        }

        /// <summary>1, 2 or 5 times a power of ten, at or below x.</summary>
        private static double Nice(double x)
        {
            if (x <= 0) return 0.01;
            double p = Math.Pow(10, Math.Floor(Math.Log10(x)));
            double f = x / p;
            return (f >= 5 ? 5 : f >= 2 ? 2 : 1) * p;
        }

        private static double SafeNum(object o)
        {
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static bool TryNum(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static bool IsNumber(Type t) =>
            t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long) || t == typeof(short) || t == typeof(byte);

        private static string[] ListValues(ConfigEntryBase e)
        {
            var av = e.Description?.AcceptableValues;
            if (av == null) return null;
            try
            {
                var vals = av.GetType().GetProperty("AcceptableValues")?.GetValue(av) as Array;
                if (vals == null || vals.Length == 0) return null;
                var list = new string[vals.Length];
                for (int i = 0; i < vals.Length; i++) list[i] = Convert.ToString(vals.GetValue(i), CultureInfo.InvariantCulture);
                return list;
            }
            catch { return null; }
        }

        private static bool IsDebug(ConfigDefinition d) =>
            d.Section.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0 ||
            d.Section.Equals("Perf", StringComparison.OrdinalIgnoreCase) ||
            d.Key.StartsWith("Log", StringComparison.Ordinal) || d.Key.StartsWith("Verbose", StringComparison.Ordinal) ||
            d.Key.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The "hub:k=v;k=v" tag from HubLink.Meta, if the entry has one.</summary>
        internal static Dictionary<string, string> Tag(ConfigEntryBase e)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var tags = e.Description?.Tags;
                if (tags == null) return d;
                foreach (var o in tags)
                {
                    if (o is not string s || !s.StartsWith("hub:", StringComparison.Ordinal)) continue;
                    foreach (var part in s.Substring(4).Split(';'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq > 0) d[part.Substring(0, eq)] = part.Substring(eq + 1);
                    }
                }
            }
            catch { /* no tags */ }
            return d;
        }

        /// <summary>"B.Walls" -> "WALLS", "General" -> "GENERAL".</summary>
        private static string SectionName(string section)
        {
            string s = section ?? "";
            if (s.Length > 2 && s[1] == '.' && char.IsLetter(s[0])) s = s.Substring(2);
            return Words(s).ToUpperInvariant();
        }

        /// <summary>"TrafficDensity" -> "Traffic Density"; keeps acronyms ("HUD", "FOV") together.</summary>
        internal static string Words(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if (c == '_' || c == '.') { sb.Append(' '); continue; }
                bool boundary = i > 0 && char.IsUpper(c) &&
                                (char.IsLower(id[i - 1]) || (i + 1 < id.Length && char.IsLower(id[i + 1]) && char.IsUpper(id[i - 1])));
                if (i > 0 && char.IsDigit(c) && char.IsLetter(id[i - 1])) boundary = true;
                if (boundary) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>"MaxPatrols" -> "Max patrols" (acronyms kept).</summary>
        private static string Sentence(string key)
        {
            var words = Words(key).Split(' ');
            for (int i = 1; i < words.Length; i++)
                if (words[i].Length > 1 && !words[i].All(char.IsUpper)) words[i] = words[i].ToLowerInvariant();
            return string.Join(" ", words);
        }

        // ------------------------------------------------------------------ values

        /// <summary>Shown value: number with the step's decimals and the unit, GAME / AUTO, ON/OFF, list item, key.</summary>
        internal static string Show(Setting s)
        {
            if (s.Kind == Kind.Action) return "";
            object v = s.Entry.BoxedValue;
            switch (s.Kind)
            {
                case Kind.Bool: return (bool)v ? "ON" : "OFF";
                case Kind.Number:
                    if (s.AtGame) return double.IsNaN(s.GameShows) ? s.GameLabel : $"{s.GameLabel} {Fmt(s, s.GameShows)}";
                    return Fmt(s, s.Num) + (s.Unit.Length > 0 ? (s.Unit == "%" || s.Unit == "°" ? s.Unit : " " + s.Unit) : "");
                case Kind.KeyBind: return Convert.ToString(v, CultureInfo.InvariantCulture)?.ToUpperInvariant() ?? "NONE";
                default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            }
        }

        internal static string Fmt(Setting s, double stored)
        {
            double shown = stored * s.Scale;
            return shown.ToString("F" + Decimals(s), CultureInfo.InvariantCulture);
        }

        internal static int Decimals(Setting s)
        {
            if (s.IsInt && s.Scale == 1) return 0;
            double step = Math.Abs(s.Step * s.Scale);
            if (step <= 0) return 2;
            int d = (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
            return Math.Max(0, Math.Min(3, d));
        }

        /// <summary>Writes a value (converted to the entry's type). Returns false if it couldn't be stored.</summary>
        internal static bool Write(Setting s, object value)
        {
            try
            {
                var t = s.Entry.SettingType;
                object v = t.IsEnum
                    ? (value is string str ? Enum.Parse(t, str, true)
                       : value is Enum en ? Enum.ToObject(t, Convert.ToInt64(en, CultureInfo.InvariantCulture))
                       : Enum.ToObject(t, value))
                    : Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
                if (Equals(v, s.Entry.BoxedValue)) return true;
                Overlays.Touched(s);
                Saver.Touch(s.Module.Config);
                s.Entry.BoxedValue = v;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RogueHub] couldn't set {s.Id}: {e.Message}");
                return false;
            }
        }

        /// <summary>Sets a number from a stored-units value: rounded to the step, clamped to the range. Returns true if it was clamped.</summary>
        internal static bool SetNumber(Setting s, double stored)
        {
            bool clamped = false;
            if (s.HasRange)
            {
                if (stored < s.Min - 1e-9) { stored = s.Min; clamped = true; }
                if (stored > s.Max + 1e-9) { stored = s.Max; clamped = true; }
            }
            if (s.IsInt) stored = Math.Round(stored);
            else stored = Math.Round(stored, Math.Min(6, Decimals(s) + 3));
            Write(s, stored);
            return clamped;
        }

        /// <summary>
        /// One press left / right on a number: by the step (x10 with Shift / long holds). A GAME / AUTO value sits one
        /// notch below the range: stepping down at the minimum switches to it, stepping up from it comes back.
        /// </summary>
        internal static void Nudge(Setting s, int dir, double mult)
        {
            switch (s.Kind)
            {
                case Kind.Bool: Write(s, !(bool)s.Entry.BoxedValue); return;
                case Kind.Choice:
                {
                    string cur = Convert.ToString(s.Entry.BoxedValue, CultureInfo.InvariantCulture);
                    int i = Array.FindIndex(s.Choices, c => string.Equals(c, cur, StringComparison.OrdinalIgnoreCase));
                    int n = s.Choices.Length;
                    Write(s, s.Choices[((i < 0 ? 0 : i) + dir + n) % n]);
                    return;
                }
                case Kind.Number:
                {
                    if (s.AtGame)
                    {
                        if (dir > 0) SetNumber(s, !double.IsNaN(s.GameShows) && (!s.HasRange || (s.GameShows >= s.Min && s.GameShows <= s.Max)) ? s.GameShows : s.HasRange ? s.Min : 0);
                        return;
                    }
                    double v = s.Num + dir * s.Step * mult;
                    if (s.HasGame && s.HasRange && dir < 0 && s.Num <= s.Min + 1e-9) { Write(s, s.Game); return; }
                    SetNumber(s, v);
                    return;
                }
            }
        }

        /// <summary>The GAME / AUTO switch: on = the plugin's "game value", off = back to the game's own number (or the range start).</summary>
        internal static void ToggleGame(Setting s)
        {
            if (!s.HasGame) return;
            if (s.AtGame) Nudge(s, 1, 1);
            else Write(s, s.Game);
        }

        internal static void Reset(Setting s)
        {
            if (s.Entry == null) return;
            Overlays.Touched(s);
            Saver.Touch(s.Module.Config);
            s.Entry.BoxedValue = s.Entry.DefaultValue;
        }

        internal static string Range(Setting s)
        {
            if (s.Kind == Kind.Choice) return string.Join(", ", s.Choices.Take(6)) + (s.Choices.Length > 6 ? ", ..." : "");
            if (s.Kind != Kind.Number) return "";
            string r = s.HasRange ? $"{Fmt(s, s.Min)} - {Fmt(s, s.Max)}" : "any";
            if (s.HasGame) r += $", or {s.GameLabel}";
            return r;
        }

        internal static string DefaultText(Setting s)
        {
            if (s.Entry == null) return "";
            if (s.Kind == Kind.Number)
            {
                double d = SafeNum(s.Entry.DefaultValue);
                if (s.HasGame && Math.Abs(d - s.Game) < 1e-6) return s.GameLabel;
                return Fmt(s, d) + (s.Unit.Length > 0 ? " " + s.Unit : "");
            }
            if (s.Kind == Kind.Bool) return (bool)s.Entry.DefaultValue ? "ON" : "OFF";
            return Convert.ToString(s.Entry.DefaultValue, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Saving: a slider held down would rewrite the plugin's .cfg file many times a second, so while the hub changes a
    /// config its SaveOnConfigSet is switched off and the file is written once, a second after the last change (and
    /// when the hub closes). A config whose flag someone else already switched off (DriverCam's Edit mode does) is
    /// left alone: its owner saves it. The flag is put back only if it still holds the hub's "off".
    /// (Plugins with their own files, like DriverCam's per-car presets, still write those on every change.)
    /// </summary>
    internal static class Saver
    {
        private static readonly HashSet<ConfigFile> Flipped = new HashSet<ConfigFile>();   // flag switched off by the hub
        private static readonly HashSet<ConfigFile> Dirty = new HashSet<ConfigFile>();
        private static float _flushAt;

        internal static void Touch(ConfigFile cfg)
        {
            if (cfg == null) return;
            if (!Flipped.Contains(cfg) && cfg.SaveOnConfigSet) { cfg.SaveOnConfigSet = false; Flipped.Add(cfg); }
            if (Flipped.Contains(cfg)) Dirty.Add(cfg);
            _flushAt = UnityEngine.Time.unscaledTime + 1f;
        }

        internal static void Tick()
        {
            if (Flipped.Count > 0 && UnityEngine.Time.unscaledTime >= _flushAt) Flush();
        }

        internal static void Flush()
        {
            foreach (var cfg in Flipped)
            {
                if (Dirty.Contains(cfg))
                    try { cfg.Save(); } catch (Exception e) { Plugin.Log.LogWarning($"[RogueHub] save failed: {e.Message}"); }
                try { if (!cfg.SaveOnConfigSet) cfg.SaveOnConfigSet = true; } catch { /* plugin gone */ }
            }
            Flipped.Clear();
            Dirty.Clear();
        }
    }
}
