using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Runtime.Data;
using Game.Runtime.Manager;
using Game.Runtime.Vehicle;
using UnityEngine;

namespace Police
{
    /// <summary>
    /// PURSUIT as a real score category (design doc "Police Pursuit - v1 Concept": Scoring, Technical approach: Score),
    /// made exactly like RacingLine's Racing Line category (RacingLine/GameApi.Native.cs).
    ///
    /// Single-player only: a copy of the level's stock Top Speed provider (never another mod's copy: ids rogue.racingline
    /// and rogue.police are skipped), made inert (its own logic can never start: timeThreshold 1e9, targetSpeedFactor 2
    /// &gt; any speed factor, scoreMultiplier 0), with the unique id rogue.police, the name PURSUIT and our icons, appended
    /// once to scoreProviderList. An existing copy is adopted by id, never added twice (duplicate ids would break
    /// LevelScoreManager.GetSnapshotData's dictionary), and an old copy is never destroyed (card effects can hold
    /// references to providers for a whole run). Side effect (as for Racing Line): the copy reports type Top Speed, so Top
    /// Speed cards and multipliers also affect PURSUIT.
    ///
    /// Live points use the game's temporary-score path, the same sequence as a drift: OnScoreBegin + OnScoreActivated at
    /// the start, AddToTemporaryScore(points, false, false) while it runs (the HUD counts it up), then
    /// TransferTempToComboScore + OnScoreEnd(true) (escaped) or CancelTemporaryScore + OnScoreEnd(false) (caught).
    /// FinishLevel banks any temporary score itself; OnComboFailed clears it and ends the action.
    /// Coins: the game pays targetCoinReward x clamp01(totalTopSpeedTime / target), so an escape adds one unit.
    /// </summary>
    internal static partial class GameApi
    {
        internal const string PursuitId = "rogue.police";
        internal const string PursuitName = "PURSUIT";
        private const string RacingLineId = "rogue.racingline";   // RacingLine's RACING LINE: also a Top Speed copy

        internal static bool PursuitOk { get; private set; }   // the score category (provider copy, live action, coins)
        internal static bool ResultsOk { get; private set; }   // per-race results row (RogueShared.ModScoreRows)
        internal static bool VictoryOk { get; private set; }   // Victory screen row (RogueShared.ModScoreRows)
        internal static bool RunOk { get; private set; }       // run position (stage, race): run total reset

        private static void CheckScore(Assembly asm, List<string> missing)
        {
            PursuitOk = Has(asm, "Game.Runtime.Manager.LevelScoreManager", missing, "scoreProviderList")
                     && Has(asm, "Game.Runtime.Data.AScoreProviderSO", missing, "GetId", "SetId", "Initialize", "IsBeingPerformed",
                            "scoreName", "scoreHudIcon", "statListIcon", "targetCoinReward", "targetActionValueMinCurvature", "targetActionValueMaxCurvature",
                            "contributeToCombo", "CurrentScore", "CoinReward",
                            "OnScoreBegin", "OnScoreActivated", "AddToTemporaryScore", "TransferTempToComboScore", "CancelTemporaryScore", "OnScoreEnd")
                     && Has(asm, "Game.Runtime.Data.TopSpeedScoreProviderSO", missing, "timeThreshold", "targetSpeedFactor", "scoreMultiplier", "cancelOnCollision", "totalTopSpeedTime");
            RogueShared.ModScoreRows.Check((type, members) => Has(asm, type, missing, members), out bool results, out bool victory);
            ResultsOk = results;
            VictoryOk = victory;
            RunOk = Has(asm, "Game.Runtime.Manager.RunWorldManager", missing, "currentStageIndex", "currentRaceIndex");
        }

        // untyped (see the class comment in GameApi.cs): _pursuit = our provider, _pursuitOwner = the score manager whose list holds it
        private static ScriptableObject _pursuit;
        private static MonoBehaviour _pursuitOwner;
        // the provider cast once per provider object (no TryCast allocation every tick)
        private static ScriptableObject _castFor, _cast;

