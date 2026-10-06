using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace Bikes
{
    /// <summary>
    /// Bikes: new garage vehicles (single-player). They sit at the end of the garage list, after the game's cars:
    /// - the BMW S1000RR and the blue Sport Bike (motorcycles, with lean);
    /// - the M2 G87 (a car model on the donor's wheel pivots).
    /// Garage.All holds the list.
    ///
    /// Each bike rides on a hidden copy of a donor car (the game's own driving, four physics wheels and collider). It has
    /// its own name, stats and look: the bike model at real size, wheels spinning and steering with the game's, and a lean
    /// into corners.
    ///
    /// Bikes never reach the save, run snapshots, the Steam leaderboards or other players (Guards).
    /// Design doc: "Sport Bikes: Lean, Grip and Braking" (claude.ai artifact PwcrEFpw9c3pgycx4Um3zi). This first version is
    /// its phase 1 (looks); bike handling, the narrow body and the rider come later.
    /// </summary>
    [BepInPlugin(Guid, "Bikes", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.bikes";
        public const string Version = "0.3.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, RodeBikeThisRun;
        internal static ConfigEntry<string> Donor;
        internal static ConfigEntry<float> MaxLean, LeanScale;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true,
                "Add the two bikes to the garage (single-player). Off = they leave the garage at once; a run on a bike carries on as its donor car next time.");
            Donor = Config.Bind("General", "DonorCar", "Saber",
                "The game car each bike drives on underneath (its speed class, handling feel, parts and trait). Applies at the next game start.");
            MaxLean = Config.Bind("Look", "MaxLean", 50f, new ConfigDescription(
                "Largest lean angle in corners, in degrees.", new AcceptableValueRange<float>(0f, 65f)));
            LeanScale = Config.Bind("Look", "LeanScale", 1f, new ConfigDescription(
                "Lean strength (1 = the physical lean for the corner's speed and turn rate).", new AcceptableValueRange<float>(0f, 2f)));

            Handling.Bind(Config);
            RodeBikeThisRun = Config.Bind("State", "RodeBikeThisRun", false,
                "Written by the plugin: true while the current run has used a bike (its leaderboard uploads are skipped, also after a resume). Not a setting.");

            int hooks;
            try { hooks = Guards.Install(new Harmony(Guid)); }
            catch (Exception e)
            {
                Log.LogError($"[Bikes] hooks failed to install, so the bikes stay out of the garage (they would reach saves or leaderboards otherwise): {e.Message}");
                return;
            }
            try { hooks += Handling.Install(new Harmony(Guid + ".handling")); }
            catch (Exception e) { Log.LogWarning($"[Bikes] handling hooks not installed (the game's own handling): {e.Message}"); }
            // rider sockets for the Driver plugin (bike frame, metres): AppDomain "rogue.bikes.rider.<Key>" = float[11]
            // { seat xyz, right grip xyz, right peg xyz, hip height above the seat, knee half-width }
            foreach (var b in Garage.All) if (b.Rider != null) AppDomain.CurrentDomain.SetData("rogue.bikes.rider." + b.Key, (float[])b.Rider.Clone());
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            Log.LogInfo($"Bikes {Version} loaded: {hooks} hooks; the bikes are built when the garage opens (donor car {Donor.Value}; single-player, off the leaderboards)." +
                        (RodeBikeThisRun.Value ? " The stored run used a bike: it stays off the leaderboards until a new run starts." : ""));
        }
    }
}
