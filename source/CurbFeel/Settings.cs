using BepInEx.Configuration;

namespace CurbFeel
{
    /// <summary>All tunables. Edit BepInEx/config/rogue.curbfeel.cfg, then press F7 in game to reload.</summary>
    internal static class Settings
    {
        // General
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> VerboseLog;
        public static ConfigEntry<string> ReloadKey;
        public static ConfigEntry<string> ToggleKey;
        public static ConfigEntry<string> OverlayKey;
        public static ConfigEntry<string> OverlayMode;
        public static ConfigEntry<float> OverlayScale;

        // A. Hull
        public static ConfigEntry<bool> HullEnabled;
        public static ConfigEntry<float> HullHalfWidth;
        public static ConfigEntry<float> HullMargin;
        public static ConfigEntry<float> HullMinRadius;

        // B. Walls
        public static ConfigEntry<bool> HullAllVehicles;

        public static ConfigEntry<bool> WallsEnabled;
        public static ConfigEntry<string> CurbReference;
        public static ConfigEntry<float> WiderBehindCurb;
        public static ConfigEntry<float> CurbFromRegular;
        public static ConfigEntry<float> AllowedOverCurb;
        public static ConfigEntry<float> BackupGap;
        public static ConfigEntry<bool> SidewalkCap;
        public static ConfigEntry<float> SidewalkMargin;
        public static ConfigEntry<float> MinOverCurb;
        public static ConfigEntry<float> CapSmoothing;

        // D. Curb ramp
        public static ConfigEntry<bool> RampEnabled;
        public static ConfigEntry<float> RampHeight;
        public static ConfigEntry<float> RampStartBeforeCurb;
        public static ConfigEntry<float> RampFullHeightAfterCurb;
        public static ConfigEntry<float> RampTopExtend;
        public static ConfigEntry<bool> ShowRamps;

        // C. Scrapes / damage
        public static ConfigEntry<bool> ScrapeEnabled;
        public static ConfigEntry<float> ShallowAngle;
        public static ConfigEntry<float> ShallowDamageMult;
        public static ConfigEntry<float> ShallowSpeedLoss;
        public static ConfigEntry<float> ShallowCooldown;
        public static ConfigEntry<float> HardDamageMult;
        public static ConfigEntry<float> HardSpeedLossMult;
        public static ConfigEntry<float> ContinuousDamageMult;
        public static ConfigEntry<float> BounceOffMult;

        // E. Traffic / lane splitting
        public static ConfigEntry<bool> TrafficEnabled;
        public static ConfigEntry<float> TrafficWidthScale;
        public static ConfigEntry<float> TrafficLengthScale;
        public static ConfigEntry<float> SideSwipeAngle;
        public static ConfigEntry<float> SideSwipeSpeedLoss;
        public static ConfigEntry<float> SideSwipeDamageMult;
        public static ConfigEntry<float> SideSwipeCooldown;
        public static ConfigEntry<float> TrafficHardHitDamageMult;
        public static ConfigEntry<float> NearMissExtraRange;
        public static ConfigEntry<bool> IncludeRacers;
        public static ConfigEntry<bool> LogLanes;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true, "Master switch. ToggleKey flips it in game so you can compare stock vs modded.");
            VerboseLog = cfg.Bind("General", "VerboseLog", true, "Log what was changed per tile / per car.");
            ReloadKey = cfg.Bind("General", "ReloadKey", "F9",
                "Input System key name that reloads this config and reapplies. Avoid F6/F7 (used by DriverCam).");
            ToggleKey = cfg.Bind("General", "ToggleKey", "F10", "Input System key name that turns CurbFeel on/off.");
            OverlayKey = cfg.Bind("General", "OverlayKey", "F8", "Key that cycles the status panel: Full -> Compact -> Hidden.");
            OverlayMode = cfg.Bind("General", "OverlayMode", "Full",
                new ConfigDescription("Status panel in the top-right corner.", new AcceptableValueList<string>("Full", "Compact", "Hidden")));
            OverlayScale = cfg.Bind("General", "OverlayScale", 1f, "Status panel size.");