        internal static bool PursuitActive => _pursuit != null && _pursuitOwner != null;

        /// <summary>The level's score manager (shared with the collision / near-miss counts), searched for at most every 2 s.</summary>
        private static LevelScoreManager ScoreManager()
        {
            if (_scoreManager == null)
            {
                if (Time.unscaledTime < _nextScoreManagerSearch) return null;
                _nextScoreManagerSearch = Time.unscaledTime + 2f;
                _scoreManager = UnityEngine.Object.FindFirstObjectByType<LevelScoreManager>();
                if (_scoreManager == null) return null;
            }
            return (LevelScoreManager)_scoreManager;
        }

        /// <summary>
        /// Makes sure the current level's score manager holds exactly one PURSUIT provider. True if it does (already
        /// there, or appended now). Never in multiplayer. Only when PursuitOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool EnsurePursuit(Sprite hudIcon, Sprite statIcon, int coinReward, out string log)
        {
            log = null;
            if (IsMultiplayer()) return false;
            var mgr = ScoreManager();
            if (mgr == null) return false;
            if (PursuitActive && _pursuitOwner.Pointer == mgr.Pointer) return true;

            var list = mgr.scoreProviderList;
            if (list == null) return false;

            // already in this list (a persistent manager)? adopt it, never add a second one
            TopSpeedScoreProviderSO template = null;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                string id = p.GetId();
                if (id == PursuitId)
                {
                    var ours = p.TryCast<TopSpeedScoreProviderSO>();
                    if (ours == null) { log = "[Police] an unexpected PURSUIT category is in this score list (restart the game after updating): no PURSUIT points"; return false; }
                    _pursuit = ours; _pursuitOwner = mgr;
                    MakeInert(ours, coinReward);   // re-applied every level: the game re-initialises providers
                    log = "[Police] Pursuit category already in this level's score list; reusing it";
                    return true;
                }
                // the stock Top Speed comes first (mod copies are appended); never copy another mod's copy
                if (template == null && id != RacingLineId) template = p.TryCast<TopSpeedScoreProviderSO>();
            }
            if (template == null) { log = "[Police] no Top Speed category to copy: no PURSUIT points"; return false; }

            var copyObj = UnityEngine.Object.Instantiate((UnityEngine.Object)template);
            var copy = copyObj == null ? null : copyObj.TryCast<TopSpeedScoreProviderSO>();
            if (copy == null) { if (copyObj != null) UnityEngine.Object.Destroy(copyObj); log = "[Police] could not copy the Top Speed category: no PURSUIT points"; return false; }

            copy.name = "Police_Pursuit_Score_Provider";
            copy.hideFlags = HideFlags.DontUnloadUnusedAsset;
            copy.SetId(PursuitId);
            copy.scoreName = PursuitName;
            if (hudIcon != null) copy.scoreHudIcon = hudIcon;
            if (statIcon != null) copy.statListIcon = statIcon;
            MakeInert(copy, coinReward);

            list.Add(copy);
            copy.Initialize(mgr);
            MakeInert(copy, coinReward);   // again, in case Initialize reset anything
            _pursuit = copy; _pursuitOwner = mgr;
            log = $"[Police] Pursuit added as a score category (id {PursuitId}, Top Speed template, {list.Count} categories in this level)";
            return true;
        }

        /// <summary>Top Speed's own logic can never start; combo on; coins via our units.</summary>
        private static void MakeInert(TopSpeedScoreProviderSO p, int coinReward)
        {
            if (p == null) return;
            p.timeThreshold = 1e9f;          // CheckTopSpeedState returns early while lastFinishedTime + timeThreshold > Time.time
            p.targetSpeedFactor = 2f;        // SpeedFactorOriginal is clamped to 0..1, so this can never be reached
            p.scoreMultiplier = 0f;
            p.cancelOnCollision = false;
            p.contributeToCombo = true;      // a chase's points go into the game's combo like any action
            p.targetCoinReward = Math.Max(0, coinReward);
        }

