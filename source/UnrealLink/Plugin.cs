using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using RogueShared;
using UnityEngine.InputSystem;

namespace UnrealLink
{
    /// <summary>
    /// UnrealLink (prototype, off by default): an Unreal Engine avatar composited over the game.
    /// Unreal (the RogueLink plugin in a small UE 5.8 window started by tools/unreallink-start.ps1, D3D11) renders the
    /// player's avatar from the game's own camera with a transparent background; this plugin
    ///   - writes the game's frame into shared memory every frame (camera, car body, view, inputs, light, and the
    ///     AppDomain data of DriverCam / HeadLook / EngineAudio / Bikes), then sets the frame event (LinkProtocol.cs);
    ///   - opens Unreal's shared D3D11 textures through the native helper UnrealLinkNative.dll and copies the newest
    ///     picture into a RenderTexture on Unity's render thread (SharedTexture.cs);
    ///   - draws it full screen under the game's HUD (Compositor.cs), in the driver view and the chase views;
    ///   - publishes AppDomain "rogue.unreallink.live" (float[2]: [0] 1 = the Unreal layer is on screen over the
    ///     player's car, [1] Time.unscaledTime of the last write) so Driver can hide its own body later.
    /// Without Unreal running nothing is drawn: one log line, and it keeps looking (Unreal's heartbeat) every 3 s.
    /// Design + measurements: source/UnrealLink/README.md.
    /// </summary>
    [BepInPlugin(Guid, "UnrealLink", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.unreallink";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, ShowInChase, FlipY, LogTimings;
        internal static ConfigEntry<float> ResolutionScale;
        internal static ConfigEntry<Key> ToggleKey;
        internal static ConfigEntry<string> LaunchCommand, LaunchArguments;
        internal static ConfigEntry<bool> AutoLaunch;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", false, new ConfigDescription(
                "Draw the Unreal avatar layer over the game (needs Unreal running with the RogueLink plugin; see the README). Prototype: off by default.",
                null, HubLink.Meta("Unreal avatar layer", applies: "now")));
            ShowInChase = Config.Bind("General", "ShowInChase", true, new ConfigDescription(
                "Also draw it in the chase / hood views (no occlusion yet: the driver shows through the car body).",
                null, HubLink.Meta("Show in chase views", applies: "now")));
            ToggleKey = Config.Bind("General", "ToggleKey", Key.Insert,
                "Hold Ctrl and press this key to show / hide the layer for this session (Input System key name; None = no key).");
            ResolutionScale = Config.Bind("Render", "ResolutionScale", 0.5f, new ConfigDescription(
                "Unreal renders at this fraction of the game's resolution (0.5 at 4K = 1920 x 1080), upscaled when drawn.",
                new AcceptableValueRange<float>(0.25f, 1f), HubLink.Meta("Render scale", 0.25, 1, 0.05, "x", applies: "now")));
            FlipY = Config.Bind("Render", "FlipY", false, new ConfigDescription(
                "Flip the Unreal picture upside down (if it shows inverted on your setup).", null, HubLink.Meta("Flip vertically", advanced: true, applies: "now")));
            LogTimings = Config.Bind("Debug", "LogTimings", true, new ConfigDescription(
                "Every 10 s while the link is live: one [Perf] line (latency in frames, new pictures, composite cost, Unreal's own timings).",
                null, HubLink.Meta("Log timings", advanced: true, applies: "now")));
            AutoLaunch = Config.Bind("Launch", "AutoLaunch", false, new ConfigDescription(
                "Start Unreal yourself when the layer is on and no Unreal answers (needs Command).", null, HubLink.Meta("Start Unreal automatically", advanced: true, applies: "now")));
            LaunchCommand = Config.Bind("Launch", "Command", "",
                "Program to start for AutoLaunch, e.g. powershell.exe (empty = never start anything).");
            LaunchArguments = Config.Bind("Launch", "Arguments", "",
                "Its arguments, e.g. -NoProfile -ExecutionPolicy Bypass -File <repo>\\tools\\unreallink-start.ps1");
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            HubLink.Status(Guid, Runner.HubStatus);
            Log.LogInfo($"UnrealLink {Version} loaded ({(Enabled.Value ? "on" : "off")}; render scale {ResolutionScale.Value:0.##}, Ctrl+{ToggleKey.Value} shows / hides the layer).");
        }
    }
}
