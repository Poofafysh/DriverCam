using BepInEx;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace DriverCam;

[BepInPlugin(Guid, "DriverCam", "0.9.1")]
public class Plugin : BasePlugin
{
    public const string Guid = "drivingrogue.drivercam";

    internal static ManualLogSource Logger;

    // Head placement: HeadHeight/HeadForward are fractions of the car body, Offset* are extra meters on top
    internal static ConfigEntry<float> HeadHeight, HeadForward, OffsetX, OffsetY, OffsetZ, Pitch, LookIntoTurn;
    internal static ConfigEntry<float> Fov, FovSpeedBoost, NearClip, SteerAngle, CockpitScale, InteriorBrightness, OutlineThickness;
    internal static ConfigEntry<bool> ShowCockpit, HideCarBody, CockpitOutline, MirrorEnabled;
    internal static ConfigEntry<int> MirrorResolution;
    internal static ConfigEntry<float> MirrorFov, MirrorPitch, MirrorYaw, MirrorOffsetX, MirrorOffsetY, MirrorOffsetZ, HeadFollowsShake;
    internal static MirrorSettings RearMirror, LeftMirror, RightMirror;
    internal static ConfigEntry<bool> HudEnabled, HudOnlyInDriverView;
    internal static ConfigEntry<float> HudPullToCenter, HudSize, HudHealthX, HudHealthY, HudSpeedoX, HudSpeedoY, HudAbilityX, HudAbilityY;
    internal static ConfigEntry<bool> ShowButton;
    internal static ConfigEntry<float> ButtonX, ButtonY, UiScale, TuneStep;
    internal static ConfigEntry<bool> AddToCycle, DumpModes, DriverSelected, ViewButtonToggles;

    /// <summary>Bumped whenever a setting changes so the view and cockpit rebuild.</summary>
    internal static int SettingsVersion;

    /// <summary>Cars that have a fitted cockpit (cockpit_&lt;Car&gt;.dcm next to the plugin).</summary>
    static IEnumerable<string> FittedCars()
    {
        var folder = System.IO.Path.Combine(Paths.PluginPath, "DriverCam");
        if (!System.IO.Directory.Exists(folder)) yield break;
        foreach (var file in System.IO.Directory.GetFiles(folder, "cockpit_*.dcm"))
        {
            var car = System.IO.Path.GetFileNameWithoutExtension(file).Substring("cockpit_".Length);
            if (!car.Contains('_')) yield return car;
        }
    }

    MirrorSettings BindSideMirror(string section, float outwardYaw) => new()
    {
        Name = section,
        Enabled = Config.Bind(section, "Enabled", true, "Working side mirror."),
        OffsetX = Config.Bind(section, "OffsetX", 0f, "Mirror camera position: left/right in meters."),
        OffsetY = Config.Bind(section, "OffsetY", 0f, "Mirror camera position: down/up in meters."),
        OffsetZ = Config.Bind(section, "OffsetZ", 0f, "Mirror camera position: back/forward in meters."),
        Yaw = Config.Bind(section, "Yaw", outwardYaw, "Mirror camera aim left/right in degrees (positive turns towards the car's left)."),
        Pitch = Config.Bind(section, "Pitch", 3f, "Mirror camera tilt in degrees (positive looks down)."),
        Fov = Config.Bind(section, "Fov", 24f, "Mirror camera vertical field of view."),
        Aspect = 1.4f,
    };

