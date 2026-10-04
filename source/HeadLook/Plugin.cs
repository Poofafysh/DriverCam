using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace HeadLook
{
    /// <summary>
    /// HeadLook: turn your head like in real life, with the right stick or by holding the right mouse button and moving
    /// the mouse. Up / down / left / right, springing back to centre on release.
    ///
    /// - Hood view: the camera turns in place. Chase views: the camera orbits around the car (Forza-style look around).
    /// - DriverCam's driver view: DriverCam reads the head angle HeadLook publishes (AppDomain data "rogue.headlook") and
    ///   adds it to the driver's eye direction itself, so first-person head turns are exact. HeadLook leaves that view alone.
    /// - The turn is applied right before each frame is drawn (Application.onBeforeRender) and the camera is put back
    ///   (Harmony prefixes on CameraControllerInGame.Update / LateUpdate / FixedUpdate) before the game's camera code runs
    ///   again, so the game's own camera smoothing never sees the offset.
    /// Camera only: nothing about the car or the game state is changed.
    /// </summary>
    [BepInPlugin(Guid, "HeadLook", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.headlook";
        public const string Version = "0.1.1";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, Stick, Mouse, InvertY, ChaseOrbit;
        internal static ConfigEntry<float> MaxYaw, MaxUp, MaxDown, Deadzone, MouseSensitivity, Speed;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Master switch.");
            Stick = Config.Bind("Input", "RightStick", true, "Controller: the right stick turns your head (absolute: full stick = full turn). The stick press stays the game's (ability / honk).");
            Mouse = Config.Bind("Input", "RightMouse", true, "Keyboard and mouse: hold the right mouse button and move the mouse to look around.");
            MouseSensitivity = Config.Bind("Input", "MouseSensitivity", 0.15f, "Degrees per pixel of mouse movement (0.02-1).");
            Deadzone = Config.Bind("Input", "Deadzone", 0.15f, "Right-stick deadzone (0-0.5).");
            InvertY = Config.Bind("Input", "InvertY", false, "Invert up / down.");
            MaxYaw = Config.Bind("Look", "MaxYaw", 100f, "Furthest head turn left / right, degrees (10-170).");
            MaxUp = Config.Bind("Look", "MaxUp", 35f, "Furthest look up, degrees (0-80).");
            MaxDown = Config.Bind("Look", "MaxDown", 25f, "Furthest look down, degrees (0-80).");
            Speed = Config.Bind("Look", "Speed", 12f, "How quickly the head follows the input and springs back (2-40; higher = snappier).");
            ChaseOrbit = Config.Bind("Look", "ChaseOrbit", true, "Chase views: orbit the camera around the car. Off = turn the chase camera in place like the hood view.");

            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[HeadLook] game check crashed, plugin stays idle: {e}"); return; }
            if (!GameApi.CameraOk) { Log.LogWarning("[HeadLook] the game's camera controller wasn't found: HeadLook only publishes the head angle (DriverCam's driver view still uses it)."); }
            else
            {
                try { CameraHooks.Install(new Harmony(Guid)); }
                catch (Exception e) { GameApi.DisableCamera(); Log.LogWarning($"[HeadLook] camera hooks failed, only DriverCam's driver view will turn: {e.Message}"); }
            }

            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            Log.LogInfo($"HeadLook {Version} loaded. Right stick, or hold the right mouse button, to look around.");
        }
    }
}
