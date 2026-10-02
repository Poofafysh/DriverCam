using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DriverCam;

/// <summary>
/// Fast controller editing: pick a target (seat, a cockpit part, or a mirror's view) with the D-pad and move it
/// smoothly with the sticks. Changes apply live and save to the current car's preset.
///   L3+R3 / F7  toggle      D-pad left/right  previous/next target
///   left stick  move left/right, back/forward     right stick  up/down, turn
///   LB held     right stick = tilt / roll         D-pad up/down  size (parts) or zoom (seat / mirror views)
///   RB held     fine adjustment
/// </summary>
internal static class EditMode
{
    public static bool Active { get; private set; }

    const float Deadzone = 0.15f;
    const float MoveSpeed = 0.15f;     // m/s at full stick
    const float TurnSpeed = 30f;       // deg/s
    const float SizeSpeed = 0.3f;      // per second
    const float ZoomSpeed = 20f;       // fov deg/s

    static int _index;
    static float _blinkUntil;
    static string _blinkGroup;

    sealed class Target
    {
        public string Name;
        public string Group;            // cockpit part group, or null
        public MirrorSettings Mirror;   // mirror view, or null
        public bool Seat;
    }

    static List<Target> Targets()
    {
        var list = new List<Target> { new() { Name = "Seat / eye", Seat = true } };
        foreach (var g in Cockpit.GroupNames) list.Add(new Target { Name = g, Group = g });
        list.Add(new Target { Name = "Rear-view mirror view", Mirror = Plugin.RearMirror });
        list.Add(new Target { Name = "Left mirror view", Mirror = Plugin.LeftMirror });
        list.Add(new Target { Name = "Right mirror view", Mirror = Plugin.RightMirror });
        return list;
    }

    public static void Update(bool driverView)
    {
        var pad = Gamepad.current;
        var kb = Keyboard.current;
        bool toggle = (kb != null && kb.f7Key.wasPressedThisFrame)
                      || (pad != null && ((pad.leftStickButton.wasPressedThisFrame && pad.rightStickButton.isPressed)
                                          || (pad.rightStickButton.wasPressedThisFrame && pad.leftStickButton.isPressed)));
        if (toggle && (driverView || Active))
        {
            SetActive(!Active);
            if (Active) Select(_index);
        }
        if (!driverView && Active) SetActive(false);
        if (!Active) return;

        UpdateBlink();
        if (pad == null) return;

        var targets = Targets();
        if (pad.dpad.right.wasPressedThisFrame) Select(_index + 1);
        if (pad.dpad.left.wasPressedThisFrame) Select(_index - 1);
        _index = Mod(_index, targets.Count);
        var t = targets[_index];

        float dt = Time.unscaledDeltaTime * (pad.rightShoulder.isPressed ? 0.25f : 1f);
        var ls = Dz(pad.leftStick.ReadValue());
        var rs = Dz(pad.rightStick.ReadValue());
        bool rotateMode = pad.leftShoulder.isPressed;
        float sizeAxis = (pad.dpad.up.isPressed ? 1f : 0f) - (pad.dpad.down.isPressed ? 1f : 0f);

        bool moving = ls != Vector2.zero || rs != Vector2.zero || sizeAxis != 0f;
        if (moving)
        {
            if (t.Seat) EditSeat(ls, rs, rotateMode, sizeAxis, dt);
            else if (t.Mirror != null) EditMirror(t.Mirror, ls, rs, rotateMode, sizeAxis, dt);
            else EditPart(t.Group, ls, rs, rotateMode, sizeAxis, dt);
            _unsaved = true;
        }
        else if (_unsaved) Commit();
    }

    static bool _unsaved;
    static ConfigFile Config => Plugin.OffsetX.ConfigFile;

    static void SetActive(bool on)
    {
        if (on == Active) return;
        Active = on;
        if (on)
        {
            // Stick edits change values every frame; write the files once the stick is let go instead
            Config.SaveOnConfigSet = false;
            CarPresets.Suspended = true;
        }
        else
        {
            StopBlink();
            Commit();
            Config.SaveOnConfigSet = true;
            CarPresets.Suspended = false;
        }
        Plugin.Logger.LogInfo(on ? "Edit mode on." : "Edit mode off.");
    }

    /// <summary>Writes the edits: BepInEx config, the car's preset and the part layout.</summary>
    static void Commit()
    {
        _unsaved = false;
        Config.Save();
        CarPresets.Save();
        PartLayout.Flush();
    }

    static void EditSeat(Vector2 ls, Vector2 rs, bool rotateMode, float sizeAxis, float dt)
    {
        Add(Plugin.OffsetX, ls.x * MoveSpeed * dt);
        Add(Plugin.OffsetZ, ls.y * MoveSpeed * dt);
        if (rotateMode) Add(Plugin.Pitch, -rs.y * TurnSpeed * dt);
        else Add(Plugin.OffsetY, rs.y * MoveSpeed * dt);
        if (sizeAxis != 0f)
        {
            Plugin.Fov.Value = Mathf.Clamp(Plugin.Fov.Value - sizeAxis * ZoomSpeed * dt, 30f, 120f);
            DriverMode.Apply();
        }
    }