    public override void Load()
    {
        Logger = Log;

        HeadHeight = Config.Bind("Driver", "HeadHeight", 0.8f, "Eye height as a fraction of the car body's height (0 = ground, 1 = roof).");
        HeadForward = Config.Bind("Driver", "HeadForward", -0.05f, "Eye position along the car as a fraction of its length from the center (negative = towards the back).");
        OffsetX = Config.Bind("Driver", "OffsetX", 0f, "Extra left/right shift in meters.");
        OffsetY = Config.Bind("Driver", "OffsetY", 0f, "Extra up/down shift in meters.");
        OffsetZ = Config.Bind("Driver", "OffsetZ", 0f, "Extra forward/back shift in meters.");
        Pitch = Config.Bind("Driver", "Pitch", 3f, "Look-down angle in degrees.");
        LookIntoTurn = Config.Bind("Driver", "LookIntoTurn", 6f, "How many degrees the driver looks into a turn at full steering.");

        Fov = Config.Bind("View", "Fov", 60f, "Field of view at low speed.");
        FovSpeedBoost = Config.Bind("View", "FovSpeedBoost", 8f, "Extra field of view at top speed.");
        NearClip = Config.Bind("View", "NearClip", 0.03f, "Camera near clip plane in driver view, so the cockpit isn't cut off.");
        ShowCockpit = Config.Bind("View", "ShowCockpit", true, "Show the dashboard, steering wheel, pillars and mirror in driver view.");
        HideCarBody = Config.Bind("View", "HideCarBody", false, "Hide the player's car body in driver view.");
        SteerAngle = Config.Bind("View", "SteerAngle", 120f, "Steering wheel rotation in degrees at full lock.");
        CockpitScale = Config.Bind("View", "CockpitScale", 1f, "Size multiplier for the cockpit model (1 = fitted to the car's width).");
        InteriorBrightness = Config.Bind("View", "InteriorBrightness", 0.35f, "How much the cockpit lights itself up, so it stays visible on dark tracks (0 = only scene lighting).");
        MirrorEnabled = Config.Bind("View", "MirrorEnabled", true, "Working rear-view mirror (renders a small extra camera).");
        MirrorResolution = Config.Bind("View", "MirrorResolution", 512, "Rear-view mirror image width in pixels (height is a quarter of it).");
        MirrorFov = Config.Bind("View", "MirrorFov", 16f, "Rear-view mirror vertical field of view.");
        MirrorPitch = Config.Bind("View", "MirrorPitch", 2f, "Rear-view mirror tilt in degrees (positive looks down).");
        MirrorYaw = Config.Bind("View", "MirrorYaw", 0f, "Rear-view mirror camera aim left/right in degrees.");
        MirrorOffsetX = Config.Bind("View", "MirrorOffsetX", 0f, "Rear-view mirror camera position: left/right in meters.");
        MirrorOffsetY = Config.Bind("View", "MirrorOffsetY", 0f, "Rear-view mirror camera position: down/up in meters.");
        MirrorOffsetZ = Config.Bind("View", "MirrorOffsetZ", 0f, "Rear-view mirror camera position: back/forward in meters.");
        HeadFollowsShake = Config.Bind("Driver", "HeadFollowsShake", 1f, "How much the driver's head moves with the car body shake (0 = steady view, 1 = locked to the car).");
        CockpitOutline = Config.Bind("View", "CockpitOutline", true, "Draw the game's black cartoon outline around cockpit parts.");
        OutlineThickness = Config.Bind("View", "OutlineThickness", 1f, "Cockpit outline thickness relative to the car's own outline.");

        ShowButton = Config.Bind("UI", "ShowButton", true, "Show the on-screen DriverCam button.");
        ButtonX = Config.Bind("UI", "ButtonX", 20f, "Button position from the left edge, in pixels.");
        ButtonY = Config.Bind("UI", "ButtonY", 420f, "Button position from the top edge, in pixels.");
        UiScale = Config.Bind("UI", "UiScale", 1.5f, "Size of the DriverCam button and panel.");

        ViewButtonToggles = Config.Bind("Controls", "ViewButtonToggles", true, "Controller View button (the small two-squares button) toggles Driver view, like F6.");
        TuneStep = Config.Bind("Controls", "TuneStep", 0.05f, "How far one press moves the seat, in meters.");
        AddToCycle = Config.Bind("General", "AddToCameraCycle", true, "Add the Driver view to the game's own camera-switch list. F6 toggles it either way.");
        DumpModes = Config.Bind("General", "LogCameraModes", true, "Write the game's built-in camera mode values to the BepInEx log.");
        DriverSelected = Config.Bind("General", "DriverViewSelected", false, "Remembers that Driver view is the chosen camera (the game itself only stores its own three views).");

        // 0.2.0 measured cars wrongly, so seat offsets saved by it are meaningless now
        var layout = Config.Bind("General", "LayoutVersion", 0, "Internal: used to reset seat offsets when the placement logic changes.");
        if (layout.Value < 4)
        {
            // 0.6 places fitted cockpits by the car's own eye position; start the seat tweaks from zero
            OffsetX.Value = 0f;
            OffsetY.Value = 0f;
            OffsetZ.Value = 0f;
            Fov.Value = 60f;
            layout.Value = 4;
            Log.LogInfo("Reset seat offsets and field of view for the new cockpit.");
        }

        // Pose camera, cockpit and mirror right before rendering, after the game's body shake has been applied
        UnityEngine.Application.add_onBeforeRender(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new System.Action(DriverView.OnBeforeRender)));