            HullEnabled = cfg.Bind("A.Hull", "Enabled", true,
                "Trim the car's invisible wall-contact capsules (BarrierCollider layer) so they stop at the bodywork instead of ~0.4 m outside it.");
            HullHalfWidth = cfg.Bind("A.Hull", "HalfWidth", 0f,
                "Wall-contact half-width in metres from the car's centreline. 0 = measure the car body automatically (about 1.2-1.3 m).");
            HullMargin = cfg.Bind("A.Hull", "Margin", 0f, "Added to HalfWidth. Negative lets the body clip slightly into walls.");
            HullMinRadius = cfg.Bind("A.Hull", "MinRadius", 0.25f, "Smallest radius a trimmed capsule may have.");
            HullAllVehicles = cfg.Bind("A.Hull", "AllVehicles", true,
                "Trim every vehicle that has wall-contact capsules (your car in any body + AI racers). false = only your own car.");

            WallsEnabled = cfg.Bind("B.Walls", "Enabled", true,
                "Move the invisible road-edge walls (Guardrail_Regular / Guardrail_Wider) outward, on every road tile of every map.");
            CurbReference = cfg.Bind("B.Walls", "CurbReference", "Wider",
                new ConfigDescription("Where the visible curb face is assumed to be. 'Wider' = WiderBehindCurb in front of the stock outer wall, " +
                    "measured per point along the road (adapts to every tile; measured median 0.17 m). 'Regular' = fixed CurbFromRegular past the inner wall.",
                    new AcceptableValueList<string>("Wider", "Regular")));
            WiderBehindCurb = cfg.Bind("B.Walls", "WiderBehindCurb", 0.15f, "Curb face = stock outer wall minus this (metres).");
            CurbFromRegular = cfg.Bind("B.Walls", "CurbFromRegular", 1.1f,
                "Curb face distance past the stock inner wall when CurbReference=Regular, or where the two walls are too close to tell (measured 1.08-1.59 m).");
            AllowedOverCurb = cfg.Bind("B.Walls", "AllowedOverCurb", 3.0f,
                "Maximum distance past the visible curb face the car's hull may go before it meets the wall (3.0 = about half a car length). " +
                "With SidewalkCap on, this is limited per point to the real sidewalk depth. Sidewalk props have no collision in this game.");
            BackupGap = cfg.Bind("B.Walls", "BackupGap", 0.3f, "The outer wall is kept at least this far behind the shifted inner wall.");
            SidewalkCap = cfg.Bind("B.Walls", "SidewalkCap", true,
                "Stop the moved wall short of building-sized scenery (buildings, walls, cliffs, bus stops) found along each point of the road. " +
                "Nothing beyond the curb has collision in this game, so without this the car can enter facades on narrow sidewalks. Thin props (posts, benches) are ignored.");
            SidewalkMargin = cfg.Bind("B.Walls", "SidewalkMargin", 0.5f, "Stop this far in front of a building / wall.");
            MinOverCurb = cfg.Bind("B.Walls", "MinOverCurb", 0.2f, "Always allow at least this much past the curb, even right next to a building.");
            CapSmoothing = cfg.Bind("B.Walls", "CapSmoothing", 3f, "Radius (m) over which the building cap is smoothed along the road (conservatively: it only ever lowers).");

            RampEnabled = cfg.Bind("D.CurbRamp", "Enabled", true,
                "Add an invisible bevelled curb collider (Street layer) so the wheels physically ride up onto the curb.");
            RampHeight = cfg.Bind("D.CurbRamp", "Height", 0f,
                "Ramp top height in metres. 0 = match the measured sidewalk height at each point (visible curbs are ~0.35-0.42 m).");
            RampStartBeforeCurb = cfg.Bind("D.CurbRamp", "StartBeforeCurb", 0.35f, "Bevel starts this far on the road side of the curb face.");
            RampFullHeightAfterCurb = cfg.Bind("D.CurbRamp", "FullHeightAfterCurb", 0.35f, "Bevel reaches full height this far past the curb face (longer bevel = gentler climb).");
            RampTopExtend = cfg.Bind("D.CurbRamp", "TopExtend", 0.30f, "Flat top continues this far past the (shifted) wall.");
            ShowRamps = cfg.Bind("D.CurbRamp", "ShowRamps", false, "Render the ramp colliders (debug; needs a URP unlit shader to be present).");

