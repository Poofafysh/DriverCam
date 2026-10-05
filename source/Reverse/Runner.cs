using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Reverse
{
    /// <summary>
    /// Once a frame: finds the player car (cached, looked up again only when it changes), decides whether reversing is
    /// allowed right now (Hooks.Allowed), ends a reverse on every exit path (paused, toggled off, multiplayer, race over,
    /// wrecked, car change, no car, no physics steps) and publishes "rogue.reverse".
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private IntPtr _carPtr;
        private VehicleManager _veh;
        private VehicleHealth _health;
        private int _errors;

        private void Update()
        {
            if (!Plugin.GameOk || Hooks.Broken) { Hooks.Allowed = false; ReverseLink.Publish(false, 0f); return; }
            try
            {
                Refresh();
                if (Hooks.Active && Time.unscaledTime - Hooks.LastStep > 0.25f) Hooks.Stop("no physics steps");   // paused / loading / car disabled
                ReverseLink.Publish(Hooks.Active && Hooks.Speed > 0f, Hooks.Speed);
            }
            catch (Exception e)
            {
                Hooks.Allowed = false;
                Hooks.Stop("error");
                Plugin.Log.LogWarning($"[Reverse] runner error ({++_errors}/5, then Reverse stays off for this session): {e.Message}");
                if (_errors >= 5) Hooks.Broken = true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Refresh()
        {
            var veh = VehicleManager.Instance;
            IntPtr car = veh == null ? IntPtr.Zero : veh.Pointer;
            if (car != _carPtr)
            {
                Hooks.Stop("car change");
                _carPtr = car; _veh = veh; _health = null;
                Hooks.MovePtr = IntPtr.Zero; Hooks.Move = null; Hooks.Input = null; Hooks.Rb = null;
                if (veh != null)
                {
                    var move = veh.VehicleMovement;
                    Hooks.Move = move;
                    Hooks.Input = veh.VehicleInputHandler;
                    Hooks.Rb = veh.Rigidbody;
                    _health = veh.VehicleHealthHandler;
                    Hooks.MovePtr = move == null ? IntPtr.Zero : move.Pointer;
                    // the game never lets its speed below MinSpeed: above 0 the car never fully stops and reverse can't start
                    if (move != null)
                        Plugin.Log.LogInfo($"[Reverse] player car found (game minimum speed {move.MinSpeed * 3.6f:0.0} km/h{(move.MinSpeed > 0.08f ? ": above 0, so reverse can't start on this car" : "")})");
                }
            }
            if (_veh == null || Hooks.Move == null || Hooks.Input == null || Hooks.Rb == null)
            {
                Hooks.Allowed = false;
                if (Hooks.Active) Hooks.Stop("no car");
                return;
            }

            string block = null;
            if (!Plugin.Enabled.Value) block = "switched off";
            else if (Time.timeScale <= 0f) block = "paused";
            else if (Game.Runtime.GameState.IsMultiplayerMode) block = "multiplayer";
            else if (_veh.LevelWasEnded) block = "race over";
            else if (_health != null && _health.Defeated) block = "car wrecked";
            Hooks.Allowed = block == null;
            if (block != null && Hooks.Active) Hooks.Stop(block);
        }

        private void OnDestroy()
        {
            Hooks.Allowed = false;
            Hooks.Stop("unloaded");
            Hooks.MovePtr = IntPtr.Zero;
            ReverseLink.Uninstall();
        }

        /// <summary>Rogue Hub: the live line on Reverse's card.</summary>
        internal static string HubStatus()
        {
            if (!Plugin.Enabled.Value) return "off";
            if (!Plugin.GameOk || Hooks.Broken) return "unavailable (see the log)";
            if (Hooks.Active) return $"reversing at {Hooks.Speed * 3.6f:0} km/h";
            return $"hold the brake at a standstill to reverse (up to {Plugin.MaxSpeedKmh.Value:0} km/h)";
        }
    }
}
