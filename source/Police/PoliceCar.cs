using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Police
{
    /// <summary>
    /// A police car look for one patrol, laid over the traffic car it rides on. The traffic car's own model is never drawn
    /// meanwhile (only our look is seen); physics, AI and the hit box stay the traffic car's. Two sources:
    /// - the plugin's own police car models (PoliceModel, built in Blender: Assets/models), the default;
    /// - a visual-only copy of a boss's car (BossModel) in a police livery.
    ///
    /// - Build: walks the boss prefab's MeshRenderers (prefab asset, never instantiated: no game script runs) and keeps
    ///   the visible car: renderers under Content/Body and Content/Wheels whose whole parent chain is active (the boss's
    ///   body kit overrides the saved flags), never VFX / shields / text. Each becomes a plain MeshFilter + MeshRenderer of
    ///   ours with the same mesh and pose relative to the car root. Wheels hang under their own spin pivot so we can turn
    ///   them. Paint materials (those with _Primary_Color: the car paint shader) are copied once per source material and
    ///   recoloured (Livery); everything else (glass, lights, rims, accessories) is shared as is.
    /// - Place (every LateUpdate): not parented to the pooled traffic car (same rule as the lightbar), moved onto it:
    ///   uniformly scaled to the traffic car's box length (0.7-1.15), centred on the box, wheels on the ground.
    /// - Hide / restore the traffic model: the renderers that are on when the look is made are switched off and
    ///   remembered, and switched back on when the patrol is let go. Re-checked every tick, so a game effect that
    ///   re-enables one is undone while the car is police. Once a second the whole traffic model is re-scanned: anything
    ///   the game switched on since is hidden too, but never switched back on by us (the game owns its state: e.g. the
    ///   pulse effect's clone renderer, which the game turns off again itself); the pulse clone is never restored.
    /// - Destroy: our objects only. Shared livery materials live until the plugin shuts down (DestroyShared).
    /// Unity members used are all in dump.cs: GetComponentsInChildren(bool), Transform pose/parent/InverseTransformPoint,
    /// MeshFilter.sharedMesh, Renderer.sharedMaterials/enabled/shadowCastingMode, Material(Material), HasProperty,
    /// SetColor(string), Mesh.bounds, GameObject.activeSelf/GetInstanceID, Object.Destroy.
    /// </summary>
    internal sealed class PoliceCar
    {
        private static readonly Dictionary<int, Material> s_livery = new Dictionary<int, Material>();
        private static readonly string[] Skip = { "VFX", "Shield", "Glitch", "Slipstream", "Text", "TMP", "wind_Mesh", "Sphere", "Decal Projector", "SideScore", "Icon" };

        private GameObject _root;
        private Transform _rootT;
        private readonly List<Transform> _spin = new List<Transform>();
        private readonly List<Quaternion> _spinBase = new List<Quaternion>();
        private readonly List<bool> _front = new List<bool>();   // front wheels steer (Place's steer angle)
        private float _wheelRadius = 0.35f, _spinAngle, _lastSteer;
        private Bounds _local;              // all kept parts, car-root space
        private float _roofLocal = 1.3f;    // top of the body (not the wing), car-root space
        private float _scale = 1f;

        // the traffic model we hid: _hidden = on when we took the car (given back), _late = switched on by the game later
        // (kept hidden while police, never switched back on by us); _hiddenIds covers both
        private readonly List<Renderer> _hidden = new List<Renderer>();
        private readonly List<Renderer> _late = new List<Renderer>();
        private readonly HashSet<int> _hiddenIds = new HashSet<int>();
        private int _hiddenSkin;
        private float _nextRescan;

        public string Name { get; private set; }
        /// <summary>Roof centre in world space after the last Place (for the lightbar), and whether it is valid.</summary>
        public Vector3 Roof { get; private set; }
        public bool Placed { get; private set; }

        /// <summary>Builds the look from one of the plugin's police car models. Throws on failure (the caller keeps the traffic look).</summary>
        internal static PoliceCar Build(PoliceModel model, string livery)
        {
            var car = new PoliceCar { Name = model.Name };
            try
            {
                car.Make(model, livery);
                return car;
            }
            catch
            {
                car.Destroy();
                throw;
            }
        }

        /// <summary>
        /// Shared meshes and materials (PoliceModels): one body renderer, one renderer per wheel under its spin pivot.
        /// Livery: Classic = black and white as modelled; Interceptor = the white panels black too. Boss = Classic here.
        /// </summary>
        private void Make(PoliceModel model, string livery)
        {
            if (model.Body == null) throw new InvalidOperationException("police model has no body");
            _root = new GameObject("Police.Car");
            _rootT = _root.transform;
            _rootT.position = new Vector3(0f, -1000f, 0f);

            var mats = (Material[])model.BodyMats.Clone();
            if (string.Equals(livery, "Interceptor", StringComparison.OrdinalIgnoreCase) && model.PaintBSlot >= 0)
            {
                var black = PoliceModels.InterceptorPaint(model);
                if (black != null) mats[model.PaintBSlot] = black;
            }
            Part(_rootT, "Body", model.Body, mats);
            foreach (var (pivot, mesh, wheelMats) in model.Wheels)
            {
                var pt = new GameObject("Wheel").transform;
                pt.SetParent(_rootT, false);
                pt.localPosition = pivot;
                _spin.Add(pt);
                _spinBase.Add(Quaternion.identity);
                Part(pt, "Tyre", mesh, wheelMats);
            }
            _local = model.Bounds;
            foreach (var w in _spin) _front.Add(w.localPosition.z > _local.center.z);
            _wheelRadius = model.WheelRadius;
            _roofLocal = model.Roof.y;
            _roofCentre = new Vector3(model.Roof.x, 0f, model.Roof.z);
            Plugin.Log.LogInfo($"[Police] police car built: {Name} ({_spin.Count} wheels, {_local.size.x:0.00} x {_local.size.y:0.00} x {_local.size.z:0.00} m, livery {livery})");
        }

        private static void Part(Transform parent, string name, Mesh mesh, Material[] mats)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        /// <summary>Builds the look from a boss model. Throws on failure (the caller keeps the traffic look).</summary>
        internal static PoliceCar Build(BossModel model, string livery)
        {
            var car = new PoliceCar { Name = $"{model.Car} ({model.Boss})" };
            try
            {
                car.Make(model, livery);
                return car;
            }
            catch
            {
                car.Destroy();
                throw;
            }
        }

        private void Make(BossModel model, string livery)
        {
            var prefab = model.Prefab;
            if (prefab == null) throw new InvalidOperationException("boss prefab is gone");
            var pT = prefab.transform;
            _root = new GameObject("Police.Car");
            _rootT = _root.transform;
            _rootT.position = new Vector3(0f, -1000f, 0f);

            var pivots = new Dictionary<int, Transform>();
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            bool haveBounds = false, haveBody = false;
            var bodyBounds = new Bounds();
            int kept = 0;
            for (int r = 0; r < renderers.Length; r++)
            {
                var mr = renderers[r];
                if (mr == null) continue;
                var t = mr.transform;
                if (!Keep(t, pT, model, out string path, out Transform spinPivot)) continue;
                var mf = mr.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                Transform parent = _rootT;
                Matrix4x4 rel;   // the mesh's pose relative to the root (or to its spin pivot)
                if (spinPivot != null)
                {
                    int pid = spinPivot.GetInstanceID();
                    if (!pivots.TryGetValue(pid, out var pivot))
                    {
                        var pgo = new GameObject("Wheel");
                        pivot = pgo.transform;
                        pivot.SetParent(_rootT, false);
                        pivot.localPosition = pT.InverseTransformPoint(spinPivot.position);
                        pivot.localRotation = Quaternion.Inverse(pT.rotation) * spinPivot.rotation;
                        pivots[pid] = pivot;
                        _spin.Add(pivot);
                        _spinBase.Add(pivot.localRotation);
                    }
                    parent = pivot;
                    rel = spinPivot.worldToLocalMatrix * t.localToWorldMatrix;
                }
                else rel = pT.worldToLocalMatrix * t.localToWorldMatrix;

                var go = new GameObject(mr.gameObject.name);
                var gt = go.transform;
                gt.SetParent(parent, false);
                gt.localPosition = rel.MultiplyPoint3x4(Vector3.zero);
                gt.localRotation = rel.rotation;
                gt.localScale = rel.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var nr = go.AddComponent<MeshRenderer>();
                nr.sharedMaterials = Paint(mr.sharedMaterials, livery);
                nr.shadowCastingMode = ShadowCastingMode.On;
                kept++;

                // bounds in root space
                var toRoot = pT.worldToLocalMatrix * t.localToWorldMatrix;
                var b = Transform(mesh.bounds, toRoot);
                if (!haveBounds) { _local = b; haveBounds = true; } else _local.Encapsulate(b);
                if (spinPivot != null) _wheelRadius = Mathf.Max(0.2f, Mathf.Min(0.6f, b.extents.y));
                bool body = path.IndexOf("/Body/", StringComparison.OrdinalIgnoreCase) >= 0 && path.IndexOf("ModdedParts", StringComparison.OrdinalIgnoreCase) < 0;
                if (body) { if (!haveBody) { bodyBounds = b; haveBody = true; } else bodyBounds.Encapsulate(b); }
            }
            if (kept == 0 || !haveBounds) throw new InvalidOperationException("no visible meshes in the boss prefab");
            foreach (var w in _spin) _front.Add(w.localPosition.z > _local.center.z);
            _roofLocal = haveBody ? bodyBounds.max.y : _local.max.y;
            _roofCentre = haveBody ? new Vector3(bodyBounds.center.x, 0f, bodyBounds.center.z) : new Vector3(_local.center.x, 0f, _local.center.z);
            Plugin.Log.LogInfo($"[Police] police car built from {Name}: {kept} parts, {_spin.Count} wheels, {_local.size.x:0.00} x {_local.size.y:0.00} x {_local.size.z:0.00} m, livery {livery}");
        }

        private Vector3 _roofCentre;

        /// <summary>Only the visible car: under Content/Body or Content/Wheels, active chain (with the boss kit overrides), no VFX.</summary>
        private static bool Keep(Transform t, Transform root, BossModel model, out string path, out Transform spinPivot)
        {
            spinPivot = null;
            var sb = new System.Text.StringBuilder();
            bool active = true;
            for (var cur = t; cur != null; cur = cur.parent)
            {
                var go = cur.gameObject;
                string n = go.name ?? "";
                foreach (var s in Skip) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) { path = null; return false; }
                int id = go.GetInstanceID();
                bool on = model.ForceOn.Contains(id) || (go.activeSelf && !model.ForceOff.Contains(id));
                if (!on) active = false;
                if (spinPivot == null && n.EndsWith("(Spin Pivot)", StringComparison.OrdinalIgnoreCase)) spinPivot = cur;
                sb.Insert(0, "/" + n);
                if (cur == root) break;
            }
            path = sb.ToString();
            if (!active) return false;
            return path.IndexOf("/Content/Body", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("/Content/Wheels", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Bounds Transform(Bounds b, Matrix4x4 m)
        {
            var c = b.center; var e = b.extents;
            var outB = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
                outB.Encapsulate(m.MultiplyPoint3x4(c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z)));
            return outB;
        }

        // ------------------------------------------------------------------ livery

        private static Material[] Paint(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> src, string livery)
        {
            var mats = new Material[src.Length];
            for (int i = 0; i < src.Length; i++) mats[i] = Livery(src[i], livery);
            return mats;
        }

        private static Material Livery(Material src, string livery)
        {
            if (src == null || string.Equals(livery, "Boss", StringComparison.OrdinalIgnoreCase)) return src;
            if (!src.HasProperty("_Primary_Color")) return src;   // not car paint: glass, rims, lights, accessories
            int id = src.GetInstanceID();
            if (s_livery.TryGetValue(id, out var m) && m != null) return m;
            m = new Material(src) { name = src.name + " (Police)" };
            bool interceptor = string.Equals(livery, "Interceptor", StringComparison.OrdinalIgnoreCase);
            var black = new Color(0.025f, 0.027f, 0.032f, 1f);
            var white = new Color(0.93f, 0.94f, 0.96f, 1f);
            var trim = new Color(0.06f, 0.12f, 0.35f, 1f);   // dark police blue
            Set(m, "_Primary_Color", black);
            Set(m, "_Secondary_Color", interceptor ? black : white);
            Set(m, "_Tertiary_Color", interceptor ? trim : black);
            s_livery[id] = m;
            return m;
        }

        private static void Set(Material m, string prop, Color c) { if (m.HasProperty(prop)) m.SetColor(prop, c); }

        /// <summary>Destroys the shared livery materials (plugin shutdown only: live police cars use them).</summary>
        internal static void DestroyShared()
        {
            foreach (var m in s_livery.Values) RogueShared.Fx.Kill(m);
            s_livery.Clear();
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>
        /// Moves the look onto the traffic car: carT = the car's transform, box = its BoxCollider (car space centre/size),
        /// speed in m/s (wheel spin), yaw = extra turn of the look about the box centre in degrees (+ = right: a daredevil's
        /// drift angle; the physics car is not turned), steer = front-wheel angle in degrees (+ = right).
        /// </summary>
        internal void Place(Transform carT, Vector3 boxCentre, Vector3 boxSize, float speed, float dt, float yaw = 0f, float steer = 0f)
        {
            if (_rootT == null || carT == null) { Placed = false; return; }
            float len = Mathf.Max(0.5f, _local.size.z), wid = Mathf.Max(0.5f, _local.size.x);
            float s = Mathf.Clamp(Mathf.Min(boxSize.z / len, boxSize.x * 1.15f / wid), 0.7f, 1.15f);
            if (boxSize.z < 1f) s = 1f;   // no usable box: natural size
            _scale = s;
            var rot = yaw != 0f ? carT.rotation * Quaternion.Euler(0f, yaw, 0f) : carT.rotation;
            // the car's root sits on the road: centre the model on the box (x/z), wheels on the ground (y)
            Vector3 anchor = carT.TransformPoint(new Vector3(boxCentre.x, 0f, boxCentre.z));
            Vector3 modelAnchor = new Vector3(_local.center.x, _local.min.y, _local.center.z) * s;
            _rootT.SetPositionAndRotation(anchor - rot * modelAnchor, rot);
            _rootT.localScale = new Vector3(s, s, s);
            Roof = _rootT.TransformPoint(new Vector3(_roofCentre.x, _roofLocal, _roofCentre.z));
            Placed = true;

            if (_spin.Count > 0 && (dt > 0f || steer != _lastSteer))
            {
                if (dt > 0f) _spinAngle = (_spinAngle + speed * dt / (_wheelRadius * s) * Mathf.Rad2Deg) % 360f;
                var q = Quaternion.Euler(_spinAngle, 0f, 0f);
                var turn = Quaternion.Euler(0f, steer, 0f);   // about the car's up axis, before the wheel's own pose
                for (int i = 0; i < _spin.Count; i++)
                    if (_spin[i] != null) _spin[i].localRotation = (i < _front.Count && _front[i] ? turn * _spinBase[i] : _spinBase[i]) * q;
                _lastSteer = steer;
            }
        }

        /// <summary>
        /// Hides the traffic car's own model (remembering exactly which renderers were on), so only our look is ever seen.
        /// Call every tick: re-hides anything switched back on, and once a second re-scans the whole model for renderers
        /// that were off before and got switched on since: they are hidden too, but never switched back on by us.
        /// </summary>
        internal void HideTraffic(GameObject skin)
        {
            if (skin == null) return;
            int id = skin.GetInstanceID();
            bool first = id != _hiddenSkin;   // the look's first scan, or the game swapped the skin: these are given back too
            if (first) { _hiddenSkin = id; _nextRescan = 0f; }
            for (int i = 0; i < _hidden.Count; i++) { var r = _hidden[i]; if (r != null && r.enabled) r.enabled = false; }
            for (int i = 0; i < _late.Count; i++) { var r = _late[i]; if (r != null && r.enabled) r.enabled = false; }
            float now = Time.unscaledTime;
            if (now < _nextRescan) return;
            _nextRescan = now + 1f;
            var pulse = GameApi.PulseRendererOf(skin);
            int pulseId = pulse != null ? pulse.GetInstanceID() : 0;
            var rs = skin.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled) continue;
                int rid = r.GetInstanceID();
                if (_hiddenIds.Add(rid)) { if (first && rid != pulseId) _hidden.Add(r); else _late.Add(r); }
                r.enabled = false;
            }
        }

        /// <summary>Gives the traffic car its model back (renderers we switched off). Safe after the scene went away.</summary>
        internal void RestoreTraffic()
        {
            for (int i = 0; i < _hidden.Count; i++)
            {
                try { var r = _hidden[i]; if (r != null) r.enabled = true; }
                catch { /* destroyed with the scene */ }
            }
            _hidden.Clear(); _late.Clear(); _hiddenIds.Clear(); _hiddenSkin = 0; _nextRescan = 0f;
        }

        internal void Destroy()
        {
            RestoreTraffic();
            RogueShared.Fx.Kill(_root);
            _root = null; _rootT = null; _spin.Clear(); _spinBase.Clear(); _front.Clear(); Placed = false;
        }
    }
}
