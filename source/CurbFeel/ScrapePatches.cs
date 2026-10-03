using System.Collections.Generic;
using Game.Runtime.Data;
using Game.Runtime.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CurbFeel
{
    /// <summary>
    /// C. Wall-contact damage and speed loss.
    /// Stock (VehicleDamage.OnCollision @ 0x18075B740 -> HandleBigCollision @ 0x18075A6A0), per wall hit, max one per 0.25 s:
    ///   speed loss = 8 % (or 25 % if the hit is more than headOnAngle=20 deg off parallel)
    ///   damage     = collisionDamageBySpeedFactor(speedFactor) x (grazeDamageMultiplier x overall) x (wallBaseDamage x overall) ~ 2.8 HP x curve
    /// plus CheckWallContinuousDamage: wallDamagePerSecond x overall (0.176 HP) every 0.5 s while pressed against a wall.
    /// </summary>
    [HarmonyPatch]
    internal static class ScrapePatches
    {
        private static readonly Dictionary<System.IntPtr, float> LastSoftScrape = new();
        private static VehicleBaseParameters[] _params;

        private static VehicleBaseParameters[] Params()
        {
            if (_params == null || _params.Length == 0 || _params[0] == null)
            {
                var found = Resources.FindObjectsOfTypeAll<VehicleBaseParameters>();
                _params = new VehicleBaseParameters[found.Length];
                for (int i = 0; i < found.Length; i++) _params[i] = found[i];
            }
            return _params;
        }

        private struct Saved
        {
            public bool Active;
            public float[] Graze, GrazeSpeed, HeadOnSpeed, WallPerSec, HeadOn;
        }

        private const int VehicleLayer = 10, RacerLayer = 27;

        /// <summary>Is the other collider a traffic car (or an AI racer when IncludeRacers)?</summary>
        private static bool IsTraffic(GameObject other)
        {
            int layer = other.layer;
            if (layer == VehicleLayer)
                return other.GetComponentInParent<AIVehicleController>(true) != null || other.GetComponentInParent<NetworkAIVehicle>(true) != null;
            if (layer == RacerLayer && Settings.IncludeRacers.Value)
                return other.GetComponentInParent<RacerVehicleManager>(true) != null;
            return false;
        }

        private static bool IsWall(VehicleBaseParameters p, int layer) => p != null && (p.guardrailsLayer.value & (1 << layer)) != 0;

        // ---------------------------------------------------------------- per-hit
        [HarmonyPatch(typeof(VehicleDamage), nameof(VehicleDamage.OnCollision))]
        [HarmonyPrefix]
        private static bool OnCollisionPrefix(VehicleDamage __instance, Collision __0, ref Saved __state)
        {
            __state = default;
            if (!Settings.Enabled.Value || __0 == null || __0.gameObject == null) return true;
            var ps = Params();
            if (ps.Length == 0) return true;

            // ---- E. traffic: soft side-swipes for lane splitting
            if (Settings.TrafficEnabled.Value && IsTraffic(__0.gameObject))
            {
                if (ImpactAngle(__0) <= Settings.SideSwipeAngle.Value)
                {
                    SideSwipe(__instance, ps[0]);
                    return false;                               // skip stock HandleVehicleCollision (damage, speed loss, drift reset)
                }
                Stats.TrafficHits++;
                Stats.Event("Traffic HIT (stock crash: damage + drift reset)");
                float m = Settings.TrafficHardHitDamageMult.Value;
                if (Mathf.Abs(m - 1f) > 1e-4f)
                {
                    __state.Active = true;
                    __state.Graze = new float[ps.Length]; __state.HeadOn = new float[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var p = ps[i]; if (p == null) continue;
                        __state.Graze[i] = p.grazeDamageMultiplier; __state.HeadOn[i] = p.headOnDamageMultiplier;
                        p.grazeDamageMultiplier *= m; p.headOnDamageMultiplier *= m;
                    }
                }
                return true;
            }

            // ---- C. walls
            if (!Settings.ScrapeEnabled.Value || !IsWall(ps[0], __0.gameObject.layer)) return true;

            float angle = ImpactAngle(__0);
            if (angle <= Settings.ShallowAngle.Value)
            {
                SoftScrape(__instance, ps[0]);
                return false;                                   // skip stock big-collision handling
            }

            // Hard hit: let the game handle it with scaled values, restored in the postfix.
            Stats.HardWallHits++;
            Stats.Event($"Wall HIT at {angle:0} deg (stock damage)");
            __state.Active = true;
            __state.Graze = new float[ps.Length]; __state.GrazeSpeed = new float[ps.Length]; __state.HeadOnSpeed = new float[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i]; if (p == null) continue;
                __state.Graze[i] = p.grazeDamageMultiplier;
                __state.GrazeSpeed[i] = p.grazingCollisionSpeedReductionFactor;
                __state.HeadOnSpeed[i] = p.headOnWallCollisionSpeedReductionFactor;
                p.grazeDamageMultiplier *= Settings.HardDamageMult.Value;
                p.grazingCollisionSpeedReductionFactor *= Settings.HardSpeedLossMult.Value;
                p.headOnWallCollisionSpeedReductionFactor *= Settings.HardSpeedLossMult.Value;
            }
            return true;
        }

        [HarmonyPatch(typeof(VehicleDamage), nameof(VehicleDamage.OnCollision))]
        [HarmonyPostfix]
        private static void OnCollisionPostfix(Saved __state)
        {
            if (!__state.Active) return;
            var ps = Params();
            for (int i = 0; i < ps.Length && i < __state.Graze.Length; i++)
            {
                var p = ps[i]; if (p == null) continue;
                p.grazeDamageMultiplier = __state.Graze[i];
                if (__state.GrazeSpeed != null) p.grazingCollisionSpeedReductionFactor = __state.GrazeSpeed[i];
                if (__state.HeadOnSpeed != null) p.headOnWallCollisionSpeedReductionFactor = __state.HeadOnSpeed[i];
                if (__state.HeadOn != null) p.headOnDamageMultiplier = __state.HeadOn[i];
            }
        }

        private static readonly Dictionary<System.IntPtr, float> LastSideSwipe = new();

        private static void SideSwipe(VehicleDamage dmg, VehicleBaseParameters p)
        {
            float now = Time.time;
            if (LastSideSwipe.TryGetValue(dmg.Pointer, out float last) && now - last < Settings.SideSwipeCooldown.Value) return;
            LastSideSwipe[dmg.Pointer] = now;
            Stats.SideSwipes++;
            Stats.Event("Traffic side-swipe (no damage, drift kept)");

            var move = dmg.vehicleMovement;
            if (move != null && Settings.SideSwipeSpeedLoss.Value > 0f)
                move.RequestReduceSpeed(Settings.SideSwipeSpeedLoss.Value, 0);

            if (Settings.SideSwipeDamageMult.Value > 0f && move != null && dmg.vehicleHealth != null)
            {
                float curve = p.collisionDamageBySpeedFactor != null ? p.collisionDamageBySpeedFactor.Evaluate(move.SpeedFactor) : 1f;
                float overall = p.overallDamageMultiplier;
                // stock traffic base damage (collisionInfoList, small traffic) is 13
                dmg.vehicleHealth.TakeDamage(curve * (p.grazeDamageMultiplier * overall) * 13f * Settings.SideSwipeDamageMult.Value);
            }
        }

        /// <summary>Degrees between the approach direction and the wall surface (0 = sliding along it, 90 = straight into it).</summary>
        private static float ImpactAngle(Collision col)
        {
            Vector3 v = col.relativeVelocity; v.y = 0f;
            float speed = v.magnitude;
            if (speed < 0.5f || col.contactCount == 0) return 0f;
            Vector3 n = Vector3.zero;
            int k = Mathf.Min(col.contactCount, 4);
            for (int i = 0; i < k; i++) n += col.GetContact(i).normal;
            n.y = 0f;
            if (n.sqrMagnitude < 1e-6f) return 0f;
            float s = Mathf.Abs(Vector3.Dot(v / speed, n.normalized));
            return Mathf.Asin(Mathf.Clamp01(s)) * Mathf.Rad2Deg;
        }

        private static void SoftScrape(VehicleDamage dmg, VehicleBaseParameters p)
        {
            float now = Time.time;
            if (LastSoftScrape.TryGetValue(dmg.Pointer, out float last) && now - last < Settings.ShallowCooldown.Value) return;
            LastSoftScrape[dmg.Pointer] = now;
            Stats.SoftScrapes++;
            Stats.Event("Soft wall scrape (no damage)");

            var move = dmg.vehicleMovement;
            if (move != null && Settings.ShallowSpeedLoss.Value > 0f)
                move.RequestReduceSpeed(Settings.ShallowSpeedLoss.Value, 0);

            if (Settings.ShallowDamageMult.Value > 0f && move != null)
            {
                var health = dmg.vehicleHealth;
                if (health != null)
                {
                    float curve = p.collisionDamageBySpeedFactor != null ? p.collisionDamageBySpeedFactor.Evaluate(move.SpeedFactor) : 1f;
                    float overall = p.overallDamageMultiplier;
                    float damage = curve * (p.grazeDamageMultiplier * overall) * (p.wallBaseDamage * overall) * Settings.ShallowDamageMult.Value;
                    health.TakeDamage(damage);
                }
            }
        }

        // ---------------------------------------------------------------- continuous contact
        [HarmonyPatch(typeof(VehicleDamage), nameof(VehicleDamage.CheckWallContinuousDamage))]
        [HarmonyPrefix]
        private static bool ContinuousPrefix(ref Saved __state)
        {
            __state = default;
            if (!Settings.Enabled.Value || !Settings.ScrapeEnabled.Value) return true;
            float m = Settings.ContinuousDamageMult.Value;
            if (m <= 0f) return false;
            var ps = Params();
            __state.Active = true;
            __state.WallPerSec = new float[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i] == null) continue;
                __state.WallPerSec[i] = ps[i].wallDamagePerSecond;
                ps[i].wallDamagePerSecond *= m;
            }
            return true;
        }

        [HarmonyPatch(typeof(VehicleDamage), nameof(VehicleDamage.CheckWallContinuousDamage))]
        [HarmonyPostfix]
        private static void ContinuousPostfix(Saved __state)
        {
            if (!__state.Active) return;
            var ps = Params();
            for (int i = 0; i < ps.Length && i < __state.WallPerSec.Length; i++)
                if (ps[i] != null) ps[i].wallDamagePerSecond = __state.WallPerSec[i];
        }
    }
}
