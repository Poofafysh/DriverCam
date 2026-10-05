using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using HarmonyLib;
using UnityEngine;
using FM = RogueShared.FastMath;

namespace Reverse
{
    /// <summary>
    /// The reverse itself, run inside the player car's physics step (Harmony, installed by hand):
    ///   ApplyMovement prefix  - the state machine (arm on a held brake at a standstill, build / bleed / decay speed)
    ///   ApplyMovement postfix - rb.AddForce(-flatForward x speed, VelocityChange) after the game's own step zeroed the
    ///                           flat velocity (game speed 0), so the car ends the step moving backwards at that speed
    ///   HandleCarRotation prefix + finalizer - reverse speed and flipped steering for that one call, then put back
    /// AI racers use the same class: every hook returns at once unless the instance is the player car's (pointer
    /// compare against Runner's cached car, refreshed once a frame).
    /// </summary>
    internal static class Hooks
    {
        // ---- player car (Runner refreshes these once a frame; read only here)
        internal static IntPtr MovePtr;
        internal static VehicleMovement Move;
        internal static VehicleInputHandler Input;
        internal static Rigidbody Rb;
        internal static bool Allowed;      // Enabled, not paused, single-player, race running, car not wrecked

        // ---- state
        internal static bool Active;       // reverse engaged (brake held at a standstill long enough)
        internal static float Speed;       // reverse speed, m/s (0 or more)
        internal static float LastStep;    // Time.unscaledTime of the last player physics step
        internal static bool Broken;       // error breaker tripped: off for this session
        private static float _hold;        // seconds the arm conditions have held
        private static bool _push;         // the postfix pushes this step
        private static float _fx, _fz;     // flat forward (unit) of this step
        private static Vector3 _lastPos;
        private static bool _hasPos;
        private static float _peak, _dist; // for the stop log line
        private static int _errors;

        // ---- HandleCarRotation save / restore
        private static bool _rotSwapped, _turnSwapped;
        private static float _savedTarget, _savedSteer, _savedTurnSpeed;
        private static VehicleMovement _swapMove;   // the instance the values were swapped on (the player car's)

        private const float BrakeOn = 0.3f, ThrottleOn = 0.05f, StillFlat = 0.5f, StillGame = 0.3f, GameZero = 0.05f, WallSlack = 1.5f;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony)
        {
            var t = typeof(VehicleMovement);
            var apply = AccessTools.Method(t, "ApplyMovement");
            var rotate = AccessTools.Method(t, "HandleCarRotation");
            if (apply == null) throw new MissingMethodException(t.Name, "ApplyMovement");
            if (rotate == null) throw new MissingMethodException(t.Name, "HandleCarRotation");
            // harmony-target: VehicleMovement.ApplyMovement
            harmony.Patch(apply, prefix: new HarmonyMethod(typeof(Hooks), nameof(ApplyPrefix)), postfix: new HarmonyMethod(typeof(Hooks), nameof(ApplyPostfix)));
            // harmony-target: VehicleMovement.HandleCarRotation
            string how = "finalizer";
            try { harmony.Patch(rotate, prefix: new HarmonyMethod(typeof(Hooks), nameof(RotatePrefix)), finalizer: new HarmonyMethod(typeof(Hooks), nameof(RotateFinalizer))); }
            catch (Exception e)
            {
                // fall back to a postfix (restores on every normal return; Restore also runs at the next prefix)
                Plugin.Log.LogWarning($"[Reverse] finalizer on HandleCarRotation refused ({e.Message}); using a postfix");
                harmony.Unpatch(rotate, HarmonyPatchType.All, harmony.Id);
                harmony.Patch(rotate, prefix: new HarmonyMethod(typeof(Hooks), nameof(RotatePrefix)), postfix: new HarmonyMethod(typeof(Hooks), nameof(RotatePostfix)));
                how = "postfix";
            }
            if (how == "finalizer") Plugin.Log.LogInfo("[Reverse] hooks installed (VehicleMovement.ApplyMovement, VehicleMovement.HandleCarRotation with finalizer)");
            else Plugin.Log.LogInfo("[Reverse] hooks installed (VehicleMovement.ApplyMovement, VehicleMovement.HandleCarRotation with postfix)");
        }

