using System;
using System.Collections.Generic;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace CurbFeel
{
    /// <summary>
    /// A. Shrinks every vehicle's BarrierCollider-layer capsules (the only colliders that meet the road-edge walls)
    /// so they stop at the car's bodywork. Stock capsules reach ~1.6-1.7 m from the centreline vs a ~1.2-1.3 m body.
    /// </summary>
    internal class HullTrimmer
    {
        private const int BarrierLayer = 16;

        private struct CapsuleState
        {
            public CapsuleCollider Collider;
            public float Radius, Height;
            public Vector3 Center;
        }

        private readonly Dictionary<int, CapsuleState> _original = new();
        private readonly Dictionary<int, float> _bodyHalfWidth = new();
        private readonly Dictionary<int, (VehicleMovement move, float value)> _bounce = new();

        // Rescan policy: a full capsule scan is only worth doing when vehicles may have appeared. After a change (player
        // car instance, loaded-scene count, revert) scan every tick for FastWindow seconds while cars spawn; otherwise
        // a safety scan every SafetyPeriod seconds catches anything else (AI racers spawning mid-race, a new body).
        private const float FastWindow = 10f, SafetyPeriod = 3f;
        private IntPtr _lastPlayer = (IntPtr)(-1);
        private int _lastSceneCount = -1;
        private float _fastUntil, _nextSafety;
        private readonly HashSet<int> _notBarrier = new();   // capsule IDs seen on another layer (cleared on full scans)

        public void Tick()
        {
            float now = Time.unscaledTime;
            var vmi = VehicleManager.Instance;
            IntPtr playerPtr = vmi != null ? vmi.Pointer : IntPtr.Zero;
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            if (playerPtr != _lastPlayer || sceneCount != _lastSceneCount)
            {
                _lastPlayer = playerPtr;
                _lastSceneCount = sceneCount;
                _fastUntil = now + FastWindow;
            }

            bool full = now >= _nextSafety;
            if (!full && now >= _fastUntil) return;
            if (full)
            {
                _nextSafety = now + SafetyPeriod;
                _notBarrier.Clear();                         // a full scan re-checks every capsule's layer
            }
            Scan(vmi != null ? vmi.transform : null);
        }

        private void Scan(Transform player)
        {
            var caps = UnityEngine.Object.FindObjectsByType<CapsuleCollider>(FindObjectsSortMode.None);
            _bikesRoot.Clear();

            for (int i = 0; i < caps.Length; i++)
            {
                var cap = caps[i];
                if (cap == null) continue;
                int id = cap.GetInstanceID();                // cheap checks first: already handled / known non-barrier
                if (_original.ContainsKey(id) || _notBarrier.Contains(id)) continue;
                if (cap.gameObject.layer != BarrierLayer) { _notBarrier.Add(id); continue; }

                Transform root = ResolveVehicleRoot(cap);
                if (root == null) continue;
                if (!Settings.HullAllVehicles.Value && (player == null || root != player)) continue;

                // cooperates with Bikes: a Bikes motorcycle squeezes this car's colliders to its own size (Bikes' Hitbox) and
                // puts them back when the rider gets off; trimming (or recording a squeezed size as stock) would fight it.
                // Not recorded, so the capsule is trimmed on a later scan once the vehicle is a car again.
                if (IsBikesVehicle(root)) continue;
                float target = TargetHalfWidth(root);
                _original[id] = new CapsuleState { Collider = cap, Radius = cap.radius, Height = cap.height, Center = cap.center };
                if (Settings.HullEnabled.Value && Trim(cap, root, target)) Stats.HullCapsules++;
                _roots.Add(root.GetInstanceID());
                Stats.HullCars = _roots.Count;
                if (player != null && root == player) Stats.PlayerBodyHalfWidth = target;
                ApplyBounce(root);
            }
        }

        private readonly HashSet<int> _roots = new();
        private readonly Dictionary<int, bool> _bikesRoot = new();   // per scan: does this vehicle carry a Bikes motorcycle body?

        /// <summary>The vehicle carries a Bikes motorcycle (a "Bikes.Lean" node under it), checked once per vehicle per scan.</summary>
        private bool IsBikesVehicle(Transform root)
        {
            int id = root.GetInstanceID();
            if (_bikesRoot.TryGetValue(id, out bool b)) return b;
            b = false;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length && !b; i++)
                if (all[i] != null && all[i].gameObject.name == "Bikes.Lean") b = true;
            _bikesRoot[id] = b;
            return b;
        }

        public void Revert()
        {
            foreach (var s in _original.Values)
            {
                if (s.Collider == null) continue;
                s.Collider.radius = s.Radius;
                s.Collider.height = s.Height;
                s.Collider.center = s.Center;
            }
            foreach (var b in _bounce.Values)
                if (b.move != null) b.move.bounceOffGuardrailMultiplier = b.value;
            _original.Clear();
            _bodyHalfWidth.Clear();
            _bounce.Clear();
            _roots.Clear();
            _notBarrier.Clear();
            _lastPlayer = (IntPtr)(-1);                      // force a fast rescan so the next ticks re-apply
            _nextSafety = 0f;
        }

        private static Transform ResolveVehicleRoot(Component c)
        {
            var vm = c.GetComponentInParent<VehicleManager>(true);
            if (vm != null) return vm.transform;
            var rm = c.GetComponentInParent<RacerVehicleManager>(true);
            if (rm != null) return rm.transform;
            // The joint capsule can be detached from the car at runtime; it keeps a reference to the body.
            var bc = c.GetComponent<VehicleBarrierCapsule>();
            if (bc != null && bc.vehicleBody != null && bc.vehicleBody != c.transform) return ResolveVehicleRoot(bc.vehicleBody);
            return null;
        }

        private float TargetHalfWidth(Transform root)
        {
            float half = Settings.HullHalfWidth.Value;
            if (half <= 0f)
            {
                int id = root.GetInstanceID();
                if (!_bodyHalfWidth.TryGetValue(id, out half))
                {
                    half = MeasureBodyHalfWidth(root);
                    _bodyHalfWidth[id] = half;
                    Plugin.Verbose($"[Hull] {root.name}: body half-width {half:F2} m");
                }
            }
            return half + Settings.HullMargin.Value;
        }

        private static readonly string[] SkipNames = { "Glitch", "Ghost", "Cockpit", "DriverCam", "FX", "Smoke", "Swirl", "Grind", "Trail", "Spark", "Shadow" };
        private const float MaxPartSize = 8f;

        /// <summary>The car's visible body transform ('GFX'), same object DriverCam measures.</summary>
        private static Transform BodyOf(Transform root)
        {
            var vm = root.GetComponent<VehicleManager>();
            if (vm != null && vm.vehicleBody != null) return vm.vehicleBody;
            var rm = root.GetComponent<RacerVehicleManager>();
            if (rm != null && rm.vehicleBody != null) return rm.vehicleBody;
            return root;
        }

        /// <summary>
        /// Half-width of the car's visible shell in the vehicle's own frame: active Mesh/SkinnedMesh renderers under the body,
        /// using their local bounds (the method DriverCam uses; gives e.g. 1.28 m for Justice).
        /// </summary>
        private static float MeasureBodyHalfWidth(Transform root)
        {
            Transform body = BodyOf(root);
            float min = float.MaxValue, max = float.MinValue;
            int used = 0;
            var renderers = body.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                string n = r.gameObject.name;
                bool skip = false;
                foreach (var s in SkipNames) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) { skip = true; break; }
                if (skip) continue;

                Bounds lb = r.localBounds;
                float pMin = float.MaxValue, pMax = float.MinValue, pMinY = float.MaxValue, pMaxY = float.MinValue, pMinZ = float.MaxValue, pMaxZ = float.MinValue;
                for (int k = 0; k < 8; k++)
                {
                    var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    var p = root.InverseTransformPoint(r.transform.TransformPoint(corner));
                    pMin = Mathf.Min(pMin, p.x); pMax = Mathf.Max(pMax, p.x);
                    pMinY = Mathf.Min(pMinY, p.y); pMaxY = Mathf.Max(pMaxY, p.y);
                    pMinZ = Mathf.Min(pMinZ, p.z); pMaxZ = Mathf.Max(pMaxZ, p.z);
                }
                if (pMax - pMin > MaxPartSize || pMaxY - pMinY > MaxPartSize || pMaxZ - pMinZ > MaxPartSize) continue;
                min = Mathf.Min(min, pMin); max = Mathf.Max(max, pMax);
                used++;
            }
            float half = used > 0 ? Mathf.Max(Mathf.Abs(min), Mathf.Abs(max)) : 0f;
            if (half < 0.6f || half > 1.8f)
            {
                Plugin.Log.LogWarning($"[Hull] {root.name}: body measurement implausible ({half:F2} m from {used} parts), using 1.25 m");
                half = 1.25f;
            }
            return half;
        }

        private static bool Trim(CapsuleCollider cap, Transform root, float target)
        {
            Transform t = cap.transform;
            float s = Mathf.Abs(t.lossyScale.x / Mathf.Max(1e-4f, root.lossyScale.x));
            Vector3 c = root.InverseTransformPoint(t.TransformPoint(cap.center));
            Vector3 axisLocal = cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward;
            Vector3 axis = root.InverseTransformDirection(t.TransformDirection(axisLocal)).normalized;

            float r = cap.radius * s;
            float halfSeg = Mathf.Max(0f, cap.height * 0.5f - cap.radius) * s;
            float reach = Mathf.Abs(c.x) + Mathf.Abs(axis.x) * halfSeg + r;
            if (reach <= target) return false;

            float minR = Settings.HullMinRadius.Value;
            string before = $"r {r:F2} reach {reach:F2}";

            if (Mathf.Abs(axis.x) < 0.5f)
            {
                // Lengthwise capsule: shrink radius, slide inward if needed. Length along the car is kept.
                float newR = target - Mathf.Abs(c.x);
                if (newR < minR)
                {
                    newR = Mathf.Min(r, minR);
                    c.x = Mathf.Sign(c.x) * Mathf.Max(0f, target - newR);
                }
                float newHalfLen = halfSeg + r;                     // keep overall length
                cap.radius = newR / s;
                cap.height = Mathf.Max(2f * newR, 2f * newHalfLen) / s;
                cap.center = t.InverseTransformPoint(root.TransformPoint(c));
                r = newR;
            }
            else
            {
                // Crosswise capsule (front/back bars): shorten it.
                float newR = Mathf.Min(r, Mathf.Max(minR, target - Mathf.Abs(c.x)));
                float newHalfSeg = Mathf.Max(0f, target - Mathf.Abs(c.x) - newR);
                cap.radius = newR / s;
                cap.height = 2f * (newHalfSeg + newR) / s;
                r = newR;
            }
            Plugin.Verbose($"[Hull] {root.name}/{cap.gameObject.name}: {before} -> r {r:F2} reach {target:F2}");
            return true;
        }

        private void ApplyBounce(Transform root)
        {
            float v = Settings.BounceOffMult.Value;
            if (v < 0f || !Settings.ScrapeEnabled.Value) return;
            int id = root.GetInstanceID();
            if (_bounce.ContainsKey(id)) return;
            var move = root.GetComponentInChildren<VehicleMovement>(true);
            if (move == null) return;
            _bounce[id] = (move, move.bounceOffGuardrailMultiplier);
            move.bounceOffGuardrailMultiplier = v;
        }
    }
}
