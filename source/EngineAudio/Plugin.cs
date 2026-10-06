using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine.InputSystem;

namespace EngineAudio
{
    /// <summary>
    /// EngineAudio: realistic engine sound from the game's own recordings. Design: "Engine Audio - Realistic Engine Sound"
    /// (claude.ai doc 391e5ae4-7e88-410a-a632-30b562d13148). The game plays its rev sweeps back to back on a timer at a
    /// fixed pitch; EngineAudio plays the same clips from a simulated RPM that follows the game's gearbox and throttle.
    /// Sound only: nothing about the car's physics, gearbox or inputs is written. Works in multiplayer too (local audio).
    /// </summary>
    [BepInPlugin(Guid, "EngineAudio", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.engineaudio";
        public const string Version = "0.4.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, Limiter, TrafficEnabled, Overlay, DriftFlare, TiresEnabled;
        internal static ConfigEntry<Key> ToggleKey;
        internal static ConfigEntry<float> IdleRpm, RedlineRpm, Volume, TopSpeedRpm, TiresVolume, TiresPitch, PitchLow, PitchHigh, PopMinRpm;
        internal static ConfigEntry<bool> Pops, MatchPitch;
        internal static ConfigEntry<float> Tone;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Master switch.");
            ToggleKey = Config.Bind("General", "ToggleKey", Key.F1, "Turns EngineAudio off / on for this session, to compare with the game's own engine sound (Input System key name).");
            IdleRpm = Config.Bind("Engine", "IdleRpm", 900f, "Simulated idle RPM (500-2000).");
            RedlineRpm = Config.Bind("Engine", "RedlineRpm", 7500f, "Simulated redline RPM (idle + 2000 to 12000). Upshifts drop to 58% of it.");
            Limiter = Config.Bind("Engine", "Limiter", true, "Bounce off the rev limiter when you hold full throttle at the top of a lower gear (never in the last gear: at top speed the engine holds TopSpeedRpm).");
            TopSpeedRpm = Config.Bind("Engine", "TopSpeedRpm", 0.93f,
                "Where the engine sits at top speed in the last gear, as a share of RedlineRpm (0.7-0.99). Most of a race is spent here: a steady high note with a slight natural wander, not the limiter.");
            DriftFlare = Config.Bind("Engine", "DriftFlare", true, "The revs flare up (wheelspin) when you drift on the throttle.");
            PitchLow = Config.Bind("Engine", "PitchAtIdle", 0.85f, new ConfigDescription(
                "Engine pitch at idle RPM, on top of where the recording plays (1 = as recorded). The pitch rises smoothly with the RPM to PitchAtRedline.",
                new AcceptableValueRange<float>(0.5f, 1.5f)));
            PitchHigh = Config.Bind("Engine", "PitchAtRedline", 1.3f, new ConfigDescription(
                "Engine pitch at the redline (and so at top speed, where the engine sits near the redline). Higher = a higher scream.",
                new AcceptableValueRange<float>(0.8f, 2f)));
            MatchPitch = Config.Bind("Engine", "MatchPitch", true,
                "0.4.0: play each recording at the moment whose measured pitch matches the RPM, and pitch the rest exactly, so the note always follows the RPM (gears 2-4 of the game's recordings barely change pitch on their own). Off = the old straight mapping with PitchAtIdle / PitchAtRedline.");
            Tone = Config.Bind("Engine", "Tone", 1f, new ConfigDescription(
                "With MatchPitch: the whole engine note up or down (1 = as measured; 0.85 deeper, 1.15 higher).", new AcceptableValueRange<float>(0.7f, 1.4f)));
            Pops = Config.Bind("Exhaust", "Pops", true, "Exhaust pops and crackles when you lift off the gas at high RPM (synthesized: a first pop 70-150 ms after the lift, then up to 4 crackles while the revs fall; never on throttle).");
            PopMinRpm = Config.Bind("Exhaust", "PopMinRpm", 0.6f, new ConfigDescription(
                "Pops only when you lift off above this share of the redline (0.3-0.95).", new AcceptableValueRange<float>(0.3f, 0.95f)));
            TiresEnabled = Config.Bind("Tires", "Enabled", true, "Tyre squeal when you drift or corner hard, layered over the game's own drift sound (synthesized, follows your slip angle).");
            TiresVolume = Config.Bind("Tires", "Volume", 0.5f, "Tyre squeal volume (0-2). The game's sound-effects volume applies on top.");
            TiresPitch = Config.Bind("Tires", "Pitch", 1f, new ConfigDescription(
                "Tyre squeal pitch (1 = as built, around 520-680 Hz; lower = deeper). Read live.", new AcceptableValueRange<float>(0.5f, 1.5f)));
            Volume = Config.Bind("Engine", "Volume", 1f, "Engine volume relative to the game's own engine volume for the car (0-2). The game's engine volume setting still applies on top.");
            TrafficEnabled = Config.Bind("Traffic", "Enabled", true, "Traffic engine pitch follows each car's speed through simple gears, with a little per-car variety.");
            Overlay = Config.Bind("Debug", "Overlay", false, "Show a one-line readout: RPM, gear, throttle and which recordings play.");

            try { GameApi.Check(); }
            catch (Exception e) { Log.LogError($"[EngineAudio] game check crashed, plugin stays idle: {e}"); return; }
            if (GameApi.TiresOk && TiresEnabled.Value) TireSqueal.Prepare();   // the squeal's samples, on a worker thread
            if (Pops.Value) ExhaustPops.Prepare();                             // the pops' samples, likewise

            // the runner first: if it can't be registered, nothing is patched either (no half-loaded plugin)
            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();

            if (GameApi.TrafficOk)
            {
                try { TrafficEngines.Install(new Harmony(Guid)); }
                catch (Exception e) { Log.LogWarning($"[EngineAudio] traffic engines not patched (the game's traffic sound stays): {e.Message}"); }
            }
            Log.LogInfo($"EngineAudio {Version} loaded. {ToggleKey.Value} switches between EngineAudio and the game's own engine sound.");
        }
    }
}
