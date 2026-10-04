using BepInEx.Configuration;
using RogueShared;

namespace CurbFeel
{
    /// <summary>All tunables. Edit them in Rogue Hub (applied a moment after a change), or in BepInEx/config/rogue.curbfeel.cfg and press F9 to reload.</summary>
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
        public static ConfigEntry<float> CurbFromWiderCity, CurbFromWiderPark;
        public static ConfigEntry<bool> ShowWalls;
        public static ConfigEntry<float> MapBudgetMs;
        public static ConfigEntry<int> ConfigVersion;

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
            Enabled = cfg.Bind("General", "Enabled", true, new ConfigDescription("Master switch. ToggleKey flips it in game so you can compare stock vs modded.", null, HubLink.Meta("CurbFeel on")));
            VerboseLog = cfg.Bind("General", "VerboseLog", true, new ConfigDescription("Log what was changed per tile / per car.", null, HubLink.Meta("Detailed log", advanced: true)));
            ReloadKey = cfg.Bind("General", "ReloadKey", "F9",
                new ConfigDescription("Input System key name that reloads this config and reapplies. Avoid F6/F7 (used by DriverCam).", null, HubLink.Meta("Reload key", advanced: true)));
            ToggleKey = cfg.Bind("General", "ToggleKey", "F10", new ConfigDescription("Input System key name that turns CurbFeel on/off.", null, HubLink.Meta("On/off key", advanced: true)));
            OverlayKey = cfg.Bind("General", "OverlayKey", "F8", new ConfigDescription("Key that cycles the status panel: Full -> Compact -> Hidden.", null, HubLink.Meta("Status panel key", advanced: true)));
            OverlayMode = cfg.Bind("General", "OverlayMode", "Full",
                new ConfigDescription("Status panel in the top-right corner.", new AcceptableValueList<string>("Full", "Compact", "Hidden"), HubLink.Meta("Status panel")));
            OverlayScale = cfg.Bind("General", "OverlayScale", 1f, new ConfigDescription("Status panel size.", new AcceptableValueRange<float>(0.5f, 2f), HubLink.Meta("Status panel size", 0.5, 2, 0.05, "x", advanced: true)));

            HullEnabled = cfg.Bind("A.Hull", "Enabled", true,
                new ConfigDescription("Trim the car's invisible wall-contact capsules (BarrierCollider layer) so they stop at the bodywork instead of ~0.4 m outside it.", null, HubLink.Meta("Trim the car's wall hitbox")));
            HullHalfWidth = cfg.Bind("A.Hull", "HalfWidth", 0f,
                new ConfigDescription("Wall-contact half-width in metres from the car's centreline. 0 = measure the car body automatically (about 1.2-1.3 m).", null, HubLink.Meta("Hull half-width", 0.8, 2, 0.01, "m", game: 0, gameShows: double.NaN, advanced: true, gameLabel: "AUTO")));
            HullMargin = cfg.Bind("A.Hull", "Margin", 0f, new ConfigDescription("Added to HalfWidth. Negative lets the body clip slightly into walls.", new AcceptableValueRange<float>(-0.3f, 0.3f), HubLink.Meta("Hull margin", -0.3, 0.3, 0.01, "m")));
            HullMinRadius = cfg.Bind("A.Hull", "MinRadius", 0.25f, new ConfigDescription("Smallest radius a trimmed capsule may have.", null, HubLink.Meta("Smallest capsule radius", advanced: true)));
            HullAllVehicles = cfg.Bind("A.Hull", "AllVehicles", true,
                new ConfigDescription("Trim every vehicle that has wall-contact capsules (your car in any body + AI racers). false = only your own car.", null, HubLink.Meta("Trim every car, not just mine")));

