using System;
using System.Collections.Generic;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Bikes
{
    /// <summary>
    /// Tyre marks, smoke and impact effects made for the two tyres of a bike. The donor car's body (each bike drives on a
    /// copy of it) carries the game's car effects (VehicleSkinHolder: front / rear skid-mark trails at four wheel
    /// positions, drift / brake / launch smoke, wall-ride sparks), all placed for a car's track and driven by the game's
    /// drift state, which the bike handling no longer enters. On a bike body (once per body, in the Runner's Find):
    /// - the car's skid trails and drift / brake / launch smoke are hidden (forceRenderingOff on their renderers; they are
    ///   parts of the bike's own body copy, gone with it);
    /// - one copy of the game's skid trail sits under each tyre's contact patch (front 12 cm, rear 19 cm wide) and one copy
    ///   of the drift smoke behind the rear tyre; the Runner turns them on from the handling model each frame: front mark
    ///   when braking at the grip limit, rear mark and smoke in a rear slide, a burnout or heavy braking;
    /// - the wall-ride sparks move in to the bike's pegs (x +-0.35 m), so a scrape sparks where the bike touches.
    /// Without the handling model the bike's marks stay off (the car's stay hidden).
    /// </summary>
    internal sealed class BikeFx
    {
        private TrailRenderer _front, _rear;
        private ParticleSystem _smoke;
        private bool _smokeOn;
        private readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        /// <summary>Builds the bike's effects from the car's on this body; null if the body has no skin holder (the car's effects stay).</summary>
        internal static BikeFx Build(Transform skin, Transform lean, Vector3 pivotF, Vector3 pivotR)
        {
            var holder = skin == null ? null : skin.GetComponent<VehicleSkinHolder>();
            if (holder == null || lean == null) return null;
            var fx = new BikeFx();
            int hidden = 0;
            var front = holder.FrontSkidMarks; var rear = holder.RearSkidMarks;
            TrailRenderer frontSrc = null, rearSrc = null;
            if (front != null) for (int i = 0; i < front.Length; i++) if (front[i] != null) { if (frontSrc == null) frontSrc = front[i]; front[i].forceRenderingOff = true; hidden++; }
            if (rear != null) for (int i = 0; i < rear.Length; i++) if (rear[i] != null) { if (rearSrc == null) rearSrc = rear[i]; rear[i].forceRenderingOff = true; hidden++; }
            ParticleSystem smokeSrc = null;
            hidden += HideAll(holder.DriftSmokeParticlesLeft, ref smokeSrc);
            hidden += HideAll(holder.DriftSmokeParticlesRight, ref smokeSrc);
            ParticleSystem none = null;
            hidden += HideAll(holder.BreakSmokeParticles, ref none);
            hidden += HideAll(holder.LaunchSmokeParticles, ref none);

            // contact patches in the body node's frame: the lean pivot is on the ground midway between the axles
            Transform parent = lean.parent;
            Vector3 lp = lean.localPosition;
            Vector3 cF = lp + lean.localRotation * new Vector3(0f, 0.02f, pivotF.z);
            Vector3 cR = lp + lean.localRotation * new Vector3(0f, 0.02f, pivotR.z);
            if (frontSrc != null) fx._front = fx.Trail(frontSrc, parent, cF, 0.12f, "Bikes.SkidF");
            if (rearSrc != null) fx._rear = fx.Trail(rearSrc, parent, cR, 0.19f, "Bikes.SkidR");
            if (smokeSrc != null)
            {
                var go = UnityEngine.Object.Instantiate(smokeSrc.gameObject, parent, false);
                go.name = "Bikes.SmokeR";
                go.transform.localPosition = cR + new Vector3(0f, 0.15f, -0.25f);
                fx._smoke = go.GetComponent<ParticleSystem>();
                var r = go.GetComponent<ParticleSystemRenderer>();
                if (r != null) r.forceRenderingOff = false;
                if (fx._smoke != null) fx._smoke.Stop();
                fx._made.Add(go);
            }
            int sparks = Inward(holder.wallRideParticleLeft, -0.35f) + Inward(holder.wallRideParticleRight, 0.35f);
            Plugin.Log.LogInfo($"[Bikes] bike effects: {hidden} car effect(s) hidden; tyre marks {(fx._front != null ? "front" : "-")} / {(fx._rear != null ? "rear" : "-")}, " +
                               $"rear smoke {(fx._smoke != null ? "yes" : "no")}, {sparks} wall-ride spark(s) moved to the pegs");
            return fx;
        }

        private static int HideAll(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ParticleSystem> arr, ref ParticleSystem first)
        {
            if (arr == null) return 0;
            int n = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                var ps = arr[i];
                if (ps == null) continue;
                if (first == null) first = ps;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                if (r != null) { r.forceRenderingOff = true; n++; }
            }
            return n;
        }

        private static int Inward(ParticleSystem ps, float x)
        {
            if (ps == null) return 0;
            var t = ps.transform;
            var p = t.localPosition; p.x = x; t.localPosition = p;
            return 1;
        }

        private TrailRenderer Trail(TrailRenderer src, Transform parent, Vector3 at, float width, string name)
        {
            var go = UnityEngine.Object.Instantiate(src.gameObject, parent, false);
            go.name = name;
            go.transform.localPosition = at;
            var t = go.GetComponent<TrailRenderer>();
            if (t == null) { UnityEngine.Object.Destroy(go); return null; }
            t.forceRenderingOff = false;
            t.widthMultiplier = width;
            t.emitting = false;
            _made.Add(go);
            return t;
        }

        /// <summary>Per frame on a bike: the marks and smoke follow the handling model's slides and braking.</summary>
        internal void Update()
        {
            bool on = Handling.Active;
            bool frontMark = on && Handling.FrontSkid;
            bool rearMark = on && (Handling.RearSlide || (Handling.Braking && Handling.FrontSkid));
            bool smoke = on && Handling.RearSlide && Handling.Speed > 3f;
            if (_front != null && _front.emitting != frontMark) _front.emitting = frontMark;
            if (_rear != null && _rear.emitting != rearMark) _rear.emitting = rearMark;
            if (_smoke != null && smoke != _smokeOn)
            {
                _smokeOn = smoke;
                if (smoke) _smoke.Play(); else _smoke.Stop();
            }
        }

        internal void Destroy()
        {
            foreach (var o in _made) { try { if (o != null) UnityEngine.Object.Destroy(o); } catch { /* gone with the body */ } }
            _made.Clear(); _front = _rear = null; _smoke = null;
        }
    }
}
