using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Cameras;
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
            if (Ok) Plugin.Log.LogInfo($"[Driver] game check OK: camera controller, car body{(InputOk ? ", pedals" : " (no pedal input: feet stay still)")}");
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
    }
}