            WallsEnabled = cfg.Bind("B.Walls", "Enabled", true,
                new ConfigDescription("Move the invisible road-edge walls (Guardrail_Regular / Guardrail_Wider) outward, on every road tile of every map.", null, HubLink.Meta("Move walls to the curb")));
            CurbReference = cfg.Bind("B.Walls", "CurbReference", "Auto",
                new ConfigDescription("Where the visible curb face is. 'Auto' = measured on each tile's own sidewalk / curb mesh at every point along the road " +
                    "(falls back to CurbFromWiderCity / CurbFromWiderPark where a tile has no curb mesh). 'Wider' = WiderBehindCurb in front of the stock outer wall " +
                    "(the old rule: right on park / industrial streets, ~1.5 m too far out on city streets). 'Regular' = fixed CurbFromRegular past the inner wall.",
                    new AcceptableValueList<string>("Auto", "Wider", "Regular"), HubLink.Meta("Where the curb is")));
            WiderBehindCurb = cfg.Bind("B.Walls", "WiderBehindCurb", 0.15f, new ConfigDescription("CurbReference=Wider: curb face = stock outer wall minus this (metres).", null, HubLink.Meta("Curb behind outer wall", advanced: true)));
            CurbFromWiderCity = cfg.Bind("B.Walls", "CurbFromWiderCity", -1.70f,
                new ConfigDescription("Auto fallback on city streets (Sides_/Base_/Sidewalk meshes): curb face relative to the stock outer wall's road face, metres (measured -1.70 on every tile).", null, HubLink.Meta("City curb offset", advanced: true)));
            CurbFromWiderPark = cfg.Bind("B.Walls", "CurbFromWiderPark", 0.10f,
                new ConfigDescription("Auto fallback on park / industrial streets (*_RoadSide meshes): curb face relative to the stock outer wall's road face, metres (measured +0.10).", null, HubLink.Meta("Park curb offset", advanced: true)));
            CurbFromRegular = cfg.Bind("B.Walls", "CurbFromRegular", 1.1f,
                new ConfigDescription("Curb face distance past the stock inner wall when CurbReference=Regular, or where the two walls are too close to tell (measured 1.08-1.59 m).", null, HubLink.Meta("Curb past inner wall", advanced: true)));
            AllowedOverCurb = cfg.Bind("B.Walls", "AllowedOverCurb", 3.0f,
                new ConfigDescription("Maximum distance past the visible curb face the car's hull may go before it meets the wall (3.0 = about half a car length). " +
                "With SidewalkCap on, this is limited per point to the real sidewalk depth. Sidewalk props have no collision in this game.", new AcceptableValueRange<float>(0f, 6f), HubLink.Meta("How far past the curb", 0, 6, 0.1, "m")));
            BackupGap = cfg.Bind("B.Walls", "BackupGap", 0.3f, new ConfigDescription("The outer wall is kept at least this far behind the shifted inner wall.", null, HubLink.Meta("Outer wall gap", advanced: true)));
            SidewalkCap = cfg.Bind("B.Walls", "SidewalkCap", true,
                new ConfigDescription("Stop the moved wall short of what's really beside the road at each point: visible guardrails and rails, fences, rock walls, barricades, " +
                "wood bars, bollards, planters, bus stops, tree trunks, buildings, cliffs and tunnel walls (read from the tile's meshes; rotated pieces along curves " +
                "are exact). Nothing beyond the curb has collision in this game, so without this the car drives through them. Lamps, signs and foliage are ignored. " +
                "Where a railing stands right at the curb, the wall can end up up to 1 m closer to the road than stock (never anywhere else).", null, HubLink.Meta("Stop at railings, fences, buildings")));
            SidewalkMargin = cfg.Bind("B.Walls", "SidewalkMargin", 0.15f, new ConfigDescription("Stop this far in front of a railing / wall / building (metres).", new AcceptableValueRange<float>(0f, 1f), HubLink.Meta("Stop before railings by", 0, 1, 0.05, "m")));
            MinOverCurb = cfg.Bind("B.Walls", "MinOverCurb", 0.2f, new ConfigDescription("Allow at least this much past the curb, unless a railing or wall is closer than that (the wall never goes into one).", new AcceptableValueRange<float>(0f, 1f), HubLink.Meta("Always allow past the curb", 0, 1, 0.05, "m")));
            ShowWalls = cfg.Bind("B.Walls", "ShowWalls", false, new ConfigDescription("Debug: draw a 1 m high cyan strip where each moved wall now stands (reload with F9 after changing).", null, HubLink.Meta("Show moved walls (debug)", advanced: true)));
            CapSmoothing = cfg.Bind("B.Walls", "CapSmoothing", 3f, new ConfigDescription("Radius (m) over which the building cap is smoothed along the road (conservatively: it only ever lowers).", null, HubLink.Meta("Cap smoothing", advanced: true)));
            MapBudgetMs = cfg.Bind("B.Walls", "MapBudgetMs", 3f,
                new ConfigDescription("Milliseconds per frame spent reading a newly loaded tile's sidewalks and obstacles. Spreads the work so big tiles don't stutter; " +
                "that tile's walls move once its map is read (usually well under a second later). 0 = read the whole tile in one frame, as before 0.7 (a hitch of up to ~1 s on big tiles).",
                new AcceptableValueRange<float>(0f, 20f), HubLink.Meta("Tile reading per frame", 0, 20, 0.5, "ms", advanced: true)));

