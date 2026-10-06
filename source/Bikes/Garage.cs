using System;
using System.Collections.Generic;
using Game.Runtime.Data;
using Game.Runtime.Data.Struct;
using Game.Runtime.Manager;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Bikes
{
    /// <summary>
    /// Adds the bikes to the game's vehicle list as extra garage vehicles (single-player).
    ///
    /// The list is GeneralReferencesData.Instance.VehicleContainer (a VehicleContainerSO, AContainerData&lt;Vehicle_SO&gt;):
    /// - its serialized itemArray feeds a cached runtime array and an id lookup;
    /// - the arrayInitialized / lookupInitialized flags say whether those caches are built.
    ///
    /// Each bike is an Object.Instantiate copy of a donor car's Vehicle_SO:
    /// - same parts / vinyl groups, trait and engine sounds (the game reads them, they must not be null);
    /// - its own id, unique int (9001+), name, stats and body prefab;
    /// - unlockedByDefault is off, so the bike never enters the save's unlocked list (achievement counts);
    ///   Guards' IsUnlocked postfix makes it selectable.
    ///
    /// Inject puts the copies at the end of itemArray and clears both flags, so the caches rebuild with them.
    /// Remove puts the stock array back (multiplayer, switched off, unload).
    ///
    /// The body prefab is a copy of the donor's VehicleSkinHolder prefab, kept under an inactive, never-destroyed holder
    /// object (copies made from it spawn active). Inside it the bike model is added under the body node as "Bikes.Lean" (frame + "Bikes.SteerF/WheelF" + "Bikes.WheelR"),
    ///   at real size, centred between the car's axles, wheels on the car's wheel bottoms.
    /// The car's four physics wheels and its collider stay as they are. The car's own meshes are hidden on every copy
    /// as it spawns (Guards' VehicleSkinHolder hooks call HideCar), with Renderer.forceRenderingOff so `enabled` stays.
    /// </summary>
    internal static class Garage
    {
        internal sealed class Bike
        {
            public string Key, Model, Title, Id;
            public float[] Rider;     // seat xyz, right grip xyz, right peg xyz, hip height above the seat, knee half-width (bike frame)
            public bool Car;          // a car model (four wheels on the donor's spin pivots, no lean)
            public float[] Eye;       // a car model: the driver's eye in its cabin, xyz (model frame, metres): the "Bikes.Eye" node (DriverCam)
            // a car model's cabin sockets for DriverCam (model frame, metres), each x y z, nx ny nz (the surface's normal toward
            // the driver), w h: "Bikes.MirrorC" / "Bikes.MirrorL" / "Bikes.MirrorR" (the mirror glass: centre, size) and
            // "Bikes.Cluster" (the instrument cluster: where DriverCam's digital readout goes, w = its width)
            public float[] MirrorC, MirrorL, MirrorR, Cluster;
            public int IntId;
            public float Speed, Accel, Handling, Durability;
            // > 0: Speed is worked out when built so the game's top speed shows this on the HUD (SpeedFactorFor); Speed is the fallback
            public float TopMph;
            public float TopScale = 1f;   // OriginalMaxSpeed x this (Guards.TopSpeed) when the stat range tops out below TopMph
            public Vehicle_SO So, Donor;
            public string Failed;
        }

        internal static readonly List<Bike> All = new List<Bike>
        {
            new Bike { Key = "S1000RR", Model = "BMW_S1000RR", Title = "BMW S1000RR", Id = "rogue.bikes.s1000rr", IntId = 9001,
                       Speed = 0.95f, Accel = 0.90f, Handling = 0.75f, Durability = 0.30f,
                       Rider = new[] { 0f, 0.82f, -0.33f, 0.32f, 0.86f, 0.38f, 0.17f, 0.36f, -0.53f, 0.10f, 0.20f } },   // seat and pegs 15 cm further back (0.2.5: the rider sat too far forward)
            new Bike { Key = "SportBike", Model = "SportBike", Title = "Sport Bike", Id = "rogue.bikes.sportbike", IntId = 9002,
                       Speed = 0.85f, Accel = 0.95f, Handling = 0.85f, Durability = 0.35f,
                       Rider = new[] { 0f, 0.90f, -0.35f, 0.33f, 0.92f, 0.40f, 0.18f, 0.38f, -0.55f, 0.10f, 0.20f } },   // seat and pegs 15 cm further back (0.2.5)
            new Bike { Key = "M2G87", Model = "BMW_M2_G87", Title = "M2 G87", Id = "rogue.bikes.m2g87", IntId = 9003,
                       // top speed 200 on the HUD (mph; 322 km/h), every other stat at the game's maximum
                       Speed = 1f, TopMph = 200f, Accel = 1f, Handling = 1f, Durability = 1f, Car = true,
                       // left-hand drive: steering wheel centre about (-0.375, 0.85, 0.19), seat cushion 0.42, headrest z -0.56
                       Eye = new[] { -0.37f, 1.12f, -0.40f },
                       // measured from BMW_M2_G87.csm (the mirrors' glass faces, the curved display's cluster half), 3-4 mm
                       // toward the driver so DriverCam's surfaces sit just in front of the model's own
                       MirrorC = new[] { -0.009f, 1.175f, 0.197f, -0.21f, 0.01f, -0.98f, 0.225f, 0.058f },
                       MirrorL = new[] { -0.919f, 0.991f, 0.297f, 0.21f, 0.09f, -0.97f, 0.150f, 0.090f },
                       MirrorR = new[] { 0.919f, 0.991f, 0.297f, -0.21f, 0.09f, -0.97f, 0.150f, 0.090f },
                       Cluster = new[] { -0.36f, 0.897f, 0.402f, 0.0f, 0.19f, -0.98f, 0.17f, 0.07f } },
        };

        internal static bool Injected { get; private set; }
        private static VehicleContainerSO s_container;
        private static Il2CppReferenceArray<Vehicle_SO> s_stock;
        private static GameObject s_holder;
        private static readonly List<UnityEngine.Object> s_owned = new List<UnityEngine.Object>();
        private static readonly string[] Skip = { "VFX", "Shield", "Glitch", "Slipstream", "Text", "TMP", "wind_Mesh", "Sphere", "Decal Projector", "SideScore", "Icon" };

        internal static bool IsBike(Vehicle_SO so)
        {
            if (so == null) return false;
            IntPtr p = so.Pointer;
            foreach (var b in All) if (b.So != null && b.So.Pointer == p) return true;
            return false;
        }

        /// <summary>The Bikes entry for this vehicle data (bike or car model), else null.</summary>
        internal static Bike Of(Vehicle_SO so)
        {
            if (so == null) return null;
            IntPtr p = so.Pointer;
            foreach (var b in All) if (b.So != null && b.So.Pointer == p) return b;
            return null;
        }

        /// <summary>The top-speed scale of a bike's vehicle data, 1 for anything else.</summary>
        internal static float TopSpeedScaleOf(Vehicle_SO so)
        {
            if (so == null) return 1f;
            IntPtr p = so.Pointer;
            foreach (var b in All) if (b.So != null && b.So.Pointer == p) return b.TopScale;
            return 1f;
        }

        internal static Bike FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var b in All) if (b.Id == id) return b;
            return null;
        }

        /// <summary>The donor's position in the stock list (multiplayer sends list positions), -1 if unknown.</summary>
        internal static int StockIndexOf(Vehicle_SO so)
        {
            if (s_stock == null || so == null) return -1;
            for (int i = 0; i < s_stock.Length; i++) if (s_stock[i] != null && s_stock[i].Pointer == so.Pointer) return i;
            return -1;
        }

        internal static int StockCount => s_stock == null ? -1 : s_stock.Length;

        /// <summary>The vehicles actually added, in list order (a vehicle that failed to build isn't in the list).</summary>
        internal static readonly List<Bike> Added = new List<Bike>();

        /// <summary>Adds the bikes to the vehicle list (once per list; safe to call again).</summary>
        internal static void Inject(string why)
        {
            var refs = GeneralReferencesData.Instance;
            if (refs == null) return;
            var c = refs.VehicleContainer;
            if (c == null) return;
            var arr = c.itemArray;
            if (arr == null || arr.Length == 0) return;
            if (Injected && s_container != null && s_container.Pointer == c.Pointer && Contains(arr)) return;

            // the stock list: what's there minus any bike a previous call left in it
            var stock = new List<Vehicle_SO>();
            for (int i = 0; i < arr.Length; i++) if (arr[i] != null && !IsBike(arr[i])) stock.Add(arr[i]);
            s_stock = new Il2CppReferenceArray<Vehicle_SO>(stock.Count);
            for (int i = 0; i < stock.Count; i++) s_stock[i] = stock[i];
            s_container = c;

            var add = new List<Vehicle_SO>();
            Added.Clear();
            foreach (var b in All)
            {
                if (b.So == null && b.Failed == null)
                {
                    try { Build(b, stock); }
                    catch (Exception e) { b.Failed = e.Message; Plugin.Log.LogWarning($"[Bikes] {b.Title}: not added ({e.Message})"); }
                }
                if (b.So != null) { add.Add(b.So); Added.Add(b); }
            }
            if (add.Count == 0) return;
            var next = new Il2CppReferenceArray<Vehicle_SO>(stock.Count + add.Count);
            for (int i = 0; i < stock.Count; i++) next[i] = stock[i];
            for (int i = 0; i < add.Count; i++) next[stock.Count + i] = add[i];
            c.itemArray = next;
            c.arrayInitialized = false;
            c.lookupInitialized = false;
            Injected = true;
            Plugin.Log.LogInfo($"[Bikes] {add.Count} bike(s) added to the garage after the {stock.Count} cars ({why})");
        }

        private static bool Contains(Il2CppArrayBase<Vehicle_SO> arr)
        {
            for (int i = 0; i < arr.Length; i++) if (IsBike(arr[i])) return true;
            return false;
        }

        private static bool _loggedDefer;

        /// <summary>
        /// Puts the stock vehicle list back (the bikes leave the garage). Waits while the player drives a bike or the
        /// garage has a bike selected (its list index would point past the stock list). Multiplayer sends the donor car
        /// in that case (Guards.SendVehicle).
        /// </summary>
        internal static void Remove(string why, bool force = false)
        {
            if (!Injected) return;
            if (!force && BikeInUse())
            {
                if (!_loggedDefer) { _loggedDefer = true; Plugin.Log.LogInfo($"[Bikes] bikes stay in the garage for now ({why}): a bike is selected or being driven"); }
                return;
            }
            _loggedDefer = false;
            Injected = false;
            try
            {
                if (s_container != null && s_stock != null)
                {
                    s_container.itemArray = s_stock;
                    s_container.arrayInitialized = false;
                    s_container.lookupInitialized = false;
                }
                Plugin.Log.LogInfo($"[Bikes] bikes taken out of the garage ({why})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] couldn't restore the vehicle list: {e.Message}"); }
        }

        private static VehicleGarageManager s_garage;

        /// <summary>A bike is driven, or is the garage's selected vehicle.</summary>
        private static bool BikeInUse()
        {
            try
            {
                if (Runner.PlayerOnBike()) return true;
                if (s_garage == null) s_garage = UnityEngine.Object.FindFirstObjectByType<VehicleGarageManager>();   // only when removing (rare)
                return s_garage != null && IsBike(s_garage.SelectedVehicle);
            }
            catch { return true; }
        }

        internal static void DestroyAll()
        {
            foreach (var o in s_owned) { try { if (o != null) UnityEngine.Object.Destroy(o); } catch { /* shutting down */ } }
            s_owned.Clear();
            foreach (var b in All) { b.So = null; b.Donor = null; }
            s_holder = null;
        }

        // ------------------------------------------------------------------ one bike

        private static void Build(Bike b, List<Vehicle_SO> stock)
        {
            var model = BikeModel.Get(b.Model);
            if (model == null) throw new InvalidOperationException($"model {b.Model}.csm missing or unreadable");
            string want = Plugin.Donor.Value;
            Vehicle_SO donor = null;
            foreach (var v in stock) if (v != null && string.Equals(v.VehicleName, want, StringComparison.OrdinalIgnoreCase)) { donor = v; break; }
            if (donor == null) { donor = stock[0]; Plugin.Log.LogWarning($"[Bikes] donor car '{want}' not found; using {donor.VehicleName}"); }
            if (donor.VehicleBody == null) throw new InvalidOperationException($"{donor.VehicleName} has no body prefab");

            var body = BuildBody(b, donor, model);
            var copy = UnityEngine.Object.Instantiate((UnityEngine.Object)donor);
            var so = copy == null ? null : copy.TryCast<Vehicle_SO>();
            if (so == null) throw new InvalidOperationException("copying the donor's vehicle data failed");
            so.name = "Bikes." + b.Key;
            so.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_owned.Add(so);
            so.SetId(b.Id);
            so.UniqueInt = new UniqueInt(b.IntId);
            so.unlockedByDefault = false;
            so.vehicleName = b.Title;
            float speed = b.TopMph > 0f ? SpeedFactorFor(b, out b.TopScale) : b.Speed;
            var st = so.baseStats;
            st.maxSpeedFactor = speed; st.accelerationFactor = b.Accel; st.handlingFactor = b.Handling; st.durabilityFactor = b.Durability;
            so.baseStats = st;
            so.vehicleBody = body;
            b.So = so; b.Donor = donor;
            Plugin.Log.LogInfo($"[Bikes] {b.Title} built on the {donor.VehicleName} (id {b.Id}, stats {speed:0.000}/{b.Accel:0.00}/{b.Handling:0.00}/{b.Durability:0.00})");
        }

        // The HUD speedometer (VehicleVisuals.Update, IDA 0x76F370) shows floor(VehicleMovement.CurrentSpeed x
        // GetMultiplierNonLogical): m/s x 2.237 x 1.1 in mph, m/s x 3.6 x 1.1 in km/h. With no boost the speed settles on
        // VehicleMovement.OriginalMaxSpeed (UpdateSpeed's cap), set by LoadVehicleAttributes to VehicleStats.MaxSpeed =
        // VehicleStatsRange.GetMaxSpeed(clamp01(base + extra factor)) / 3.6; the range is VehicleContainerSO.StatsRange (stat
        // km/h; the shipped one is 130-200). GetMaxSpeed clamps the factor to 0..1, so a factor above 1 gains nothing.
        private const float MphPerMps = 2.237f * 1.1f, KphPerMps = 3.6f * 1.1f;

        /// <summary>
        /// The base speed factor for b.TopMph on the HUD, aiming at TopMph + 0.5 (the middle of the floored reading):
        /// bisection on the game's own GetMaxSpeed, or the linear formula on the serialized range if that call fails. If
        /// the range can't reach it, the factor is 1 and `scale` (> 1) is what OriginalMaxSpeed needs (Guards.TopSpeed).
        /// </summary>
        internal static float SpeedFactorFor(Bike b, out float scale)
        {
            scale = 1f;
            float wantKph = (b.TopMph + 0.5f) / MphPerMps * 3.6f;
            VehicleStatsRange r = null;
            try
            {
                var refs = GeneralReferencesData.Instance;
                var c = refs == null ? null : refs.VehicleContainer;
                if (c != null) r = c.StatsRange;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] {b.Title}: no stats range ({e.Message})"); }
            if (r == null) { Plugin.Log.LogWarning($"[Bikes] {b.Title}: no stats range; speed factor {b.Speed:0.000}, top speed not set"); return b.Speed; }
            float f, topKph, kph; string how;
            try
            {
                topKph = r.GetMaxSpeed(1f);
                if (topKph < wantKph) f = 1f;
                else if (r.GetMaxSpeed(0f) >= wantKph) f = 0f;
                else
                {
                    float lo = 0f, hi = 1f;
                    for (int i = 0; i < 40; i++) { float m = 0.5f * (lo + hi); if (r.GetMaxSpeed(m) < wantKph) lo = m; else hi = m; }
                    f = hi;
                }
                kph = r.GetMaxSpeed(f);
                how = "the game's GetMaxSpeed";
            }
            catch (Exception e)
            {
                var v = r.maxSpeedStatRange;   // GetMaxSpeed = max(50, x + (y - x) x clamp01(f))
                topKph = Mathf.Max(50f, v.y);
                f = v.y > v.x ? Mathf.Clamp01((wantKph - v.x) / (v.y - v.x)) : 1f;
                kph = Mathf.Max(50f, v.x + (v.y - v.x) * f);
                how = $"the linear formula ({e.Message})";
            }
            if (kph < wantKph && kph > 0f) { scale = wantKph / kph; kph = wantKph; }
            float mps = kph / 3.6f;
            var rv = r.maxSpeedStatRange;
            Plugin.Log.LogInfo($"[Bikes] {b.Title}: stat range {rv.x:0}-{rv.y:0} km/h; speed factor {f:0.0000} ({how})" +
                               (scale != 1f ? $", the range tops out at {topKph:0.0} so top speed x{scale:0.0000}" : "") +
                               $" -> {Mathf.Floor(mps * MphPerMps)} mph / {Mathf.Floor(mps * KphPerMps)} km/h on the HUD (target {b.TopMph:0} mph)");
            return f;
        }

        private static VehicleSkinHolder BuildBody(Bike b, Vehicle_SO donor, BikeModel model)
        {
            if (s_holder == null)
            {
                s_holder = new GameObject("Bikes.Templates");
                s_holder.SetActive(false);   // children keep activeSelf, so copies spawn active
                UnityEngine.Object.DontDestroyOnLoad(s_holder);
                s_holder.hideFlags = HideFlags.HideAndDontSave;
                s_owned.Add(s_holder);
            }
            var go = UnityEngine.Object.Instantiate(donor.VehicleBody.gameObject, s_holder.transform);
            go.name = "Bikes." + b.Key + "_Body";
            var holder = go.GetComponent<VehicleSkinHolder>();
            if (holder == null) throw new InvalidOperationException("the donor body has no VehicleSkinHolder");

            Transform bodyNode = null;
            var spin = new Dictionary<string, Transform>();
            var all = go.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.gameObject.name;
                if (n.EndsWith("(Spin Pivot)", StringComparison.Ordinal))
                    foreach (var k in new[] { "FL", "FR", "RL", "RR" }) if (n.StartsWith(k + " ", StringComparison.Ordinal) && !spin.ContainsKey(k)) spin[k] = t;
                if (bodyNode == null && n == "Body" && t.parent != null && t.parent.gameObject.name == "Content") bodyNode = t;
            }
            if (bodyNode == null || spin.Count != 4) throw new InvalidOperationException($"the donor body has no Content/Body node or 4 spin pivots ({spin.Count})");

            int carMeshes = CountCar(go.transform);

            Vector3 gF = (bodyNode.InverseTransformPoint(spin["FL"].position) + bodyNode.InverseTransformPoint(spin["FR"].position)) * 0.5f;
            Vector3 gR = (bodyNode.InverseTransformPoint(spin["RL"].position) + bodyNode.InverseTransformPoint(spin["RR"].position)) * 0.5f;
            var turn = gF.z < gR.z ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            float scale = bodyNode.lossyScale.x;
            if (!(scale > 1e-4f)) scale = 1f;
            float carR = WheelRadius(spin["FL"], bodyNode);

            if (b.Car) return FinishCar(b, holder, model, bodyNode, spin, gF, gR, turn, carMeshes);

            var lean = new GameObject("Bikes.Lean");
            lean.transform.SetParent(bodyNode, false);
            lean.transform.localPosition = new Vector3((gF.x + gR.x) * 0.5f, (gF.y + gR.y) * 0.5f - carR, (gF.z + gR.z) * 0.5f);
            lean.transform.localRotation = turn;
            lean.transform.localScale = Vector3.one / scale;   // real size whatever the car prefab's scale
            AddRenderer(new GameObject("Bikes.Frame"), lean.transform, Vector3.zero, model.Body, model.BodyMats);
            var steer = new GameObject("Bikes.SteerF");
            steer.transform.SetParent(lean.transform, false);
            steer.transform.localPosition = model.PivotF;
            AddRenderer(new GameObject("Bikes.WheelF"), steer.transform, Vector3.zero, model.WheelF, model.WheelFMats);
            AddRenderer(new GameObject("Bikes.WheelR"), lean.transform, model.PivotR, model.WheelR, model.WheelRMats);
            // the handlebar pivot (no mesh: the bars are part of the frame mesh): turned with the steering, so a rider's
            // hands (Driver plugin) can follow the bars
            var bars = new GameObject("Bikes.Bars");
            bars.transform.SetParent(lean.transform, false);
            bars.transform.localPosition = b.Rider != null ? new Vector3(0f, b.Rider[4], b.Rider[5]) : model.PivotF;
            Plugin.Log.LogInfo($"[Bikes] {b.Title} body: {carMeshes} car meshes to hide, bike at real size (car wheelbase {Math.Abs(gF.z - gR.z) / 1f:0.00} body units, wheel radius {carR:0.00})");
            return holder;
        }

        /// <summary>
        /// A car model, CarSkins-style. It's scaled so its wheelbase matches the donor's and placed with its front axle on
        /// the donor's front axle. The body sits under the body node as "Bikes.Car", with an empty "Bikes.Eye" child at the
        /// driver's eye in the model's cabin, and empty cabin sockets "Bikes.MirrorC/L/R" and "Bikes.Cluster" (Socket; DriverCam's
        /// mirror surfaces and digital readout). Each wheel goes under the donor's spin
        /// pivot (it spins and steers with it), turned to the body's frame and scaled like the body.
        /// </summary>
        private static VehicleSkinHolder FinishCar(Bike b, VehicleSkinHolder holder, BikeModel model, Transform bodyNode,
                                                   Dictionary<string, Transform> spin, Vector3 gF, Vector3 gR, Quaternion turn, int carMeshes)
        {
            float gWb = Math.Abs(gF.z - gR.z), mWb = model.PivotF.z - model.PivotR.z;
            if (!(gWb > 0.5f) || !(mWb > 0.5f)) throw new InvalidOperationException($"odd wheelbase (donor {gWb:0.00}, model {mWb:0.00})");
            float s = gWb / mWb;
            var root = new GameObject("Bikes.Car");
            root.transform.SetParent(bodyNode, false);
            root.transform.localRotation = turn;
            root.transform.localScale = new Vector3(s, s, s);
            root.transform.localPosition = gF - turn * (model.PivotF * s);
            AddRenderer(new GameObject("Bikes.Frame"), root.transform, Vector3.zero, model.Body, model.BodyMats);
            if (b.Eye != null)
            {
                // the driver's eye in the model's own cabin (no mesh), so DriverCam's driver view sits in this cabin
                var eye = new GameObject("Bikes.Eye");
                eye.transform.SetParent(root.transform, false);
                eye.transform.localPosition = new Vector3(b.Eye[0], b.Eye[1], b.Eye[2]);
            }
            Socket(root.transform, "Bikes.MirrorC", b.MirrorC);
            Socket(root.transform, "Bikes.MirrorL", b.MirrorL);
            Socket(root.transform, "Bikes.MirrorR", b.MirrorR);
            Socket(root.transform, "Bikes.Cluster", b.Cluster);
            if (model.SteeringWheel != null)
            {
                // 0.2.3: the steering wheel on its own pivot at the rim centre, +z along the column toward the dash (rest pose);
                // DriverCam turns this node about its z with the steering. The mesh (written around the pivot in the model's
                // axes) sits under it with the inverse rest rotation, so it looks as modelled until the node turns.
                var up = Vector3.ProjectOnPlane(Vector3.up, model.SteeringAxis);
                var rest = Quaternion.LookRotation(model.SteeringAxis, up.sqrMagnitude > 1e-4f ? up : Vector3.up);
                var sw = new GameObject("Bikes.SteeringWheel");
                sw.transform.SetParent(root.transform, false);
                sw.transform.localPosition = model.SteeringPivot;
                sw.transform.localRotation = rest;
                var mesh = new GameObject("Bikes.SteeringWheelMesh");
                AddRenderer(mesh, sw.transform, Vector3.zero, model.SteeringWheel, model.SteeringWheelMats);
                mesh.transform.localRotation = Quaternion.Inverse(rest);
            }
            float world = s * bodyNode.lossyScale.x;
            foreach (var k in new[] { "FL", "FR", "RL", "RR" })
            {
                var p = spin[k];
                var w = model.Wheels[k];
                var go = new GameObject("Bikes.Wheel" + k);
                go.transform.SetParent(p, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = turn;   // the pivots are identity at rest: the pivot's own spin and steer turn it
                float ps = p.lossyScale.x;
                float ls = ps > 1e-4f ? world / ps : world;
                go.transform.localScale = new Vector3(ls, ls, ls);
                go.AddComponent<MeshFilter>().sharedMesh = w.mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = w.mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            int layer = MatchLayer(holder.transform);
            Plugin.Log.LogInfo($"[Bikes] {b.Title} body: {carMeshes} car meshes to hide, car model at scale {s:0.000} (wheelbase {gWb:0.00} vs {mWb:0.00}); " +
                               $"steering wheel {(model.SteeringWheel != null ? "turns" : "part of the body (static)")}; on the car's layer {layer}");
            return holder;
        }

        /// <summary>
        /// An empty cabin socket under the car model (no mesh): at the surface's centre, its +z away from the driver (into
        /// the surface), +y up along it, and its localScale (w, h, 1) = the surface's size in model metres. DriverCam reads
        /// the name, pose and scale; nothing else does.
        /// </summary>
        private static void Socket(Transform root, string name, float[] s)
        {
            if (s == null || s.Length < 8) return;
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(s[0], s[1], s[2]);
            var fwd = new Vector3(-s[3], -s[4], -s[5]);
            go.transform.localRotation = Quaternion.LookRotation(fwd, Vector3.up);
            go.transform.localScale = new Vector3(s[6], s[7], 1f);
        }

        /// <summary>
        /// 0.2.3: a car model (Bikes.Car and its wheels) or a bike (Bikes.Lean) on the layer of the donor's own body mesh (the first car mesh). The
        /// game's velocity motion blur (its URP renderer feature MotionBlurVelocityFeature) draws a mask of the vehicle's
        /// layer(s) and skips those pixels; a car model left on the Default layer was blurred along the car's speed, which
        /// in the driver view smeared its cabin across the screen. DriverCam's cockpit and the Driver plugin use the body's layer too.
        /// </summary>
        /// Called on the template and again when a car model body is first driven (Runner.Find), in case the game moves the
        /// player's car to another layer. Returns the layer (-1: no car model or no car mesh under this body).
        internal static int MatchLayer(Transform skin)
        {
            if (skin == null) return -1;
            var rs = skin.GetComponentsInChildren<Renderer>(true);
            int layer = -1;
            for (int i = 0; i < rs.Length && layer < 0; i++)
                if (rs[i] != null && IsCarMesh(rs[i], skin)) layer = rs[i].gameObject.layer;
            if (layer < 0) return -1;
            var all = skin.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.gameObject.name;
                if (n == "Bikes.Car" || n == "Bikes.Lean") SetLayer(t, layer);   // a car model / a bike (and anything parented under it)
                else if (n.StartsWith("Bikes.Wheel", StringComparison.Ordinal) && n.Length == 13) t.gameObject.layer = layer;   // Bikes.WheelFL..RR
            }
            return layer;
        }

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }

        private static void AddRenderer(GameObject go, Transform parent, Vector3 pos, Mesh mesh, Material[] mats)
        {
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        /// <summary>The donor's front wheel radius in body-node units (its mesh bounds), 0.35 if it can't be read.</summary>
        private static float WheelRadius(Transform pivot, Transform bodyNode)
        {
            var mfs = pivot.GetComponentsInChildren<MeshFilter>(true);
            float best = 0f;
            for (int i = 0; i < mfs.Length; i++)
            {
                var mf = mfs[i];
                if (mf == null || mf.sharedMesh == null) continue;
                float r = mf.sharedMesh.bounds.extents.y * mf.transform.lossyScale.y / Math.Max(1e-4f, bodyNode.lossyScale.y);
                if (r > best) best = r;
            }
            return best > 0.1f && best < 1.5f ? best : 0.35f;
        }

        private static int CountCar(Transform root)
        {
            int n = 0;
            var rs = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++) if (rs[i] != null && IsCarMesh(rs[i], root)) n++;
            return n;
        }

        /// <summary>Hides the car's meshes (and parts the game added) under a bike body with Renderer.forceRenderingOff; `enabled` stays as the game set it.</summary>
        internal static void HideCar(Transform root)
        {
            if (root == null) return;
            var rs = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || r.forceRenderingOff || !IsCarMesh(r, root)) continue;
                r.forceRenderingOff = true;
            }
        }

        /// <summary>A mesh under Content/Body or Content/Wheels that isn't ours, an effect, a shield, text or an icon.</summary>
        internal static bool IsCarMesh(Renderer r, Transform root)
        {
            if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) return false;
            bool car = false;
            for (var cur = r.transform; cur != null && cur != root; cur = cur.parent)
            {
                string n = cur.gameObject.name;
                if (n.StartsWith("Bikes.", StringComparison.Ordinal)) return false;
                foreach (var s in Skip) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if ((n == "Body" || n == "Wheels") && cur.parent != null && cur.parent.gameObject.name == "Content") car = true;
            }
            return car;
        }
    }
}