        /// <summary>Ends a reverse (every exit path calls this). Nothing in the game needs restoring: the push is per step.</summary>
        internal static void Stop(string why)
        {
            if (Active && why != null)
                Plugin.Log.LogInfo($"[Reverse] stopped: {why} (top {_peak * 3.6f:0} km/h, {_dist:0.0} m back)");
            Active = false; Speed = 0f; _hold = 0f; _push = false; _hasPos = false; _peak = 0f; _dist = 0f;
        }

        private static void Fail(Exception e)
        {
            _errors++;
            Plugin.Log.LogWarning($"[Reverse] error ({_errors}/5, then Reverse stays off for this session): {e.Message}");
            if (_errors >= 5) { Broken = true; Stop("error breaker"); }
        }

        private static void ApplyPrefix(VehicleMovement __instance)
        {
            _push = false;
            if (Broken || MovePtr == IntPtr.Zero || __instance.Pointer != MovePtr) return;   // AI racers: untouched
            try { Step(); }
            catch (Exception e) { Stop(null); Fail(e); }
        }

        private static void Step()
        {
            LastStep = Time.unscaledTime;
            var input = Input; var move = Move; var rb = Rb;
            if (!Allowed || input == null || rb == null || Time.timeScale <= 0f) { Stop("not allowed now"); return; }
            if (!input.CanControl || !input.CanTakeInput) { Stop("no control"); return; }
            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) return;

            bool grounded = move.IsGrounded;
            float throttle = input.Throttle, brake = input.BrakeInput;
            if (throttle > ThrottleOn) { Stop("throttle"); return; }
            if (input.Handbrake) { Stop("handbrake"); return; }
            if (!grounded) { Stop("airborne"); return; }
            if (move.Drifting) { Stop("drifting"); return; }

            // flat forward from the rigidbody's rotation (the column of the rotation matrix; no Unity math calls)
            Quaternion q = rb.rotation;
            float fx = 2f * (q.x * q.z + q.w * q.y), fz = 1f - 2f * (q.x * q.x + q.y * q.y);
            float len = MathF.Sqrt(fx * fx + fz * fz);
            if (len < 0.2f) { Stop("car on its side"); return; }   // nose straight up or down: no flat forward
            fx /= len; fz /= len;
            Vector3 v = rb.linearVelocity;
            float back = -(v.x * fx + v.z * fz);                     // real backward speed after the last step
            float gameSpeed = move.CurrentSpeed;
            bool brakeHeld = brake > BrakeOn;

            if (!Active)
            {
                float flat = MathF.Sqrt(v.x * v.x + v.z * v.z);
                if (brakeHeld && flat < StillFlat && MathF.Abs(gameSpeed) < StillGame) _hold += dt;
                else _hold = 0f;
                if (_hold < Plugin.HoldDelay.Value) return;
                Active = true; Speed = 0f; _peak = 0f; _dist = 0f; _hasPos = false;
                Plugin.Log.LogInfo("[Reverse] reversing (brake held at a standstill)");
            }

            // respawn / teleport: the car jumped further than it could move in one step
            Vector3 p = rb.position;
            if (_hasPos)
            {
                float dx = p.x - _lastPos.x, dy = p.y - _lastPos.y, dz = p.z - _lastPos.z;
                if (dx * dx + dy * dy + dz * dz > 64f) { Stop("car moved (respawn)"); return; }
            }
            _lastPos = p; _hasPos = true;