            RampEnabled = cfg.Bind("D.CurbRamp", "Enabled", true,
                new ConfigDescription("Add an invisible bevelled curb collider (Street layer) so the wheels physically ride up onto the curb.", null, HubLink.Meta("Ride up onto curbs")));
            RampHeight = cfg.Bind("D.CurbRamp", "Height", 0f,
                new ConfigDescription("Ramp top height in metres. 0 = match the measured sidewalk height at each point (visible curbs are ~0.35-0.42 m).", null, HubLink.Meta("Curb ramp height", 0.1, 0.6, 0.01, "m", game: 0, gameLabel: "AUTO")));
            RampStartBeforeCurb = cfg.Bind("D.CurbRamp", "StartBeforeCurb", 0.35f, new ConfigDescription("Bevel starts this far on the road side of the curb face.", null, HubLink.Meta("Ramp starts before the curb", advanced: true)));
            RampFullHeightAfterCurb = cfg.Bind("D.CurbRamp", "FullHeightAfterCurb", 0.35f, new ConfigDescription("Bevel reaches full height this far past the curb face (longer bevel = gentler climb).", new AcceptableValueRange<float>(0.1f, 1.5f), HubLink.Meta("Ramp length past the curb", 0.1, 1.5, 0.05, "m")));
            RampTopExtend = cfg.Bind("D.CurbRamp", "TopExtend", 0.30f, new ConfigDescription("Flat top continues this far past the (shifted) wall.", null, HubLink.Meta("Ramp top extends", advanced: true)));
            ShowRamps = cfg.Bind("D.CurbRamp", "ShowRamps", false, new ConfigDescription("Render the ramp colliders (debug; needs a URP unlit shader to be present).", null, HubLink.Meta("Show ramps (debug)", advanced: true)));

            ScrapeEnabled = cfg.Bind("C.Scrape", "Enabled", true, new ConfigDescription("Change how wall contacts hurt you.", null, HubLink.Meta("Softer wall scrapes")));
            ShallowAngle = cfg.Bind("C.Scrape", "ShallowAngle", 5f,
                new ConfigDescription("Wall contacts at or below this impact angle (degrees between your travel direction and the wall) count as a soft scrape.", new AcceptableValueRange<float>(0f, 20f), HubLink.Meta("Soft-scrape angle", 0, 20, 0.5, "deg")));
            ShallowDamageMult = cfg.Bind("C.Scrape", "ShallowDamageMult", 0f, new ConfigDescription("Damage of a soft scrape relative to stock (0 = none).", new AcceptableValueRange<float>(0f, 1f), HubLink.Meta("Soft-scrape damage", 0, 1, 0.05, "x")));
            ShallowSpeedLoss = cfg.Bind("C.Scrape", "ShallowSpeedLoss", 0.02f, new ConfigDescription("Fraction of speed lost per soft scrape (stock graze: 0.08).", new AcceptableValueRange<float>(0f, 0.1f), HubLink.Meta("Soft-scrape speed loss", 0, 0.1, 0.005, "%", scale: 100)));
            ShallowCooldown = cfg.Bind("C.Scrape", "ShallowCooldown", 0.25f, new ConfigDescription("Seconds between soft-scrape penalties.", null, HubLink.Meta("Soft-scrape cooldown", advanced: true)));
            HardDamageMult = cfg.Bind("C.Scrape", "HardDamageMult", 1f, new ConfigDescription("Damage multiplier for wall hits steeper than ShallowAngle.", new AcceptableValueRange<float>(0f, 2f), HubLink.Meta("Hard-hit damage", 0, 2, 0.05, "x")));
            HardSpeedLossMult = cfg.Bind("C.Scrape", "HardSpeedLossMult", 1f, new ConfigDescription("Speed-loss multiplier for wall hits steeper than ShallowAngle.", new AcceptableValueRange<float>(0f, 2f), HubLink.Meta("Hard-hit speed loss", 0, 2, 0.05, "x", advanced: true)));
            ContinuousDamageMult = cfg.Bind("C.Scrape", "ContinuousDamageMult", 0.25f, new ConfigDescription("Multiplier for the damage tick while you stay pressed against a wall.", new AcceptableValueRange<float>(0f, 1f), HubLink.Meta("Damage while rubbing a wall", 0, 1, 0.05, "x")));
            BounceOffMult = cfg.Bind("C.Scrape", "BounceOffGuardrail", -1f,
                new ConfigDescription("Override VehicleMovement.bounceOffGuardrailMultiplier (stock 0.2). -1 = leave stock.", null, HubLink.Meta("Wall bounce", 0, 1, 0.05, game: -1, gameShows: 0.2)));

