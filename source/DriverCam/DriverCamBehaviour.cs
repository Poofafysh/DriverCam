using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Game.Runtime.Cameras;
using Game.Runtime.Data;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace DriverCam;

/// <summary>Hotkeys, the on-screen DriverCam panel, and restoring the game's camera/body when leaving driver view.</summary>
public class DriverCamBehaviour : MonoBehaviour
{
    public DriverCamBehaviour(IntPtr ptr) : base(ptr) { }

    CameraModeSO _previousMode;
    bool _wasActive;
    float _normalNearClip = 0.3f;
    Transform _hiddenBody;
    readonly List<Renderer> _hiddenRenderers = new();

    bool _panelOpen;
    bool _savedCursorVisible;
    CursorLockMode _savedLockState;
    bool _guiErrorLogged;

    void Update()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    void Tick()
    {
        var kb = Keyboard.current;
        var ctrl = CameraControllerInGame.Instance;

        if (_panelOpen)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        if (ctrl == null)
        {
            _wasActive = false;
            EditMode.Update(false);
            return;
        }

        EnsureInCameraCycle();

        // F6, or the controller's View button (the small two-squares button)
        var pad = Gamepad.current;
        bool viewButton = Plugin.ViewButtonToggles.Value && pad != null && pad.selectButton.wasPressedThisFrame;
        if ((kb != null && kb.f6Key.wasPressedThisFrame) || viewButton)
            Toggle(ctrl);

        bool active = DriverView.IsActive(ctrl);
        EditMode.Update(active);
        if (active)
        {
            if (kb != null) HandleNumpad(kb);
            UpdateBodyVisibility(ctrl);
        }
        else
        {
            if (_wasActive) LeaveDriverView(ctrl);
            var cam = ctrl.CurrentCamera;
            if (cam != null) _normalNearClip = cam.nearClipPlane;
        }
        _wasActive = active;
    }

    float _nextCycleCheck;
    CameraManager _checkedManager;

    /// <summary>
    /// The CameraManager keeps its own copy of the camera list for the Change Camera button (C / Y), so the
    /// Driver mode is appended there directly. Checked once a second because the manager is rebuilt per scene.
    /// </summary>
    void EnsureInCameraCycle()
    {
        if (!Plugin.AddToCycle.Value || Time.unscaledTime < _nextCycleCheck) return;
        _nextCycleCheck = Time.unscaledTime + 1f;

        var mgr = UnityEngine.Object.FindFirstObjectByType<CameraManager>();
        var modes = mgr?.cameraModes;
        if (modes == null || modes.Length == 0) return;
        if (_checkedManager != null && !_checkedManager.WasCollected && _checkedManager.Pointer == mgr.Pointer) return;

        if (!Contains(modes))
        {
            var driver = DriverMode.GetOrCreate(modes);
            if (driver == null) return;

            // The manager's list comes from the game's camera-mode data container; add Driver at the source
            foreach (var container in Resources.FindObjectsOfTypeAll<CameraModeContainerSO>())
            {
                container.itemArray = Append(container.itemArray, driver);
                if (container.runtimeItemArray != null) container.runtimeItemArray = Append(container.runtimeItemArray, driver);
            }
            modes = mgr.cameraModes;
        }

        var names = new List<string>();
        foreach (var m in modes) names.Add(m?.cameraModeName ?? "?");
        Plugin.Logger.LogInfo($"Change Camera (C / Y) cycles {names.Count} views: {string.Join(" -> ", names)}");
        _checkedManager = mgr;
    }

    static bool Contains(Il2CppArrayBase<CameraModeSO> modes)
    {
        foreach (var m in modes)
            if (DriverMode.Is(m)) return true;
        return false;
    }

    static Il2CppArrayBase<CameraModeSO> Append(Il2CppArrayBase<CameraModeSO> source, CameraModeSO driver)
    {
        if (source == null || Contains(source)) return source;
        var extended = new Il2CppReferenceArray<CameraModeSO>(source.Length + 1);
        for (int i = 0; i < source.Length; i++) extended[i] = source[i];
        extended[source.Length] = driver;
        return extended;
    }

    // ---------------------------------------------------------------- Change Camera (C / Y) cycling

