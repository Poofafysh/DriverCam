using BepInEx.Configuration;
using BepInEx.Logging;
#if !HOT
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
#endif

namespace CurbFeel
{
#if HOT
    /// <summary>Hot-module build (-p:Hot=true): no BepInEx plugin class and no injected MonoBehaviour. HotModule.cs fills Log/Cfg.</summary>
    internal static class Plugin
    {
        internal static ManualLogSource Log;
#else
    [BepInPlugin(Guid, "CurbFeel", Version)]
    public class Plugin : BasePlugin
    {
        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;
            Cfg = Config;
            Settings.Bind(Config);

            ClassInjector.RegisterTypeInIl2Cpp<CurbFeelRunner>();
            AddComponent<CurbFeelRunner>();

            ScrapePatches.Install(new Harmony(Guid));
            Log.LogInfo($"[CurbFeel] start: {CurbFeelCore.StateLine()}");
            Log.LogInfo($"CurbFeel {Version} loaded. {Settings.ReloadKey.Value} = reload config, {Settings.ToggleKey.Value} = toggle on/off, {Settings.OverlayKey.Value} = status panel.");
        }
#endif
        public const string Guid = "rogue.curbfeel";
        public const string Version = "0.4.1";

        /// <summary>BepInEx/config/rogue.curbfeel.cfg (the plugin's Config, or the one the HotReload host passes in).</summary>
        internal static ConfigFile Cfg;

        internal static void Verbose(string msg)
        {
            if (Settings.VerboseLog.Value) Log.LogInfo(msg);
        }
    }
}