            TrafficEnabled = cfg.Bind("E.Traffic", "Enabled", true,
                new ConfigDescription("Lane splitting: soft side-swipes with traffic and slightly slimmer traffic hit boxes. Applies to every traffic model.", null, HubLink.Meta("Lane splitting")));
            TrafficWidthScale = cfg.Bind("E.Traffic", "WidthScale", 0.92f,
                new ConfigDescription("Traffic hit-box width relative to stock (stock boxes are already ~0.1-0.25 m inside the visible body). 1 = stock.", new AcceptableValueRange<float>(0.7f, 1f), HubLink.Meta("Traffic hit-box width", 0.7, 1, 0.01, "x")));
            TrafficLengthScale = cfg.Bind("E.Traffic", "LengthScale", 1f, new ConfigDescription("Traffic hit-box length relative to stock.", new AcceptableValueRange<float>(0.7f, 1.2f), HubLink.Meta("Traffic hit-box length", 0.7, 1.2, 0.01, "x", advanced: true)));
            SideSwipeAngle = cfg.Bind("E.Traffic", "SideSwipeAngle", 5f,
                new ConfigDescription("Traffic contacts at or below this impact angle (degrees between the relative motion and the contact surface) are soft side-swipes. " +
                "Stock treats every touch as a crash: damage, speed loss and drift reset.", new AcceptableValueRange<float>(0f, 20f), HubLink.Meta("Side-swipe angle", 0, 20, 0.5, "deg")));
            SideSwipeSpeedLoss = cfg.Bind("E.Traffic", "SideSwipeSpeedLoss", 0.03f, new ConfigDescription("Fraction of speed lost per side-swipe.", new AcceptableValueRange<float>(0f, 0.1f), HubLink.Meta("Side-swipe speed loss", 0, 0.1, 0.005, "%", scale: 100)));
            SideSwipeDamageMult = cfg.Bind("E.Traffic", "SideSwipeDamageMult", 0f, new ConfigDescription("Side-swipe damage relative to a stock graze (0 = none). Drift is never reset by a side-swipe.", new AcceptableValueRange<float>(0f, 1f), HubLink.Meta("Side-swipe damage", 0, 1, 0.05, "x")));
            SideSwipeCooldown = cfg.Bind("E.Traffic", "SideSwipeCooldown", 0.3f, new ConfigDescription("Seconds between side-swipe penalties.", null, HubLink.Meta("Side-swipe cooldown", advanced: true)));
            TrafficHardHitDamageMult = cfg.Bind("E.Traffic", "HardHitDamageMult", 1f, new ConfigDescription("Damage multiplier for traffic hits steeper than SideSwipeAngle.", new AcceptableValueRange<float>(0f, 2f), HubLink.Meta("Hard traffic-hit damage", 0, 2, 0.05, "x")));
            NearMissExtraRange = cfg.Bind("E.Traffic", "NearMissExtraRange", -1f,
                new ConfigDescription("VehicleStuntHandler.withinNearMissExtraRange for your car (stock 0.1 m). -1 = stock + the width removed by WidthScale, so near-misses still score.", null, HubLink.Meta("Near-miss extra range", advanced: true)));
            IncludeRacers = cfg.Bind("E.Traffic", "IncludeRacers", false, new ConfigDescription("Also treat shallow contacts with AI racers as side-swipes.", null, HubLink.Meta("Treat AI racers like traffic")));
            LogLanes = cfg.Bind("E.Traffic", "LogLanes", true, new ConfigDescription("Log the lane offsets traffic actually uses.", null, HubLink.Meta("Log lanes (debug)", advanced: true)));
            ConfigVersion = cfg.Bind("General", "ConfigVersion", 0, "Written by the plugin (settings migration). Don't edit.");
            Migrate();
        }

        /// <summary>
        /// Once (0.5.0): settings still at an old default move to the new one; anything the player changed is kept.
        /// CurbReference Wider -> Auto (the measured curb), SidewalkMargin 0.5 -> 0.15 (walls follow railings closely).
        /// </summary>
        private static void Migrate()
        {
            if (ConfigVersion.Value >= 1) return;
            var moved = new System.Collections.Generic.List<string>();
            if (CurbReference.Value == "Wider") { CurbReference.Value = "Auto"; moved.Add("CurbReference Wider -> Auto"); }
            if (System.Math.Abs(SidewalkMargin.Value - 0.5f) < 1e-4f) { SidewalkMargin.Value = 0.15f; moved.Add("SidewalkMargin 0.5 -> 0.15"); }
            ConfigVersion.Value = 1;
            if (moved.Count > 0) Plugin.Log?.LogInfo($"[CurbFeel] settings updated to the 0.5 defaults (values you had changed are kept): {string.Join(", ", moved)}");
        }
    }
}
