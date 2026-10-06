using System;
using System.Collections.Generic;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Bikes
{
    /// <summary>
    /// While the player rides a bike, the donor car's collision shell is squeezed to the bike's size (0.8 m wide, 2.2 m
    /// long round the axles' midpoint): every enabled, solid collider under the player vehicle (the base's hull capsules
    /// and ground box, the body's barrier capsules, whatever the game adds) keeps its height but is narrowed and
    /// shortened in the vehicle's own frame (capsules: radius and length, slid inward; boxes aligned with the vehicle:
    /// size; spheres: radius; mesh colliders: switched off). The rigidbody's centre of mass and inertia are put back
    /// after each change, so the handling stays the game's. Re-checked twice a second (a collider the game or CurbFeel
    /// reset is squeezed again, from its new size); everything is restored exactly when the player leaves the bike,
    /// switches Bikes off, on the error breaker and on unload.
    /// Also hides the game's glitch / dissolve copies of the car body (renderers under "Glitch" nodes and every GlitchFX's
    /// renderers) while any Bikes vehicle is driven: they'd draw the donor car's shape round the bike or the M2. Shown again
    /// when the player is back on a stock car.
    /// </summary>
    internal static class Hitbox
    {
        internal const float HalfWidth = 0.40f, HalfLength = 1.10f;

        private sealed class Saved
        {
            public Collider Col;
            public int Kind;                 // 0 capsule, 1 box, 2 sphere, 3 mesh (disabled)
            public float A, B;               // capsule radius / height; sphere radius
            public Vector3 V, C;             // box size; centre
            public float A2, B2;             // what we wrote (capsule / sphere)
            public Vector3 V2, C2;           // what we wrote (box size / centre)
        }

        private static readonly Dictionary<int, Saved> s_saved = new Dictionary<int, Saved>();
        private static readonly List<Renderer> s_glitch = new List<Renderer>();
        private static Rigidbody s_rb;
        private static Vector3 s_com, s_inertia;
        private static Quaternion s_inertiaRot;
        private static IntPtr s_root;
        private static bool s_logged;
        private static readonly List<Collider> s_detached = new List<Collider>();   // barrier capsules the game moved off the car
        private static float s_nextDetached;
        private static bool s_glitchBroken, s_detachedBroken;   // a game change broke that lookup: only that part stops (not Bikes)

        internal static int Squeezed => s_saved.Count;
        /// <summary>Anything to put back (cheap: checked every frame off the bike).</summary>
        internal static bool Any => s_saved.Count > 0 || s_glitch.Count > 0 || s_root != IntPtr.Zero;

        /// <summary>Squeezes the shell (bikes) and hides the glitch copies (bikes and the M2). Twice a second while driven.</summary>
        internal static void Apply(Transform root, Rigidbody rb, Transform spinF, Transform spinR, bool bike)
        {
            if (root == null) return;
            if (root.Pointer != s_root) { Restore("vehicle changed"); s_root = root.Pointer; }
            if (!s_glitchBroken)
            {
                try { HideGlitch(root); }
                catch (Exception e) { s_glitchBroken = true; Plugin.Log.LogWarning($"[Bikes] glitch-copy hiding switched off for this session: {e.Message}"); }
            }
            if (!bike) return;
            if (s_rb == null && rb != null)
            {
                s_rb = rb; s_com = rb.centerOfMass; s_inertia = rb.inertiaTensor; s_inertiaRot = rb.inertiaTensorRotation;
            }
            float cz = 0f;
            if (spinF != null && spinR != null) cz = (root.InverseTransformPoint(spinF.position).z + root.InverseTransformPoint(spinR.position).z) * 0.5f;
            float now = Time.unscaledTime;
            if (now >= s_nextDetached && !s_detachedBroken)
            {
                s_nextDetached = now + 5f;   // a scene scan: every 5 s at most
                try { FindDetached(root); }
                catch (Exception e) { s_detachedBroken = true; s_detached.Clear(); Plugin.Log.LogWarning($"[Bikes] detached barrier capsule lookup switched off for this session: {e.Message}"); }
            }
            var cols = new List<Collider>(root.GetComponentsInChildren<Collider>(true));
            cols.AddRange(s_detached);
            int changed = 0;
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                try { if (Squeeze(c, root, cz)) changed++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] hitbox: {c.gameObject.name} left as it is ({e.Message})"); }
            }
            if (changed > 0 && s_rb != null)
            {
                s_rb.centerOfMass = s_com; s_rb.inertiaTensor = s_inertia; s_rb.inertiaTensorRotation = s_inertiaRot;
            }
            if (changed > 0 && !s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo($"[Bikes] bike hitbox: {s_saved.Count} car collider(s) squeezed to {HalfWidth * 2f:0.0} x {HalfLength * 2f:0.0} m " +
                                   $"(glitch copies hidden: {s_glitch.Count})");
            }
        }

        /// <summary>The car's barrier capsule can be detached from the car at runtime (it keeps a reference to the body,
        /// as CurbFeel's HullTrimmer notes): those whose body is under this vehicle are squeezed too.</summary>
        private static void FindDetached(Transform root)
        {
            s_detached.Clear();
            var caps = UnityEngine.Object.FindObjectsByType<VehicleBarrierCapsule>(FindObjectsSortMode.None);
            for (int i = 0; i < caps.Length; i++)
            {
                var bc = caps[i];
                if (bc == null || bc.vehicleBody == null || bc.transform.IsChildOf(root)) continue;
                if (bc.vehicleBody != root && !bc.vehicleBody.IsChildOf(root)) continue;
                var col = bc.GetComponent<Collider>();
                if (col != null) s_detached.Add(col);
            }
        }

        private static bool Squeeze(Collider col, Transform root, float cz)
        {
            int id = col.GetInstanceID();
            s_saved.TryGetValue(id, out var s);
            var cap = col.TryCast<CapsuleCollider>();
            if (cap != null)
            {
                if (s != null && cap.radius == s.A2 && cap.height == s.B2 && cap.center == s.C2) return false;   // still ours
                s = new Saved { Col = col, Kind = 0, A = cap.radius, B = cap.height, C = cap.center };
                SqueezeCapsule(cap, root, cz);
                s.A2 = cap.radius; s.B2 = cap.height; s.C2 = cap.center;
                s_saved[id] = s;
                return true;
            }
            var box = col.TryCast<BoxCollider>();
            if (box != null)
            {
                if (s != null && box.size == s.V2 && box.center == s.C2) return false;
                var t = box.transform;
                // only boxes aligned with the vehicle (their x along its x): others are left alone
                if (Math.Abs(Vector3.Dot(t.right, root.right)) < 0.98f || Math.Abs(Vector3.Dot(t.forward, root.forward)) < 0.98f) return false;
                s = new Saved { Col = col, Kind = 1, V = box.size, C = box.center };
                float rs = Math.Max(1e-4f, Math.Abs(root.lossyScale.x));
                Vector3 ls = t.lossyScale;
                float sx = Math.Max(1e-4f, Math.Abs(ls.x) / rs), sz = Math.Max(1e-4f, Math.Abs(ls.z) / rs);
                Vector3 c = root.InverseTransformPoint(t.TransformPoint(box.center));
                float hx = Math.Min(Math.Abs(box.size.x) * sx * 0.5f, HalfWidth), hz = Math.Min(Math.Abs(box.size.z) * sz * 0.5f, HalfLength);
                c.x = Clamp(c.x, -(HalfWidth - hx), HalfWidth - hx);
                c.z = Clamp(c.z, cz - (HalfLength - hz), cz + (HalfLength - hz));
                var size = box.size; size.x = 2f * hx / sx; size.z = 2f * hz / sz;
                box.size = size;
                box.center = t.InverseTransformPoint(root.TransformPoint(c));
                s.V2 = box.size; s.C2 = box.center;
                s_saved[id] = s;
                return true;
            }
            var sph = col.TryCast<SphereCollider>();
            if (sph != null)
            {
                if (s != null && sph.radius == s.A2 && sph.center == s.C2) return false;
                var t = sph.transform;
                s = new Saved { Col = col, Kind = 2, A = sph.radius, C = sph.center };
                float sc = Math.Max(1e-4f, Math.Abs(t.lossyScale.x / Math.Max(1e-4f, root.lossyScale.x)));
                float r = Math.Min(sph.radius * sc, HalfWidth);
                Vector3 c = root.InverseTransformPoint(t.TransformPoint(sph.center));
                c.x = Clamp(c.x, -(HalfWidth - r), HalfWidth - r);
                c.z = Clamp(c.z, cz - (HalfLength - r), cz + (HalfLength - r));
                sph.radius = r / sc;
                sph.center = t.InverseTransformPoint(root.TransformPoint(c));
                s.A2 = sph.radius; s.C2 = sph.center;
                s_saved[id] = s;
                return true;
            }
            var mesh = col.TryCast<MeshCollider>();
            if (mesh != null)
            {
                s_saved[id] = new Saved { Col = col, Kind = 3 };
                col.enabled = false;   // restored on leaving the bike
                return true;
            }
            return false;
        }

        /// <summary>A capsule narrowed to HalfWidth and shortened to HalfLength round cz, in the vehicle's frame; height kept.</summary>
        private static void SqueezeCapsule(CapsuleCollider cap, Transform root, float cz)
        {
            var t = cap.transform;
            float s = Math.Max(1e-4f, Math.Abs(t.lossyScale.x / Math.Max(1e-4f, root.lossyScale.x)));
            Vector3 c = root.InverseTransformPoint(t.TransformPoint(cap.center));
            Vector3 axisLocal = cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward;
            Vector3 axis = root.InverseTransformDirection(t.TransformDirection(axisLocal)).normalized;
            float r = cap.radius * s;
            float halfSeg = Math.Max(0f, cap.height * 0.5f - cap.radius) * s;
            float newR = Math.Min(r, HalfWidth);
            float ax = Math.Abs(axis.x), az = Math.Abs(axis.z);
            float newSeg;
            if (ax >= 0.5f) newSeg = Math.Min(halfSeg, Math.Max(0f, HalfWidth - newR) / ax);          // crosswise bar
            else if (az >= 0.5f) newSeg = Math.Min(halfSeg, Math.Max(0f, HalfLength - newR) / az);    // lengthwise
            else newSeg = halfSeg;                                                                     // upright: height kept
            float reachX = newR + newSeg * ax, reachZ = newR + newSeg * az;
            c.x = Clamp(c.x, -Math.Max(0f, HalfWidth - reachX), Math.Max(0f, HalfWidth - reachX));
            c.z = Clamp(c.z, cz - Math.Max(0f, HalfLength - reachZ), cz + Math.Max(0f, HalfLength - reachZ));
            cap.radius = newR / s;
            cap.height = 2f * (newSeg + newR) / s;
            cap.center = t.InverseTransformPoint(root.TransformPoint(c));
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The game's glitch / dissolve copies of the car: hidden (forceRenderingOff), remembered for Restore.</summary>
        private static void HideGlitch(Transform root)
        {
            var rs = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || r.forceRenderingOff) continue;
                bool glitch = false;
                for (var cur = r.transform; cur != null && cur != root; cur = cur.parent)
                {
                    string n = cur.gameObject.name;
                    if (n.StartsWith("Bikes.", StringComparison.Ordinal)) { glitch = false; break; }
                    if (n.IndexOf("Glitch", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0) glitch = true;
                }
                if (!glitch) continue;
                r.forceRenderingOff = true;
                s_glitch.Add(r);
            }
            var fx = root.GetComponentsInChildren<GlitchFX>(true);
            for (int i = 0; i < fx.Length; i++)
            {
                var arr = fx[i] != null ? fx[i].glitchRendererArray : null;
                if (arr == null) continue;
                for (int k = 0; k < arr.Length; k++)
                {
                    var r = arr[k];
                    if (r == null || r.forceRenderingOff || r.gameObject.name.StartsWith("Bikes.", StringComparison.Ordinal)) continue;
                    r.forceRenderingOff = true;
                    s_glitch.Add(r);
                }
            }
        }

        /// <summary>Everything back as the game had it (only where it's still what we wrote). Safe to call any time.</summary>
        internal static void Restore(string why)
        {
            int n = 0;
            foreach (var s in s_saved.Values)
            {
                try
                {
                    if (s.Col == null) continue;
                    switch (s.Kind)
                    {
                        case 0:
                            var cap = s.Col.TryCast<CapsuleCollider>();
                            if (cap != null && cap.radius == s.A2 && cap.height == s.B2 && cap.center == s.C2) { cap.radius = s.A; cap.height = s.B; cap.center = s.C; n++; }
                            break;
                        case 1:
                            var box = s.Col.TryCast<BoxCollider>();
                            if (box != null && box.size == s.V2 && box.center == s.C2) { box.size = s.V; box.center = s.C; n++; }
                            break;
                        case 2:
                            var sph = s.Col.TryCast<SphereCollider>();
                            if (sph != null && sph.radius == s.A2 && sph.center == s.C2) { sph.radius = s.A; sph.center = s.C; n++; }
                            break;
                        case 3:
                            s.Col.enabled = true; n++;
                            break;
                    }
                }
                catch { /* destroyed with its car */ }
            }
            if (n > 0 && s_rb != null)
            {
                try { s_rb.centerOfMass = s_com; s_rb.inertiaTensor = s_inertia; s_rb.inertiaTensorRotation = s_inertiaRot; } catch { /* gone */ }
            }
            int g = 0;
            foreach (var r in s_glitch) { try { if (r != null) { r.forceRenderingOff = false; g++; } } catch { /* gone */ } }
            if (n > 0 || g > 0) Plugin.Log.LogInfo($"[Bikes] car hitbox and glitch effect restored ({n} collider(s), {g} renderer(s); {why})");
            s_saved.Clear(); s_glitch.Clear(); s_detached.Clear(); s_rb = null; s_root = IntPtr.Zero; s_logged = false; s_nextDetached = 0f;
        }
    }
}
