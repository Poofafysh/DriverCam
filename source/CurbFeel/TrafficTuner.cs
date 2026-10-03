using System.Collections.Generic;
using System.Linq;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace CurbFeel
{
    /// <summary>
    /// E. Traffic hit boxes for lane splitting.
    /// AISkinSelector.InitializeSkin copies each model's BoxCollider size onto AIVehicleController.vehicleCollider
    /// (and disables the model's own box), so the model box stays as an untouched reference we can scale from.
    /// Pooled cars get re-skinned, so this is enforced every tick rather than once.
    /// </summary>
    internal class TrafficTuner
    {
        private readonly HashSet<int> _touched = new();
        private readonly SortedSet<float> _lanes = new();
        private int _lanesLogged;
        private VehicleStuntHandler _stunt;
        private float _stuntOriginal = -1f;

        public void Tick()
        {
            var cars = Object.FindObjectsByType<AIVehicleController>(FindObjectsSortMode.None);
            float w = Settings.TrafficEnabled.Value ? Settings.TrafficWidthScale.Value : 1f;
            float l = Settings.TrafficEnabled.Value ? Settings.TrafficLengthScale.Value : 1f;
            int resized = 0, total = 0, matched = 0;

            for (int i = 0; i < cars.Length; i++)
            {
                var car = cars[i];
                if (car == null) continue;
                var col = car.vehicleCollider;
                var skin = car.skinSelector != null ? car.skinSelector.CurrentSkin : null;
                var reference = skin != null ? skin.BoxCollider : null;
                if (col == null || reference == null) continue;

                Vector3 s = reference.size;
                var want = new Vector3(s.x * w, s.y, s.z * l);
                if ((col.size - want).sqrMagnitude > 1e-6f)
                {
                    col.size = want;
                    resized++;
                }
                total++;
                if (Mathf.Abs(w - 1f) > 1e-4f || Mathf.Abs(l - 1f) > 1e-4f) matched++;
                _touched.Add(col.GetInstanceID());

                // only settled cars (not mid lane-change), rounded to 0.5 m
                if (Settings.LogLanes.Value && car.laneHandler != null && !car.laneHandler.IsChangingLanes)
                    _lanes.Add(Mathf.Round(car.laneHandler.CurrentLaneOffset * 2f) / 2f);
            }

            Stats.TrafficCars = total;
            Stats.TrafficResized = matched;
            if (resized > 0) Plugin.Verbose($"[Traffic] resized {resized} traffic hit boxes (width x{w:F2}, length x{l:F2})");
            if (Settings.LogLanes.Value && _lanes.Count > _lanesLogged && cars.Length >= 4)
            {
                _lanesLogged = _lanes.Count;
                Stats.Lanes = string.Join(", ", _lanes.Select(x => x.ToString("0.##")));
                Plugin.Log.LogInfo($"[Traffic] lane offsets in use: {Stats.Lanes}");
            }

            ApplyNearMiss(w);
        }

        private void ApplyNearMiss(float widthScale)
        {
            var vm = VehicleManager.Instance;
            if (vm == null) return;
            var stunt = vm.GetComponentInChildren<VehicleStuntHandler>(true);
            if (stunt == null) return;
            if (_stunt == null || _stunt.Pointer != stunt.Pointer)
            {
                _stunt = stunt;
                _stuntOriginal = stunt.withinNearMissExtraRange;
            }
            float want = Settings.NearMissExtraRange.Value >= 0f
                ? Settings.NearMissExtraRange.Value
                : _stuntOriginal + (1f - widthScale) * 1.2f;     // half of a ~2.4 m wide car
            if (!Settings.TrafficEnabled.Value) want = _stuntOriginal;
            Stats.NearMissRange = want;
            if (Mathf.Abs(stunt.withinNearMissExtraRange - want) > 1e-4f)
            {
                stunt.withinNearMissExtraRange = want;
                Plugin.Verbose($"[Traffic] near-miss extra range {_stuntOriginal:F2} -> {want:F2} m");
            }
        }

        public void Revert()
        {
            var cars = Object.FindObjectsByType<AIVehicleController>(FindObjectsSortMode.None);
            for (int i = 0; i < cars.Length; i++)
            {
                var car = cars[i];
                var col = car != null ? car.vehicleCollider : null;
                var skin = car != null && car.skinSelector != null ? car.skinSelector.CurrentSkin : null;
                if (col != null && skin != null && skin.BoxCollider != null) col.size = skin.BoxCollider.size;
            }
            if (_stunt != null && _stuntOriginal >= 0f) _stunt.withinNearMissExtraRange = _stuntOriginal;
            _touched.Clear();
        }
    }
}