        HudEnabled = Config.Bind("HUD", "Enabled", true, "Move/scale the health bar, speedometer and ability bar.");
        HudOnlyInDriverView = Config.Bind("HUD", "OnlyInDriverView", true, "Only change the HUD layout while in Driver view.");
        HudPullToCenter = Config.Bind("HUD", "PullToCenter", 0.35f, "Move the HUD elements towards the middle of the screen (0 = game layout, 1 = all in the middle).");
        HudSize = Config.Bind("HUD", "Size", 0.85f, "Size of the HUD elements (1 = game size).");
        HudHealthX = Config.Bind("HUD", "HealthX", 0f, "Health bar extra left/right offset.");
        HudHealthY = Config.Bind("HUD", "HealthY", 0f, "Health bar extra down/up offset.");
        HudSpeedoX = Config.Bind("HUD", "SpeedometerX", 0f, "Speedometer extra left/right offset.");
        HudSpeedoY = Config.Bind("HUD", "SpeedometerY", 0f, "Speedometer extra down/up offset.");
        HudAbilityX = Config.Bind("HUD", "AbilityX", 0f, "Ability bar extra left/right offset.");
        HudAbilityY = Config.Bind("HUD", "AbilityY", 0f, "Ability bar extra down/up offset.");

        RearMirror = new MirrorSettings { Name = "RearMirror", Enabled = MirrorEnabled, OffsetX = MirrorOffsetX, OffsetY = MirrorOffsetY, OffsetZ = MirrorOffsetZ, Yaw = MirrorYaw, Pitch = MirrorPitch, Fov = MirrorFov };
        LeftMirror = BindSideMirror("MirrorLeft", +12f);
        RightMirror = BindSideMirror("MirrorRight", -12f);

        // Everything that shapes the driver view is saved per car
        CarPresets.Track(new ConfigEntryBase[] { OffsetX, OffsetY, OffsetZ, Pitch, LookIntoTurn, HeadFollowsShake, Fov, FovSpeedBoost,
            SteerAngle, CockpitScale, InteriorBrightness, OutlineThickness, ShowCockpit, HideCarBody, CockpitOutline });
        CarPresets.Track(RearMirror.All());
        CarPresets.Track(LeftMirror.All());
        CarPresets.Track(RightMirror.All());
        CarPresets.CreateMissing(FittedCars());

        ClassInjector.RegisterTypeInIl2Cpp<DriverCamBehaviour>();
        AddComponent<DriverCamBehaviour>();

        new Harmony(Guid).PatchAll(typeof(Patches));
        Log.LogInfo("DriverCam 0.8 loaded. Click the DriverCam button on screen, or press F6 to toggle driver view.");
    }
}


