        private static AScoreProviderSO PursuitProvider()
        {
            if (_pursuit == null) return null;
            if (_castFor == null || _castFor.Pointer != _pursuit.Pointer || _cast == null) { _castFor = _pursuit; _cast = _pursuit.TryCast<AScoreProviderSO>(); }
            return (AScoreProviderSO)_cast;
        }

        /// <summary>True while the game shows our live action (false again after the game failed the combo).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool PursuitLiveRunning()
        {
            var p = PursuitActive ? PursuitProvider() : null;
            return p != null && p.IsBeingPerformed;
        }

        /// <summary>Starts the live action (HUD counter appears). activate = also the game's activation: once per chase.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PursuitBegin(bool activate)
        {
            if (!PursuitActive || IsMultiplayer()) return;
            var p = PursuitProvider();
            if (p == null || p.IsBeingPerformed) return;
            p.OnScoreBegin();
            if (activate) p.OnScoreActivated();
        }

        /// <summary>Adds points to the running live action (the game applies its own multipliers and card effects).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PursuitAdd(double points)
        {
            if (!PursuitActive || points <= 0 || IsMultiplayer()) return;
            var p = PursuitProvider();
            if (p == null || !p.IsBeingPerformed) return;
            p.AddToTemporaryScore(points, false, false);
        }

        /// <summary>Ends the live action: completed = into the combo (escaped, race over), else cancelled (caught, quit).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PursuitEnd(bool completed)
        {
            if (!PursuitActive) return;
            var p = PursuitProvider();
            if (p == null) return;
            if (completed) p.TransferTempToComboScore(); else p.CancelTemporaryScore();
            if (p.IsBeingPerformed) p.OnScoreEnd(completed);
        }

        /// <summary>Adds coin units (escapes) to the copy's own counter, which the game resets every level.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PursuitCoinUnits(float units)
        {
            if (!PursuitActive || units <= 0f || IsMultiplayer()) return;
            var p = _pursuit.TryCast<TopSpeedScoreProviderSO>();
            if (p != null) p.totalTopSpeedTime += units;
        }

        /// <summary>Coin target for this race (units for the full reward), read by the game only at level end.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PursuitCoinTarget(float units)
        {
            if (!PursuitActive) return;
            var p = PursuitProvider();
            if (p == null) return;
            float t = Math.Max(1f, units);   // the game clamps these to >= 1
            p.targetActionValueMinCurvature = t;
            p.targetActionValueMaxCurvature = t;
        }

        /// <summary>The game's own PURSUIT score for this level (card multipliers included).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static double PursuitScoreNow()
        {
            var p = PursuitActive ? PursuitProvider() : null;
            return p == null ? 0 : p.CurrentScore;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int PursuitCoins()
        {
            var p = PursuitActive ? PursuitProvider() : null;
            return p == null ? 0 : p.CoinReward;
        }

        /// <summary>A new race: re-check the score list (EnsurePursuit adopts our copy if it is still there). Never destroys anything.</summary>
        internal static void ForgetPursuit() { _pursuit = null; _pursuitOwner = null; _castFor = null; _cast = null; }

        // ------------------------------------------------------------------ race identity and run position

        /// <summary>The local player's car object (a new one = a new race or a new car), zero if none. Only when PlayerOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IntPtr PlayerCar()
        {
            var veh = VehicleManager.Instance;
            return veh == null ? IntPtr.Zero : veh.Pointer;
        }

        private static MonoBehaviour _world;   // RunWorldManager
        private static float _nextWorldSearch;

        /// <summary>The run's position: stage and race index (both 0 = the first race of a new run). Only when RunOk.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool RunPosition(out int stage, out int race)
        {
            stage = -1; race = -1;
            if (_world == null)
            {
                if (Time.unscaledTime < _nextWorldSearch) return false;
                _nextWorldSearch = Time.unscaledTime + 2f;   // never a scene search every tick
                _world = UnityEngine.Object.FindFirstObjectByType<RunWorldManager>();
                if (_world == null) return false;
            }
            var w = (RunWorldManager)_world;
            stage = w.currentStageIndex; race = w.currentRaceIndex;
            return true;
        }
    }
}
