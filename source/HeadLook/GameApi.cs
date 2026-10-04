using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Cameras;
using UnityEngine;

namespace HeadLook
{
    /// <summary>
    /// The only file that touches game types (dump.cs): Game.Runtime.Cameras.CameraControllerInGame.Instance /
    /// CurrentCamera / CurrentCameraMode (CameraModeSO.GetId(): "CameraMode_Speed", "CameraMode_Drift1" = chase,
    /// "CameraMode_Hood" = hood, "drivercam_driver" = DriverCam's driver view) / VehicleProvider.BodyTransform.
    /// </summary>
    internal static class GameApi
    {
        internal static bool CameraOk { get; private set; }

        internal static void Check()
        {
            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            var t = asm?.GetType("Game.Runtime.Cameras.CameraControllerInGame");
            if (t == null) missing.Add("CameraControllerInGame");
            else foreach (var m in new[] { "Instance", "CurrentCamera", "CurrentCameraMode", "VehicleProvider", "Update", "LateUpdate", "FixedUpdate" })
                    if (t.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0) missing.Add($"CameraControllerInGame.{m}");
            var so = asm?.GetType("Game.Runtime.Data.CameraModeSO");
            if (so == null) missing.Add("CameraModeSO");
            else foreach (var m in new[] { "GetId", "followTarget" })
                    if (so.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length == 0) missing.Add($"CameraModeSO.{m}");
            CameraOk = missing.Count == 0;
            if (CameraOk) Plugin.Log.LogInfo("[HeadLook] game check OK: camera controller");
            else Plugin.Log.LogWarning($"[HeadLook] game check: missing {string.Join(", ", missing)}");
        }

        internal static void DisableCamera() => CameraOk = false;

        internal enum View { None, Hood, Chase, Driver, Other }

        private static IntPtr _modePtr;
        private static View _modeView = View.None;

        /// <summary>The game's camera, its current view kind and the car body (pivot for chase orbits). False in menus.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Camera(out Transform cam, out View view, out Transform body)
        {
            cam = null; body = null; view = View.None;
            var ctrl = CameraControllerInGame.Instance;
            if (ctrl == null) return false;
            var c = ctrl.CurrentCamera;
            if (c == null) return false;
            cam = c.transform;
            var mode = ctrl.CurrentCameraMode;
            if (mode == null) { view = View.Other; }
            else
            {
                if (mode.Pointer != _modePtr)
                {
                    _modePtr = mode.Pointer;
                    string id = mode.GetId() ?? "";
                    // DriverCam's driver view by its id; otherwise by what the mode follows (as DriverCam does): the hood
                    // pivot = a view from the car (turn in place), the player = a chase view (orbit)
                    _modeView = id == "drivercam_driver" ? View.Driver
                              : mode.followTarget == Game.Runtime.Data.CameraModeSO.CameraFollowTarget.Hood ? View.Hood
                              : mode.followTarget == Game.Runtime.Data.CameraModeSO.CameraFollowTarget.Player ? View.Chase
                              : View.Other;
                    Plugin.Log.LogInfo($"[HeadLook] camera mode '{id}' -> {_modeView}");
                }
                view = _modeView;
            }
            var vp = ctrl.VehicleProvider;
            if (vp != null) body = vp.BodyTransform;
            return true;
        }
    }
}
