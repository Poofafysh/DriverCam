using System;
using UnityEngine;

namespace Sandbox
{
    /// <summary>
    /// Per-frame driver for the sandbox maps (the work is in <see cref="WideBuild"/>, a plain class): hides the old
    /// scenery a slice per frame after the race loads, re-checks tiles for colliders / renderers / lights added later and
    /// moves the street-light pool. Skips its work only while the pause menu is open (GameState.IsGamePaused), not while
    /// timeScale is 0 (loading screen, countdown), so the hiding never stalls. Restores everything when the race ends
    /// (Plugin.Active goes false: single-player or multiplayer sandbox race left, disconnect), on 3 errors (breaker) and on OnDestroy.
    /// </summary>
    public class MapsRunner : MonoBehaviour
    {
        public MapsRunner(IntPtr ptr) : base(ptr) { }

        private int _errors;
        private bool _broken, _wasInRace;
        private float _next;

        private void Update()
        {
            if (_broken) return;
            try
            {
                float now = Time.unscaledTime;
                if (now >= _next)
                {
                    _next = now + 0.25f;
                    bool inRace = Plugin.Active;   // covers an agreed multiplayer sandbox race too (Multiplayer.SandboxAgreed)
                    if (_wasInRace && !inRace) WideRoads.RestoreEverything("left the sandbox race");
                    _wasInRace = inRace;
                }
                if (!WideBuild.Busy) return;
                if (PausedNow()) return;   // pause menu open: no writes
                WideBuild.Tick();
            }
            catch (Exception e)
            {
                if (++_errors >= 3) { _broken = true; WideRoads.Break(e); }
                else Plugin.Log.LogWarning($"[Sandbox] maps error ({_errors}/3): {e.Message}");
            }
        }

        private static bool PausedNow()
        {
            try { return Game.Runtime.GameState.IsGamePaused; } catch { return false; }
        }

        private void OnDestroy()
        {
            try { WideRoads.Shutdown(); } catch { /* shutting down */ }
        }
    }
}