    static void EditMirror(MirrorSettings m, Vector2 ls, Vector2 rs, bool rotateMode, float sizeAxis, float dt)
    {
        Add(m.OffsetX, ls.x * MoveSpeed * dt);
        Add(m.OffsetZ, ls.y * MoveSpeed * dt);
        if (rotateMode) Add(m.Pitch, -rs.y * TurnSpeed * dt);
        else
        {
            Add(m.OffsetY, rs.y * MoveSpeed * dt);
            Add(m.Yaw, rs.x * TurnSpeed * dt);
        }
        if (sizeAxis != 0f) m.Fov.Value = Mathf.Clamp(m.Fov.Value - sizeAxis * ZoomSpeed * dt, 5f, 90f);
    }

    static void EditPart(string group, Vector2 ls, Vector2 rs, bool rotateMode, float sizeAxis, float dt)
    {
        var e = PartLayout.Get(group);
        float scale = Mathf.Max(0.01f, Cockpit.CurrentModelScale);   // part offsets are in model units
        e.Position.x += ls.x * MoveSpeed * dt / scale;
        e.Position.z += ls.y * MoveSpeed * dt / scale;
        if (rotateMode)
        {
            e.Rotation.x -= rs.y * TurnSpeed * dt;
            e.Rotation.z -= rs.x * TurnSpeed * dt;
        }
        else
        {
            e.Position.y += rs.y * MoveSpeed * dt / scale;
            e.Rotation.y += rs.x * TurnSpeed * dt;
        }
        e.Scale = Mathf.Clamp(e.Scale + sizeAxis * SizeSpeed * dt, 0.05f, 5f);
        PartLayout.SetDeferred(group, e);
        Cockpit.ApplyLayout();
    }

    static void Add(ConfigEntry<float> entry, float delta)
    {
        if (delta != 0f) entry.Value += delta;
    }

    static Vector2 Dz(Vector2 v) => v.magnitude < Deadzone ? Vector2.zero : v * ((v.magnitude - Deadzone) / (1f - Deadzone) / v.magnitude);
    static int Mod(int a, int n) => n == 0 ? 0 : (a % n + n) % n;

    static void Select(int index)
    {
        var targets = Targets();
        _index = Mod(index, targets.Count);
        StopBlink();
        var g = targets[_index].Group;
        if (g != null)
        {
            _blinkGroup = g;
            _blinkUntil = Time.unscaledTime + 0.7f;
        }
    }

    static void UpdateBlink()
    {
        if (_blinkGroup == null) return;
        if (Time.unscaledTime >= _blinkUntil) { StopBlink(); return; }
        bool on = ((int)(Time.unscaledTime * 10f)) % 2 == 0;
        Cockpit.SetGroupVisible(_blinkGroup, on);
    }

    static void StopBlink()
    {
        if (_blinkGroup != null) Cockpit.SetGroupVisible(_blinkGroup, true);
        _blinkGroup = null;
    }

    /// <summary>Overlay at the top of the screen while editing.</summary>
    public static void DrawOverlay(float s)
    {
        if (!Active) return;
        var targets = Targets();
        var t = targets[Mod(_index, targets.Count)];
        string values;
        if (t.Seat)
            values = $"seat {Plugin.OffsetX.Value:0.00} / {Plugin.OffsetY.Value:0.00} / {Plugin.OffsetZ.Value:0.00}   tilt {Plugin.Pitch.Value:0}   fov {Plugin.Fov.Value:0}";
        else if (t.Mirror != null)
            values = $"camera {t.Mirror.OffsetX.Value:0.00} / {t.Mirror.OffsetY.Value:0.00} / {t.Mirror.OffsetZ.Value:0.00}   aim {t.Mirror.Yaw.Value:0}   tilt {t.Mirror.Pitch.Value:0}   zoom {t.Mirror.Fov.Value:0}";
        else
        {
            var e = PartLayout.Get(t.Group);
            values = $"move {e.Position.x:0.00} / {e.Position.y:0.00} / {e.Position.z:0.00}   turn {e.Rotation.y:0}  tilt {e.Rotation.x:0}  roll {e.Rotation.z:0}   size {e.Scale:0.00}";
        }
        float w = 760 * s, h = 92 * s;
        var r = new Rect((Screen.width - w) / 2, 12 * s, w, h);
        GUI.Box(r, "");
        GUI.Label(new Rect(r.x + 10 * s, r.y + 4 * s, w, 28 * s), $"EDIT MODE  -  {t.Name}   ({_index + 1}/{targets.Count})   [{Cockpit.CarId}]");
        GUI.Label(new Rect(r.x + 10 * s, r.y + 32 * s, w, 28 * s), values);
        GUI.Label(new Rect(r.x + 10 * s, r.y + 60 * s, w, 28 * s), "D-pad </> pick   L-stick move   R-stick up/down, turn   LB tilt/roll   D-pad up/down size/zoom   RB fine   L3+R3 done");
    }
}
