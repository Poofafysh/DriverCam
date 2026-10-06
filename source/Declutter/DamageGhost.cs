using System;
using System.Collections.Generic;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Declutter
{
    /// <summary>
    /// Hides the see-through copy of the player's car that the game flashes when it's hit (VehicleDamage plays its
    /// GlitchFX, the "ghost phasing" effect): every renderer in the player vehicle's GlitchFX components gets
    /// Renderer.forceRenderingOff (the effect only turns `enabled` on and off, so it stays hidden). Polled once a second
    /// (a new car brings new renderers); not while paused. Cosmetic and local (single-player and multiplayer). Shown again
    /// when [Effects] HideDamageGhost or Declutter is switched off, after repeated errors and on unload.
    /// </summary>
    internal static class DamageGhost
    {
        private static readonly List<Renderer> s_hidden = new List<Renderer>();
        private static readonly HashSet<int> s_ids = new HashSet<int>();
        private static float s_next;
        private static bool s_logged;

        internal static int Hidden => s_hidden.Count;

        internal static void Tick(float now)
        {
            if (!(Plugin.Enabled.Value && Plugin.HideDamageGhost.Value)) { if (s_hidden.Count > 0) Restore("switched off"); return; }
            if (Time.timeScale <= 0f || now < s_next) return;
            s_next = now + 1f;
            var veh = VehicleManager.Instance;
            if (veh == null) return;
            var fx = veh.GetComponentsInChildren<GlitchFX>(true);
            int added = 0;
            for (int i = 0; i < fx.Length; i++)
            {
                var arr = fx[i] != null ? fx[i].glitchRendererArray : null;
                if (arr == null) continue;
                for (int k = 0; k < arr.Length; k++)
                {
                    var r = arr[k];
                    if (r == null) continue;
                    if (!r.forceRenderingOff) r.forceRenderingOff = true;
                    if (s_ids.Add(r.GetInstanceID())) { s_hidden.Add(r); added++; }
                }
            }
            if (added > 0 && !s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo($"[Declutter] damage ghost hidden: {s_hidden.Count} renderer(s) of the car's glitch effect");
            }
        }

        internal static void Restore(string why)
        {
            int n = 0;
            foreach (var r in s_hidden) { try { if (r != null) { r.forceRenderingOff = false; n++; } } catch { /* gone with its car */ } }
            if (n > 0) Plugin.Log.LogInfo($"[Declutter] damage ghost shown again ({n} renderer(s); {why})");
            s_hidden.Clear(); s_ids.Clear(); s_logged = false;
        }
    }
}
