using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Bikes
{
    /// <summary>
    /// Once a second (not while paused): bikes in the garage only in single-player with Enabled on, the hooks installed
    /// and the error breaker not tripped (Garage.Inject / Remove). The same tick keeps the run's bike flag:
    /// RodeBikeThisRun is set while a bike is driven and cleared when a new run starts (a new car at stage 0, race 0).
    /// While the player drives a bike, each frame (LateUpdate, after the game moved its wheels):
    /// - the bike's front wheel copies the game's FL spin pivot and its fork the FL steer pivot; the rear wheel copies RL;
    /// - the bike leans into corners: lean = atan(speed x yaw rate / g), smoothed, capped at MaxLean;
    /// - twice a second (not while paused), any car mesh the game added late is hidden with Renderer.forceRenderingOff.
    ///   Bodies are also hidden as they spawn (Guards' VehicleSkinHolder hooks), which covers the garage turntable.
    /// </summary>
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private float _nextGarage, _nextHide;
        private IntPtr _skinPtr;
        private Transform _skin, _lean, _steerF, _bars, _wheelF, _wheelR, _carSpinF, _carSteerF, _carSpinR;
        private Rigidbody _rb;
        private float _leanDeg;
        private int _errors;
        internal static bool Broken;
        private IntPtr _lastCar;
        private bool _loggedRide;

        internal static bool WantBikes()
        {
            if (!Guards.Ok || Broken || !Plugin.Enabled.Value) return false;
            try { return !Game.Runtime.GameState.IsMultiplayerMode; } catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool PlayerOnBike()
        {
            var veh = VehicleManager.Instance;
            return veh != null && Garage.IsBike(veh.VehicleSO);
        }

        private void Update()
        {
            try
            {
                if (Time.timeScale <= 0f) return;   // paused: a Rogue Hub toggle applies on resume
                float now = Time.unscaledTime;
                if (now < _nextGarage) return;
                _nextGarage = now + 1f;
                if (WantBikes()) Garage.Inject("single-player");
                else Garage.Remove(Broken ? "error breaker" : !Plugin.Enabled.Value ? "switched off" : "multiplayer");
                RunFlag();
            }
            catch (Exception e) { if (!Broken) Fault(e); }
        }

        /// <summary>Keeps RodeBikeThisRun: set while a bike is driven, cleared when a new run starts on a stock car.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RunFlag()
        {
            var veh = VehicleManager.Instance;
            IntPtr car = veh == null ? IntPtr.Zero : veh.Pointer;
            bool bike = veh != null && Garage.IsBike(veh.VehicleSO);
            if (bike && !Plugin.RodeBikeThisRun.Value)
            {
                Plugin.RodeBikeThisRun.Value = true;   // saved to rogue.bikes.cfg: a resumed run stays off the leaderboards
                Plugin.Log.LogInfo("[Bikes] this run uses a bike: it stays off the leaderboards");
            }
            if (car == _lastCar) return;
            _lastCar = car;
            if (car == IntPtr.Zero || bike || !Plugin.RodeBikeThisRun.Value) return;
            var world = UnityEngine.Object.FindFirstObjectByType<Game.Runtime.Manager.RunWorldManager>();   // once per new car
            if (world == null) { _lastCar = IntPtr.Zero; return; }
            if (world.currentStageIndex == 0 && world.currentRaceIndex == 0)
            {
                Plugin.RodeBikeThisRun.Value = false;
                Guards.ResetSkipLog();
                Plugin.Log.LogInfo("[Bikes] new run on a car: leaderboard uploads allowed again");
            }
        }

        private void LateUpdate()
        {
            if (Broken) return;
            try { Animate(); }
            catch (Exception e) { Fault(e); }
        }

        private void Fault(Exception e)
        {
            Clear();
            if (++_errors >= 5)
            {
                Broken = true;   // WantBikes is false from now on: the next tick takes the bikes out of the garage
                Plugin.Log.LogError($"[Bikes] switched off for this session after repeated errors (the bikes leave the garage): {e}");
                try { Garage.Remove("error breaker"); } catch { /* retried by the tick */ }
            }
            else Plugin.Log.LogWarning($"[Bikes] error ({_errors}/5): {e.Message}");
        }

        private void OnDestroy()
        {
            try { Garage.Remove("plugin unloaded", force: true); } catch { /* shutting down */ }
            Garage.DestroyAll();
            BikeModel.DestroyAll();
        }

        private void Clear()
        {
            _skinPtr = IntPtr.Zero; _skin = null; _lean = null; _steerF = null; _bars = null; _wheelF = null; _wheelR = null;
            _carSpinF = null; _carSteerF = null; _carSpinR = null; _rb = null; _leanDeg = 0f;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Animate()
        {
            float now = Time.unscaledTime;
            var veh = VehicleManager.Instance;
            if (veh == null || !Garage.IsBike(veh.VehicleSO))
            {
                if (_skinPtr != IntPtr.Zero) Clear();
                return;
            }
            var holder = veh.VehicleSkin;
            if (holder == null) { Clear(); return; }
            if (holder.Pointer != _skinPtr || _skin == null) Find(holder.transform, veh);
            if (now >= _nextHide && Time.timeScale > 0f) { _nextHide = now + 0.5f; Garage.HideCar(_skin); }
            if (_lean == null) return;   // a car model: its wheels ride on the donor's pivots, no lean

            // wheels: copy the game's own spin and steer
            if (_wheelF != null && _carSpinF != null) _wheelF.localRotation = _carSpinF.localRotation;
            if (_steerF != null && _carSteerF != null) _steerF.localRotation = _carSteerF.localRotation;
            if (_bars != null && _carSteerF != null) _bars.localRotation = _carSteerF.localRotation;
            if (_wheelR != null && _carSpinR != null) _wheelR.localRotation = _carSpinR.localRotation;

            // lean into the corner: tan(lean) = v x yaw rate / g
            float target = 0f;
            float dt = Time.deltaTime;
            if (_rb != null && dt > 0f)
            {
                Vector3 v = _rb.linearVelocity, w = _rb.angularVelocity;
                float speed = (float)Math.Sqrt(v.x * v.x + v.z * v.z);
                float lat = speed * w.y;   // + = turning right
                float max = Math.Max(0f, Math.Min(65f, Plugin.MaxLean.Value));
                target = -(float)(Math.Atan2(lat, 9.81) * 180.0 / Math.PI) * Plugin.LeanScale.Value;
                if (speed < 3f) target *= speed / 3f;   // upright at a crawl
                if (target > max) target = max; else if (target < -max) target = -max;
            }
            float k = dt > 0f ? 1f - (float)Math.Exp(-8f * dt) : 0f;
            _leanDeg += (target - _leanDeg) * k;
            _lean.localRotation = _leanBase * Quaternion.Euler(0f, 0f, _leanDeg);
        }

        private Quaternion _leanBase = Quaternion.identity;

        private void Find(Transform skin, VehicleManager veh)
        {
            Clear();
            _skin = skin; _skinPtr = veh.VehicleSkin.Pointer;
            _rb = veh.Rigidbody;
            var all = skin.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.gameObject.name;
                switch (n)
                {
                    case "Bikes.Lean": _lean = t; break;
                    case "Bikes.SteerF": _steerF = t; break;
                    case "Bikes.Bars": _bars = t; break;
                    case "Bikes.WheelF": _wheelF = t; break;
                    case "Bikes.WheelR": _wheelR = t; break;
                    case "FL Wheel (Spin Pivot)": _carSpinF = t; break;
                    case "FL Wheel (Steer Pivot)": _carSteerF = t; break;
                    case "RL Wheel (Spin Pivot)": _carSpinR = t; break;
                }
            }
            _leanBase = _lean != null ? _lean.localRotation : Quaternion.identity;
            int layer = Garage.MatchLayer(skin);   // 0.2.3: on the car's layer, so the game's velocity blur skips it (Garage.MatchLayer)
            if (!_loggedRide)
            {
                _loggedRide = true;
                Plugin.Log.LogInfo($"[Bikes] driving {veh.VehicleSO.VehicleName}: {(_lean != null ? "bike" : "car or missing")} model, " +
                                   $"wheels {(_wheelF != null && _carSpinF != null ? "follow the game's" : "static")}, steering {(_steerF != null && _carSteerF != null ? "follows" : "static")}, layer {layer}");
            }
            Garage.HideCar(skin);
        }
    }
}