    CameraModeSO _lastSeenMode;
    bool _cycleErrorLogged;

    void LateUpdate()
    {
        try
        {
            HudLayout.Update(DriverView.IsActive(CameraControllerInGame.Instance));
            CycleCameras();
        }
        catch (Exception e)
        {
            if (!_cycleErrorLogged) Plugin.Logger.LogError(e);
            _cycleErrorLogged = true;
        }
    }

    /// <summary>
    /// The game's own Change Camera handling doesn't reliably step onto the Driver view, so after the game has
    /// handled the press (CameraManager.Update), step to the next view of Chase -> Chase 2 -> Hood -> Driver ourselves.
    /// Uses the game's binding, so C, controller Y and any rebinding all work.
    /// </summary>
    void CycleCameras()
    {
        var ctrl = CameraControllerInGame.Instance;
        if (ctrl == null || !Plugin.AddToCycle.Value)
        {
            _lastSeenMode = null;
            return;
        }

        var current = ctrl.CurrentCameraMode;
        var mgr = _checkedManager != null && !_checkedManager.WasCollected ? _checkedManager : null;
        bool pressed = false;
        if (mgr != null)
        {
            var input = mgr.playerInput;
            pressed = input != null && input.GetChangeCameraKey();
        }

        var driver = DriverMode.Instance;
        if (mgr == null || driver == null)
        {
            _lastSeenMode = current;
            return;
        }

        // The game's own three views (Driver is not one of them as far as its saved index is concerned)
        var gameViews = new List<CameraModeSO>();
        foreach (var m in mgr.cameraModes)
            if (m != null && !DriverMode.Is(m)) gameViews.Add(m);
        bool IsGameView(CameraModeSO m) => m != null && gameViews.Exists(g => g.Pointer == m.Pointer);

        // 0.6.3 could save index 3, which the game can't hold; park it on the game's last view = Driver selected
        var settings = mgr.gameplaySettings;
        if (settings != null && gameViews.Count > 0 && settings.CameraModeIndex >= gameViews.Count)
        {
            settings.SetCameraMode(gameViews.Count - 1, true);
            Plugin.DriverSelected.Value = true;
        }

        if (pressed && _lastSeenMode != null)
        {
            if (DriverMode.Is(_lastSeenMode))
            {
                // Driver -> first game view. The game's saved index is still on its last view, so its own
                // handler has already stepped to the first one; just make sure we end up there.
                Plugin.DriverSelected.Value = false;
                var first = gameViews.Count > 0 ? gameViews[0] : null;
                if (first != null && (current == null || current.Pointer != first.Pointer)) ctrl.SetCameraMode(first);
                try { mgr.gameplaySettings?.SetCameraMode(0, true); } catch (Exception) { }
                current = first ?? current;
            }
            else if (gameViews.Count > 0 && _lastSeenMode.Pointer == gameViews[gameViews.Count - 1].Pointer)
            {
                // Last game view -> Driver. Leave the game's saved index on its last view (it can't store a 4th).
                Plugin.DriverSelected.Value = true;
                ctrl.SetCameraMode(driver);
                current = driver;
            }
            // other steps between the game's own views are handled by the game itself
            Plugin.Logger.LogInfo($"Change Camera: {_lastSeenMode.cameraModeName} -> {current?.cameraModeName}");
        }
        else if (Plugin.DriverSelected.Value && IsGameView(current))
        {
            // Driver view is the chosen camera: the game (re)applying its own saved view shouldn't override it
            ctrl.SetCameraMode(driver);
            current = driver;
        }
        _lastSeenMode = current;
    }

    void Toggle(CameraControllerInGame ctrl)
    {
        var current = ctrl.CurrentCameraMode;
        if (DriverMode.Is(current))
        {
            Plugin.DriverSelected.Value = false;
            if (_previousMode != null && !_previousMode.WasCollected)
                ctrl.SetCameraMode(_previousMode);
            Plugin.Logger.LogInfo("Driver view off.");
            return;
        }

        if (current == null) return;
        var driver = DriverMode.Instance ?? DriverMode.GetOrCreate(new Il2CppReferenceArray<CameraModeSO>(new[] { current }));
        if (driver == null) return;

        _previousMode = current;
        Plugin.DriverSelected.Value = true;
        ctrl.SetCameraMode(driver);
        Plugin.Logger.LogInfo($"Driver view on (was '{current.cameraModeName}').");
    }

