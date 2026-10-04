using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EngineAudio
{
    /// <summary>
    /// Traffic engines (doc: "Traffic engines"). The game loops one 1-second clip per car and eases its pitch towards
    /// 0.1 + 0.9 x clamp01(speed), which sits at 1.0 at any normal speed. A Harmony postfix on
    /// AIVehicleSoundHandler.HandleSFXs (its per-frame sound update) overwrites that pitch with a 4-gear curve of the car's
    /// own speed (0.75-1.35, a drop at each shift point), a fixed per-car offset (up to +-6%, from the car's instance id)
    /// and a volume that rises a little with speed. A postfix on SetupEngineSound starts each loop at a random point.
    /// The game rewrites pitch every frame itself, so turning this off (config / F1) hands pitch straight back; the
    /// volume each source had before we first touched it is remembered and written back.
    /// </summary>
    internal static class TrafficEngines
    {
        private static readonly float[] ShiftAt = { 0f, 11f, 20f, 29f, 60f };   // m/s: gear 1 below 11, gear 2 to 20 ...
        // per traffic engine source: the volume the game gave it (read once, before we ever scale it) and its pitch offset
        private sealed class Entry { public AudioSource Source; public float Volume, Offset; }
        private static readonly Dictionary<IntPtr, Entry> Known = new Dictionary<IntPtr, Entry>();
        private static readonly List<IntPtr> Gone = new List<IntPtr>();
        private static readonly System.Random Rng = new System.Random();
        internal static bool Installed;
        private static bool _wasOn;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony)
        {
            var handle = AccessTools.Method(typeof(AIVehicleSoundHandler), "HandleSFXs");
            var setup = AccessTools.Method(typeof(AIVehicleSoundHandler), "SetupEngineSound");
            if (handle == null) return;
            harmony.Patch(handle, postfix: new HarmonyMethod(typeof(TrafficEngines), nameof(AfterHandle)));
            if (setup != null) harmony.Patch(setup, postfix: new HarmonyMethod(typeof(TrafficEngines), nameof(AfterSetup)));
            Installed = true;
            Plugin.Log.LogInfo("[EngineAudio] traffic engines patched (AIVehicleSoundHandler.HandleSFXs, SetupEngineSound)");
        }

        private static bool On => Plugin.Enabled.Value && Plugin.TrafficEnabled.Value && Runner.SessionOn;

        private static void AfterHandle(object __instance)
        {
            try
            {
                if (!GameApi.TrafficEngine(__instance, out var src, out float speed)) return;
                IntPtr key = src.Pointer;
                if (!On)
                {
                    // hand the game's volume back once (pitch the game rewrites every frame itself)
                    if (_wasOn && Known.TryGetValue(key, out var old)) { src.volume = old.Volume; Known.Remove(key); }
                    if (Known.Count == 0) _wasOn = false;
                    return;
                }
                _wasOn = true;
                if (!Known.TryGetValue(key, out var e))
                {
                    if (Known.Count > 256) Prune();   // pooled cars come and go: drop only sources that no longer exist
                    int id = src.GetInstanceID();
                    e = new Entry { Source = src, Volume = src.volume, Offset = ((id % 13 + 13) % 13 - 6) * 0.01f };
                    Known[key] = e;
                }
                int gear = 0;
                for (int i = ShiftAt.Length - 2; i >= 0; i--) if (speed >= ShiftAt[i]) { gear = i; break; }
                float lo = ShiftAt[gear], hi = ShiftAt[gear + 1];
                float p = Mathf.Clamp01((speed - lo) / Mathf.Max(1f, hi - lo));
                float pitch = speed < 0.5f ? 0.7f : 0.75f + 0.5f * p + 0.04f * gear;
                src.pitch = Mathf.Clamp(pitch + e.Offset, 0.6f, 1.45f);
                src.volume = e.Volume * (0.85f + 0.3f * Mathf.Clamp01(speed / 35f));
            }
            catch { /* never break the game's own sound update */ }
        }

        /// <summary>Drops entries whose source was destroyed (never re-reads a volume we already scaled).</summary>
        private static void Prune()
        {
            Gone.Clear();
            foreach (var kv in Known) if (kv.Value.Source == null) Gone.Add(kv.Key);
            foreach (var k in Gone) Known.Remove(k);
        }

        private static void AfterSetup(object __instance)
        {
            try
            {
                if (!GameApi.TrafficEngine(__instance, out var src, out _)) return;
                Known.Remove(src.Pointer);   // a fresh setup (on or off): its volume is the game's again, read anew next frame
                if (!On) return;
                var clip = src.clip;
                if (clip != null && clip.length > 0.1f) src.time = (float)Rng.NextDouble() * (clip.length - 0.05f);
            }
            catch { /* never break the game's own setup */ }
        }
    }
}