            float max = FM.Clamp(Plugin.MaxSpeedKmh.Value, 5f, 60f) / 3.6f;
            if (brakeHeld) Speed = FM.Min(Speed + FM.Clamp(Plugin.Accel.Value, 1f, 15f) * dt, max);
            else
            {
                Speed -= FM.Clamp(Plugin.Decel.Value, 2f, 40f) * dt;
                if (Speed <= 0f) { Stop("brake released"); return; }
            }
            if (back < Speed - WallSlack) Speed = FM.Max(0f, back + FM.Clamp(Plugin.Accel.Value, 1f, 15f) * dt);   // held back (wall, car): don't build up speed

            _push = Speed > 0f && MathF.Abs(gameSpeed) < GameZero;   // a speed boost or the game moving the car: the game wins
            _fx = fx; _fz = fz;
            if (Speed > _peak) _peak = Speed;
        }

        private static void ApplyPostfix(VehicleMovement __instance)
        {
            if (!_push) return;
            _push = false;
            if (__instance.Pointer != MovePtr) return;
            try
            {
                var rb = Rb;
                if (rb == null) return;
                // the game's VelocityChange this step already cancels the flat velocity (its speed is 0): this one sets it to -forward x Speed
                rb.AddForce(FM.V3(-_fx * Speed, 0f, -_fz * Speed), ForceMode.VelocityChange);
                _dist += Speed * Time.fixedDeltaTime;
            }
            catch (Exception e) { Stop(null); Fail(e); }
        }

        private static void RotatePrefix(VehicleMovement __instance)
        {
            if (_rotSwapped || _turnSwapped) { try { Restore(); } catch (Exception e) { Fail(e); } }   // a call that never returned (postfix fallback)
            if (!Active || Speed < 0.1f || Broken || __instance.Pointer != MovePtr) return;
            try
            {
                var input = Input;
                if (input == null) return;
                // the game scales turning to 0 below its minimum turning speed (it reads TargetSpeed) and turns
                // forward-wise: give it the reverse speed and the mirrored steering input, for this call only
                _swapMove = __instance;
                _savedTarget = __instance.TargetSpeed;
                _savedSteer = input.SmoothTurnInput;
                _rotSwapped = true;   // before the writes: a throw half-way still puts both back (Restore)
                __instance.SetTargetSpeed(Speed);
                input.SmoothTurnInput = -_savedSteer;
                if (Plugin.NoBrakeTurnPenalty.Value && input.Braking)
                {
                    // HandleMovementConditions multiplied the turn speed by the car's brake-turning factor (brake held)
                    float m = __instance.vehicleParams == null ? 1f : __instance.vehicleParams.BrakeTurningMultiplier;
                    if (m > 0.05f && m < 1f)
                    {
                        _savedTurnSpeed = __instance.CurrentTurnSpeed;
                        _turnSwapped = true;
                        __instance.CurrentTurnSpeed = _savedTurnSpeed / m;
                    }
                }
            }
            catch (Exception e) { try { Restore(); } catch { } Fail(e); }
        }

        private static Exception RotateFinalizer(Exception __exception)
        {
            try { Restore(); } catch (Exception e) { Fail(e); }
            return __exception;
        }

        private static void RotatePostfix()
        {
            try { Restore(); } catch (Exception e) { Fail(e); }
        }

        /// <summary>Puts back what RotatePrefix swapped (TargetSpeed, SmoothTurnInput, CurrentTurnSpeed) on the same car.</summary>
        private static void Restore()
        {
            var move = _swapMove;
            _swapMove = null;
            if (move == null) { _rotSwapped = false; _turnSwapped = false; return; }
            if (_rotSwapped)
            {
                _rotSwapped = false;
                move.SetTargetSpeed(_savedTarget);
                var input = Input;
                if (input != null) input.SmoothTurnInput = _savedSteer;
            }
            if (_turnSwapped)
            {
                _turnSwapped = false;
                move.CurrentTurnSpeed = _savedTurnSpeed;
            }
        }
    }
}
