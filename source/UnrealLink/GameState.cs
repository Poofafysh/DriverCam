using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Cameras;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace UnrealLink
{
    /// <summary>
    /// The only file that touches game types (read-only, checked by name at startup like Driver's GameApi.cs):
    /// CameraControllerInGame.Instance / CurrentCamera / CurrentCameraMode (CameraModeSO.GetId, followTarget) /
    /// VehicleProvider (ICameraVehicle.BodyTransform, TurnInput), VehicleManager.Instance.VehicleInputHandler (Throttle,
    /// Accelerating, BrakeInput) and VehicleMovement.CurrentSpeed. Plus Unity's RenderSettings (sun, ambient).
    /// Nothing is written to the game.
    /// </summary>
    internal static class GameState
    {
        internal enum View { None, Driver, Hood, Chase, Other }

        internal static bool Ok { get; private set; }
        internal static bool InputOk { get; private set; }
        internal static bool SpeedOk { get; private set; }

        internal static void Check()
        {
            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            Ok = Has(asm, "Game.Runtime.Cameras.CameraControllerInGame", missing, "Instance", "CurrentCamera", "CurrentCameraMode", "VehicleProvider")
              && Has(asm, "Game.Runtime.Data.CameraModeSO", missing, "GetId", "followTarget")
              && Has(asm, "Game.Runtime.Cameras.ICameraVehicle", missing, "BodyTransform", "TurnInput");
            InputOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "VehicleInputHandler")
                   && Has(asm, "Game.Runtime.Vehicle.VehicleInputHandler", missing, "Throttle", "Accelerating", "BrakeInput");
            SpeedOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "VehicleMovement")
                   && Has(asm, "Game.Runtime.Vehicle.VehicleMovement", missing, "CurrentSpeed");
            if (Ok) Plugin.Log.LogInfo($"[UnrealLink] game check OK: camera controller, car body{(InputOk ? ", pedals" : " (no pedals)")}{(SpeedOk ? ", speed" : " (no speed)")}");
            else Plugin.Log.LogWarning($"[UnrealLink] game check: missing {string.Join(", ", missing)}; UnrealLink stays off");
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

        /// <summary>The game camera, the view kind, the car body and the steer input. False in menus.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Read(out Camera cam, out View view, out Transform body, out float turn)
        {
            cam = null; view = View.None; body = null; turn = 0f;
            var ctrl = CameraControllerInGame.Instance;
            if (ctrl == null) return false;
            cam = ctrl.CurrentCamera;
            if (cam == null) return false;
            var mode = ctrl.CurrentCameraMode;
            if (mode == null) view = View.Other;
            else
            {
                if (mode.Pointer != _modePtr)
                {
                    _modePtr = mode.Pointer;
                    string id = mode.GetId() ?? "";
                    _modeView = id == "drivercam_driver" ? View.Driver
                              : mode.followTarget == Game.Runtime.Data.CameraModeSO.CameraFollowTarget.Hood ? View.Hood
                              : mode.followTarget == Game.Runtime.Data.CameraModeSO.CameraFollowTarget.Player ? View.Chase
                              : View.Other;
                    Plugin.Log.LogInfo($"[UnrealLink] camera mode '{id}' -> {_modeView}");
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

        private static MonoBehaviour _movement;
        private static IntPtr _carPtr;

        /// <summary>Throttle (0-1), brake (0-1), speed (m/s, -1 unknown).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Pedals(out float throttle, out float brake, out float speed)
        {
            throttle = 0f; brake = 0f; speed = -1f;
            if (!InputOk) return;
            var veh = VehicleManager.Instance;
            if (veh == null) { _movement = null; _carPtr = IntPtr.Zero; return; }
            var input = veh.VehicleInputHandler;
            if (input != null)
            {
                throttle = input.Accelerating ? input.Throttle : 0f;
                brake = input.BrakeInput;
            }
            if (!SpeedOk) return;
            if (veh.Pointer != _carPtr || _movement == null) { _carPtr = veh.Pointer; _movement = MovementOf(veh); }
            if (_movement != null) speed = SpeedOf(_movement);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static MonoBehaviour MovementOf(VehicleManager veh) => veh.VehicleMovement;
        [MethodImpl(MethodImplOptions.NoInlining)] private static float SpeedOf(MonoBehaviour m) => ((VehicleMovement)m).CurrentSpeed;

        /// <summary>The scene's sun (RenderSettings.sun: direction it shines, colour * intensity, linear) and ambient.</summary>
        internal static bool Light(out Vector3 dir, out Color sun, out Color ambient)
        {
            dir = default; sun = default; ambient = default;
            var l = RenderSettings.sun;
            ambient = RenderSettings.ambientLight;
            if (l == null || !l.isActiveAndEnabled) return false;
            dir = l.transform.forward;
            var c = l.color;
            float i = l.intensity;
            sun = new Color(c.r * i, c.g * i, c.b * i, 1f);
            return true;
        }

        /// <summary>A Bikes motorcycle (a "Bikes.&lt;Key&gt;_Body" node under the car root) and its key, else null. Once per car.</summary>
        internal static string BikeKey(Transform body)
        {
            var root = body.root;
            if (root == null) return null;
            var all = root.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Transform>(), true);   // the Type overload (no generic instantiation needed)
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.name;
                if (n != null && n.StartsWith("Bikes.", StringComparison.Ordinal) && n.EndsWith("_Body", StringComparison.Ordinal) && n.Length > 11)
                    return n.Substring(6, n.Length - 11);
            }
            return null;
        }

        internal static void Forget() { _movement = null; _carPtr = IntPtr.Zero; _modePtr = IntPtr.Zero; }
    }
}
