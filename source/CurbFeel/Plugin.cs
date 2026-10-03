using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace CurbFeel
{
    [BepInPlugin(Guid, "CurbFeel", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.curbfeel";
        public const string Version = "0.3.1";

        internal static new ManualLogSource Log;
        internal static Plugin Instance;

        public override void Load()
        {
            Instance = this;
            Log = base.Log;
            Settings.Bind(Config);

            ClassInjector.RegisterTypeInIl2Cpp<CurbFeelRunner>();
            AddComponent<CurbFeelRunner>();

            new Harmony(Guid).PatchAll(typeof(ScrapePatches));
            Log.LogInfo($"[CurbFeel] start: {CurbFeelRunner.StateLine()}");
            Log.LogInfo($"CurbFeel {Version} loaded. {Settings.ReloadKey.Value} = reload config, {Settings.ToggleKey.Value} = toggle on/off, {Settings.OverlayKey.Value} = status panel.");
        }

        internal static void Verbose(string msg)
        {
            if (Settings.VerboseLog.Value) Log.LogInfo(msg);
        }
    }
}
