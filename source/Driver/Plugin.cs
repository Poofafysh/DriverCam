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
    /// On a Bikes motorcycle (0.3.0) the driver rides it: the ride_sportbike pose, parented under the bike's "Bikes.Lean"
    /// node, hands on the grips, feet on the pegs, hang-off in corners ([Bike] section; SolverBike.cs).
    /// No Harmony: reads the camera mode, the car body and the inputs; never writes to the game.
    /// </summary>
    [BepInPlugin(Guid, "Driver", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.driver";
        public const string Version = "0.4.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, ShowInDriverView, ShowInChaseView, Outline, LogEvents, ForceCpuSkin, AnimEnabled, ShiftHand, Celebrate;
        internal static ConfigEntry<bool> BikeEnabled, HangOff;
        internal static ConfigEntry<bool> RideStyle, Tuck, LegDangle, KneeDown, LookIntoCorner, FootDown;
        internal static ConfigEntry<RideStyleKind> Style;
        internal static ConfigEntry<FootSide> FootDownSide;
        internal static ConfigEntry<int> BrakeFingers;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Show a driver in your car (cosmetic; nothing else changes).");
            ShowInDriverView = Config.Bind("Look", "ShowInDriverView", true, "DriverCam's driver view: show your body, arms and hands on the wheel (the head is left out: the camera is at the eyes).");
            ShowInChaseView = Config.Bind("Look", "ShowInChaseView", false, "Chase and hood views: show the driver too. The game's cars have painted (opaque) windows, so the driver is hidden inside the body: off saves the drawing cost.");
            Outline = Config.Bind("Look", "Outline", false, "Draw the game's cartoon outline around the driver (a copy of your car's outline material). Off by default: in driver view the inverted outline hull can fill the screen (DriverCam strips the car's outline there for the same reason). Applies when the driver is next built (restart or Enabled off / on).");
            AnimEnabled = Config.Bind("Anim", "Enabled", true, "Animation clips (driver_anims.dra) layered under the steering-wheel IK: idle breathing and small head moves, a lean with the steering, the body turning with HeadLook's look, bracing on hard braking, a jolt when you crash. Off = the 0.1 driver (breathing only).");
            ShiftHand = Config.Bind("Anim", "ShiftHand", true, "On a gear change the right hand leaves the wheel for the cockpit's gear knob and comes back (cars whose DriverCam cockpit has no knob keep both hands on the wheel). Needs [Anim] Enabled.");
            Celebrate = Config.Bind("Anim", "Celebrate", true, "A fist pump when you complete a level. Needs [Anim] Enabled.");
            BikeEnabled = Config.Bind("Bike", "Enabled", true, "On a Bikes motorcycle the driver rides it: sport-bike tuck, hands on the grips, feet on the pegs, leaning with the bike, shown in every view (head-less in DriverCam's driver view). Off = the driver sits in the hidden donor car's seat as in a car.");
            HangOff = Config.Bind("Bike", "HangOff", true, "Hang off in corners: the hips slide up to 15 cm to the inside with the lean (full at 30 deg), the upper body leans in up to 12 deg more past 30 deg, the inside knee opens. Off = the rider stays centred on the seat. Needs [Bike] Enabled.");
            RideStyle = Config.Bind("Bike", "RideStyle", true, "RIDE-style rider (0.4.0): the body moves with the riding: tuck at speed, sits up and braces on the brakes, the inside leg out under braking, a slow visible weight shift side to side, knee down and elbow drop at full lean, the head leading into corners, a foot down at a standstill, the left foot on the shifter, brake and clutch fingers, the throttle hand rolling. Off = the 0.3.0 rider. Needs [Bike] Enabled.");
            Style = Config.Bind("Bike", "Style", RideStyleKind.Balanced, "Upper body in corners: Balanced (up to 12 deg more lean past 30 deg, elbow drop at knee-down), ShouldersOut (up to 22 deg, full elbow drop), OldSchool (6 deg, hips 60% across, knee half out, no elbow drop). Needs [Bike] RideStyle.");
            Tuck = Config.Bind("Bike", "Tuck", true, "Tuck behind the screen above 28 m/s (about 100 km/h) on more than 60% throttle with the bike nearly upright; sits up the moment you lift off, brake or lean past 15 deg. Needs [Bike] RideStyle.");
            LegDangle = Config.Bind("Bike", "LegDangle", true, "Hard braking above 20 m/s: the inside leg comes off its peg and swings out (back on the peg past 30% of Bikes' MaxLean or under 20% brake). Needs [Bike] RideStyle.");
            KneeDown = Config.Bind("Bike", "KneeDown", true, "Past 80% of Bikes' MaxLean the inside knee opens wider to the tarmac and (Balanced / ShouldersOut) the inside elbow drops. Needs [Bike] RideStyle and HangOff.");
            LookIntoCorner = Config.Bind("Bike", "LookIntoCorner", true, "The head turns into the corner before the body (up to 26 deg, led by the steering and the lean 0.35 s ahead), the shoulders follow. Adds to HeadLook. Needs [Bike] RideStyle.");
            FootDown = Config.Bind("Bike", "FootDown", true, "Below 1.5 m/s a foot goes down to the ground (and back onto its peg as you pull away). Needs [Bike] RideStyle.");
            FootDownSide = Config.Bind("Bike", "FootDownSide", FootSide.Left, "Which foot goes down at a standstill (Left keeps the right foot on the rear brake).");
            BrakeFingers = Config.Bind("Bike", "BrakeFingers", 2, new ConfigDescription("Fingers on the front brake lever when braking (2 or 4).", new AcceptableValueList<int>(2, 4)));
            LogEvents = Config.Bind("Debug", "LogEvents", false, "Log camera-mode changes, re-fits, show / hide, object builds and animation events (shift, crash jolt, celebrate).");
            ForceCpuSkin = Config.Bind("Debug", "ForceCpuSkin", false, "Skin the driver on the CPU instead of the GPU (used automatically when the GPU skinning self-test fails). Applies when the driver is next built.");

            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[Driver] game check crashed, plugin stays idle: {e}"); return; }
            if (!GameApi.Ok) return;
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            Log.LogInfo($"Driver {Version} loaded: a driver in your car (driver view {(ShowInDriverView.Value ? "on" : "off")}, chase view {(ShowInChaseView.Value ? "on" : "off")}, animations {(AnimEnabled.Value ? "on" : "off")}, bike rider {(BikeEnabled.Value ? (HangOff.Value ? "on with hang-off" : "on") : "off")}, ride style {(BikeEnabled.Value && RideStyle.Value ? Style.Value.ToString() : "off")}).");
        }
    }
}
