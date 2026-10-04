#if HOT
using System;
using HarmonyLib;
using HotReload;

namespace CurbFeel
{
    /// <summary>
    /// Hot-module entry point, only in the -p:Hot=true build (deployed to BepInEx/hot/ and loaded by the HotReload host).
    /// Same features and config file as the plugin; the host's MonoBehaviour drives Update/OnGUI, so nothing is injected.
    /// Unload puts the game back exactly as stock so the next build starts clean.
    /// </summary>
    public sealed class CurbFeelHotModule : IHotModule
    {
        private Harmony _harmony;

        public string Id => Plugin.Guid;

        public void Load(HotContext ctx)
        {
            Plugin.Log = ctx.Log;
            Plugin.Cfg = ctx.Config;
            Settings.Bind(ctx.Config);

            _harmony = new Harmony(ctx.HarmonyId);
            ScrapePatches.Install(_harmony);
            Plugin.Log.LogInfo($"[CurbFeel] start: {CurbFeelCore.StateLine()}");
            Plugin.Log.LogInfo($"CurbFeel {Plugin.Version} loaded as a hot module (load #{ctx.Generation}). " +
                               $"{Settings.ReloadKey.Value} = reload config, {Settings.ToggleKey.Value} = toggle on/off, {Settings.OverlayKey.Value} = status panel.");
        }

        public void Update() => CurbFeelCore.Update();

        public void OnGUI() => CurbFeelCore.OnGUI();

        public void Unload()
        {
            try { CurbFeelCore.RevertAll(); }
            catch (Exception e) { Plugin.Log.LogError($"[CurbFeel] revert on unload failed: {e}"); }
            try { _harmony?.UnpatchSelf(); }
            catch (Exception e) { Plugin.Log.LogError($"[CurbFeel] unpatch on unload failed: {e}"); }
            _harmony = null;
            try { CurbFeelCore.ReleaseCaches(); }
            catch (Exception e) { Plugin.Log.LogError($"[CurbFeel] cleanup on unload failed: {e}"); }
            Plugin.Log.LogInfo("[CurbFeel] hot module unloaded, game back to stock.");
        }
    }
}
#endif