    void LeaveDriverView(CameraControllerInGame ctrl)
    {
        var cam = ctrl.CurrentCamera;
        if (cam != null) cam.nearClipPlane = _normalNearClip;
        ShowBody();
        RestoreOutlines();
        DriverView.Reset();
    }

    void HandleNumpad(Keyboard kb)
    {
        float step = Plugin.TuneStep.Value;
        Nudge(kb.numpad6Key, kb.numpad4Key, Plugin.OffsetX, step);
        Nudge(kb.numpad9Key, kb.numpad3Key, Plugin.OffsetY, step);
        Nudge(kb.numpad8Key, kb.numpad2Key, Plugin.OffsetZ, step);
        Nudge(kb.numpad1Key, kb.numpad7Key, Plugin.Pitch, 1f);
        Nudge(kb.numpadPlusKey, kb.numpadMinusKey, Plugin.Fov, 2f);
        if (kb.numpad5Key.wasPressedThisFrame) Flip(Plugin.HideCarBody);
    }

    static void Nudge(KeyControl up, KeyControl down, ConfigEntry<float> entry, float step)
    {
        if (up.wasPressedThisFrame) Change(entry, step);
        else if (down.wasPressedThisFrame) Change(entry, -step);
    }

    static void Change(ConfigEntry<float> entry, float delta)
    {
        entry.Value = (float)Math.Round(entry.Value + delta, 3);
        SettingsChanged();
    }

    static void Flip(ConfigEntry<bool> entry)
    {
        entry.Value = !entry.Value;
        SettingsChanged();
    }

    static void SettingsChanged()
    {
        Plugin.SettingsVersion++;
        DriverMode.Apply();
    }

    static void ResetSeat()
    {
        foreach (var e in new[] { Plugin.OffsetX, Plugin.OffsetY, Plugin.OffsetZ, Plugin.HeadHeight, Plugin.HeadForward, Plugin.Pitch, Plugin.Fov, Plugin.LookIntoTurn })
            e.Value = (float)e.DefaultValue;
        SettingsChanged();
    }

    void UpdateBodyVisibility(CameraControllerInGame ctrl)
    {
        var body = ctrl.VehicleProvider?.BodyTransform;
        bool wantHidden = Plugin.HideCarBody.Value && body != null;
        bool bodyChanged = _hiddenBody != null && (body == null || _hiddenBody.Pointer != body.Pointer);
        if (!wantHidden || bodyChanged) ShowBody();
        if (wantHidden && _hiddenBody == null) HideBody(body);

        bool outlineBodyChanged = _strippedBody != null && (body == null || _strippedBody.Pointer != body.Pointer);
        if (outlineBodyChanged) RestoreOutlines();
        if (body != null && _strippedBody == null) StripOutlines(body);
    }

    // The car's cartoon outline is an inside-out copy of the body; from the driver's seat it encloses the
    // camera and turns everything black, so it's removed from the player's car while in driver view.
    Transform _strippedBody;
    readonly List<(Renderer renderer, Il2CppReferenceArray<Material> materials)> _strippedRenderers = new();

