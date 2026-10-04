using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace RacingLine
{
    /// <summary>
    /// Racing Line: points for holding the optimal line through corners. Design: "Racing Line Mechanic - Design &amp; Build
    /// Plan" (claude.ai artifact 799a19cf-48f1-4019-9d37-925b9838d47f).
    ///
    /// Builds the minimum-curvature line from the run's centre-line path, previews it on the road, scores corners
    /// (LineScorer) and, in single-player, adds Racing Line as a real score category with its own results row
    /// (GameApi.Native / GameApi.Results). No Harmony patches.
    /// </summary>
    [BepInPlugin(Guid, "RacingLine", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.racingline";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShowLine;
        internal static ConfigEntry<float> DrawAhead;
        internal static ConfigEntry<float> Margin;
        internal static ConfigEntry<float> SampleStep;
        internal static ConfigEntry<float> FrameBudgetMs;
        internal static ConfigEntry<bool> NativeScoring, TickPopups;
        internal static ConfigEntry<int> CoinReward;
        internal static ConfigEntry<float> CoinTargetPerCorner, CornerMinRadius;
        internal static ConfigEntry<float> LineFull, LineZero, LineFloor, WSpeed, WGrip, WPedal, Base, TickInterval, TickMinQ, DriftFactor, TrafficGrace;
        internal static ConfigEntry<float> ExitWeight, CleanBonus, GripBonus, CoastPerSecond, CoastFloor, Gold, Silver, Bronze, StreakStep, StreakMax;
        internal static ConfigEntry<float> GripStart, BrakeDecel, AccelRate;
        internal static ConfigEntry<bool> LogCorners;
        internal static ConfigEntry<string> HudIconFile, StatIconFile;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Build and preview the racing line.");
            ShowLine = Config.Bind("Preview", "ShowLine", false, "Draw the computed line and the score readout (F5 toggles it).");
            DrawAhead = Config.Bind("Preview", "DrawAhead", 150f, "How far ahead to draw the line, in metres (20-400).");
            Margin = Config.Bind("Line", "Margin", 1.5f, "How far inside the road edge the line must stay, in metres.");
            SampleStep = Config.Bind("Line", "SampleStep", 2.5f, "Metres between line samples (1-10). Smaller = smoother but slower to build.");
            FrameBudgetMs = Config.Bind("Line", "FrameBudgetMs", 1f, "Milliseconds per frame spent building the line (0.5-10). The build is spread over frames so it never hitches.");

            // ---- Racing Line v2 (design doc "Racing Line v2 - Scoring Algorithm"); every default is a starting value to tune
            NativeScoring = Config.Bind("Scoring", "NativeCategory", true,
                "Single-player: Racing Line is a real score category (results row, HUD popups, combo, coins, counts toward TOTAL, grade, XP). " +
                "Its points are part of the run total the game uploads to its Steam leaderboard. Implemented as an inert copy of Top Speed, so " +
                "Top Speed cards also affect it. Multiplayer always uses display mode (nothing counts). Off = display mode everywhere.");
            CoinReward = Config.Bind("Scoring", "CoinReward", 130, "Coins at full target (the game's own categories use 130).");
            CoinTargetPerCorner = Config.Bind("Scoring", "CoinTargetPerCorner", 0.6f,
                "Full coins need this many grade units per corner of the race (GOLD 1, SILVER 0.6, BRONZE 0.3, x1.5 Grip line). 0.6 = a steady SILVER run maxes it.");
            TickPopups = Config.Bind("Scoring", "TickPopups", false, "Show a HUD popup for every 0.5 s combo tick (off: only corner bonuses pop up; ticks still keep the combo).");
            CornerMinRadius = Config.Bind("Scoring", "CornerMinRadius", 300f, "Bends tighter than this radius (metres) are corners.");
            LineFull = Config.Bind("Quality", "LineFull", 2.5f, "Within this many metres of the line, the position term is perfect.");
            LineZero = Config.Bind("Quality", "LineZero", 8f, "From this many metres off the line, the position term is at its floor.");
            LineFloor = Config.Bind("Quality", "LineFloor", 0.25f, "Position credit when far off the line (never 0: partial credit).");
            WSpeed = Config.Bind("Quality", "WeightSpeed", 0.45f, "Weight of speed vs the reference profile in q.");
            WGrip = Config.Bind("Quality", "WeightGrip", 0.25f, "Weight of grip used in q.");
            WPedal = Config.Bind("Quality", "WeightPedals", 0.30f, "Weight of pedals in q (before the apex anything goes; after it, throttle).");
            Base = Config.Bind("Quality", "PointsPerMetre", 0.22f, "Points per metre at q = 1, before bonuses.");
            TickInterval = Config.Bind("Quality", "TickInterval", 0.5f, "Seconds between combo ticks (must stay below the game's 0.85 s combo timeout).");
            TickMinQ = Config.Bind("Quality", "TickMinQ", 0.3f, "Ticks (and so the combo) need at least this quality.");
            DriftFactor = Config.Bind("Quality", "DriftFactor", 0.5f, "Running points while drifting (drifting also loses the Grip line bonus).");
            TrafficGrace = Config.Bind("Quality", "TrafficGrace", 1.5f, "Seconds the position term is held after a near miss (dodging traffic isn't punished).");
            ExitWeight = Config.Bind("Bonuses", "ExitWeight", 0.5f, "Exit bonus weight: corner x (1 + ExitWeight x exit), exit = early full throttle + exit speed.");
            CleanBonus = Config.Bind("Bonuses", "Clean", 1.15f, "No collision in the corner.");
            GripBonus = Config.Bind("Bonuses", "GripLine", 2f, "No drifting anywhere in the corner: the Grip line bonus.");
            CoastPerSecond = Config.Bind("Bonuses", "CoastPerSecond", 0.15f, "Penalty per second coasting AFTER the apex (coasting before it is free: no trail braking in this game).");
            CoastFloor = Config.Bind("Bonuses", "CoastFloor", 0.6f, "The coasting penalty never goes below this.");
            Gold = Config.Bind("Bonuses", "Gold", 0.8f, "Mean q for a GOLD corner.");
            Silver = Config.Bind("Bonuses", "Silver", 0.6f, "Mean q for a SILVER corner.");
            Bronze = Config.Bind("Bonuses", "Bronze", 0.4f, "Mean q for a BRONZE corner.");
            StreakStep = Config.Bind("Bonuses", "StreakStep", 0.15f, "Each good corner in a row adds this to the multiplier.");
            StreakMax = Config.Bind("Bonuses", "StreakMax", 2f, "Streak multiplier cap.");
            GripStart = Config.Bind("Car", "GripStart", 9f, "Starting cornering limit (m/s^2); it then learns each car's 95th percentile while you drive without drifting.");
            BrakeDecel = Config.Bind("Car", "BrakeDecel", 10f, "Braking (m/s^2) assumed by the reference speed profile.");
            AccelRate = Config.Bind("Car", "AccelRate", 5f, "Acceleration (m/s^2) assumed by the reference speed profile.");
            LogCorners = Config.Bind("Scoring", "LogCorners", false, "Log every corner's result with q, speed, exit and coasting (for tuning with /game-log). Off by default: logging costs frames.");
            HudIconFile = Config.Bind("Icons", "HudIcon", "racingline_hud.png", "PNG in plugins/RacingLine/ for HUD popups (~56x44 plus glow). Missing = built-in placeholder.");
            StatIconFile = Config.Bind("Icons", "StatIcon", "racingline_stat.png", "PNG in plugins/RacingLine/ for the results row (~44x32). Missing = built-in placeholder.");

            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[RacingLine] game check crashed, plugin stays idle: {e}"); return; }

            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            Log.LogInfo($"RacingLine {Version} loaded (v2 scoring: quality per metre, combo ticks, coins). F5 shows or hides the line.");
        }
    }
}
