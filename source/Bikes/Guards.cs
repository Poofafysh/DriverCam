using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Runtime.Data;
using Game.Runtime.Manager;
using Game.Runtime.Multiplayer;
using HarmonyLib;

namespace Bikes
{
    /// <summary>
    /// Harmony hooks (by hand) that keep the bikes from ever reaching the save, run snapshots, the Steam leaderboards or
    /// other players, and that hide the car under a bike body. All targets are resolved first and patched together: if
    /// one is missing or refuses its patch, every patch is undone and <see cref="Ok"/> stays false, so the bikes never
    /// enter the garage.
    /// - VehicleGarageManager.Awake prefix: adds the bikes to the vehicle list before the garage reads it.
    /// - Vehicle_SO.get_IsUnlocked postfix: a bike is always unlocked, though it's never in the save's unlocked list.
    /// - VehicleGarageManager.SaveData(manager) and SnapshotData(manager) constructor postfixes. Both are real functions
    ///   (IDA: called from the manager's save / snapshot getters), and they build a fresh unlocked array and a fresh
    ///   per-vehicle dictionary from the vehicle list (LINQ), not the manager's live ones. The postfixes:
    ///   - write a selected bike as its donor car;
    ///   - drop bike ids from the unlocked array and the dictionary.
    ///   With the mod removed, the save and a resumed run name only stock cars (RestoreSnapshot throws on an unknown id).
    /// - LeaderboardsManager.PublishEntry and SteamLeaderboardsManager.PublishEntry skip-only prefixes: no upload while on
    ///   a bike, or for a run that used a bike at any point (RodeBikeThisRun, saved in the config, cleared when a new run
    ///   starts). The entry would carry a vehicle id nobody else has, and a resumed run continues on the donor car.
    ///   Cooperates with PitStop's and Sandbox's prefixes.
    /// - NetworkPlayer.CMD_SetVehicleAndVynilIndex prefix: a list position past the stock cars is sent as the donor's.
    /// - VehicleSkinHolder.Awake, InitializeVehicleMeshes and SetVehiclePart postfixes: on a bike body ("Bikes." name),
    ///   the car's meshes and any part the game adds are hidden with Renderer.forceRenderingOff (enabled stays, for
    ///   DriverCam's body measurement and HideCarBody).
    /// </summary>
    internal static class Guards
    {
        internal static bool Ok { get; private set; }

        private struct Hook { public MethodBase Target; public string Name, Prefix, Postfix; }

        internal static int Install(Harmony h)
        {
            var gm = typeof(VehicleGarageManager);
            var hooks = new List<Hook>
            {
                // harmony-target: VehicleGarageManager.Awake
                new Hook { Name = "VehicleGarageManager.Awake", Target = AccessTools.Method(gm, "Awake"), Prefix = nameof(GarageAwake) },
                // harmony-target: Vehicle_SO.get_IsUnlocked
                new Hook { Name = "Vehicle_SO.get_IsUnlocked", Target = AccessTools.PropertyGetter(typeof(Vehicle_SO), "IsUnlocked"), Postfix = nameof(IsUnlocked) },
                // harmony-target: VehicleGarageManager.SaveData..ctor
                new Hook { Name = "VehicleGarageManager.SaveData..ctor", Target = AccessTools.Constructor(typeof(VehicleGarageManager.SaveData), new[] { gm }), Postfix = nameof(SaveBuilt) },
                // harmony-target: VehicleGarageManager.SnapshotData..ctor
                new Hook { Name = "VehicleGarageManager.SnapshotData..ctor", Target = AccessTools.Constructor(typeof(VehicleGarageManager.SnapshotData), new[] { gm }), Postfix = nameof(SnapshotBuilt) },
                // harmony-target: LeaderboardsManager.PublishEntry (cooperates with PitStop, Sandbox, RacingLine, CurbFeel, TrafficDensity)
                new Hook { Name = "LeaderboardsManager.PublishEntry", Target = AccessTools.Method(typeof(LeaderboardsManager), "PublishEntry", new[] { typeof(LeaderboardSO), typeof(int), typeof(LeaderboardDetails) }), Prefix = nameof(Publish) },
                // harmony-target: SteamLeaderboardsManager.PublishEntry (cooperates with Sandbox)
                new Hook { Name = "SteamLeaderboardsManager.PublishEntry", Target = AccessTools.Method(typeof(Game.Runtime.Steamworks.SteamLeaderboardsManager), "PublishEntry"), Prefix = nameof(Publish) },
                // harmony-target: NetworkPlayer.CMD_SetVehicleAndVynilIndex
                new Hook { Name = "NetworkPlayer.CMD_SetVehicleAndVynilIndex", Target = AccessTools.Method(typeof(NetworkPlayer), "CMD_SetVehicleAndVynilIndex"), Prefix = nameof(SendVehicle) },
                // harmony-target: VehicleSkinHolder.Awake
                new Hook { Name = "VehicleSkinHolder.Awake", Target = AccessTools.Method(typeof(VehicleSkinHolder), "Awake"), Postfix = nameof(SkinChanged) },
                // harmony-target: VehicleSkinHolder.InitializeVehicleMeshes
                new Hook { Name = "VehicleSkinHolder.InitializeVehicleMeshes", Target = AccessTools.Method(typeof(VehicleSkinHolder), "InitializeVehicleMeshes"), Postfix = nameof(SkinChanged) },
                // harmony-target: VehicleSkinHolder.SetVehiclePart
                new Hook { Name = "VehicleSkinHolder.SetVehiclePart", Target = AccessTools.Method(typeof(VehicleSkinHolder), "SetVehiclePart"), Postfix = nameof(SkinChanged) },
            };
            foreach (var k in hooks) if (k.Target == null) throw new MissingMethodException($"hook target missing: {k.Name}");
            try
            {
                foreach (var k in hooks)
                    h.Patch(k.Target, prefix: k.Prefix == null ? null : new HarmonyMethod(typeof(Guards), k.Prefix),
                                      postfix: k.Postfix == null ? null : new HarmonyMethod(typeof(Guards), k.Postfix));
            }
            catch
            {
                try { h.UnpatchSelf(); } catch { /* best effort */ }
                throw;
            }
            Ok = true;
            return hooks.Count;
        }

