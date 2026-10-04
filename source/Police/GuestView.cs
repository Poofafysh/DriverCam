using System;
using System.Collections.Generic;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// A multiplayer guest's police and daredevil looks (0.8.0). The host drives the cars and sends which ones are
    /// patrols (and whether they chase) and which are rivals (with their boss); this draws the same looks on this game's
    /// copies of those cars, found by netId: the plugin's police car (or a boss car / the traffic look, by this game's
    /// Look settings) with a lightbar and marker for patrols, the boss car for rivals. The copy's own model is hidden
    /// (Renderer.enabled of this game's local copy, never synced) and given back when the look goes. Nothing here
    /// writes to the car or to anything networked; the look only follows the copy's transform.
    /// </summary>
    internal sealed class GuestView
    {
        private sealed class Car
        {
            public uint NetId;
            public bool Rival;
            public byte Hint, Flags;
            public MonoBehaviour Nav;          // NetworkAIVehicle (this game's copy), untyped
            public Transform T;
            public Vector3 BoxC, BoxS;
            public PoliceCar Look;
            public GameObject Skin;
            public Lightbar Bar;
            public Marker Mark;
            public bool Built, LookFailed;
            public float Seen, NextFind;
            public float Speed, Dist;
            public Vector3 Roof;
            public bool HasRoof, Lit;
            public float CamDist2, RoofHeight = 1.6f;
        }

        private const float LightOnDist = 70f, LightOffDist = 80f, KeepSeconds = 0.75f;
        private const int MaxLit = 2;
        private readonly List<Car> _cars = new List<Car>();
        private readonly List<BossModel> _models = new List<BossModel>();
        private float _nextModelLoad;
        private int _failures;
        private bool _looksOff, _markersOff, _logged;

        internal int Count => _cars.Count;

        /// <summary>Takes the host's latest lists (every frame while linked): new cars get their look, ones gone lose it.</summary>
        internal void Sync(GuestNet net, float now, bool rivalsWanted)
        {
            Mark(net.Patrols, false, now);
            if (rivalsWanted) Mark(net.Rivals, true, now);
            for (int i = _cars.Count - 1; i >= 0; i--)
            {
                var c = _cars[i];
                if (now - c.Seen > KeepSeconds || (c.Rival && !rivalsWanted)) { Drop(c); _cars.RemoveAt(i); }
            }
        }

        private void Mark(List<NetCar> list, bool rival, float now)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                Car c = null;
                for (int j = 0; j < _cars.Count; j++) if (_cars[j].NetId == n.NetId && _cars[j].Rival == rival) { c = _cars[j]; break; }
                if (c == null)
                {
                    c = new Car { NetId = n.NetId, Rival = rival, Hint = n.Look };
                    _cars.Add(c);
                }
                c.Flags = n.Flags;
                c.Seen = now;
            }
        }

        /// <summary>Every frame: finds copies not found yet (twice a second), builds looks, places everything.</summary>
        internal void Frame(Vector3 camPos, bool haveCam, float youDist, float zoneRange, float now)
        {
            float dt = Time.deltaTime, t = Time.time;
            var flash = ((int)(Time.time * 4f) & 1) == 0 ? Lightbar.Look.FlashRed : Lightbar.Look.FlashBlue;
            for (int i = 0; i < _cars.Count; i++)
            {
                var c = _cars[i];
                if (c.Nav == null || c.T == null)
                {
                    if (c.Built) { Unbuild(c); }
                    if (now < c.NextFind) continue;
                    c.NextFind = now + 0.5f;
                    c.Nav = GameApi.FindNetCar(c.NetId);
                    if (c.Nav == null) continue;
                    c.T = c.Nav.transform;
                    GameApi.NetCarBox(c.Nav, out c.BoxC, out c.BoxS);
                }
                if (!c.Built) Build(c);
                GameApi.NetCarRead(c.Nav, out c.Dist, out c.Speed);
                if (c.Look != null)
                {
                    if (c.Skin == null) { c.Skin = GameApi.NetCarSkin(c.Nav); }
                    if (c.Skin != null) c.Look.HideTraffic(c.Skin);
                    c.Look.Place(c.T, c.BoxC, c.BoxS, c.Speed, dt);
                }
                if (c.Rival) continue;
                Vector3 pos = c.T.position, up = c.T.up;
                c.Roof = c.Look != null && c.Look.Placed ? c.Look.Roof : pos + up * c.RoofHeight;
                c.HasRoof = true;
                c.CamDist2 = haveCam ? (c.Roof - camPos).sqrMagnitude : float.MaxValue;
            }
            // real lights: the chasing patrols nearest the camera, as on the host
            for (int i = 0; i < _cars.Count; i++)
            {
                var c = _cars[i];
                bool chasing = (c.Flags & NetCar.FChasing) != 0;
                bool near = !c.Rival && chasing && c.HasRoof && haveCam && c.CamDist2 < (c.Lit ? LightOffDist * LightOffDist : LightOnDist * LightOnDist);
                if (near)
                {
                    int closer = 0;
                    for (int j = 0; j < _cars.Count; j++)
                    {
                        var q = _cars[j];
                        if (j != i && !q.Rival && (q.Flags & NetCar.FChasing) != 0 && q.HasRoof && (q.CamDist2 < c.CamDist2 || q.CamDist2 == c.CamDist2 && j < i)) closer++;
                    }
                    near = closer < MaxLit;
                }
                c.Lit = near;
            }
            for (int i = 0; i < _cars.Count; i++)
            {
                var c = _cars[i];
                if (c.Rival || c.T == null || !c.HasRoof) continue;
                bool chasing = (c.Flags & NetCar.FChasing) != 0;
                if (c.Bar != null)
                {
                    c.Bar.Show(chasing ? flash : Lightbar.Look.Idle, c.Lit);
                    c.Bar.Place(c.Roof, c.T.rotation, c.T.right, c.T.up, camPos);
                }
                if (c.Mark != null && haveCam)
                {
                    bool zone = !float.IsNaN(youDist) && Mathf.Abs(youDist - c.Dist) <= zoneRange;
                    c.Mark.Set(chasing ? Marker.State.Chase : zone ? Marker.State.Zone : Marker.State.Idle);
                    c.Mark.Place(c.Roof, camPos, t);
                }
            }
        }

        private void Build(Car c)
        {
            c.Built = true;
            string models = Plugin.CarModels.Value ?? "Police";
            bool wantLook = c.Rival ? Plugin.DareBossLooks.Value : !string.Equals(models, "Traffic", StringComparison.OrdinalIgnoreCase);
            if (wantLook && !_looksOff && !c.LookFailed)
            {
                try
                {
                    PoliceCar look = null;
                    if (c.Rival)
                    {
                        var m = c.Hint != 255 ? BossByRank(c.Hint) : null;
                        if (m != null) look = PoliceCar.Build(m, "Boss");
                    }
                    else
                    {
                        string livery = Plugin.Livery.Value ?? "Classic";
                        if (string.Equals(models, "Police", StringComparison.OrdinalIgnoreCase) && PoliceModels.EnsureLoaded() > 0) look = PoliceCar.Build(PoliceModels.Get(c.Hint), livery);
                        else
                        {
                            var m = BossAt(c.Hint);
                            if (m != null) look = PoliceCar.Build(m, livery);
                        }
                    }
                    if (look != null)
                    {
                        c.Look = look;
                        c.Skin = GameApi.NetCarSkin(c.Nav);
                        if (c.Skin != null) look.HideTraffic(c.Skin);
                    }
                }
                catch (Exception e)
                {
                    try { c.Look?.Destroy(); } catch { /* scene takes it */ }
                    c.Look = null; c.Skin = null; c.LookFailed = true;
                    if (++_failures >= 3) { _looksOff = true; Plugin.Log.LogWarning($"[Police] multiplayer guest: car looks switched off for this session after 3 failures (lightbars stay): {e.Message}"); }
                    else Plugin.Log.LogWarning($"[Police] multiplayer guest: a car look failed ({_failures}/3): {e.Message}");
                }
            }
            if (!c.Rival)
            {
                try { c.Bar = Lightbar.Create(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Police] multiplayer guest: lightbar failed for one patrol: {e.Message}"); }
                if (Plugin.Markers.Value && !_markersOff)
                {
                    try { c.Mark = Marker.Create(); if (c.Mark == null) _markersOff = true; }
                    catch (Exception e) { _markersOff = true; Plugin.Log.LogWarning($"[Police] multiplayer guest: 3D markers switched off for this session: {e.Message}"); }
                }
            }
            if (!_logged) { _logged = true; Plugin.Log.LogInfo($"[Police] multiplayer guest: first {(c.Rival ? "daredevil" : "patrol")} from the host drawn ({(c.Look != null ? c.Look.Name : "traffic look")})"); }
        }

        /// <summary>The game's boss list, loaded once it is available (same order on every machine: the game's data).</summary>
        private bool EnsureModels()
        {
            if (!GameApi.BossOk) return false;
            if (_models.Count > 0) return true;
            if (Time.unscaledTime < _nextModelLoad) return false;
            _nextModelLoad = Time.unscaledTime + 10f;
            GameApi.LoadBossModels(_models, w => Plugin.Log.LogWarning($"[Police] {w}"));
            return _models.Count > 0;
        }

        /// <summary>A boss car for a patrol look (CarModels = Boss, or no police model files): the host's cursor, modulo the list.</summary>
        private BossModel BossAt(int hint)
        {
            if (!EnsureModels()) return null;
            var m = _models[hint % _models.Count];
            if (m.Prefab == null) { _models.Clear(); return null; }
            return m;
        }

        /// <summary>A daredevil's boss car: the same rank the host gave it.</summary>
        private BossModel BossByRank(int rank)
        {
            if (!EnsureModels()) return null;
            for (int i = 0; i < _models.Count; i++)
            {
                if (_models[i].Prefab == null) { _models.Clear(); return null; }   // unloaded: reload next time
                if (_models[i].Rank == rank) return _models[i];
            }
            return null;
        }

        /// <summary>The copy went away (despawned on this game): our objects go, its model is given back.</summary>
        private static void Unbuild(Car c)
        {
            Drop(c);
            c.Built = false; c.Nav = null; c.T = null; c.HasRoof = false; c.LookFailed = false;
        }

        private static void Drop(Car c)
        {
            if (c.Look != null) { try { c.Look.Destroy(); } catch { /* scene */ } c.Look = null; c.Skin = null; }
            if (c.Bar != null) { try { c.Bar.Destroy(); } catch { /* scene */ } c.Bar = null; }
            if (c.Mark != null) { try { c.Mark.Destroy(); } catch { /* scene */ } c.Mark = null; }
        }

        /// <summary>Every look, lightbar and marker destroyed and every copy's model given back. Never throws.</summary>
        internal void DestroyAll()
        {
            foreach (var c in _cars) { try { Drop(c); } catch { /* scene */ } }
            _cars.Clear();
        }
    }
}
