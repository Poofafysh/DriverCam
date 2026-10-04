using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace EngineAudio
{
    /// <summary>What we read from the player's car each frame. Read only.</summary>
    internal struct CarRead
    {
        public IntPtr Car;            // VehicleManager object (a new one = a new car / level)
        public float Speed;           // m/s
        public int Gear;              // VehicleGearboxHandler.CurrentGearIndex
        public float GearProgress;    // VehicleGearboxHandler.CurrentGearProgress
        public bool Shifting;         // VehicleGearboxHandler.IsShifting
        public bool HasGearbox;
        public float Throttle;        // VehicleInputHandler.Throttle (0 when not Accelerating)
        public float SpeedFactor;     // speed / current max speed (fallback gears)
        public float GamePitch;       // the game's own engine pitch (1.2 during boost)
        public float MaxVolume;       // VehicleEngineSounds.engineMaxVolume (the car's engine volume)
        public bool LevelEnded;
        public int Gears;             // the gearbox's gear count (VehicleGearboxHandler.gearArray), 5 if unreadable
        // tyres (only when TiresOk)
        public Vector3 BodyPos, BodyForward;   // VehicleManager.VehicleBody: the visible body (it turns in a drift)
        public bool Drifting;         // VehicleMovement.Drifting
        public bool Grounded;         // VehicleMovement.IsGrounded
    }

    /// <summary>
    /// The only file that touches game types (verified in dump.cs / GameAssembly.dll):
    /// VehicleManager.Instance -> VehicleMovement.CurrentSpeed / CurrentMaxSpeed, VehicleInputHandler.Throttle /
    /// Accelerating, VehicleGearboxHandler.CurrentGearIndex / CurrentGearProgress / IsShifting, VehicleSoundHandler
    /// (VehicleSoundManager) .vehicleEngineSounds (VehicleEngineSounds) ._acceleratingEngineSounds[4] /
    /// _deceleratingEngineSounds[4] (index 0 = the looping idle) / _blowOffEngineSounds[4] / engineMaxVolume.
    /// The game's engine code (VehicleSoundManager.Update, which inlines UpdateEngineSound and calls Play / Stop / DOFade
    /// on those sources directly) keeps running untouched; EngineAudio only sets AudioSource.mute on them (no game code
    /// ever writes mute: its only references are metadata) and plays the same clips on sources of its own.
    /// </summary>
    internal static class GameApi
    {
        internal static bool CarOk { get; private set; }
        internal static bool GearboxOk { get; private set; }
        internal static bool TrafficOk { get; private set; }
        internal static bool TiresOk { get; private set; }

        internal static void Check()
        {
            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            CarOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "VehicleMovement", "VehicleInputHandler", "VehicleSoundHandler", "LevelWasEnded")
                 && Has(asm, "Game.Runtime.Vehicle.VehicleMovement", missing, "CurrentSpeed", "CurrentMaxSpeed")
                 && Has(asm, "Game.Runtime.Vehicle.VehicleInputHandler", missing, "Throttle", "Accelerating")
                 && Has(asm, "Game.Runtime.Vehicle.VehicleSoundManager", missing, "vehicleEngineSounds")
                 && Has(asm, "Game.Runtime.Vehicle.VehicleEngineSounds", missing, "_acceleratingEngineSounds", "_deceleratingEngineSounds", "_blowOffEngineSounds", "engineMaxVolume");
            GearboxOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "VehicleGearboxHandler")
                     && Has(asm, "Game.Runtime.Vehicle.VehicleGearboxHandler", missing, "CurrentGearIndex", "CurrentGearProgress", "IsShifting");
            TiresOk = CarOk
                   && Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "VehicleBody")
                   && Has(asm, "Game.Runtime.Vehicle.VehicleMovement", missing, "Drifting", "IsGrounded")
                   && Has(asm, "Game.Runtime.Vehicle.VehicleSoundManager", missing, "driftLoopSounds");
            TrafficOk = Has(asm, "AIVehicleSoundHandler", missing, "HandleSFXs", "SetupEngineSound", "engineAudioSource", "controller")
                     && Has(asm, "AIVehicleController", missing, "PathFollower")
                     && Has(asm, "AIPathFollower", missing, "Speed");
            if (missing.Count == 0) Plugin.Log.LogInfo("[EngineAudio] game check OK: player engine, gearbox, tyres, traffic engines");
            else Plugin.Log.LogWarning($"[EngineAudio] game check: missing {string.Join(", ", missing)}. Player engine {On(CarOk)}, real gearbox {On(GearboxOk)} " +
                                       $"(off = simulated gears from speed), tyre squeal {On(TiresOk)}, traffic {On(TrafficOk)}.");
        }

        private static string On(bool ok) => ok ? "on" : "OFF";

        private static bool Has(Assembly asm, string typeName, List<string> missing, params string[] members)
        {
            var t = asm?.GetType(typeName);
            if (t == null) { missing.Add(typeName); return false; }
            bool ok = true;
            foreach (var m in members)
                if (t.GetMember(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length == 0)
                { missing.Add($"{t.Name}.{m}"); ok = false; }
            return ok;
        }

        // ------------------------------------------------------------------ player car (cached per car object)

        private static MonoBehaviour _veh, _move, _input, _gearbox, _sounds, _soundManager;
        private static Transform _body;
        private static int _gears = 5;
        private static AudioSource _pitchSource;   // the game's first rev-up source: its pitch is the game's engine pitch (boost)
        private static IntPtr _vehPtr;

        /// <summary>Reads the local player's car. False with no car (menus, loading). Only when CarOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Read(ref CarRead r)
        {
            var veh = VehicleManager.Instance;
            if (veh == null) { _veh = null; _vehPtr = IntPtr.Zero; return false; }
            if (_veh == null || _vehPtr != veh.Pointer || _move == null || _input == null || _sounds == null)
            {
                _veh = veh; _vehPtr = veh.Pointer;
                _move = veh.VehicleMovement; _input = veh.VehicleInputHandler;
                var sm = veh.VehicleSoundHandler;
                _soundManager = sm;
                _sounds = sm != null ? sm.vehicleEngineSounds : null;
                _gearbox = GearboxOk ? GearboxOf(veh) : null;
                _gears = 5;
                if (_gearbox != null) { try { _gears = GearCount(_gearbox); } catch { _gears = 5; } }
                _body = TiresOk ? BodyOf(veh) : null;
                _pitchSource = null;
                if (_sounds != null)
                {
                    var a = ((VehicleEngineSounds)_sounds)._acceleratingEngineSounds;
                    if (a != null && a.Length > 0) _pitchSource = a[0];
                }
                if (_move == null || _input == null || _sounds == null) { _veh = null; return false; }
            }
            var move = (VehicleMovement)_move;
            var input = (VehicleInputHandler)_input;
            r.Car = _vehPtr;
            r.Speed = Math.Abs(move.CurrentSpeed);
            float max = move.CurrentMaxSpeed;
            r.SpeedFactor = max > 1f ? Mathf.Clamp01(r.Speed / max) : 0f;
            r.Throttle = input.Accelerating ? Mathf.Clamp01(input.Throttle) : 0f;
            r.LevelEnded = veh.LevelWasEnded;
            r.HasGearbox = _gearbox != null;
            if (_gearbox != null) ReadGearbox(ref r);
            r.MaxVolume = ((VehicleEngineSounds)_sounds).engineMaxVolume;
            r.GamePitch = _pitchSource != null ? _pitchSource.pitch : 1f;
            r.Gears = _gears;
            if (_body != null) ReadTires(ref r);
            else { r.Drifting = false; r.Grounded = false; }   // no body: no squeal from stale values
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Transform BodyOf(VehicleManager veh) => veh.VehicleBody;

        /// <summary>The gearbox's gear count; outside the per-frame read (a missing field only costs the 5-gear default).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int GearCount(MonoBehaviour gearbox)
        {
            var arr = ((VehicleGearboxHandler)gearbox).gearArray;
            return arr != null && arr.Length >= 2 ? arr.Length : 5;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadTires(ref CarRead r)
        {
            var move = (VehicleMovement)_move;
            r.BodyPos = _body.position;
            r.BodyForward = _body.forward;
            r.Drifting = move.Drifting;
            r.Grounded = move.IsGrounded;
        }

        /// <summary>The mixer group of the game's drift sound (the squeal goes there too), or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static UnityEngine.Audio.AudioMixerGroup DriftMixer()
        {
            if (_soundManager == null) return null;
            var arr = ((VehicleSoundManager)_soundManager).driftLoopSounds;
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++) { var s = arr[i]; if (s != null && s.outputAudioMixerGroup != null) return s.outputAudioMixerGroup; }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MonoBehaviour GearboxOf(VehicleManager veh) => veh.VehicleGearboxHandler;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadGearbox(ref CarRead r)
        {
            var g = (VehicleGearboxHandler)_gearbox;
            r.Gear = g.CurrentGearIndex;
            r.GearProgress = g.CurrentGearProgress;
            r.Shifting = g.IsShifting;
        }

        /// <summary>The game's engine sources for the current car: accel[4], decel[4] (0 = idle loop), blow-off[4]. Copied to managed arrays.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Sources(out AudioSource[] accel, out AudioSource[] decel, out AudioSource[] blowOff, out GameObject host)
        {
            accel = decel = blowOff = null; host = null;
            if (_sounds == null) return false;
            var es = (VehicleEngineSounds)_sounds;
            accel = Copy(es._acceleratingEngineSounds);
            decel = Copy(es._deceleratingEngineSounds);
            blowOff = Copy(es._blowOffEngineSounds);
            host = es.gameObject;
            return accel.Length > 0 && decel.Length > 0;
        }

        private static AudioSource[] Copy(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<AudioSource> a)
        {
            if (a == null) return Array.Empty<AudioSource>();
            var r = new AudioSource[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i];
            return r;
        }

        internal static void Forget() { _veh = null; _vehPtr = IntPtr.Zero; _sounds = null; _soundManager = null; _gearbox = null; _pitchSource = null; _body = null; _gears = 5; }

        // ------------------------------------------------------------------ traffic

        /// <summary>A traffic car's engine source and its speed (m/s). False if unreadable.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TrafficEngine(object handlerObj, out AudioSource source, out float speed)
        {
            source = null; speed = 0f;
            var h = handlerObj as AIVehicleSoundHandler;
            if (h == null) return false;
            source = h.engineAudioSource;
            var c = h.controller;
            var pf = c != null ? c.PathFollower : null;
            if (source == null || pf == null) return false;
            speed = Math.Abs(pf.Speed);
            return true;
        }
    }
}