        private static void GarageAwake()
        {
            try { if (Runner.WantBikes()) Garage.Inject("garage opened"); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] adding the bikes failed: {e.Message}"); }
        }

        private static void IsUnlocked(Vehicle_SO __instance, ref bool __result)
        {
            if (!__result && Garage.IsBike(__instance)) __result = true;
        }

        private static void SaveBuilt(VehicleGarageManager.SaveData __instance)
        {
            try
            {
                var b = Garage.FindById(__instance.selectedVehicleId);
                if (b != null && b.Donor != null) __instance.selectedVehicleId = b.Donor.GetId();
                var un = __instance.unlockedVehicleIdArray;
                if (un != null)
                {
                    int keep = 0;
                    for (int i = 0; i < un.Length; i++) if (Garage.FindById(un[i]) == null) keep++;
                    if (keep != un.Length)
                    {
                        var next = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(keep);
                        int j = 0;
                        for (int i = 0; i < un.Length; i++) if (Garage.FindById(un[i]) == null) next[j++] = un[i];
                        __instance.unlockedVehicleIdArray = next;
                    }
                }
                var dict = __instance.vehicleSaveDataDict;   // a fresh dictionary built for this save, not the manager's
                if (dict != null) foreach (var bk in Garage.All) if (dict.ContainsKey(bk.Id)) dict.Remove(bk.Id);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] save clean-up failed: {e.Message}"); }
        }

        private static void SnapshotBuilt(VehicleGarageManager.SnapshotData __instance)
        {
            try
            {
                var b = Garage.FindById(__instance.selectedVehicleId);
                if (b != null && b.Donor != null) __instance.selectedVehicleId = b.Donor.GetId();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] run snapshot clean-up failed: {e.Message}"); }
        }

        private static bool _loggedSkip;

        private static bool Publish()
        {
            try
            {
                bool bike = Runner.PlayerOnBike();
                if (bike && !Plugin.RodeBikeThisRun.Value) Plugin.RodeBikeThisRun.Value = true;
                if (!bike && !Plugin.RodeBikeThisRun.Value) return true;
                if (!_loggedSkip) { _loggedSkip = true; Plugin.Log.LogInfo("[Bikes] leaderboard upload skipped: this run used a bike"); }
                return false;
            }
            catch { return false; }   // can't tell: keep it off the leaderboard
        }

        internal static void ResetSkipLog() => _loggedSkip = false;

        private static void SendVehicle(ref int vehicleIndex)
        {
            try
            {
                int stock = Garage.StockCount;
                if (stock <= 0 || vehicleIndex < stock) return;
                int idx = vehicleIndex - stock;
                Vehicle_SO donor = idx >= 0 && idx < Garage.Added.Count ? Garage.Added[idx].Donor : null;   // the list as injected
                int d = Garage.StockIndexOf(donor);
                Plugin.Log.LogInfo($"[Bikes] multiplayer: sending the donor car (list position {(d >= 0 ? d : 0)}) instead of a bike");
                vehicleIndex = d >= 0 ? d : 0;
            }
            catch { vehicleIndex = 0; }
        }

        private static void SkinChanged(VehicleSkinHolder __instance)
        {
            try
            {
                if (__instance == null || !__instance.gameObject.name.StartsWith("Bikes.", StringComparison.Ordinal)) return;
                Garage.HideCar(__instance.transform);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Bikes] hiding the car under a bike failed: {e.Message}"); }
        }
    }
}
