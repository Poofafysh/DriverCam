using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Cameras;
using Game.Runtime.Manager;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Driver
{
    /// <summary>
    /// The only file that touches game types (checked by name at startup, as HeadLook and RacingLine do):
    /// CameraControllerInGame.Instance / CurrentCamera / CurrentCameraMode (CameraModeSO.GetId, followTarget) /
    /// VehicleProvider (ICameraVehicle.BodyTransform, TurnInput) and VehicleManager.Instance.VehicleInputHandler
    /// (Throttle, BrakeInput). Nothing is written to the game.
    /// </summary>
    internal static class GameApi
    {
        internal static bool Ok { get; private set; }
        internal static bool InputOk { get; private set; }
        internal static bool GearOk { get; private set; }    // VehicleGearboxHandler.CurrentGearIndex (shift hand)
        internal static bool SpeedOk { get; private set; }   // VehicleMovement.CurrentSpeed (brake brace only at speed)
        internal static bool HitsOk { get; private set; }    // CollisionScoreProviderSO.TotalHits (crash jolt)
        internal static bool WonOk { get; private set; }     // GameState.LevelCompleted (celebrate)

        internal enum View { None, Driver, Hood, Chase, Other }

        internal static void Check()
        {
            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            Ok = Has(asm, "Game.Runtime.Cameras.CameraControllerInGame", missing, "Instance", "CurrentCamera", "CurrentCameraMode", "VehicleProvider")
              && Has(asm, "Game.Runtime.Data.CameraModeSO", missing, "GetId", "followTarget")
              && Has(asm, "Game.Runtime.Cameras.ICameraVehicle", missing, "BodyTransform", "TurnInput");
            InputOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "VehicleInputHandler")
                   && Has(asm, "Game.Runtime.Vehicle.VehicleInputHandler", missing, "Throttle", "Accelerating", "BrakeInput");
            // 0.2.0 animation triggers (each optional: a missing one only switches off that animation)
            GearOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "VehicleGearboxHandler")
                  && Has(asm, "Game.Runtime.Vehicle.VehicleGearboxHandler", missing, "CurrentGearIndex");
            SpeedOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "VehicleMovement")
                   && Has(asm, "Game.Runtime.Vehicle.VehicleMovement", missing, "CurrentSpeed");
            HitsOk = Has(asm, "Game.Runtime.Manager.LevelScoreManager", missing, "scoreProviderList")
                  && Has(asm, "Game.Runtime.Data.CollisionScoreProviderSO", missing, "TotalHits");
            WonOk = Has(asm, "Game.Runtime.GameState", missing, "LevelCompleted");
            if (Ok)
            {
                Plugin.Log.LogInfo($"[Driver] game check OK: camera controller, car body{(InputOk ? ", pedals" : " (no pedal input: feet stay still)")}");
                bool all = GearOk && SpeedOk && HitsOk && WonOk;
                string trig = $"[Driver] animation triggers: gear {(GearOk ? "yes" : "no")}, speed {(SpeedOk ? "yes" : "no")}, collisions {(HitsOk ? "yes" : "no")}, level completed {(WonOk ? "yes" : "no")}";
                if (all) Plugin.Log.LogInfo(trig);
                else Plugin.Log.LogWarning($"{trig} (missing {string.Join(", ", missing)})");
            }
            else Plugin.Log.LogWarning($"[Driver] game check: missing {string.Join(", ", missing)}; Driver stays off");
        }

        private static bool Has(Assembly asm, string typeName, List<string> missing, params string[] members)
        {
            var t = asm?.GetType(typeName);
            if (t == null) { missing.Add(typeName); return false; }
            bool ok = true;
            foreach (var m in members)
                if (t.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0)
                { missing.Add($"{t.Name}.{m}"); ok = false; }
            return ok;
        }

        private static IntPtr _modePtr;
        private static View _modeView = View.None;
        private static string _modeId = "";
        internal static string ModeId => _modeId;

        /// <summary>The current view and the player's car body transform (VehicleProvider.BodyTransform). False in menus.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Read(out View view, out Transform body, out float turn)
        {
            view = View.None; body = null; turn = 0f;
            var ctrl = CameraControllerInGame.Instance;
            if (ctrl == null || ctrl.CurrentCamera == null) return false;
            var mode = ctrl.CurrentCameraMode;
            if (mode == null) view = View.Other;
            else
            {
                if (mode.Pointer != _modePtr)
                {
                    _modePtr = mode.Pointer;
                    _modeId = mode.GetId() ?? "";
                    _modeView = _modeId == "drivercam_driver" ? View.Driver
                              : mode.followTarget == Game.Runtime.Data.CameraModeSO.CameraFollowTarget.Hood ? View.Hood
                              : mode.followTarget == Game.Runtime.Data.CameraModeSO.CameraFollowTarget.Player ? View.Chase
                              : View.Other;
                    if (Plugin.LogEvents.Value) Plugin.Log.LogInfo($"[Driver] camera mode '{_modeId}' -> {_modeView}");
                }
                view = _modeView;
            }
            var vp = ctrl.VehicleProvider;
            if (vp == null) return false;
            body = vp.BodyTransform;
            if (body == null) return false;
            turn = vp.TurnInput;
            return true;
        }

        /// <summary>Throttle and brake, 0-1 (0 when unknown).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Pedals(out float throttle, out float brake)
        {
            throttle = 0f; brake = 0f;
            if (!InputOk) return;
            var veh = VehicleManager.Instance;
            if (veh == null) return;
            var input = veh.VehicleInputHandler;
            if (input == null) return;
            throttle = input.Accelerating ? input.Throttle : 0f;   // Throttle is only meaningful while Accelerating (as in EngineAudio / DriverCam)
            brake = input.BrakeInput;
        }
        // ------------------------------------------------------------------------------------------ animation triggers (read only)
        // game objects held as Unity base types (a field of a game type would stop this class loading if the type went away)
        private static MonoBehaviour _gearbox, _movement, _scoreManager;
        private static ScriptableObject _collision;
        private static IntPtr _carPtr, _providersOwner;
        private static float _nextScoreSearch, _nextProviderScan;

        /// <summary>
        /// Gear index, the game's collision count and level completed (1 / 0), each -1 when unknown; speed (m/s, -1 unknown).
        /// The car's gearbox / movement wrappers are kept per car; the score manager is looked up at most every 2 s while
        /// missing (never a scene search every frame). Called a few times a second, not per frame.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ReadAnim(out int gear, out int hits, out int won, out float speed)
        {
            gear = -1; hits = -1; won = -1; speed = -1f;
            var veh = VehicleManager.Instance;
            if (veh == null) { _gearbox = null; _movement = null; _carPtr = IntPtr.Zero; return; }
            if (veh.Pointer != _carPtr || (GearOk && _gearbox == null) || (SpeedOk && _movement == null))
            {
                _carPtr = veh.Pointer;
                _gearbox = GearOk ? GearboxOf(veh) : null;
                _movement = SpeedOk ? MovementOf(veh) : null;
            }
            if (_gearbox != null) gear = GearOf(_gearbox);
            if (_movement != null) speed = SpeedOf(_movement);
            if (HitsOk) hits = Hits();
            if (WonOk) won = Completed() ? 1 : 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static MonoBehaviour GearboxOf(VehicleManager veh) => veh.VehicleGearboxHandler;
        [MethodImpl(MethodImplOptions.NoInlining)] private static MonoBehaviour MovementOf(VehicleManager veh) => veh.VehicleMovement;
        [MethodImpl(MethodImplOptions.NoInlining)] private static int GearOf(MonoBehaviour g) => ((VehicleGearboxHandler)g).CurrentGearIndex;
        [MethodImpl(MethodImplOptions.NoInlining)] private static float SpeedOf(MonoBehaviour m) => ((VehicleMovement)m).CurrentSpeed;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool Completed() => Game.Runtime.GameState.LevelCompleted;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Hits()
        {
            if (_scoreManager == null)
            {
                if (Time.unscaledTime < _nextScoreSearch) return -1;
                _nextScoreSearch = Time.unscaledTime + 2f;
                _scoreManager = UnityEngine.Object.FindFirstObjectByType<LevelScoreManager>();
                if (_scoreManager == null) return -1;
            }
            var mgr = (LevelScoreManager)_scoreManager;
            if (_providersOwner != mgr.Pointer || _collision == null)
            {
                if (_providersOwner == mgr.Pointer && Time.unscaledTime < _nextProviderScan) return -1;   // list not filled yet: every 2 s
                _nextProviderScan = Time.unscaledTime + 2f;
                _providersOwner = mgr.Pointer; _collision = null;
                var list = mgr.scoreProviderList;
                if (list == null) return -1;
                for (int i = 0; i < list.Count && _collision == null; i++)
                {
                    var p = list[i];
                    if (p != null && p.TryCast<Game.Runtime.Data.CollisionScoreProviderSO>() is var col && col != null) _collision = col;
                }
                if (_collision == null) return -1;
            }
            return ((Game.Runtime.Data.CollisionScoreProviderSO)_collision).TotalHits;
        }

        /// <summary>Scene / car gone: drop every cached game object.</summary>
        internal static void ForgetAnim()
        {
            _gearbox = null; _movement = null; _scoreManager = null; _collision = null;
            _carPtr = IntPtr.Zero; _providersOwner = IntPtr.Zero; _nextScoreSearch = 0f; _nextProviderScan = 0f;
        }
    }
}
