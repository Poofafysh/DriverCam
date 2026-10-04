using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace CarSkins
{
    /// <summary>
    /// CarSkins: draws the player's car as a different model (cosmetic, single-player only). First skin: the Saber as a
    /// BMW E46 (1998), a CC BY Sketchfab model decimated to about 5k triangles (Assets/build_e46.py). The game's own Saber
    /// renderers are only hidden (Renderer.forceRenderingOff, so `enabled` stays: DriverCam's body measurement and cockpit
    /// fit see the same car); physics, the hit box and everything else stay the game's.
    /// </summary>
    [BepInPlugin(Guid, "CarSkins", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.carskins";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Car, Model;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Draw the player's car as the replacement model (single-player only; cosmetic, nothing else changes).");
            Car = Config.Bind("General", "Car", "Saber", "Which of your cars gets the skin (the game's car name, e.g. Saber).");
            Model = Config.Bind("General", "Model", "BMW_E46", "The replacement model: a .csm file in BepInEx/plugins/CarSkins (without the extension).");
            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[CarSkins] game check crashed, plugin stays idle: {e}"); return; }
            if (!GameApi.Ok) return;
            ClassInjector.RegisterTypeInIl2Cpp<Skinner>();
            AddComponent<Skinner>();
            Log.LogInfo($"CarSkins {Version} loaded: {Car.Value} drawn as {Model.Value} (single-player).");
        }
    }

    /// <summary>The only place that touches game types (checked by name at startup; each accessor non-inlined).</summary>
    internal static class GameApi
    {
        internal static bool Ok { get; private set; }

        internal static void Check()
        {
            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            Ok = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "VehicleSO", "VehicleSkin")
              && Has(asm, "Game.Runtime.Data.Vehicle_SO", missing, "vehicleName")
              && Has(asm, "Game.Runtime.GameState", missing, "IsMultiplayerMode");
            if (Ok) Plugin.Log.LogInfo("[CarSkins] game check OK: player car, car name, skin holder, game mode");
            else Plugin.Log.LogWarning($"[CarSkins] game check: missing {string.Join(", ", missing)}; CarSkins stays off");
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

        /// <summary>The player's car: its object pointer, the game's car name and its skin holder's transform. False with no car.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool PlayerCar(out IntPtr car, out string name, out Transform skin)
        {
            car = IntPtr.Zero; name = null; skin = null;
            var veh = Game.Runtime.Vehicle.VehicleManager.Instance;
            if (veh == null) return false;
            var so = veh.VehicleSO;
            var holder = veh.VehicleSkin;
            if (so == null || holder == null) return false;
            car = veh.Pointer;
            name = so.vehicleName;
            skin = holder.transform;
            return skin != null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsMultiplayer()
        {
            try { return Game.Runtime.GameState.IsMultiplayerMode; }
            catch { return true; }   // unknown: stay off
        }
    }
}
