using System;
using System.Collections.Generic;
using System.Linq;
using Game.Runtime.Vehicle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CurbFeel
{
    /// <summary>
    /// E. Traffic hit boxes for lane splitting.
    /// AISkinSelector.InitializeSkin copies each model's BoxCollider size onto AIVehicleController.vehicleCollider
    /// (and disables the model's own box), so the model box stays as an untouched reference we can scale from.
    /// Pooled cars get re-skinned, so this is enforced every tick rather than once.
    /// Cars come from the spawner's own active list (AISpawnerBase.Instance -> DefaultAISpawner.activeAiCars), with a
    /// whole-scene search only as a fallback (no single-player spawner) and as a slow safety sweep for any AI car the
    /// spawner doesn't list.
    /// </summary>
    internal class TrafficTuner
    {
        private const float SafetySweepPeriod = 5f;

        private readonly SortedSet<float> _lanes = new();
        private readonly HashSet<IntPtr> _done = new();          // cars handled this tick (spawner list + sweep can overlap)
        private int _lanesLogged;
        private VehicleStuntHandler _stunt;
        private IntPtr _stuntOwner;                               // VehicleManager the cached stunt handler belongs to
        private float _stuntOriginal = -1f;
        private float _nextSweep;

        public void Tick()
        {
            float w = Settings.TrafficEnabled.Value ? Settings.TrafficWidthScale.Value : 1f;
            float l = Settings.TrafficEnabled.Value ? Settings.TrafficLengthScale.Value : 1f;
            bool scaled = Mathf.Abs(w - 1f) > 1e-4f || Mathf.Abs(l - 1f) > 1e-4f;
            bool logLanes = Settings.LogLanes.Value;
            int resized = 0, total = 0, matched = 0;
            _done.Clear();

            var active = ActiveCars();
            if (active != null)
            {
                for (int i = 0; i < active.Count; i++)
                    TuneCar(active[i], w, l, scaled, logLanes, ref resized, ref total, ref matched);
            }

            float now = Time.unscaledTime;
            if (active == null || now >= _nextSweep)
            {
                _nextSweep = now + SafetySweepPeriod;
                var cars = Object.FindObjectsByType<AIVehicleController>(FindObjectsSortMode.None);
                for (int i = 0; i < cars.Length; i++)
                    TuneCar(cars[i], w, l, scaled, logLanes, ref resized, ref total, ref matched);
            }

            // On the fast path (spawner list only) the count is the spawner's; a car only the sweep finds is still
            // resized every SafetySweepPeriod but only counted on sweep ticks.
            Stats.TrafficCars = total;
            Stats.TrafficResized = matched;
            if (resized > 0) Plugin.Verbose($"[Traffic] resized {resized} traffic hit boxes (width x{w:F2}, length x{l:F2})");
            if (logLanes && _lanes.Count > _lanesLogged && _done.Count >= 4)
            {
                _lanesLogged = _lanes.Count;
                Stats.Lanes = string.Join(", ", _lanes.Select(x => x.ToString("0.##")));
                Plugin.Log.LogInfo($"[Traffic] lane offsets in use: {Stats.Lanes}");
            }

            ApplyNearMiss(w);
        }

        /// <summary>The single-player spawner's live car list, or null if there is none (menus, multiplayer).</summary>
        private static Il2CppSystem.Collections.Generic.List<AIVehicleController> ActiveCars()
        {
            var sp = AISpawnerBase.Instance;
            if (sp == null) return null;
            var def = sp.TryCast<DefaultAISpawner>();
            return def != null ? def.activeAiCars : null;
        }

        private void TuneCar(AIVehicleController car, float w, float l, bool scaled, bool logLanes, ref int resized, ref int total, ref int matched)
        {
            if (car == null || !_done.Add(car.Pointer)) return;
            var col = car.vehicleCollider;
            var sel = car.skinSelector;
            var skin = sel != null ? sel.CurrentSkin : null;      // not cached: pooled cars are re-skinned
            var reference = skin != null ? skin.BoxCollider : null;
            if (col == null || reference == null) return;

            Vector3 s = reference.size;
            var want = new Vector3(s.x * w, s.y, s.z * l);
            if ((col.size - want).sqrMagnitude > 1e-6f)
            {
                col.size = want;
                resized++;
            }
            total++;
            if (scaled) matched++;

            // only settled cars (not mid lane-change), rounded to 0.5 m
            if (logLanes)
            {
                var lane = car.laneHandler;
                if (lane != null && !lane.IsChangingLanes)
                    _lanes.Add(Mathf.Round(lane.CurrentLaneOffset * 2f) / 2f);
            }
        }

        private void ApplyNearMiss(float widthScale)
        {
            var vm = VehicleManager.Instance;
            if (vm == null) return;
            // GetComponentInChildren only when the player's car changed (or the cached handler died)
            if (_stunt == null || _stuntOwner != vm.Pointer)
            {
                var stunt = vm.GetComponentInChildren<VehicleStuntHandler>(true);
                if (stunt == null) return;
                if (_stunt == null || _stunt.Pointer != stunt.Pointer)
                {
                    _stunt = stunt;
                    _stuntOriginal = stunt.withinNearMissExtraRange;
                }
                _stuntOwner = vm.Pointer;
            }
            float want = Settings.NearMissExtraRange.Value >= 0f
                ? Settings.NearMissExtraRange.Value
                : _stuntOriginal + (1f - widthScale) * 1.2f;     // half of a ~2.4 m wide car
            if (!Settings.TrafficEnabled.Value) want = _stuntOriginal;
            Stats.NearMissRange = want;
            if (Mathf.Abs(_stunt.withinNearMissExtraRange - want) > 1e-4f)
            {
                _stunt.withinNearMissExtraRange = want;
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
            _done.Clear();
            _nextSweep = 0f;
        }
    }
}
