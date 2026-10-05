using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RogueShared;

namespace Reverse
{
    /// <summary>
    /// Reverse: the game has no reverse gear. Hold the brake with the car completely stopped and, after HoldDelay, the
    /// car starts rolling backwards (up to MaxSpeedKmh); steering works the way a real car's does in reverse. Letting go
    /// of the brake slows the reverse to a stop; the throttle cancels it at once. Player car only, single-player only.
    ///
    /// How (GameAssembly.dll, see README): the game's speed is a number (VehicleMovement.TargetSpeed) clamped to
    /// MinSpeed..max every frame, and VehicleMovement.ApplyMovement sets the rigidbody's flat velocity to
    /// forward x CurrentSpeed each physics step with AddForce(target - velocity, VelocityChange). So the game's speed
    /// stays 0 while reversing (its own step cancels all flat motion) and a postfix adds -forward x reverse speed as
    /// one more VelocityChange in the same step. Steering: HandleCarRotation turns by SmoothTurnInput x turn speed,
    /// scaled to 0 at speed 0, so around that call only the speed is set to the reverse speed and the steering input
    /// flipped, both put back right after (finalizer). Nothing is saved or left changed in the game.
    /// </summary>
    [BepInPlugin(Guid, "Reverse", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.reverse";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> MaxSpeedKmh, Accel, Decel, HoldDelay;
        internal static ConfigEntry<bool> NoBrakeTurnPenalty;
        internal static bool GameOk;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription(
                "Master switch: hold the brake with the car stopped to reverse.", null, HubLink.Meta("Reverse on", applies: "now")));
            MaxSpeedKmh = Config.Bind("General", "MaxSpeedKmh", 25f, new ConfigDescription(
                "Top reverse speed in km/h.", new AcceptableValueRange<float>(5f, 60f), HubLink.Meta("Top reverse speed", 5, 60, 1, "km/h", applies: "now")));
            HoldDelay = Config.Bind("General", "HoldDelay", 0.3f, new ConfigDescription(
                "Seconds the brake must be held with the car fully stopped before it starts reversing.", new AcceptableValueRange<float>(0f, 2f),
                HubLink.Meta("Brake hold before reversing", 0, 2, 0.05, "s", applies: "now")));
            Accel = Config.Bind("Tuning", "Accel", 4f, new ConfigDescription(
                "How quickly reverse speed builds while the brake is held, in m/s per second.", new AcceptableValueRange<float>(1f, 15f),
                HubLink.Meta("Reverse acceleration", 1, 15, 0.5, "m/s2", advanced: true, applies: "now")));
            Decel = Config.Bind("Tuning", "Decel", 10f, new ConfigDescription(
                "How quickly the car stops after you let go of the brake while reversing, in m/s per second.", new AcceptableValueRange<float>(2f, 40f),
                HubLink.Meta("Stop after release", 2, 40, 1, "m/s2", advanced: true, applies: "now")));
            NoBrakeTurnPenalty = Config.Bind("Tuning", "NoBrakeTurnPenalty", true, new ConfigDescription(
                "The game turns slower while the brake is held; reversing holds the brake, so this undoes that slowdown while reversing.",
                null, HubLink.Meta("Full steering in reverse", advanced: true, applies: "now")));

            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            GameOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "VehicleMovement", "VehicleInputHandler", "Rigidbody", "LevelWasEnded", "VehicleHealthHandler")
                  && Has(asm, "Game.Runtime.Vehicle.VehicleMovement", missing, "ApplyMovement", "HandleCarRotation", "SetTargetSpeed", "TargetSpeed",
                         "CurrentSpeed", "IsGrounded", "Drifting", "MinSpeed", "CurrentTurnSpeed", "vehicleParams")
                  && Has(asm, "Game.Runtime.Data.VehicleBaseParameters", missing, "BrakeTurningMultiplier")
                  && Has(asm, "Game.Runtime.Vehicle.VehicleInputHandler", missing, "Throttle", "BrakeInput", "SmoothTurnInput", "Braking", "Handbrake", "CanControl", "CanTakeInput")
                  && Has(asm, "Game.Runtime.Vehicle.VehicleHealth", missing, "Defeated")
                  && Has(asm, "Game.Runtime.GameState", missing, "IsMultiplayerMode");
            if (missing.Count > 0) Log.LogWarning($"[Reverse] game check: missing {string.Join(", ", missing)}.");
            if (GameOk)
            {
                try { Hooks.Install(new Harmony(Guid)); }
                catch (Exception e) { Log.LogWarning($"[Reverse] hooks failed to install: {e.Message}"); GameOk = false; }
            }
            if (!GameOk) Log.LogWarning("[Reverse] reverse stays off (game check or hooks failed).");

            ReverseLink.Install();
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            HubLink.Status(Guid, Runner.HubStatus);
            Log.LogInfo($"Reverse {Version} loaded. Hold the brake with the car stopped to reverse (up to {MaxSpeedKmh.Value:0} km/h; single-player).");
        }

        private static bool Has(Assembly asm, string typeName, List<string> missing, params string[] members)
        {
            var t = asm?.GetType(typeName);
            if (t == null) { missing.Add(typeName); return false; }
            bool ok = true;
            foreach (var m in members)
                if (t.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0)
                { missing.Add($"{t.Name}.{m}"); ok = false; }
            return ok;
        }
    }
}
