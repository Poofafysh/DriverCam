using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace Driver
{
    /// <summary>
    /// Driver: a 3D racing driver in the player's car (cosmetic). An original, fully scripted character
    /// (Assets/model/build_driver.py: UE-named 55-bone skeleton, runtime files driver.drm / driver_anims.dra) seated on the
    /// car's driver seat, hands on DriverCam's steering wheel (two-bone IK, following its spin), right foot on the pedal,
    /// head turning with HeadLook. In DriverCam's driver view the head and collar are left out (the camera sits at the eyes)
    /// and the helmet only casts its shadow.
    ///
    /// Seat data, best first: DriverCam's published AppDomain data "rogue.drivercam" (exact, includes Edit-mode tweaks),
    /// DriverCam's cockpit_&lt;Car&gt;.dcm and car settings read-only, or an estimate from the car body's size. Design spec:
    /// "Driver plugin design spec (rogue.driver)" (the workflow notes); README.md in this folder.
    /// No Harmony: reads the camera mode, the car body and the inputs; never writes to the game.
    /// </summary>
    [BepInPlugin(Guid, "Driver", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.driver";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, ShowInDriverView, ShowInChaseView, Outline, LogEvents, ForceCpuSkin;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Show a driver in your car (cosmetic; nothing else changes).");
            ShowInDriverView = Config.Bind("Look", "ShowInDriverView", true, "DriverCam's driver view: show your body, arms and hands on the wheel (the head is left out: the camera is at the eyes).");
            ShowInChaseView = Config.Bind("Look", "ShowInChaseView", false, "Chase and hood views: show the driver too. The game's cars have painted (opaque) windows, so the driver is hidden inside the body: off saves the drawing cost.");
            Outline = Config.Bind("Look", "Outline", false, "Draw the game's cartoon outline around the driver (a copy of your car's outline material). Off by default: in driver view the inverted outline hull can fill the screen (DriverCam strips the car's outline there for the same reason). Applies when the driver is next built (restart or Enabled off / on).");
            LogEvents = Config.Bind("Debug", "LogEvents", false, "Log camera-mode changes, re-fits, show / hide and object builds.");
            ForceCpuSkin = Config.Bind("Debug", "ForceCpuSkin", false, "Skin the driver on the CPU instead of the GPU (used automatically when the GPU skinning self-test fails). Applies when the driver is next built.");

            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[Driver] game check crashed, plugin stays idle: {e}"); return; }
            if (!GameApi.Ok) return;
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            Log.LogInfo($"Driver {Version} loaded: a driver in your car (driver view {(ShowInDriverView.Value ? "on" : "off")}, chase view {(ShowInChaseView.Value ? "on" : "off")}).");
        }
    }
}
