using Game.Runtime.Cameras;
using Game.Runtime.Data;
using Game.Runtime.Manager;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace DriverCam;

internal static class Patches
{
    /// <summary>Appends the Driver mode to the list the game cycles through and shows in settings.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SettingsManager_Gameplay), nameof(SettingsManager_Gameplay.GetAvailableCameraModes))]
    static void AddDriverMode(ref Il2CppReferenceArray<CameraModeSO> __result)
    {
        if (__result == null || !Plugin.AddToCycle.Value) return;

        foreach (var m in __result)
            if (DriverMode.Is(m)) return;

        var driver = DriverMode.GetOrCreate(__result);
        if (driver == null) return;

        var extended = new Il2CppReferenceArray<CameraModeSO>(__result.Length + 1);
        for (int i = 0; i < __result.Length; i++)
            extended[i] = __result[i];
        extended[__result.Length] = driver;
        __result = extended;
    }

    /// <summary>Fallback: make sure the list the Change Camera button cycles through includes Driver.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.cameraModes), MethodType.Getter)]
    static void AddDriverToManager(ref Il2CppReferenceArray<CameraModeSO> __result) => AddDriverMode(ref __result);

    // The controller may move the camera in any of its update callbacks; re-apply the driver pose after each
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CameraControllerInGame), nameof(CameraControllerInGame.LateUpdate))]
    static void AfterLateUpdate(CameraControllerInGame __instance) => DriverView.Apply(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CameraControllerInGame), nameof(CameraControllerInGame.Update))]
    static void AfterUpdate(CameraControllerInGame __instance) => DriverView.Apply(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CameraControllerInGame), nameof(CameraControllerInGame.FixedUpdate))]
    static void AfterFixedUpdate(CameraControllerInGame __instance) => DriverView.Apply(__instance);
}
