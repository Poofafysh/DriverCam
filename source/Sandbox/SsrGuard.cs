using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sandbox
{
    /// <summary>
    /// Turns the game's screen-space reflections off for the length of a sandbox run, so the wide road isn't mirror-like,
    /// and puts them back exactly as they were when the run ends. SSR (PostProcessingManager.ssrRendererFeature, the
    /// UniversalScreenSpaceReflection renderer feature) is a global screen effect, so a matte road material can't stop it:
    /// it reflects whatever is on screen, including the moving cars. Only the features that were on get switched off, and
    /// only those are switched back on, so a player who has reflections off keeps them off. Restored on every exit path.
    /// </summary>
    internal static class SsrGuard
    {
        private static bool _off;        // we currently hold SSR disabled
        private static bool _broken;     // gave up after errors; leave the game's reflections alone
        private static float _nextFind;  // throttle the manager search while it's missing
        private static readonly List<ScriptableRendererFeature> _disabled = new List<ScriptableRendererFeature>();

        /// <summary>Call every frame with whether a sandbox run is active. Cheap once resolved.</summary>
        internal static void Apply(bool sandboxActive)
        {
            if (_broken) return;
            try
            {
                if (sandboxActive) { if (!_off) Disable(); }
                else if (_off) RestoreNow("sandbox ended");
            }
            catch (Exception e)
            {
                try { RestoreNow("error"); } catch { /* scene gone */ }
                _broken = true;
                Plugin.Log.LogWarning($"[Sandbox] SSR guard off after an error (the game's reflections are left as they are): {e.Message}");
            }
        }

        private static void Disable()
        {
            var ppm = Manager();
            if (ppm == null) return;   // not found yet: try again next frame (throttled)
            var feats = ppm.ssrRendererFeature;
            if (feats == null) return;   // nothing to toggle; don't latch _off so we don't claim a restore later
            _disabled.Clear();
            for (int i = 0; i < feats.Length; i++)
            {
                var f = feats[i];
                if (f != null && f.isActive) { f.SetActive(false); _disabled.Add(f); }
            }
            _off = true;
            Plugin.Log.LogInfo($"[Sandbox] screen-space reflections off for this run ({_disabled.Count} feature(s)); the road isn't mirror-like.");
        }

        /// <summary>Put SSR back exactly as it was (only re-enables the features we turned off).</summary>
        internal static void RestoreNow(string why)
        {
            if (!_off && _disabled.Count == 0) return;
            int n = 0;
            foreach (var f in _disabled) { try { if (f != null) { f.SetActive(true); n++; } } catch { /* feature gone */ } }
            _disabled.Clear();
            _off = false;
            if (n > 0) Plugin.Log.LogInfo($"[Sandbox] screen-space reflections back on ({why}).");
        }

        private static PostProcessingManager Manager()
        {
            if (Time.unscaledTime < _nextFind) return null;   // don't scan the scene every frame while it's missing
            _nextFind = Time.unscaledTime + 1f;
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PostProcessingManager>());
            if (all == null || all.Length == 0) return null;
            return all[0] == null ? null : all[0].TryCast<PostProcessingManager>();
        }
    }
}
