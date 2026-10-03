using BepInEx.Configuration;
using BepInEx.Logging;

namespace HotReload
{
    /// <summary>
    /// A piece of mod code the HotReload host can load, unload and reload while the game runs.
    /// Put a public class with a public parameterless constructor that implements this in a DLL in <c>BepInEx/hot/</c>.
    ///
    /// Rules for reloadable code:
    /// - Don't inject IL2CPP types (no <c>ClassInjector.RegisterTypeInIl2Cpp</c>, no custom MonoBehaviours): an injected
    ///   type can't be unregistered or registered again under the same name. The host's Update/OnGUI drive you instead.
    /// - Patch with <c>new Harmony(ctx.HarmonyId)</c>; the id is unique per load. The host also unpatches it after Unload.
    /// - <see cref="Unload"/> must put the game back exactly as it was: restore every value you changed, destroy every
    ///   object you created, remove every callback you registered (Unity events, <c>Application.onBeforeRender</c>, ...).
    /// </summary>
    public interface IHotModule
    {
        /// <summary>Stable id, normally the plugin GUID (e.g. "rogue.curbfeel"). Names the config file
        /// (<c>BepInEx/config/&lt;Id&gt;.cfg</c>). The host refuses a module whose id BepInEx already loaded as a normal plugin.</summary>
        string Id { get; }

        /// <summary>Called on the main thread after the DLL is loaded (also after every reload).</summary>
        void Load(HotContext ctx);

        /// <summary>Called on the main thread before a reload and when the DLL is deleted from the hot folder (not when
        /// the game quits). Undo everything Load/Update changed in the game.</summary>
        void Unload();

        /// <summary>Called every frame from the host's MonoBehaviour.Update.</summary>
        void Update();

        /// <summary>Called from the host's MonoBehaviour.OnGUI (IMGUI; several times per frame, like a normal OnGUI).</summary>
        void OnGUI();
    }

    /// <summary>What the host gives a module on each load.</summary>
    public sealed class HotContext
    {
        /// <summary>The module's <see cref="IHotModule.Id"/>.</summary>
        public string Id { get; internal set; }

        /// <summary>Log source named after the module's DLL (e.g. "CurbFeel"), written to BepInEx/LogOutput.log like a plugin's.</summary>
        public ManualLogSource Log { get; internal set; }

        /// <summary><c>BepInEx/config/&lt;Id&gt;.cfg</c>, the same file a normal plugin with that GUID uses. A fresh ConfigFile
        /// per load, so changed defaults and descriptions take effect; saved values are read from disk.</summary>
        public ConfigFile Config { get; internal set; }

        /// <summary>Harmony id unique to this load, e.g. "rogue.curbfeel.hot3".</summary>
        public string HarmonyId { get; internal set; }

        /// <summary>Counts every module load since the game started (1, 2, 3, ...).</summary>
        public int Generation { get; internal set; }

        /// <summary>Full path of the module DLL in the hot folder.</summary>
        public string ModulePath { get; internal set; }

        /// <summary>The hot folder, <c>BepInEx/hot</c> by default.</summary>
        public string HotDir { get; internal set; }

        /// <summary><c>BepInEx/config</c>.</summary>
        public string ConfigDir { get; internal set; }

        /// <summary><c>BepInEx/plugins</c>, for modules that read files a normal plugin would ship there.</summary>
        public string PluginDir { get; internal set; }
    }
}
