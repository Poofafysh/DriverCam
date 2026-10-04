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
        public const string Version = "0.0.2";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, LogEvents;
        internal static ConfigEntry<string> Mode;
        internal static ConfigEntry<int> MaxPatrols;
        internal static ConfigEntry<float> PatrolSpacing, NoticeRange, OverspeedKmh;
        internal static ConfigEntry<float> Duration, CaughtPenaltySeconds, Cooldown, SpeedFactor;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Master switch. F3 turns patrols off / on for the current session.");
            Mode = Config.Bind("General", "Mode", "Normal",
                new ConfigDescription("Normal = patrols notice you and chase. Chill = patrols drive around with their lightbars but never notice you. Off = no patrols.",
                                      new AcceptableValueList<string>("Normal", "Chill", "Off")));
            MaxPatrols = Config.Bind("Patrols", "MaxPatrols", 2, "Most patrol cars on the road at once (0-4). Patrols are ordinary traffic cars given a lightbar.");
            PatrolSpacing = Config.Bind("Patrols", "PatrolSpacing", 800f, "On average one new patrol per this many metres driven (300-10000).");
            NoticeRange = Config.Bind("Notice", "NoticeRange", 60f, "A patrol only notices you within this many metres along the road, ahead or behind (10-200).");
            OverspeedKmh = Config.Bind("Notice", "OverspeedKmh", 80f, "Passing a patrol this many km/h faster than it is going gets you noticed (10-300).");
            Duration = Config.Bind("Chase", "Duration", 40f, "Seconds a chase lasts at most; then the lead bar decides (above 50% = escaped) (10-300).");
            CaughtPenaltySeconds = Config.Bind("Chase", "CaughtPenaltySeconds", 5f,
                "Seconds taken off the race timer when caught (0-30, 0 = none). Only on a running countdown timer, and never so much that it would end the race (at least 3 s stay).");
            Cooldown = Config.Bind("Chase", "Cooldown", 20f, "Seconds after a chase ends before any patrol can notice you again (0-300).");
            SpeedFactor = Config.Bind("Chase", "SpeedFactor", 0.97f,
                "The chasing car's top speed as a fraction of your car's top speed (0.5-0.99). Below 1 so it can keep up but never out-run you.");
            LogEvents = Config.Bind("Debug", "LogEvents", true, "Log patrols picked and released, notices, chases and their outcome (for /game-log).");

            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[Police] game check crashed, plugin stays idle: {e}"); return; }

            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            Log.LogInfo($"Police {Version} loaded (pursuit lite: patrols, noticing, chase with lead bar; single-player only). F3 turns patrols off / on.");
        }
    }
}
