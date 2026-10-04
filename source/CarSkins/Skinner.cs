using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CarSkins
{
    /// <summary>
    /// Puts the replacement model on the player's car and hides the game's own model (checked twice a second).
    ///
    /// - Body: one renderer parented under the node that holds the game's body mesh (the parent of "&lt;Car&gt;_Body", so it
    ///   moves with the body: suspension, roll, the drift turn). It is scaled uniformly so its wheelbase matches the game
    ///   car's (front and rear axle midpoints of the four wheel spin pivots), and placed so its front axle sits on the game's
    ///   front axle (height and centre line too).
    /// - Wheels: one renderer per wheel parented under the game's "FL/FR/RL/RR Wheel (Spin Pivot)" transforms, so they spin
    ///   and steer with the game's own wheels, turned to the body's frame once when placed and scaled like the body.
    /// - Hidden: the game's MeshRenderers / SkinnedMeshRenderers under the skin's Content/Body and Content/Wheels (never VFX,
    ///   shields, slipstream, text or icons), with Renderer.forceRenderingOff, so `enabled` stays as the game set it (DriverCam
    ///   measures and fits its cockpit to the same car; its own HideCarBody still toggles `enabled`). Re-checked twice a
    ///   second (a body kit change or a new part is hidden too).
    /// - Given back (renderers shown, our objects destroyed): another car, the setting off, multiplayer, no car, an error
    ///   (3 errors switch CarSkins off for the session), plugin unload. Police / daredevil boss looks copy the game's prefab
    ///   and are never touched.
    /// </summary>
    public class Skinner : MonoBehaviour
    {
        public Skinner(IntPtr ptr) : base(ptr) { }

        private static readonly string[] Skip = { "VFX", "Shield", "Glitch", "Slipstream", "Text", "TMP", "wind_Mesh", "Sphere", "Decal Projector", "SideScore", "Icon" };
        private static readonly string[] WheelKeys = { "FL", "FR", "RL", "RR" };

        private IntPtr _car, _failedCar;
        private Transform _skin;
        private GameObject _body;
        private readonly List<GameObject> _wheels = new List<GameObject>();
        private readonly HashSet<int> _ours = new HashSet<int>();
        private readonly List<Renderer> _hidden = new List<Renderer>();
        private readonly HashSet<int> _hiddenIds = new HashSet<int>();
        private readonly HashSet<int> _notCar = new HashSet<int>();   // renderers already judged not part of the car (effects...)
        private readonly List<Renderer> _ourRenderers = new List<Renderer>();
        private Renderer _gameBody;                                    // the game's body mesh: its `enabled` (DriverCam's HideCarBody) is mirrored
        private float _next;
        private int _errors;
        private bool _broken;

        private void Update()
        {
            if (_broken) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.5f;
            try { Tick(); }
            catch (Exception e)
            {
                try { Release("error"); } catch { /* scene gone */ }
                if (++_errors >= 3) { _broken = true; Plugin.Log.LogError($"[CarSkins] switched off for this session after repeated errors (the game's car is back): {e}"); }
                else Plugin.Log.LogWarning($"[CarSkins] error ({_errors}/3): {e.Message}");
            }
        }

        private void OnDestroy()
        {
            try { Release("plugin unloaded"); } catch { /* shutting down */ }
            CarModel.DestroyAll();
        }

        private void Tick()
        {
            if (!Plugin.Enabled.Value) { Release("switched off"); return; }
            if (GameApi.IsMultiplayer()) { Release("multiplayer"); return; }
            if (!GameApi.PlayerCar(out var car, out var name, out var skin)) { Release("no car"); return; }
            bool wanted = string.Equals(name, Plugin.Car.Value, StringComparison.OrdinalIgnoreCase);
            // _car (not _body, which dies with the scene) says whether a skin was applied
            if (_car != IntPtr.Zero && (car != _car || !wanted || _skin == null || _skin.Pointer != skin.Pointer || _body == null))
                Release(car != _car ? "another car" : "car changed");
            if (!wanted) return;
            if (_body == null)
            {
                if (car == _failedCar) return;   // this car couldn't take the skin: don't retry it twice a second
                if (!Apply(car, name, skin)) { _failedCar = car; return; }
            }
            Hide(skin);   // also re-hides anything the game switched back on, and new parts
        }

        private bool Apply(IntPtr car, string name, Transform skin)
        {
            var model = CarModel.Get(Plugin.Model.Value);
            if (model == null) return false;
            // the game's wheel spin pivots and the node holding its body mesh
            var pivots = new Dictionary<string, Transform>();
            Transform bodyNode = null;
            var all = skin.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.gameObject.name;
                if (n.EndsWith("(Spin Pivot)", StringComparison.Ordinal))
                    foreach (var k in WheelKeys) if (n.StartsWith(k + " ", StringComparison.Ordinal) && !pivots.ContainsKey(k)) pivots[k] = t;
                if (bodyNode == null && string.Equals(n, name + "_Body", StringComparison.OrdinalIgnoreCase) && t.parent != null
                    && t.parent.gameObject.name == "Body" && t.parent.parent != null && t.parent.parent.gameObject.name == "Content")
                { bodyNode = t.parent; _gameBody = t.GetComponent<Renderer>(); }
            }
            if (pivots.Count != 4 || bodyNode == null)
            {
                Plugin.Log.LogWarning($"[CarSkins] {name}: couldn't find its wheels ({pivots.Count}/4 spin pivots) or body node ({(bodyNode != null ? bodyNode.gameObject.name : "none")}); the game's car stays");
                return false;
            }
            // fit: wheelbase match, front axle on front axle
            Vector3 gF = (bodyNode.InverseTransformPoint(pivots["FL"].position) + bodyNode.InverseTransformPoint(pivots["FR"].position)) * 0.5f;
            Vector3 gR = (bodyNode.InverseTransformPoint(pivots["RL"].position) + bodyNode.InverseTransformPoint(pivots["RR"].position)) * 0.5f;
            Vector3 mF = (model.Wheels["FL"].pivot + model.Wheels["FR"].pivot) * 0.5f;
            Vector3 mR = (model.Wheels["RL"].pivot + model.Wheels["RR"].pivot) * 0.5f;
            float gWb = gF.z - gR.z, mWb = mF.z - mR.z;
            var turn = gWb < 0f ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;   // a body node facing -z: turn the model round
            gWb = Mathf.Abs(gWb);
            if (!(gWb > 0.5f) || !(mWb > 0.5f)) { Plugin.Log.LogWarning($"[CarSkins] {name}: odd wheelbase (game {gWb:0.00}, model {mWb:0.00}); the game's car stays"); return false; }
            float s = gWb / mWb;

            _body = new GameObject("CarSkins.Body");
            var bt = _body.transform;
            bt.SetParent(bodyNode, false);
            bt.localRotation = turn;
            bt.localScale = new Vector3(s, s, s);
            bt.localPosition = gF - turn * (mF * s);
            AddRenderer(_body, model.Body, model.BodyMats);

            float world = s * bodyNode.lossyScale.x;   // the model's world scale
            foreach (var k in WheelKeys)
            {
                var p = pivots[k];
                var go = new GameObject("CarSkins.Wheel" + k);
                var wt = go.transform;
                wt.SetParent(p, false);
                wt.localPosition = Vector3.zero;
                wt.localRotation = turn;   // the pivots and Content/Body are identity at rest: the pivot's own spin and steer turn it
                float ps = p.lossyScale.x;
                float ls = ps > 1e-4f ? world / ps : world;
                wt.localScale = new Vector3(ls, ls, ls);
                AddRenderer(go, model.Wheels[k].mesh, model.Wheels[k].mats);
                _wheels.Add(go);
            }
            _car = car; _skin = skin;
            Plugin.Log.LogInfo($"[CarSkins] {name} drawn as {model.Name}: scale {s:0.000} (wheelbase {gWb:0.00} m vs {mWb:0.00} m), body under '{bodyNode.gameObject.name}'");
            return true;
        }

        private void AddRenderer(GameObject go, Mesh mesh, Material[] mats)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
            _ours.Add(r.GetInstanceID());
            _ourRenderers.Add(r);
        }

        /// <summary>Hides the game's car meshes (body and wheels only), remembering which were drawn.</summary>
        private void Hide(Transform skin)
        {
            // DriverCam's HideCarBody switches the game body's `enabled` off: the skin follows it
            bool show = _gameBody == null || _gameBody.enabled;
            foreach (var o in _ourRenderers) if (o != null && o.enabled != show) o.enabled = show;
            var rs = skin.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null) continue;
                int id = r.GetInstanceID();
                if (_ours.Contains(id) || _notCar.Contains(id)) continue;
                if (_hiddenIds.Contains(id)) { if (!r.forceRenderingOff) r.forceRenderingOff = true; continue; }   // switched back on: off again
                if ((r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) || !IsCarMesh(r.transform, skin))
                { _notCar.Add(id); continue; }   // particles, effects, shields, icons: judged once
                r.forceRenderingOff = true;
                _hiddenIds.Add(id); _hidden.Add(r);
            }
        }

        /// <summary>Under Content/Body or Content/Wheels and not an effect, a shield, text or an icon.</summary>
        private static bool IsCarMesh(Transform t, Transform root)
        {
            bool body = false;
            for (var cur = t; cur != null && cur != root; cur = cur.parent)
            {
                string n = cur.gameObject.name;
                foreach (var s in Skip) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if ((n == "Body" || n == "Wheels") && cur.parent != null && cur.parent.gameObject.name == "Content") body = true;
            }
            return body;
        }

        private void Release(string why)
        {
            bool had = _body != null || _hidden.Count > 0;
            foreach (var r in _hidden) { try { if (r != null) r.forceRenderingOff = false; } catch { /* gone with the car */ } }
            _hidden.Clear(); _hiddenIds.Clear();
            if (_body != null) { try { Destroy(_body); } catch { /* gone */ } }
            foreach (var w in _wheels) { try { if (w != null) Destroy(w); } catch { /* gone */ } }
            _body = null; _wheels.Clear(); _ours.Clear(); _ourRenderers.Clear(); _notCar.Clear(); _gameBody = null; _skin = null; _car = IntPtr.Zero;
            if (why != "no car") _failedCar = IntPtr.Zero;
            if (had) Plugin.Log.LogInfo($"[CarSkins] the game's car is back ({why})");
        }
    }
}
