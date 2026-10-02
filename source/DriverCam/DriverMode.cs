using Game.Runtime.Data;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using FollowTarget = Game.Runtime.Data.CameraModeSO.CameraFollowTarget;

namespace DriverCam;

/// <summary>
/// Builds and maintains the extra "Driver" CameraModeSO. The game drives it like any other mode
/// (cycling, settings, FOV); DriverView then overrides the final camera pose each frame.
/// </summary>
internal static class DriverMode
{
    public const string Id = "drivercam_driver";

    static CameraModeSO _mode;

    public static CameraModeSO Instance => _mode != null && _mode.WasCollected == false ? _mode : null;

    public static bool Is(CameraModeSO mode) => mode != null && Instance != null && mode.Pointer == Instance.Pointer;

    /// <summary>Creates the driver mode by cloning a built-in mode, preferring one that already follows the hood.</summary>
    public static CameraModeSO GetOrCreate(Il2CppArrayBase<CameraModeSO> builtIn)
    {
        if (Instance != null) return Instance;
        if (builtIn == null || builtIn.Length == 0) return null;

        CameraModeSO template = null;
        for (int i = 0; i < builtIn.Length; i++)
        {
            var m = builtIn[i];
            if (m == null) continue;
            if (Plugin.DumpModes.Value) LogMode(i, m);
            if (template == null && m.followTarget == FollowTarget.Hood) template = m;
        }
        template ??= builtIn[0];

        _mode = Keep.Hold(Object.Instantiate(template));
        _mode.name = "DriverCam";
        _mode.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Apply();
        Plugin.Logger.LogInfo($"Created Driver camera mode from template '{template.cameraModeName}'.");
        return _mode;
    }

    /// <summary>Pushes the current config values into the mode.</summary>
    public static void Apply()
    {
        var m = Instance;
        if (m == null) return;

        m.id = Id;
        m.cameraModeName = "Driver";
        m.followTarget = FollowTarget.Hood;
        m.lookAtPathRotation = false;
        m.hoodUseBodyRotationFactor = 1f;
        m.fovBySpeedRange = new Vector2(Plugin.Fov.Value, Plugin.Fov.Value + Plugin.FovSpeedBoost.Value);

        // A seat-mounted camera shouldn't sway, tilt or slide around the car
        m.noisePower = 0f;
        m.regularTiltAngle = 0f;
        m.driftingTiltAngle = 0f;
        m.regularTurningOffset = 0f;
        m.driftingTurningOffset = 0f;
        m.enableDriftingOffset = false;
        m.brakingCameraPositionOffset = Vector3.zero;

        var boost = m.boostingCameraParameters;
        var neutral = CameraModeSO.CameraParameterValues.Default();
        neutral.fovMultiplier = boost.fovMultiplier;
        m.boostingCameraParameters = neutral;
        m.driftingCameraParameters = CameraModeSO.CameraParameterValues.Default();
        m.slipstreamCameraParameters = CameraModeSO.CameraParameterValues.Default();
    }

    static void LogMode(int index, CameraModeSO m)
    {
        Plugin.Logger.LogInfo(
            $"Camera mode [{index}] '{m.cameraModeName}' id={m.id} follow={m.followTarget} " +
            $"offset={m.minPositionOffset}..{m.maxPositionOffset} pivot={m.minPivotRotation}..{m.maxPivotRotation} " +
            $"fov={m.fovBySpeedRange} posSmooth={m.positionSmoothness} rotSmooth={m.rotationSmoothness} " +
            $"hoodBodyRot={m.hoodUseBodyRotationFactor} bodyFollow={m.vehicleBodyFollowOffset} bodyLook={m.vehicleBodyLookOffset}");
    }
}

