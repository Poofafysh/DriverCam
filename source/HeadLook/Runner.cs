using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using FM = RogueShared.FastMath;

namespace HeadLook
{
    /// <summary>
    /// Harmony prefixes on CameraControllerInGame.Update / LateUpdate / FixedUpdate: put the camera back where the game
    /// left it before the game's camera code runs, so the game never sees (or smooths) our head offset.
    /// </summary>
    internal static class CameraHooks
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony)
        {
            var t = typeof(Game.Runtime.Cameras.CameraControllerInGame);
            var prefix = new HarmonyMethod(typeof(CameraHooks), nameof(Before));
            // harmony-target: CameraControllerInGame.Update, CameraControllerInGame.LateUpdate, CameraControllerInGame.FixedUpdate (cooperates with DriverCam)
            foreach (var name in new[] { "Update", "LateUpdate", "FixedUpdate" })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) throw new MissingMethodException(t.Name, name);
                harmony.Patch(m, prefix: prefix);
            }
            Plugin.Log.LogInfo("[HeadLook] camera hooks installed (CameraControllerInGame.Update / LateUpdate / FixedUpdate)");
        }

        private static void Before()
        {
            try { Runner.Restore(); } catch { /* never break the game's camera */ }
        }
    }

    /// <summary>
    /// Reads the right stick / right mouse, eases the head angle, publishes it for DriverCam and turns the game's camera
    /// right before each frame is drawn (Application.onBeforeRender).
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        /// <summary>Shared with DriverCam through AppDomain data "rogue.headlook": [yaw deg (+ right), pitch deg (+ up), 1 = HeadLook running].</summary>
        internal static readonly float[] Shared = new float[3];
        internal const string SharedKey = "rogue.headlook";

        private static float _yaw, _pitch;            // current head angle, degrees
        private float _mouseYaw, _mousePitch;
        private bool _subscribed, _broken;
        private int _errors;
        private Action _beforeRender;
        private UnityEngine.Events.UnityAction _beforeRenderAction;   // the converted delegate: the same object must be removed

        // the pose we changed, to put back
        private static Transform _camT;
        private static Vector3 _origPos;
        private static Quaternion _origRot;
        private static bool _applied;

        private void Awake()
        {
            AppDomain.CurrentDomain.SetData(SharedKey, Shared);
        }

        private void Update()
        {
            if (_broken) return;
            try
            {
                if (!_subscribed && GameApi.CameraOk)
                {
                    _subscribed = true;
                    _beforeRender = BeforeRender;
                    _beforeRenderAction = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(_beforeRender);
                    Application.add_onBeforeRender(_beforeRenderAction);
                }
                ReadInput(out float targetYaw, out float targetPitch);
                if (Time.timeScale <= 0f) { targetYaw = 0f; targetPitch = 0f; _mouseYaw = _mousePitch = 0f; }   // paused / menus: head back to centre, input ignored
                float dt = FM.Min(0.1f, Time.unscaledDeltaTime);
                float k = 1f - MathF.Exp(-FM.Clamp(Plugin.Speed.Value, 2f, 40f) * dt);
                _yaw += (targetYaw - _yaw) * k;
                _pitch += (targetPitch - _pitch) * k;
                if (Math.Abs(_yaw) < 0.01f && targetYaw == 0f) _yaw = 0f;
                if (Math.Abs(_pitch) < 0.01f && targetPitch == 0f) _pitch = 0f;
                Shared[0] = _yaw; Shared[1] = _pitch; Shared[2] = Plugin.Enabled.Value ? 1f : 0f;
            }
            catch (Exception e) { Fault(e); }
        }

        private void ReadInput(out float yaw, out float pitch)
        {
            yaw = 0f; pitch = 0f;
            if (!Plugin.Enabled.Value) { _mouseYaw = _mousePitch = 0f; return; }
            float maxYaw = FM.Clamp(Plugin.MaxYaw.Value, 10f, 170f);
            float maxUp = FM.Clamp(Plugin.MaxUp.Value, 0f, 80f), maxDown = FM.Clamp(Plugin.MaxDown.Value, 0f, 80f);
            float inv = Plugin.InvertY.Value ? -1f : 1f;

            // controller: absolute, full stick = full turn
            var pad = Gamepad.current;
            if (Plugin.Stick.Value && pad != null)
            {
                Vector2 s = pad.rightStick.ReadValue();
                float dz = FM.Clamp(Plugin.Deadzone.Value, 0f, 0.5f), mag = s.magnitude;
                if (mag > dz)
                {
                    s = s / mag * FM.Clamp01((mag - dz) / (1f - dz));   // radial deadzone, rescaled
                    yaw = s.x * maxYaw;
                    float y = s.y * inv;
                    pitch = y >= 0f ? y * maxUp : y * maxDown;
                    _mouseYaw = _mousePitch = 0f;
                    return;
                }
            }
            // mouse: hold the right button and move; released = back to centre
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (Plugin.Mouse.Value && mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                float sens = FM.Clamp(Plugin.MouseSensitivity.Value, 0.02f, 1f);
                _mouseYaw = FM.Clamp(_mouseYaw + d.x * sens, -maxYaw, maxYaw);
                _mousePitch = FM.Clamp(_mousePitch + d.y * sens * inv, -maxDown, maxUp);
                yaw = _mouseYaw; pitch = _mousePitch;
            }
            else { _mouseYaw = 0f; _mousePitch = 0f; }
        }

        /// <summary>Right before the frame is drawn: turn the game's camera by the head angle (not in DriverCam's view: it does it itself).</summary>
        private void BeforeRender()
        {
            if (_broken) return;
            try
            {
                Restore();   // a camera the game didn't update since our last frame: start from its real pose, never stack offsets
                if (!Plugin.Enabled.Value || (_yaw == 0f && _pitch == 0f) || Time.timeScale <= 0f) return;
                if (!GameApi.Camera(out var cam, out var view, out var body)) return;
                if (view == GameApi.View.Driver || view == GameApi.View.Other) return;

                Vector3 pos = cam.position; Quaternion rot = cam.rotation;
                _camT = cam; _origPos = pos; _origRot = rot; _applied = true;
                Vector3 up = body != null ? body.up : Vector3.up;
                if (view == GameApi.View.Chase && Plugin.ChaseOrbit.Value && body != null)
                {
                    // orbit around the car for left / right (yaw about the car's up axis, through the car), then look up / down
                    // in place: pitching the orbit itself would swing a low chase camera under the road
                    Vector3 pivot = body.position + up * 1.1f;
                    Quaternion qy = Quaternion.AngleAxis(_yaw, up);
                    pos = pivot + qy * (pos - pivot);
                    rot = qy * rot;
                    rot = Quaternion.AngleAxis(-_pitch, rot * Vector3.right) * rot;
                }
                else
                {
                    // turn in place: yaw about the car's up axis, then pitch about the turned camera's right axis
                    rot = Quaternion.AngleAxis(_yaw, up) * rot;
                    rot = Quaternion.AngleAxis(-_pitch, rot * Vector3.right) * rot;
                }
                cam.SetPositionAndRotation(pos, rot);
            }
            catch (Exception e) { Fault(e); }
        }

        /// <summary>Puts the camera back to the pose the game gave it (before the game's camera code runs again).</summary>
        internal static void Restore()
        {
            if (!_applied) return;
            _applied = false;
            if (_camT != null) _camT.SetPositionAndRotation(_origPos, _origRot);
        }

        private void OnDestroy()
        {
            try { Restore(); } catch { /* scene gone */ }
            try
            {
                if (_subscribed && _beforeRenderAction != null) Application.remove_onBeforeRender(_beforeRenderAction);
            }
            catch { /* shutting down */ }
            Shared[0] = Shared[1] = Shared[2] = 0f;
        }

        private void Fault(Exception e)
        {
            _errors++;
            Plugin.Log.LogWarning($"[HeadLook] error ({_errors}/3): {e.Message}");
            if (_errors < 3) return;
            _broken = true;
            try { Restore(); } catch { /* gone */ }
            _yaw = _pitch = 0f;
            Shared[0] = Shared[1] = Shared[2] = 0f;
            Plugin.Log.LogError($"[HeadLook] switched off for this session; the camera is the game's again. Last error: {e}");
        }
    }
}
