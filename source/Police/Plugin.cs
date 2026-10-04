using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace Police
{
    /// <summary>
    /// Police: patrol cars picked from the game's own traffic notice what you do (crashing near them, blasting past
    /// them) and chase you. Design: "Police Pursuit - v1 Concept" (claude.ai doc, revised): police are a consequence of
    /// what the player does, never random escalation; no heat levels, nothing carries over between races.
    ///
    /// 0.0.1 = doc phases 1-3 in "pursuit lite" form: patrols (traffic cars with a lightbar), noticing, and a chase by the
    /// traffic AI itself with a lead bar (ESCAPED / CAUGHT). Single-player only. No Harmony patches, no score category yet.
    /// </summary>
    [BepInPlugin(Guid, "Police", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.police";
        public const string Version = "0.5.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, LogEvents, Markers, NoticeNearMiss, NoticeDrift;
        internal static ConfigEntry<string> Mode, CarModels, Livery;
        internal static ConfigEntry<int> MaxPatrols, MaxChasers, ConfigVersion;
        internal static ConfigEntry<float> PatrolSpacing, NoticeRange, OverspeedKmh, CloseLaneMetres, PassWindow;
        internal static ConfigEntry<float> Duration, CaughtPenaltySeconds, Cooldown, SpeedFactor, ChaseSmoothness, BackupPenalty;
        internal static ConfigEntry<bool> DareEnabled, DareBossLooks, DareRaceLine, DareDefend, DareSlipstream;
        internal static ConfigEntry<string> DareDriftCars;
        internal static ConfigEntry<float> DareSpeedFactor, DareSkillMin, DareSkillMax, DareDriftCornerFactor, DareMaxSlip;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Master switch. F3 turns patrols off / on for the current session.");
            Mode = Config.Bind("General", "Mode", "Normal",
                new ConfigDescription("Normal = patrols notice you and chase. Chill = patrols drive around with their lightbars but never notice you. Off = no patrols.",
                                      new AcceptableValueList<string>("Normal", "Chill", "Off")));
            MaxPatrols = Config.Bind("Patrols", "MaxPatrols", 3, "Most patrol cars on the road at once (0-4). Patrols are traffic cars turned into police cars.");
            PatrolSpacing = Config.Bind("Patrols", "PatrolSpacing", 800f, "On average one new patrol per this many metres driven (300-10000).");
            CarModels = Config.Bind("Look", "CarModels", "Police",
                new ConfigDescription("What patrol cars look like (the traffic car underneath is never drawn). Police = the plugin's own police cars (Interceptor sedan, Pursuit coupe, Utility SUV). Boss = the game's boss cars (each boss's own car and body kit) in a police livery. Traffic = the traffic car's own look with a lightbar.",
                                      new AcceptableValueList<string>("Police", "Boss", "Traffic")));
            Livery = Config.Bind("Look", "Livery", "Classic",
                new ConfigDescription("Classic = black and white. Interceptor = all black (boss cars: with blue trim). Boss = the boss car's own paint (Police models: Classic).",
                                      new AcceptableValueList<string>("Classic", "Interceptor", "Boss")));
            Markers = Config.Bind("Look", "Markers", true, "A 3D marker floats above patrol cars further than ~35 m away (blue patrol, amber you're in its zone, red chasing).");
            NoticeRange = Config.Bind("Notice", "NoticeRange", 90f, "Within this many metres of a patrol (along the road) its marker turns amber: a pass here is being watched (10-200). Police only ever engage on a reckless pass.");
            OverspeedKmh = Config.Bind("Notice", "OverspeedKmh", 35f, "A pass is reckless when you go by a patrol this many km/h faster than it is going (10-300).");
            CloseLaneMetres = Config.Bind("Notice", "CloseLaneMetres", 3.5f, "A pass is reckless when you cut by closer than this across (metres between your line and the patrol's) at 15+ km/h faster (0 = off; lanes are 5 m apart).");
            PassWindow = Config.Bind("Notice", "PassWindow", 1.5f, "Seconds before and after a pass in which a crash, near miss or drift makes that pass reckless (0.2-5).");
            NoticeNearMiss = Config.Bind("Notice", "NearMiss", true, "A near miss during a pass makes it reckless.");
            NoticeDrift = Config.Bind("Notice", "Drift", true, "Drifting past a patrol makes the pass reckless.");
            MaxChasers = Config.Bind("Chase", "MaxChasers", 3, "Most police cars chasing you at once (1-4). During a chase, a patrol you pass recklessly joins in as backup.");
            BackupPenalty = Config.Bind("Chase", "BackupPenalty", 5f, "Lead (%) lost each time a backup unit joins the chase (0-50).");
            Duration = Config.Bind("Chase", "Duration", 45f, "Seconds a chase lasts at most; then the lead bar decides (above 50% = escaped) (10-300).");
            CaughtPenaltySeconds = Config.Bind("Chase", "CaughtPenaltySeconds", 5f,
                "Seconds taken off the race timer when caught (0-30, 0 = none). Only on a running countdown timer, and never so much that it would end the race (at least 3 s stay).");
            Cooldown = Config.Bind("Chase", "Cooldown", 8f, "Seconds after a chase ends before any patrol can notice you again (0-300).");
            SpeedFactor = Config.Bind("Chase", "SpeedFactor", 1f,
                "Chasing cars inherit your car's stats: their top speed is this fraction of your car's current top speed, upgrades and boosts included (0.5-1.1). 1 = exactly yours.");
            ChaseSmoothness = Config.Bind("Chase", "Acceleration", 0.8f,
                "How quickly a chasing car reaches its top speed: the traffic AI's speed smoothing time in seconds while chasing (0.2-5; lower = quicker; it never gets slower than the car's own).");
            DareEnabled = Config.Bind("Daredevils", "Enabled", true,
                "The game's red daredevil cars (the devil icon when one is close behind you) become rivals: drawn as the game's boss cars and racing the optimal racing line. Independent of patrols (Mode, F3). Single-player only.");
            DareBossLooks = Config.Bind("Daredevils", "BossLooks", true, "Draw daredevils as the game's boss cars (each boss's own car, paint and body kit). Off = the game's red traffic car.");
            DareRaceLine = Config.Bind("Daredevils", "RaceLine", true,
                "Daredevils drive the optimal racing line (needs the RacingLine plugin; without it they keep the game's driving), overtaking traffic and you round the line.");
            DareDriftCars = Config.Bind("Daredevils", "DriftCars", "Rotary, Delivery, Centipede, Centaur",
                "Boss cars that drift through corners (comma-separated car names). Every other car is a grip car: it holds the line without sliding and carries more corner speed.");
            DareSkillMin = Config.Bind("Daredevils", "SkillMin", 0.90f,
                "Rivals drive YOUR car's numbers (top speed, measured cornering, braking) scaled by a skill. Each boss has a fixed skill, spread from SkillMin to SkillMax in the game's boss order. This is the slowest (0.5-1.5; 1 = your pace).");
            DareSkillMax = Config.Bind("Daredevils", "SkillMax", 1.10f, "The fastest boss's skill (0.5-1.5). With 0.90-1.10 half the bosses are quicker than you, half slower.");
            DareDriftCornerFactor = Config.Bind("Daredevils", "DriftCornerFactor", 0.93f, "Drift cars' cornering grip against grip cars' (0.6-1.2): a little slower through corners, but sliding.");
            DareSpeedFactor = Config.Bind("Daredevils", "SpeedFactor", 1f, "A multiplier on every rival's pace (top speed) on top of its skill (0.5-1.5).");
            DareDefend = Config.Bind("Daredevils", "Defend", true, "Rivals cover the inside line before a corner when you close in from 25-60 m behind (one move per corner; it gives way rather than move across you).");
            DareSlipstream = Config.Bind("Daredevils", "Slipstream", true, "Rivals tow behind other cars (within 30 m, lined up within 1.5 m): +6% top speed.");
            DareMaxSlip = Config.Bind("Daredevils", "MaxSlipAngle", 30f, "How far drift cars slide (degrees between where the car points and where it goes) in a hard corner (0-50).");
            LogEvents = Config.Bind("Debug", "LogEvents", true, "Log patrols picked and released, notices, chases and their outcome (for /game-log).");
            ConfigVersion = Config.Bind("Debug", "ConfigVersion", 0, "Written by the plugin (settings migration). Don't edit.");
            Migrate();

            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[Police] game check crashed, plugin stays idle: {e}"); return; }

            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            ClassInjector.RegisterTypeInIl2Cpp<Daredevils>();
            AddComponent<Daredevils>();
            Log.LogInfo($"Police {Version} loaded (police-car patrols that only engage on a reckless pass, backup units, chases that match your car; daredevils as boss cars racing the racing line; single-player only). F3 turns patrols off / on.");
        }

        /// <summary>
        /// Settings written by an older version keep their old values (BepInEx never overwrites a saved value). Values
        /// still at an old default move to the new, stricter default once; anything the player changed is kept.
        /// </summary>
        private void Migrate()
        {
            if (ConfigVersion.Value >= 4) return;
            var moved = new System.Collections.Generic.List<string>();
            // 0.5.0: daredevil cornering comes from your car x skill; the fixed GripCornering / DriftCornering keys go
            foreach (var key in new[] { "GripCornering", "DriftCornering" })
            {
                var def = new ConfigDefinition("Daredevils", key);
                bool existed = !float.IsNaN(Config.Bind(def, float.NaN).Value);   // a value from the file replaces the NaN default
                Config.Remove(def);
                if (existed) moved.Add($"Daredevils.{key} removed (cornering now follows your car)");
            }
            if (ConfigVersion.Value >= 3) { ConfigVersion.Value = 4; Config.Save(); if (moved.Count > 0) Log.LogInfo($"[Police] settings updated: {string.Join(", ", moved)}"); return; }
            // 0.3.0: Look.BossCars (on/off) became Look.CarModels; an old "off" keeps the traffic look, and the old entry goes
            var bossCars = new ConfigDefinition("Look", "BossCars");
            var old = Config.Bind(bossCars, true);
            if (!old.Value) { CarModels.Value = "Traffic"; moved.Add("BossCars false -> CarModels Traffic"); }
            Config.Remove(bossCars);
            void Move(ConfigEntry<float> e, float oldDefault)
            {
                if (Math.Abs(e.Value - oldDefault) < 1e-4f && Math.Abs(e.Value - (float)e.DefaultValue) > 1e-4f)
                { moved.Add($"{e.Definition.Key} {oldDefault} -> {e.DefaultValue}"); e.Value = (float)e.DefaultValue; }
            }
            if (ConfigVersion.Value < 1)
            {
                Move(NoticeRange, 60f); Move(OverspeedKmh, 80f); Move(Duration, 40f); Move(Cooldown, 20f); Move(SpeedFactor, 0.97f);
                if (MaxPatrols.Value == 2) { MaxPatrols.Value = 3; moved.Add("MaxPatrols 2 -> 3"); }
            }
            if (ConfigVersion.Value < 2) Move(BackupPenalty, 10f);   // 0.1.1: busts came far too fast
            ConfigVersion.Value = 4;
            Config.Save();
            if (moved.Count > 0) Log.LogInfo($"[Police] settings updated to the current defaults (values you had changed are kept): {string.Join(", ", moved)}");
        }
    }
}
