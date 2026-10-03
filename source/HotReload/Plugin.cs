using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace HotReload
{
    /// <summary>
    /// Host for hot-reloadable mod code. Installed once in BepInEx/plugins (needs one game restart); it then loads
    /// every IHotModule DLL from BepInEx/hot/ from bytes (the files are never locked), reloads a DLL when it changes on
    /// disk or when ReloadKey (F11) is pressed, and drives the modules' Update/OnGUI from the only injected MonoBehaviour.
    /// </summary>
    [BepInPlugin(Guid, "HotReload", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.hotreload";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;

        internal static ConfigEntry<string> ReloadKey;
        internal static ConfigEntry<bool> WatchFolder;
        internal static ConfigEntry<string> HotFolder;
        internal static ConfigEntry<int> DebounceMs;
        internal static ConfigEntry<string> LoadMode;
        internal static ConfigEntry<bool> ShowToast;

        public override void Load()
        {
            Log = base.Log;
            ReloadKey = Config.Bind("General", "ReloadKey", "F11",
                "Input System key name that reloads every hot module now (even if unchanged). Avoid F6-F10 (DriverCam, CurbFeel).");
            WatchFolder = Config.Bind("General", "WatchFolder", true, "Reload a hot module automatically when its DLL in HotFolder changes.");
            HotFolder = Config.Bind("General", "HotFolder", "hot",
                "Folder with the hot module DLLs, relative to the BepInEx folder (or an absolute path). Must NOT be inside BepInEx/plugins.");
            DebounceMs = Config.Bind("General", "DebounceMs", 400,
                "Wait this long after the last change to a DLL before reloading it, so a build that is still copying isn't read half-written.");
            LoadMode = Config.Bind("General", "LoadMode", "Collectible",
                new ConfigDescription("Collectible: load each version into its own collectible AssemblyLoadContext, so old versions can be freed " +
                    "(falls back to Individual by itself if the runtime refuses). Individual: Assembly.Load(bytes); old versions stay in memory (a small leak).",
                    new AcceptableValueList<string>("Collectible", "Individual")));
            ShowToast = Config.Bind("General", "ShowToast", true, "Show a short on-screen message after each reload.");

            ClassInjector.RegisterTypeInIl2Cpp<HotHost>();
            AddComponent<HotHost>();
            Log.LogInfo($"HotReload {Version} loaded. Hot modules: {ModuleManager.HotDir}. {ReloadKey.Value} = reload all now.");
        }
    }

    /// <summary>The only IL2CPP-injected type: forwards Unity's Update/OnGUI to the module manager.</summary>
    public class HotHost : MonoBehaviour
    {
        public HotHost(IntPtr ptr) : base(ptr) { }

        private void Update() => ModuleManager.Update();

        private void OnGUI() => ModuleManager.OnGUI();
    }
}