            ScrapeEnabled = cfg.Bind("C.Scrape", "Enabled", true, "Change how wall contacts hurt you.");
            ShallowAngle = cfg.Bind("C.Scrape", "ShallowAngle", 5f,
                "Wall contacts at or below this impact angle (degrees between your travel direction and the wall) count as a soft scrape.");
            ShallowDamageMult = cfg.Bind("C.Scrape", "ShallowDamageMult", 0f, "Damage of a soft scrape relative to stock (0 = none).");
            ShallowSpeedLoss = cfg.Bind("C.Scrape", "ShallowSpeedLoss", 0.02f, "Fraction of speed lost per soft scrape (stock graze: 0.08).");
            ShallowCooldown = cfg.Bind("C.Scrape", "ShallowCooldown", 0.25f, "Seconds between soft-scrape penalties.");
            HardDamageMult = cfg.Bind("C.Scrape", "HardDamageMult", 1f, "Damage multiplier for wall hits steeper than ShallowAngle.");
            HardSpeedLossMult = cfg.Bind("C.Scrape", "HardSpeedLossMult", 1f, "Speed-loss multiplier for wall hits steeper than ShallowAngle.");
            ContinuousDamageMult = cfg.Bind("C.Scrape", "ContinuousDamageMult", 0.25f, "Multiplier for the damage tick while you stay pressed against a wall.");
            BounceOffMult = cfg.Bind("C.Scrape", "BounceOffGuardrail", -1f,
                "Override VehicleMovement.bounceOffGuardrailMultiplier (stock 0.2). -1 = leave stock.");

            TrafficEnabled = cfg.Bind("E.Traffic", "Enabled", true,
                "Lane splitting: soft side-swipes with traffic and slightly slimmer traffic hit boxes. Applies to every traffic model.");
            TrafficWidthScale = cfg.Bind("E.Traffic", "WidthScale", 0.92f,
                "Traffic hit-box width relative to stock (stock boxes are already ~0.1-0.25 m inside the visible body). 1 = stock.");
            TrafficLengthScale = cfg.Bind("E.Traffic", "LengthScale", 1f, "Traffic hit-box length relative to stock.");
            SideSwipeAngle = cfg.Bind("E.Traffic", "SideSwipeAngle", 5f,
                "Traffic contacts at or below this impact angle (degrees between the relative motion and the contact surface) are soft side-swipes. " +
                "Stock treats every touch as a crash: damage, speed loss and drift reset.");
            SideSwipeSpeedLoss = cfg.Bind("E.Traffic", "SideSwipeSpeedLoss", 0.03f, "Fraction of speed lost per side-swipe.");
            SideSwipeDamageMult = cfg.Bind("E.Traffic", "SideSwipeDamageMult", 0f, "Side-swipe damage relative to a stock graze (0 = none). Drift is never reset by a side-swipe.");
            SideSwipeCooldown = cfg.Bind("E.Traffic", "SideSwipeCooldown", 0.3f, "Seconds between side-swipe penalties.");
            TrafficHardHitDamageMult = cfg.Bind("E.Traffic", "HardHitDamageMult", 1f, "Damage multiplier for traffic hits steeper than SideSwipeAngle.");
            NearMissExtraRange = cfg.Bind("E.Traffic", "NearMissExtraRange", -1f,
                "VehicleStuntHandler.withinNearMissExtraRange for your car (stock 0.1 m). -1 = stock + the width removed by WidthScale, so near-misses still score.");
            IncludeRacers = cfg.Bind("E.Traffic", "IncludeRacers", false, "Also treat shallow contacts with AI racers as side-swipes.");
            LogLanes = cfg.Bind("E.Traffic", "LogLanes", true, "Log the lane offsets traffic actually uses.");
        }
    }
}
