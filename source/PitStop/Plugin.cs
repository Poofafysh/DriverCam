using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using RogueShared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PitStop
{
    /// <summary>
    /// PitStop: one key (F2) refills your car's health, through the game's own heal (the same path its repair pickups
    /// use: VehicleHealth.Heal(100%, forceFinalValue), which updates the health bar, plays the heal sound and fires the
    /// game's heal event), then SetHealthFactor(1) in case a card scaled the heal down. In multiplayer your own car only, never while
    /// the car is already defeated, and only during a race (a player car exists and the level hasn't ended).
    ///
    /// Leaderboard (the player's decision, 2026-10-03): a run in which F2 refilled health is never uploaded to the game's
    /// Steam leaderboards. Every leaderboard upload goes through LeaderboardsManager.PublishEntry (called only by
    /// LeaderboardSO.PublishScore, which the run-end updaters call: total score, full-run time, completed runs, completed
    /// races, cards used; verified in GameAssembly.dll). A Harmony prefix skips it while the current run is marked. The
    /// mark is saved in the config (so quitting and continuing the run keeps it) and cleared when a new run starts
    /// (stage 0, race 0). If the patch can't be installed, F2 stays off: a refill can never reach the leaderboard.
    /// </summary>
    [BepInPlugin(Guid, "PitStop", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "rogue.pitstop";
        public const string Version = "0.3.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<Key> RefillKey;
        internal static ConfigEntry<float> Cooldown;
        internal static ConfigEntry<bool> RefilledThisRun;
        internal static bool GameOk, GuardOk;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription("Master switch.", null, HubLink.Meta("PitStop on")));
            RefillKey = Config.Bind("General", "RefillKey", Key.F2, new ConfigDescription(
                "Key that refills your car's health (Input System key name).", null, HubLink.Meta("Refill key")));
            Cooldown = Config.Bind("General", "Cooldown", 0f, new ConfigDescription(
                "Seconds before the key works again (0 = any time).", new AcceptableValueRange<float>(0f, 120f),
                HubLink.Meta("Cooldown between refills", 0, 120, 1, "s")));
            RefilledThisRun = Config.Bind("Leaderboard", "RefilledThisRun", false, new ConfigDescription(
                "Written by the plugin: true after a refill, until the next run starts. While true, this run is not uploaded to the Steam leaderboards.",
                null, HubLink.Meta(hidden: true)));

            var missing = new List<string>();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            GameOk = Has(asm, "Game.Runtime.Vehicle.VehicleManager", missing, "Instance", "VehicleHealthHandler", "LevelWasEnded")
                  && Has(asm, "Game.Runtime.Vehicle.VehicleHealth", missing, "Heal", "SetHealthFactor", "HealthFactor", "Defeated")
                  && Has(asm, "Game.Runtime.GameState", missing, "IsMultiplayerMode")
                  && Has(asm, "Game.Runtime.Manager.RunWorldManager", missing, "currentStageIndex", "currentRaceIndex");
            bool lbOk = Has(asm, "Game.Runtime.Manager.LeaderboardsManager", missing, "PublishEntry");
            if (missing.Count > 0) Log.LogWarning($"[PitStop] game check: missing {string.Join(", ", missing)}.");
            if (lbOk)
            {
                try { GuardOk = LeaderboardGuard.Install(new Harmony(Guid)); }
                catch (Exception e) { Log.LogWarning($"[PitStop] leaderboard guard failed to install: {e.Message}"); GuardOk = false; }
            }
            if (!GameOk || !GuardOk)
                Log.LogWarning($"[PitStop] the refill key stays off ({(!GameOk ? "game check failed" : "the leaderboard guard isn't installed, so a refilled run could be uploaded")}).");

            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            AddComponent<Runner>();
            // Rogue Hub (optional): a Refill button and the live line on PitStop's card
            HubLink.Status(Guid, Runner.HubStatus);
            HubLink.Action(Guid, "refill", "Refill health",
                "Fills your car's health now. During a race (in multiplayer: your own car); this run is then kept off the Steam leaderboards.", Runner.HubRefill);
            Log.LogInfo($"PitStop {Version} loaded. {RefillKey.Value} refills your car's health (also your own car in multiplayer; a run with a refill isn't uploaded to the leaderboards)." +
                        (RefilledThisRun.Value ? " The current run had a refill: its leaderboard upload stays blocked until a new run starts." : ""));
        }

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
    }

    /// <summary>Skips leaderboard uploads while the current run had a refill. Installed by hand so a missing type can't break loading.</summary>
    internal static class LeaderboardGuard
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(Game.Runtime.Manager.LeaderboardsManager), nameof(Game.Runtime.Manager.LeaderboardsManager.PublishEntry));
            if (target == null) return false;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(LeaderboardGuard), nameof(Prefix)));
            Plugin.Log.LogInfo("[PitStop] leaderboard guard installed (LeaderboardsManager.PublishEntry)");
            return true;
        }

        private static bool Prefix()
        {
            try
            {
                if (!Plugin.RefilledThisRun.Value) return true;
                Plugin.Log.LogInfo("[PitStop] leaderboard upload skipped: this run used a health refill");
                return false;   // skip the game's upload for this run
            }
            catch { return true; }   // never break the game's own flow
        }
    }

    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        private float _nextKbFetch, _nextRunCheck;
        private Keyboard _kb;
        private UnityEngine.InputSystem.Controls.KeyControl _key;
        private Key _keyName = Key.None;
        private int _errors;
        private IntPtr _lastCar;

        private void Update()
        {
            if (!Plugin.GameOk || _errors >= 5) return;
            try
            {
                float now = Time.unscaledTime;
                if (now >= _nextRunCheck) { _nextRunCheck = now + 1f; CheckNewRun(); }
                if (!Plugin.Enabled.Value || !Plugin.GuardOk) return;
                if (now >= _nextKbFetch || _keyName != Plugin.RefillKey.Value)
                {
                    _nextKbFetch = now + 2f;   // keyboard looked up at most every 2 s (and when the key setting changes)
                    var kb = Keyboard.current;
                    _keyName = Plugin.RefillKey.Value;
                    if (kb == null) { _kb = null; _key = null; return; }
                    _kb = kb;
                    _key = _keyName == Key.None ? null : kb[_keyName];
                }
                if (Time.timeScale <= 0f) return;   // paused: no key reading, no writes to the game
                if (_pendingFromHub)
                {
                    // a refill asked for in Rogue Hub while the race was paused happens now
                    _pendingFromHub = false;
                    string r = Use(now, out bool ok);
                    HubLink.Toast(Plugin.Guid, ok ? "Health refilled" : "No refill", r, ok ? "good" : "warn");
                }
                if (_key == null || !_key.wasPressedThisFrame) return;
                string result = Use(now, out bool refilled);
                if (HubLink.HubPresent) HubLink.Toast(Plugin.Guid, refilled ? "Health refilled" : "No refill", result, refilled ? "good" : "warn");
            }
            catch (Exception e)
            {
                _errors++;
                Plugin.Log.LogWarning($"[PitStop] error ({_errors}/5, then PitStop stays off for this session): {e.Message}");
            }
        }

        private static float _readyAt;
        private static bool _pendingFromHub;

        /// <summary>One refill (key or Rogue Hub button): cooldown, refill, leaderboard mark, log. Returns the result line.</summary>
        private static string Use(float now, out bool refilled)
        {
            refilled = false;
            if (now < _readyAt) return $"cooldown: {_readyAt - now:0} s left";
            string result = Refill(out refilled);
            if (refilled) _readyAt = now + Mathf.Max(0f, Plugin.Cooldown.Value);   // a refused refill doesn't use up the cooldown
            if (refilled && !Plugin.RefilledThisRun.Value)
            {
                Plugin.RefilledThisRun.Value = true;   // saved to rogue.pitstop.cfg
                result += "; this run won't be uploaded to the leaderboards";
            }
            Plugin.Log.LogInfo($"[PitStop] {result}");
            return result;
        }

        /// <summary>Rogue Hub button (the hub shows the returned line as a notification).</summary>
        internal static string HubRefill()
        {
            try
            {
                if (!Plugin.Enabled.Value) return "PitStop is switched off";
                if (!Plugin.GameOk || !Plugin.GuardOk) return "refill unavailable: the game check or the leaderboard guard failed (see the log)";
                if (Time.timeScale <= 0f)
                {
                    // the hub is open over the paused race: nothing is written to the game until it runs again
                    _pendingFromHub = true;
                    return "refill queued: it happens when the race resumes";
                }
                return Use(Time.unscaledTime, out _);
            }
            catch (Exception e) { return "refill failed: " + e.Message; }
        }

        /// <summary>Rogue Hub: the live line on PitStop's card.</summary>
        internal static string HubStatus()
        {
            if (!Plugin.Enabled.Value) return "off";
            if (!Plugin.GameOk || !Plugin.GuardOk) return "unavailable (see the log)";
            return Plugin.RefilledThisRun.Value ? "refill used: this run is off the leaderboards" : $"{Plugin.RefillKey.Value} refills; this run is still ranked";
        }

        /// <summary>A new player car at stage 0 / race 0 = a new run: its leaderboard uploads are allowed again.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CheckNewRun()
        {
            var veh = Game.Runtime.Vehicle.VehicleManager.Instance;
            IntPtr car = veh == null ? IntPtr.Zero : veh.Pointer;
            if (car == _lastCar) return;
            _lastCar = car;
            if (car == IntPtr.Zero || !Plugin.RefilledThisRun.Value) return;
            var world = UnityEngine.Object.FindFirstObjectByType<Game.Runtime.Manager.RunWorldManager>();   // once per new car, not per frame
            if (world == null) { _lastCar = IntPtr.Zero; return; }   // not ready yet: look again next second
            if (world.currentStageIndex == 0 && world.currentRaceIndex == 0)
            {
                Plugin.RefilledThisRun.Value = false;
                Plugin.Log.LogInfo("[PitStop] new run: leaderboard uploads allowed again");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Refill(out bool refilled)
        {
            refilled = false;
            // multiplayer (0.3.0): your own car only; the game sends your car's health to the other players itself
            var veh = Game.Runtime.Vehicle.VehicleManager.Instance;
            if (veh == null) return "no player car (not in a race)";
            if (veh.LevelWasEnded) return "race already over";
            var health = veh.VehicleHealthHandler;
            if (health == null) return "the car has no health component";
            if (health.Defeated) return "the car is already wrecked";
            float before = health.HealthFactor;
            if (before >= 0.999f) return "health already full";
            health.Heal(100f, true, true);                                  // the game's own heal: bar, sound, heal event
            if (health.HealthFactor < 0.999f) health.SetHealthFactor(1f);   // a card scaled the heal down: fill it anyway
            refilled = true;
            return $"health refilled: {before * 100f:0}% -> {health.HealthFactor * 100f:0}%{(Game.Runtime.GameState.IsMultiplayerMode ? " (multiplayer: your car)" : "")}";
        }
    }
}
