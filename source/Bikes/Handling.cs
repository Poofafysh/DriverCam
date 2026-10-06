using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Game.Runtime.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace Bikes
{
    /// <summary>
    /// Real handling for the player's bike ([Handling] Bikes; the M2 keeps the game's handling since 0.3.2). The game's own
    /// model (verified in GameAssembly.dll) is arcade: HandleCarRotation adds steer x CurrentTurnSpeed x dt to the
    /// heading and MoveRotations the body onto the ground; ApplyMovement sets the velocity to a direction blended from the
    /// velocity towards the heading (TractionStat) times TargetSpeed x the speed modifiers, plus extraVelocity and the
    /// downforce, keeping the vertical velocity. Here, for the player's Bikes vehicle only, each physics step:
    /// - HandleCarRotation prefix: one step of this model, then the body's heading is set to the model's (pitch / roll
    ///   and the game's ground alignment kept: only the yaw is replaced) and CurrentTurnSpeed is 0 for that call (put
    ///   back by the finalizer), so the game adds no turn of its own;
    /// - ApplyMovement prefix (grounded only): the velocity is the model's course x speed, plus the game's extraVelocity
    ///   (then cleared, as the game does) and downforce, vertical velocity kept; the game's own step is skipped. In the
    ///   air the game's step runs.
    /// The model:
    /// - speed: the game's TargetSpeed x speed modifiers is the throttle / brake demand; the model follows it within its
    ///   acceleration (traction, then power over speed, minus drag; boosts add power) and braking (tyre grip); the
    ///   result is written back to TargetSpeed, so the HUD, sounds and the game's logic see the real speed;
    /// - grip: one friction circle per vehicle: braking or accelerating uses grip that cornering then lacks;
    /// - bike (after RIDE 4 / 5): the steer input asks for a lean angle; the lean builds at a lean rate that falls with
    ///   speed; the turn follows from the lean (yaw rate = g tan(lean) / speed, a coordinated turn), so a bike turns
    ///   because it leans; the largest lean is what the remaining grip holds (brake hard and it can't lean as far) and
    ///   [Look] MaxLean; at walking pace it steers by the bars instead;
    /// It stands aside (the game's own handling) while the Reverse plugin reverses, off a Bikes vehicle, switched off, or
    /// for the session after 3 errors. State follows reality: a collision that slows or turns the car, or the game's own
    /// path-angle limit, resets the model to what the body really does.
    /// </summary>
    internal static class Handling
    {
        internal static ConfigEntry<bool> BikeOn;
        internal static bool Ok;

        // published for the Runner (and the FX): the model's lean and fork angle, slides
        internal static bool Active;
        internal static float LeanDeg, SteerDeg, SlipDeg, Speed;
        internal static bool RearSlide, FrontSkid, Braking;

        private const float G = 9.81f;
        // the player's VehicleMovement while the handling applies (set once a frame by Track, from the Runner): the
        // hooks run for every car each physics step and compare this pointer before touching anything else
        private static IntPtr s_ptr, s_vehPtr, s_vm;
        private static Rigidbody s_rb;
        private static bool s_init, s_swapped, s_off, s_loggedBike;
        private static float s_checkedAt;
        private static float s_savedTurn;
        private static float s_psi, s_chi, s_v, s_phi, s_r;
        private static int s_errors;
        private static float[] s_reverse;
        private static float s_nextReverse;

        internal static void Bind(ConfigFile cfg)
        {
            BikeOn = cfg.Bind("Handling", "Bikes", true,
                "Bikes turn by leaning (RIDE 4 / 5 style): steer = lean, the lean makes the turn, braking takes grip from cornering. Off = the game's car handling.");
        }

        /// <summary>The handling hooks, separate from the Bikes guards: a failure only leaves the game's own handling.</summary>
        internal static int Install(Harmony h)
        {
            var t = typeof(VehicleMovement);
            var missing = new List<string>();
            Has(t, missing, "HandleCarRotation", "ApplyMovement", "CurrentTurnSpeed", "TargetSpeed", "turnInput", "IsGrounded",
                "extraVelocity", "GetDownforceVelocity", "totalSpeedModifier");
            Has(typeof(SpeedModifier), missing, "currentSpeedMultiplier", "currentSpeedIncrement");
            Has(typeof(VehicleManager), missing, "Instance", "VehicleMovement", "Rigidbody", "VehicleSO");
            if (missing.Count > 0) throw new MissingMemberException("game members missing: " + string.Join(", ", missing));
            var rotate = AccessTools.Method(t, "HandleCarRotation");
            var apply = AccessTools.Method(t, "ApplyMovement");
            if (rotate == null || apply == null) throw new MissingMethodException("VehicleMovement", rotate == null ? "HandleCarRotation" : "ApplyMovement");
            try
            {
                // harmony-target: VehicleMovement.HandleCarRotation (cooperates with Reverse)   (stands aside while it reverses)
                h.Patch(rotate, prefix: new HarmonyMethod(typeof(Handling), nameof(Rotate)), finalizer: new HarmonyMethod(typeof(Handling), nameof(RotateDone)));
                // harmony-target: VehicleMovement.ApplyMovement (cooperates with Reverse)   (stands aside while it reverses)
                h.Patch(apply, prefix: new HarmonyMethod(typeof(Handling), nameof(Move)));
            }
            catch
            {
                try { h.Unpatch(rotate, HarmonyPatchType.All, h.Id); h.Unpatch(apply, HarmonyPatchType.All, h.Id); } catch { /* best effort */ }
                throw;
            }
            Ok = true;
            return 2;
        }

        private static void Has(Type t, List<string> missing, params string[] members)
        {
            foreach (var m in members)
                if (t.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0)
                    missing.Add(t.Name + "." + m);
        }

        // ---------------------------------------------------------------- which vehicle

        /// <summary>
        /// Once a frame (Runner.LateUpdate): the player's vehicle, whether the handling applies to it (a Bikes vehicle,
        /// its Handling switch on, Bikes not broken), its VehicleMovement pointer and Rigidbody for the hooks.
        /// </summary>
        internal static void Track()
        {
            if (!Ok || s_off) { s_ptr = IntPtr.Zero; return; }
            var veh = VehicleManager.Instance;
            float now = Time.unscaledTime;
            bool mp = false;
            try { mp = Game.Runtime.GameState.IsMultiplayerMode; } catch { mp = true; }
            if (veh == null || Runner.Broken || mp)
            {
                if (s_ptr != IntPtr.Zero) Leave(veh == null ? "no player car" : mp ? "multiplayer" : "Bikes switched off after errors");
                s_ptr = IntPtr.Zero; s_vehPtr = IntPtr.Zero; s_rb = null;
                return;
            }
            if (veh.Pointer == s_vehPtr && now < s_checkedAt) return;
            s_vehPtr = veh.Pointer; s_checkedAt = now + 0.5f;
            var b = Garage.Of(veh.VehicleSO);
            bool on = b != null && !b.Car && BikeOn.Value;   // bikes only (0.3.2: the M2 is back on the game's handling)
            var move = on ? veh.VehicleMovement : null;
            var rb = on ? veh.Rigidbody : null;
            IntPtr p = move == null || rb == null ? IntPtr.Zero : move.Pointer;
            if (p == IntPtr.Zero)
            {
                if (s_ptr != IntPtr.Zero) Leave("off a Bikes vehicle or switched off");
                s_ptr = IntPtr.Zero; s_rb = null;
                return;
            }
            if (p != s_vm) { s_vm = p; s_init = false; }
            s_rb = rb;
            s_ptr = p;
        }

        internal static void Shutdown() { if (s_ptr != IntPtr.Zero) Leave("plugin unloaded"); s_ptr = IntPtr.Zero; s_rb = null; }

        /// <summary>The Reverse plugin is reversing ("rogue.reverse": [0] reversing, [2] its last write, live under 0.25 s).</summary>
        private static bool Reversing()
        {
            if (s_reverse == null)
            {
                if (Time.unscaledTime < s_nextReverse) return false;
                s_nextReverse = Time.unscaledTime + 2f;   // Reverse may load after us, or not be installed
                s_reverse = AppDomain.CurrentDomain.GetData("rogue.reverse") as float[];
                if (s_reverse == null || s_reverse.Length < 3) { s_reverse = null; return false; }
            }
            bool rev = s_reverse[0] > 0.5f && Time.unscaledTime - s_reverse[2] < 0.25f;
            if (rev) s_init = false;   // pick up from what the body does once it drives forwards again
            return rev;
        }

        internal static void Leave(string why)
        {
            if (Active) Plugin.Log.LogInfo($"[Bikes] handling: the game's own handling again ({why})");
            Active = false; s_init = false; LeanDeg = 0f; SteerDeg = 0f; SlipDeg = 0f; RearSlide = false; FrontSkid = false; Braking = false;
        }

        internal static bool Off => s_off;

        internal static void Fault(Exception e)
        {
            if (s_off) return;   // already switched off: nothing more to log
            Leave("error");
            if (++s_errors >= 3)
            {
                s_off = true; s_ptr = IntPtr.Zero; s_rb = null;   // the hooks compare s_ptr first: the game's handling from now on
                Plugin.Log.LogError($"[Bikes] handling switched off for this session after repeated errors (the game's handling): {e}");
            }
            else Plugin.Log.LogWarning($"[Bikes] handling error ({s_errors}/3): {e.Message}");
        }

        // ---------------------------------------------------------------- hooks

        private static void Rotate(VehicleMovement __instance)
        {
            s_swapped = false;
            if (s_off || s_ptr == IntPtr.Zero || __instance.Pointer != s_ptr) return;   // every other car: one pointer compare
            try { RotateBody(__instance); }
            catch (Exception e) { Fault(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RotateBody(VehicleMovement vm)
        {
            if (Reversing() || s_rb == null) return;
            bool grounded = vm.IsGrounded;
            Step(vm, s_rb, Time.fixedDeltaTime, grounded);
            if (!grounded) return;   // in the air the game's own turn applies (the model follows the body)
            s_savedTurn = vm.CurrentTurnSpeed;
            vm.CurrentTurnSpeed = 0f;   // the game adds no turn of its own this call (finalizer puts it back)
            s_swapped = true;
        }

        private static Exception RotateDone(VehicleMovement __instance, Exception __exception)
        {
            if (s_swapped)
            {
                s_swapped = false;
                try { RestoreTurn(__instance); } catch { /* vehicle gone */ }
            }
            return __exception;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RestoreTurn(VehicleMovement vm) => vm.CurrentTurnSpeed = s_savedTurn;

        private static bool Move(VehicleMovement __instance)
        {
            if (s_off || !Active || s_ptr == IntPtr.Zero || __instance.Pointer != s_ptr) return true;
            try { return MoveBody(__instance); }
            catch (Exception e) { Fault(e); return true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool MoveBody(VehicleMovement vm)
        {
            var rb = s_rb;
            if (rb == null || !vm.IsGrounded || Reversing()) return true;
            Vector3 vel = rb.linearVelocity;
            float tx = (float)Math.Sin(s_chi) * s_v, tz = (float)Math.Cos(s_chi) * s_v, ty = vel.y;
            Vector3 extra = vm.extraVelocity;
            tx += extra.x; ty += extra.y; tz += extra.z;
            vm.extraVelocity = V3(0f, 0f, 0f);   // the game clears it after adding it
            Vector3 down = vm.GetDownforceVelocity();
            tx += down.x; ty += down.y; tz += down.z;
            rb.AddForce(V3(tx - vel.x, ty - vel.y, tz - vel.z), ForceMode.VelocityChange);
            return false;
        }

        // ---------------------------------------------------------------- the model

        private static void Step(VehicleMovement vm, Rigidbody rb, float dt, bool grounded)
        {
            if (dt <= 0f) return;
            Quaternion q = rb.rotation;
            float psiAct = Yaw(q);
            Vector3 vel = rb.linearVelocity;
            float vMeas = (float)Math.Sqrt(vel.x * vel.x + vel.z * vel.z);
            float chiMeas = vMeas > 0.5f ? (float)Math.Atan2(vel.x, vel.z) : psiAct;
            if (!s_init)
            {
                s_init = true; s_psi = psiAct; s_chi = chiMeas; s_v = vMeas; s_phi = 0f; s_r = 0f;
                if (!s_loggedBike) { s_loggedBike = true; Plugin.Log.LogInfo("[Bikes] handling: bike (steer = lean, turn from the lean) on the player's vehicle"); }
            }
            // follow reality: the game's path-angle limit or a collision turned the body; an impact slowed or pushed it
            if (Math.Abs(Wrap(psiAct - s_psi)) > 0.03f) s_psi = psiAct;
            if (vMeas < s_v - 1.5f) s_v = vMeas;
            if (vMeas > 2f && Math.Abs(Wrap(chiMeas - s_chi)) > 0.35f) s_chi = chiMeas;

            Active = true;
            Speed = s_v;
            if (!grounded)
            {
                s_chi = chiMeas; s_v = vMeas; s_r = 0f;
                s_phi *= 1f - Math.Min(1f, 1.5f * dt);
                LeanDeg = s_phi * Mathf.Rad2Deg; RearSlide = FrontSkid = Braking = false;
                return;
            }

            // demand from the game: TargetSpeed x its speed modifiers (boosts, slowdowns) = throttle / brake
            var mod = vm.totalSpeedModifier;
            float mult = mod == null ? 1f : mod.currentSpeedMultiplier, inc = mod == null ? 0f : mod.currentSpeedIncrement;
            if (!(mult > 0.01f)) mult = 1f;
            float demand = vm.TargetSpeed * mult + inc;
            float steer = vm.turnInput;
            if (steer > 1f) steer = 1f; else if (steer < -1f) steer = -1f;

            // longitudinal: traction, then power over speed, minus drag; braking by grip
            bool boost = mult > 1.05f;
            const float mu = 1.25f, muBrake = 1.1f, launch = 9.5f, drag = 0.00022f;
            float power = 420f * (boost ? 1.8f : 1f);
            float aUp = Math.Min(launch, power / Math.Max(s_v, 4f)) - drag * s_v * s_v;
            float aDown = muBrake * G;
            float want = demand - s_v;
            // braking takes grip from leaning (trail braking), so does accelerating
            float aLong = want >= 0f ? Math.Min(want / dt, Math.Max(0f, aUp)) : Math.Max(want / dt, -aDown);
            float latAvail = (float)Math.Sqrt(Math.Max(0f, mu * mu * G * G - aLong * aLong));
            s_v = Math.Max(0f, s_v + aLong * dt);
            Braking = aLong < -0.35f * G;
            float spd = Math.Max(s_v, 0.5f);
            float rGrip = latAvail / spd;

            {
                // RIDE-style: steer asks for a lean; the lean rate falls with speed; the lean makes the turn
                float maxLean = Math.Max(5f, Math.Min(65f, Plugin.MaxLean.Value)) * Mathf.Deg2Rad;
                float leanGrip = (float)Math.Atan(latAvail / G);
                float upright = Clamp01((s_v - 2f) / 8f);
                float phiMax = Math.Min(maxLean, leanGrip) * upright;
                float target = steer * phiMax;
                float rate = 2.6f / (1f + s_v / 25f) + 0.6f;   // rad/s: quick at town speed, slow at 250 km/h
                s_phi += Clamp(target - s_phi, -rate * dt, rate * dt);
                float rLean = s_v > 1f ? G * (float)Math.Tan(s_phi) / spd : 0f;
                float rBars = s_v * (float)Math.Tan(steer * 0.55f) / 1.42f;   // walking pace: steer by the bars
                float blend = Clamp01((s_v - 3f) / 6f);
                float rWant = rBars + (rLean - rBars) * blend;
                float rT = Clamp(rWant, -rGrip, rGrip);
                s_r += (rT - s_r) * (1f - (float)Math.Exp(-dt / 0.08f));
                s_psi = Wrap(s_psi + s_r * dt);
                s_chi = Approach(s_chi, s_psi, rGrip * 1.1f * dt + 0.5f * dt);
                float beta = Wrap(s_psi - s_chi);
                LeanDeg = s_phi * Mathf.Rad2Deg;
                SteerDeg = steer * 28f * (1f - 0.85f * blend);
                SlipDeg = beta * Mathf.Rad2Deg;
                RearSlide = Math.Abs(beta) > 0.06f || (aLong > 0.85f * aUp && aUp > 3f && Math.Abs(s_phi) > 0.5f);
                FrontSkid = aLong < -0.97f * aDown;
            }

            // write back: the heading (yaw only) and the real speed for the game's own speed logic
            float dPsi = Wrap(s_psi - psiAct);
            if (Math.Abs(dPsi) > 1e-5f) rb.rotation = YawBy(q, dPsi);
            vm.TargetSpeed = Math.Max(0f, (s_v - inc) / mult);
        }

        // ---------------------------------------------------------------- plain maths (no Unity calls)

        private static Vector3 V3(float x, float y, float z) { var v = default(Vector3); v.x = x; v.y = y; v.z = z; return v; }
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static float Wrap(float a)
        {
            const float pi = (float)Math.PI, twoPi = 2f * (float)Math.PI;
            while (a > pi) a -= twoPi;
            while (a < -pi) a += twoPi;
            return a;
        }

        private static float Approach(float from, float to, float step)
        {
            float d = Wrap(to - from);
            if (d > step) d = step; else if (d < -step) d = -step;
            return Wrap(from + d);
        }

        /// <summary>The heading (radians, 0 = +z, + = towards +x) of the rotation's forward axis.</summary>
        private static float Yaw(Quaternion q)
        {
            float fx = 2f * (q.x * q.z + q.w * q.y), fz = 1f - 2f * (q.x * q.x + q.y * q.y);
            return (float)Math.Atan2(fx, fz);
        }

        /// <summary>q turned by d radians about the world's up axis.</summary>
        private static Quaternion YawBy(Quaternion q, float d)
        {
            float s = (float)Math.Sin(d * 0.5f), c = (float)Math.Cos(d * 0.5f);
            // (0, s, 0, c) * q
            var r = default(Quaternion);
            r.x = c * q.x + s * q.z;
            r.y = c * q.y + s * q.w;
            r.z = c * q.z - s * q.x;
            r.w = c * q.w - s * q.y;
            return r;
        }
    }
}