    void StripOutlines(Transform body)
    {
        Cockpit.CaptureStyle(body);
        foreach (var r in body.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length < 2) continue;

            var keep = new List<Material>();
            foreach (var m in mats)
                if (m == null || m.shader == null || !m.shader.name.Contains("Outline")) keep.Add(m);
            if (keep.Count == mats.Length) continue;

            _strippedRenderers.Add((r, mats));
            r.sharedMaterials = new Il2CppReferenceArray<Material>(keep.ToArray());
        }
        _strippedBody = body;
        Plugin.Logger.LogInfo($"Removed the outline from {_strippedRenderers.Count} car parts for driver view.");
    }

    void RestoreOutlines()
    {
        foreach (var (r, mats) in _strippedRenderers)
            if (r != null && !r.WasCollected) r.sharedMaterials = mats;
        _strippedRenderers.Clear();
        _strippedBody = null;
    }

    void HideBody(Transform body)
    {
        foreach (var r in body.GetComponentsInChildren<Renderer>(false))
        {
            if (!r.enabled) continue;
            r.enabled = false;
            _hiddenRenderers.Add(r);
        }
        _hiddenBody = body;
    }

    void ShowBody()
    {
        foreach (var r in _hiddenRenderers)
            if (r != null && !r.WasCollected) r.enabled = true;
        _hiddenRenderers.Clear();
        _hiddenBody = null;
    }

    // ---------------------------------------------------------------- on-screen panel

    void OnGUI()
    {
        try
        {
            DrawGui();
        }
        catch (Exception e)
        {
            if (!_guiErrorLogged) Plugin.Logger.LogError(e);
            _guiErrorLogged = true;
        }
    }

    void DrawGui()
    {
        float s = Mathf.Max(0.5f, Plugin.UiScale.Value);
        int font = Mathf.RoundToInt(13 * s);
        GUI.skin.button.fontSize = font;
        GUI.skin.label.fontSize = font;
        GUI.skin.box.fontSize = font;

        EditMode.DrawOverlay(s);
        if (!Plugin.ShowButton.Value && !_panelOpen) return;

        float x = Plugin.ButtonX.Value, y = Plugin.ButtonY.Value;
        float rowH = 30 * s, gap = 4 * s;

        if (!_panelOpen)
        {
            if (GUI.Button(new Rect(x, y, 120 * s, rowH), "DriverCam")) OpenPanel();
            return;
        }

        var ctrl = CameraControllerInGame.Instance;
        bool active = DriverView.IsActive(ctrl);
        string modeName = ctrl?.CurrentCameraMode?.cameraModeName ?? "(not driving)";

        float w = 380 * s;
        int rows = _page == 1 ? 15 : _page == 2 ? 13 : _page == 3 ? 14 : 17;
        float h = rowH * rows + gap * (rows + 2);
        y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - h));
        GUI.Box(new Rect(x, y, w, h), "");

        float cy = y + gap;
        float inner = w - 2 * gap;
        float half = (inner - gap) / 2;
        GUI.Label(new Rect(x + gap, cy, inner - 40 * s, rowH), $"DriverCam   camera: {modeName}");
        if (GUI.Button(new Rect(x + inner + gap - 36 * s, cy, 36 * s, rowH), "X")) { ClosePanel(); return; }
        cy += rowH + gap;

        if (GUI.Button(new Rect(x + gap, cy, inner, rowH), active ? "Driver view: ON" : "Driver view: OFF") && ctrl != null)
            Toggle(ctrl);
        cy += rowH + gap;

        float third = (inner - 3 * gap) / 4;
        string[] tabs = { "Seat & view", "Parts", "Mirror", "HUD" };
        for (int t = 0; t < tabs.Length; t++)
            if (GUI.Button(new Rect(x + gap + t * (third + gap), cy, third, rowH), _page == t ? $"[ {tabs[t]} ]" : tabs[t])) _page = t;
        cy += rowH + gap;

        if (_page == 1) DrawPartsPage(ref cy, x, s, gap, rowH, inner, half);
        else if (_page == 2) DrawMirrorPage(ref cy, x, s, gap, rowH, inner, half);
        else if (_page == 3) DrawHudPage(ref cy, x, s, gap, rowH, inner, half);
        else DrawSeatPage(ref cy, x, s, gap, rowH, inner, half);
    }

    void DrawSeatPage(ref float cy, float x, float s, float gap, float rowH, float inner, float half)
    {
        float step = Plugin.TuneStep.Value;
        Row(ref cy, x, s, gap, rowH, "Seat back / fwd", Plugin.OffsetZ, step, "0.00");
        Row(ref cy, x, s, gap, rowH, "Seat down / up", Plugin.OffsetY, step, "0.00");
        Row(ref cy, x, s, gap, rowH, "Seat left / right", Plugin.OffsetX, step, "0.00");
        Row(ref cy, x, s, gap, rowH, "Look up / down", Plugin.Pitch, 1f, "0");
        Row(ref cy, x, s, gap, rowH, "Field of view", Plugin.Fov, 2f, "0");
        Row(ref cy, x, s, gap, rowH, "Look into turns", Plugin.LookIntoTurn, 1f, "0");
        Row(ref cy, x, s, gap, rowH, "Wheel turn angle", Plugin.SteerAngle, 15f, "0");
        Row(ref cy, x, s, gap, rowH, "Cockpit size", Plugin.CockpitScale, 0.05f, "0.00");
        Row(ref cy, x, s, gap, rowH, "Interior brightness", Plugin.InteriorBrightness, 0.05f, "0.00");
        Row(ref cy, x, s, gap, rowH, "Outline thickness", Plugin.OutlineThickness, 0.1f, "0.0");

        if (GUI.Button(new Rect(x + gap, cy, half, rowH), Plugin.ShowCockpit.Value ? "Cockpit: ON" : "Cockpit: OFF")) Flip(Plugin.ShowCockpit);
        if (GUI.Button(new Rect(x + gap * 2 + half, cy, half, rowH), Plugin.HideCarBody.Value ? "Car body: HIDDEN" : "Car body: SHOWN")) Flip(Plugin.HideCarBody);
        cy += rowH + gap;

        if (GUI.Button(new Rect(x + gap, cy, half, rowH), "Reset seat")) ResetSeat();
        if (GUI.Button(new Rect(x + gap * 2 + half, cy, half, rowH), CarPresets.HasShared ? "Use shared setup" : "No shared setup") && CarPresets.HasShared)
            CarPresets.UseShared();
        cy += rowH + gap;

        GUI.Label(new Rect(x + gap, cy, inner, rowH), "C / Y (game camera button) or F6 for driver view");
    }

    // ---------------------------------------------------------------- Parts editor

    int _page;   // 0 = Seat & view, 1 = Parts, 2 = Mirror

    void DrawHudPage(ref float cy, float x, float s, float gap, float rowH, float inner, float half)
    {
        if (GUI.Button(new Rect(x + gap, cy, half, rowH), Plugin.HudEnabled.Value ? "HUD layout: ON" : "HUD layout: OFF")) Plugin.HudEnabled.Value = !Plugin.HudEnabled.Value;
        if (GUI.Button(new Rect(x + gap * 2 + half, cy, half, rowH), Plugin.HudOnlyInDriverView.Value ? "Driver view only" : "All cameras")) Plugin.HudOnlyInDriverView.Value = !Plugin.HudOnlyInDriverView.Value;
        cy += rowH + gap;
        MirrorRow(ref cy, x, s, gap, rowH, "Pull to center", Plugin.HudPullToCenter, 0.05f, "0.00");
        MirrorRow(ref cy, x, s, gap, rowH, "HUD size", Plugin.HudSize, 0.05f, "0.00");
        MirrorRow(ref cy, x, s, gap, rowH, "Health left / right", Plugin.HudHealthX, 10f, "0");
        MirrorRow(ref cy, x, s, gap, rowH, "Health down / up", Plugin.HudHealthY, 10f, "0");
        MirrorRow(ref cy, x, s, gap, rowH, "Speedo left / right", Plugin.HudSpeedoX, 10f, "0");
        MirrorRow(ref cy, x, s, gap, rowH, "Speedo down / up", Plugin.HudSpeedoY, 10f, "0");
        MirrorRow(ref cy, x, s, gap, rowH, "Ability left / right", Plugin.HudAbilityX, 10f, "0");
        MirrorRow(ref cy, x, s, gap, rowH, "Ability down / up", Plugin.HudAbilityY, 10f, "0");
        if (GUI.Button(new Rect(x + gap, cy, half, rowH), "Reset HUD"))
            foreach (var e in new[] { Plugin.HudPullToCenter, Plugin.HudSize, Plugin.HudHealthX, Plugin.HudHealthY, Plugin.HudSpeedoX, Plugin.HudSpeedoY, Plugin.HudAbilityX, Plugin.HudAbilityY })
                e.Value = (float)e.DefaultValue;
        if (GUI.Button(new Rect(x + gap * 2 + half, cy, half, rowH), "Close")) ClosePanel();
        cy += rowH + gap;
    }

    int _mirrorIndex;

    void DrawMirrorPage(ref float cy, float x, float s, float gap, float rowH, float inner, float half)
    {
        var mirrors = new[] { Plugin.RearMirror, Plugin.LeftMirror, Plugin.RightMirror };
        string[] names = { "Rear-view mirror", "Left side mirror", "Right side mirror" };
        _mirrorIndex = (_mirrorIndex % mirrors.Length + mirrors.Length) % mirrors.Length;
        var m = mirrors[_mirrorIndex];

        float btnW = 40 * s;
        if (GUI.Button(new Rect(x + gap, cy, btnW, rowH), "<")) _mirrorIndex--;
        GUI.Label(new Rect(x + gap * 2 + btnW, cy, inner - 2 * btnW - 2 * gap, rowH), $"  {names[_mirrorIndex]}");
        if (GUI.Button(new Rect(x + inner - btnW + gap, cy, btnW, rowH), ">")) _mirrorIndex++;
        cy += rowH + gap;

        if (GUI.Button(new Rect(x + gap, cy, inner, rowH), m.Enabled.Value ? "Mirror: ON" : "Mirror: OFF")) m.Enabled.Value = !m.Enabled.Value;
        cy += rowH + gap;
        float step = PartSteps[_partStepIndex];
        MirrorRow(ref cy, x, s, gap, rowH, "Camera left / right", m.OffsetX, step, "0.000");
        MirrorRow(ref cy, x, s, gap, rowH, "Camera down / up", m.OffsetY, step, "0.000");
        MirrorRow(ref cy, x, s, gap, rowH, "Camera back / fwd", m.OffsetZ, step, "0.000");
        MirrorRow(ref cy, x, s, gap, rowH, "Aim left / right", m.Yaw, 1f, "0");
        MirrorRow(ref cy, x, s, gap, rowH, "Tilt up / down", m.Pitch, 1f, "0");
        MirrorRow(ref cy, x, s, gap, rowH, "Zoom (FOV)", m.Fov, 1f, "0");
        if (GUI.Button(new Rect(x + gap, cy, inner, rowH), $"Move step: {step:0.###} m (click to change)"))
            _partStepIndex = (_partStepIndex + 1) % PartSteps.Length;
        cy += rowH + gap;
        if (GUI.Button(new Rect(x + gap, cy, half, rowH), "Reset this mirror"))
            foreach (var entry in m.All()) entry.BoxedValue = entry.DefaultValue;
        if (GUI.Button(new Rect(x + gap * 2 + half, cy, half, rowH), "Close")) ClosePanel();
        cy += rowH + gap;
        GUI.Label(new Rect(x + gap, cy, inner, rowH), "Move the mirror glass itself on the Parts tab");
    }

    // Mirror settings apply live every frame, so no cockpit rebuild is needed (unlike Row)
    static void MirrorRow(ref float cy, float x, float s, float gap, float rowH, string label, ConfigEntry<float> entry, float step, string format)
    {
        float v = entry.Value;
        if (PartRow(ref cy, x, s, gap, rowH, label, ref v, step, format)) entry.Value = v;
    }
    string _exportStatus;
    int _partIndex;
    static readonly float[] PartSteps = { 0.005f, 0.01f, 0.025f, 0.05f, 0.1f };
    int _partStepIndex = 1;

    void DrawPartsPage(ref float cy, float x, float s, float gap, float rowH, float inner, float half)
    {
        var groups = Cockpit.GroupNames;
        if (groups.Count == 0)
        {
            GUI.Label(new Rect(x + gap, cy, inner, rowH * 2), "Switch to Driver view to load the cockpit parts.");
            cy += rowH * 2 + gap;
            if (GUI.Button(new Rect(x + gap, cy, inner, rowH), "Close")) ClosePanel();
            return;
        }

        _partIndex = (_partIndex % groups.Count + groups.Count) % groups.Count;
        string group = groups[_partIndex];
        float btnW = 40 * s;
        if (GUI.Button(new Rect(x + gap, cy, btnW, rowH), "<")) _partIndex--;
        GUI.Label(new Rect(x + gap * 2 + btnW, cy, inner - 2 * btnW - 2 * gap, rowH), $"  {group}   ({_partIndex + 1}/{groups.Count})");
        if (GUI.Button(new Rect(x + inner - btnW + gap, cy, btnW, rowH), ">")) _partIndex++;
        cy += rowH + gap;

        float step = PartSteps[_partStepIndex];
        var e = PartLayout.Get(group);
        bool changed = false;
        changed |= PartRow(ref cy, x, s, gap, rowH, "Move left / right", ref e.Position.x, step, "0.000");
        changed |= PartRow(ref cy, x, s, gap, rowH, "Move down / up", ref e.Position.y, step, "0.000");
        changed |= PartRow(ref cy, x, s, gap, rowH, "Move back / fwd", ref e.Position.z, step, "0.000");
        changed |= PartRow(ref cy, x, s, gap, rowH, "Tilt", ref e.Rotation.x, 1f, "0");
        changed |= PartRow(ref cy, x, s, gap, rowH, "Turn", ref e.Rotation.y, 1f, "0");
        changed |= PartRow(ref cy, x, s, gap, rowH, "Roll", ref e.Rotation.z, 1f, "0");
        changed |= PartRow(ref cy, x, s, gap, rowH, "Size", ref e.Scale, 0.02f, "0.00");
        if (changed)
        {
            e.Scale = Mathf.Max(0.05f, e.Scale);
            PartLayout.Set(group, e);
            Cockpit.ApplyLayout();
        }

        if (GUI.Button(new Rect(x + gap, cy, inner, rowH), $"Move step: {step:0.###} m (click to change)"))
            _partStepIndex = (_partStepIndex + 1) % PartSteps.Length;
        cy += rowH + gap;

        if (GUI.Button(new Rect(x + gap, cy, half, rowH), "Reset this part"))
        {
            PartLayout.Reset(group);
            Cockpit.ApplyLayout();
        }
        if (GUI.Button(new Rect(x + gap * 2 + half, cy, half, rowH), "Close")) ClosePanel();
        cy += rowH + gap;

        if (GUI.Button(new Rect(x + gap, cy, inner, rowH), "Export car model (for Blender)"))
        {
            try { _exportStatus = CarExporter.Export(); }
            catch (Exception ex) { _exportStatus = "Export failed: " + ex.Message; Plugin.Logger.LogError(ex); }
        }
        cy += rowH + gap;
        if (_exportStatus != null)
        {
            GUI.Label(new Rect(x + gap, cy, inner, rowH), _exportStatus);
            cy += rowH + gap;
        }

        GUI.Label(new Rect(x + gap, cy, inner, rowH), "Changes save automatically");
    }

    static bool PartRow(ref float cy, float x, float s, float gap, float rowH, string label, ref float value, float step, string format)
    {
        float labelW = 170 * s, btnW = 40 * s, valueW = 80 * s;
        float cx = x + gap;
        bool changed = false;
        GUI.Label(new Rect(cx, cy, labelW, rowH), label);
        cx += labelW;
        if (GUI.Button(new Rect(cx, cy, btnW, rowH), "-")) { value = (float)Math.Round(value - step, 4); changed = true; }
        cx += btnW + gap;
        GUI.Label(new Rect(cx, cy, valueW - gap, rowH), value.ToString(format));
        cx += valueW - gap;
        if (GUI.Button(new Rect(cx, cy, btnW, rowH), "+")) { value = (float)Math.Round(value + step, 4); changed = true; }
        cy += rowH + gap;
        return changed;
    }

    static void Row(ref float cy, float x, float s, float gap, float rowH, string label, ConfigEntry<float> entry, float step, string format)
    {
        float labelW = 170 * s, btnW = 40 * s, valueW = 80 * s;
        float cx = x + gap;
        GUI.Label(new Rect(cx, cy, labelW, rowH), label);
        cx += labelW;
        if (GUI.Button(new Rect(cx, cy, btnW, rowH), "-")) Change(entry, -step);
        cx += btnW + gap;
        GUI.Label(new Rect(cx, cy, valueW - gap, rowH), entry.Value.ToString(format));
        cx += valueW - gap;
        if (GUI.Button(new Rect(cx, cy, btnW, rowH), "+")) Change(entry, step);
        cy += rowH + gap;
    }

    void OpenPanel()
    {
        _savedCursorVisible = Cursor.visible;
        _savedLockState = Cursor.lockState;
        _panelOpen = true;
    }

    void ClosePanel()
    {
        _panelOpen = false;
        Cursor.visible = _savedCursorVisible;
        Cursor.lockState = _savedLockState;
    }
}









