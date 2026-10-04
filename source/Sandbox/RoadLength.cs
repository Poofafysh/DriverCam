using System;
using System.Diagnostics;
using HarmonyLib;
using Game.Runtime.Data;
using Game.Runtime.Systems.LevelGeneration;

namespace Sandbox
{
    /// <summary>
    /// Road length ([Run] LengthMultiplier) for sandbox races. LevelGeneratorTileSelector.GetRandomTiles(RunRaceSO)
    /// (static, 0x7A9BB0, called only by GenerateRegularLevel) adds road tiles until their estimated durations reach
    /// RunRaceSO.desiredDurationSeconds (field 0x24). The prefix multiplies that field on the shared race asset; the
    /// postfix puts the original back, and the Runner puts it back on the next frame if the postfix never ran (an
    /// exception inside the game's method), so the asset is never left changed. The race timer follows the real path
    /// length by itself (TimerManager.InitializeTimer). RoadPathGenerator.GeneratePath (0x7B2260, runs once after every
    /// tile scene has loaded) is postfixed only to log how long the longer road took to load.
    /// </summary>
    internal static class RoadLength
    {
        private static RunRaceSO _race;
        private static float _original;
        private static bool _pending;
        private static readonly Stopwatch Load = new Stopwatch();
        private static float _mult;

        internal static int Install(Harmony h)
        {
            int n = 0;
            try
            {
                var tiles = AccessTools.Method(typeof(LevelGeneratorTileSelector), "GetRandomTiles");
                if (tiles == null) throw new MissingMethodException("LevelGeneratorTileSelector", "GetRandomTiles");
                // harmony-target: LevelGeneratorTileSelector.GetRandomTiles
                h.Patch(tiles, prefix: new HarmonyMethod(typeof(RoadLength), nameof(BeforeTiles)),
                               postfix: new HarmonyMethod(typeof(RoadLength), nameof(AfterTiles)));
                n++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] road length not installed (roads keep their length): {e.Message}"); return n; }
            try
            {
                var path = AccessTools.Method(typeof(RoadPathGenerator), "GeneratePath");
                if (path == null) throw new MissingMethodException("RoadPathGenerator", "GeneratePath");
                // harmony-target: RoadPathGenerator.GeneratePath
                h.Patch(path, postfix: new HarmonyMethod(typeof(RoadLength), nameof(AfterPath)));
                n++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] road load timing not installed: {e.Message}"); }
            return n;
        }

        private static void BeforeTiles(RunRaceSO currentRace)
        {
            try
            {
                Restore();   // never stack on a value a failed call left behind
                if (currentRace == null || !Plugin.ActiveNow()) return;
                float mult = Math.Max(1f, Math.Min(5f, Plugin.LengthMultiplier.Value));
                if (mult <= 1.001f) return;
                _race = currentRace;
                _original = currentRace.desiredDurationSeconds;
                _mult = mult;
                _pending = true;
                currentRace.desiredDurationSeconds = _original * mult;
                Load.Restart();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] road length: {e.Message}"); Restore(); }
        }

        private static void AfterTiles(RunRaceSO currentRace, Il2CppSystem.Collections.Generic.List<RoadTileSO> __result)
        {
            if (!_pending) return;
            float target = 0f;
            try { target = _race == null ? 0f : _race.desiredDurationSeconds; } catch { }
            Restore();
            try
            {
                int count = __result == null ? 0 : __result.Count;
                float meters = 0f, seconds = 0f;
                for (int i = 0; i < count; i++)
                {
                    var t = __result[i];
                    if (t == null) continue;
                    meters += t.RoadLength;
                    seconds += t.EstimatedDuration;
                }
                Plugin.Log.LogInfo($"[Sandbox] road length x{_mult:0.#}: {count} tiles, {meters:0} m (about {seconds:0} s of {target:0} s asked), picked in {Load.Elapsed.TotalMilliseconds:0} ms; loading the tiles...");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] road length log: {e.Message}"); }
        }

        private static void AfterPath(RoadPathGenerator __instance)
        {
            try
            {
                if (!Load.IsRunning) return;
                Load.Stop();
                float finish = 0f;
                try { finish = __instance == null ? 0f : __instance.finishLineDistance; } catch { }
                Plugin.Log.LogInfo($"[Sandbox] road ready: {finish:0} m to the finish line, {Load.Elapsed.TotalSeconds:0.0} s after the tiles were picked (tile scenes loaded)");
            }
            catch { /* logging only */ }
        }

        /// <summary>Puts the race asset's own duration back (postfix, next frame from the Runner, plugin shutdown).</summary>
        internal static void Restore()
        {
            if (!_pending) return;
            _pending = false;
            try { if (_race != null) _race.desiredDurationSeconds = _original; }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sandbox] road length restore: {e.Message}"); }
            _race = null;
        }
    }
}
