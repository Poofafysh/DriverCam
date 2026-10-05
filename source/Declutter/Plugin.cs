using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using RogueShared;

namespace Declutter
{
    /// <summary>
    /// Declutter: hides the scenery you can't really see, in every race (single-player and multiplayer; purely cosmetic,
    /// local, nothing that collides changes). On each road tile, once it has loaded, a renderer under the tile's
    /// "Biomes" group is hidden (Renderer.forceRenderingOff, so `enabled` stays for other plugins) when:
    /// - it's small (largest side under SmallSize, 2 m) and more than SmallDistance (40 m) from the road; or
    /// - it's more than FarDistance (120 m) from the road.
    /// "From the road" is the gap between the renderer's bounds and the tile's road path (its PathWaypoints).
    /// Never hidden: anything on the Street / Guardrail / weather layers, and road, sidewalk, curb, barrier, wall, tunnel
    /// and bridge parts. Switched off, it shows everything again. It stands aside in Sandbox races (Sandbox strips those
    /// maps itself).
    /// </summary>
    [BepInPlugin(Guid, "Declutter", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.declutter";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> SmallSize, SmallDistance, FarDistance, BudgetMs;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription(
                "Hide small props away from the road and everything far from it (cosmetic, local).", null, HubLink.Meta("Declutter on", applies: "now")));
            SmallSize = Config.Bind("Rules", "SmallSize", 2f, new ConfigDescription(
                "Props whose largest side is under this many metres count as small.", new AcceptableValueRange<float>(0.5f, 6f),
                HubLink.Meta("Small prop size", 0.5, 6, 0.5, "m", applies: "next race")));
            SmallDistance = Config.Bind("Rules", "SmallDistance", 40f, new ConfigDescription(
                "Small props further than this from the road are hidden.", new AcceptableValueRange<float>(10f, 150f),
                HubLink.Meta("Hide small props beyond", 10, 150, 5, "m", applies: "next race")));
            FarDistance = Config.Bind("Rules", "FarDistance", 120f, new ConfigDescription(
                "Anything further than this from the road is hidden.", new AcceptableValueRange<float>(40f, 400f),
                HubLink.Meta("Hide everything beyond", 40, 400, 10, "m", applies: "next race")));
            BudgetMs = Config.Bind("Tuning", "BudgetMs", 1.5f, new ConfigDescription(
                "Time per frame spent sorting a newly loaded tile, in milliseconds.", new AcceptableValueRange<float>(0.5f, 10f),
                HubLink.Meta("Work per frame", 0.5, 10, 0.5, "ms", advanced: true, applies: "now")));
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            HubLink.Status(Guid, Runner.HubStatus);
            Log.LogInfo($"Declutter {Version} loaded: hiding props under {SmallSize.Value:0.#} m beyond {SmallDistance.Value:0} m and everything beyond {FarDistance.Value:0} m from the road.");
        }
    }
}
