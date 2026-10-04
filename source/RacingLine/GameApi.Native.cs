using System;
using System.Runtime.CompilerServices;
using Game.Runtime.Data;
using Game.Runtime.Manager;
using UnityEngine;

namespace RacingLine
{
    /// <summary>
    /// Racing Line as a real score category (design docs: v1 "native score category", v2 "Coins (gold)").
    ///
    /// Single-player only: a copy of the level's Top Speed provider, made inert (its own logic can never start:
    /// timeThreshold 1e9, targetSpeedFactor 2 > any speed factor, scoreMultiplier 0), with a unique id, our name and icons,
    /// appended once to scoreProviderList. Points go through the game's own AddToScore (contributeToCombo on, so ticks keep
    /// the combo alive; the popup flag only controls the HUD popup, verified in AddToScore 0x1806EC990). Coins: the game
    /// pays targetCoinReward x clamp01(totalTopSpeedTime / target), so we add corner-grade units to totalTopSpeedTime.
    /// Side effect accepted on 2026-10-03: the copy reports type Top Speed, so Top Speed cards also affect Racing Line.
    /// </summary>
    internal static partial class GameApi
    {
        internal const string NativeId = "rogue.racingline";
        internal const string NativeName = "RACING LINE";
        private const string OtherModId = "rogue.police";   // Police's PURSUIT: also a Top Speed copy

        // untyped: see _world. _native = our provider, _nativeOwner = the score manager whose list holds it
        private static ScriptableObject _native;
        private static MonoBehaviour _nativeOwner;

        /// <summary>True in any multiplayer session (or if the game mode can't be read): display mode, nothing counts.</summary>
        internal static bool IsMultiplayer()
        {
            if (!ModeOk) return true;
            try { return ReadMultiplayerFlag(); }   // separate method: it's only compiled once ModeOk says GameState exists
            catch { return true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadMultiplayerFlag() => Game.Runtime.GameState.IsMultiplayerMode;

        internal static bool NativeActive => _native != null && _nativeOwner != null;

        /// <summary>
        /// Makes sure the current level's score manager holds exactly one Racing Line provider. Returns true if it does
        /// (already there, or appended now). Never in multiplayer. Only call when ScoreOk.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool EnsureNative(Sprite hudIcon, Sprite statIcon, int coinReward, out string log)
        {
            log = null;
            if (IsMultiplayer()) return false;
            var mgr = ScoreManager();
            if (mgr == null) return false;
            if (NativeActive && _nativeOwner.Pointer == mgr.Pointer) return true;

            var list = mgr.scoreProviderList;
            if (list == null) return false;

            // already in this list (scene reload with a persistent manager)? adopt it, never add a second one
            TopSpeedScoreProviderSO template = null;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                if (p.GetId() == NativeId)
                {
                    if (p.TryCast<TopSpeedScoreProviderSO>() == null) { log = "[RacingLine] an older Racing Line category is in this score list (restart the game after updating): display mode"; return false; }
                    _native = p; _nativeOwner = mgr;
                    MakeInert(p.TryCast<TopSpeedScoreProviderSO>(), coinReward);   // re-applied every level: the game re-initialises providers
                    log = "[RacingLine] Racing Line category already in this level's score list; reusing it";
                    return true;
                }
                // the stock Top Speed comes first (mod copies are appended); never copy another mod's copy (Police's PURSUIT)
                if (template == null && p.GetId() != OtherModId) template = p.TryCast<TopSpeedScoreProviderSO>();
            }
            if (template == null) { log = "[RacingLine] no Top Speed category to copy: display mode"; return false; }

            var copyObj = UnityEngine.Object.Instantiate((UnityEngine.Object)template);
            var copy = copyObj == null ? null : copyObj.TryCast<TopSpeedScoreProviderSO>();
            if (copy == null) { if (copyObj != null) UnityEngine.Object.Destroy(copyObj); log = "[RacingLine] could not copy the Top Speed category: display mode"; return false; }

            copy.name = "RacingLine_Score_Provider";
            copy.hideFlags = HideFlags.DontUnloadUnusedAsset;
            copy.SetId(NativeId);
            copy.scoreName = NativeName;
            if (hudIcon != null) copy.scoreHudIcon = hudIcon;
            if (statIcon != null) copy.statListIcon = statIcon;
            MakeInert(copy, coinReward);

            list.Add(copy);
            copy.Initialize(mgr);
            MakeInert(copy, coinReward);   // again, in case Initialize reset anything
            _native = copy; _nativeOwner = mgr;
            log = $"[RacingLine] Racing Line added as a score category (id {NativeId}, Top Speed template, {list.Count} categories in this level)";
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
            p.contributeToCombo = true;      // ticks keep the game's combo alive through grip corners (Grip vs Drift doc)
            p.targetCoinReward = Math.Max(0, coinReward);
        }

        /// <summary>
        /// Pays a lump of points through the game's own path (not used by the live scoring; kept for one-off awards).
        /// Re-checks multiplayer at every payout. Only when NativeActive.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void AwardNative(double points, bool popup)
        {
            if (!NativeActive || points <= 0 || IsMultiplayer()) return;
            var p = _native.TryCast<AScoreProviderSO>();
            p?.AddToScore(points, popup);
        }

        // ------------------------------------------------------------------ live action (the game's own temporary-score path)
        // Same sequence as DriftScoreProviderSO (verified in GameAssembly.dll): OnDriftStarts = OnScoreBegin + OnScoreActivated;
        // UpdateDriftScore = AddToTemporaryScore(points, false, false) every frame (the HUD shows the provider's temporary
        // score counting up while IsBeingPerformed); OnDriftEnds = TransferTempToComboScore (or CancelTemporaryScore on a
        // failed drift) + OnScoreEnd(completed). FinishLevel(completed) itself transfers any temporary score to the final
        // score, and OnComboFailed clears it and ends the action, so nothing in flight is ever lost or double-counted.
        // The copy's own Top Speed logic never starts (its private active flag stays false), so it never touches these.

        /// <summary>True while the game shows our live action (false again after the game failed the combo).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool LiveRunning()
        {
            var p = NativeActive ? Provider() : null;
            return p != null && p.IsBeingPerformed;
        }

        // the provider cast once per provider object (no TryCast allocation every frame)
        private static ScriptableObject _castFor, _cast;

        private static AScoreProviderSO Provider()
        {
            if (_native == null) return null;
            if (_castFor == null || _castFor.Pointer != _native.Pointer || _cast == null) { _castFor = _native; _cast = _native.TryCast<AScoreProviderSO>(); }
            return (AScoreProviderSO)_cast;
        }

        /// <summary>
        /// Starts a live action (HUD counter appears). activate = also the game's activation (counters, activation
        /// bonus): once per corner. Only when NativeActive, single-player.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void BeginLive(bool activate)
        {
            if (!NativeActive || IsMultiplayer()) return;
            var p = Provider();
            if (p == null || p.IsBeingPerformed) return;
            p.OnScoreBegin();
            if (activate) p.OnScoreActivated();
        }

        /// <summary>Adds this frame's points to the live action (the game applies its own multipliers and card effects).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void AddLive(double points)
        {
            if (!NativeActive || points <= 0 || IsMultiplayer()) return;
            var p = Provider();
            if (p == null || !p.IsBeingPerformed) return;
            p.AddToTemporaryScore(points, false, false);
        }

        /// <summary>Ends the live action: completed = into the combo (like a finished drift), else cancelled (a crash).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void EndLive(bool completed)
        {
            if (!NativeActive) return;
            var p = Provider();
            if (p == null) return;
            if (completed) p.TransferTempToComboScore(); else p.CancelTemporaryScore();
            if (p.IsBeingPerformed) p.OnScoreEnd(completed);
        }

        /// <summary>Adds coin action units (corner grades) to the copy's own counter, which the game resets every level.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void AddCoinUnits(float units)
        {
            if (!NativeActive || units <= 0f || IsMultiplayer()) return;
            var p = _native.TryCast<TopSpeedScoreProviderSO>();
            if (p != null) p.totalTopSpeedTime += units;
        }

        /// <summary>Coin target for this race (units for the full reward), read by the game only at level end.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetCoinTarget(float units)
        {
            if (!NativeActive) return;
            var p = _native.TryCast<AScoreProviderSO>();
            if (p == null) return;
            float t = Math.Max(1f, units);   // the game clamps these to >= 1
            p.targetActionValueMinCurvature = t;
            p.targetActionValueMaxCurvature = t;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static double NativeScore()
        {
            var p = NativeActive ? _native.TryCast<AScoreProviderSO>() : null;
            return p == null ? 0 : p.CurrentScore;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int NativeCoins()
        {
            var p = NativeActive ? _native.TryCast<AScoreProviderSO>() : null;
            return p == null ? 0 : p.CoinReward;
        }

        /// <summary>
        /// A new level: the old manager (and our provider with it) may be gone. Never removes or destroys anything.
        /// The old copy is deliberately NOT destroyed: game card effects (e.g. the unique-action combo effects'
        /// registeredProviderList, ScoreMultiplierByConditionTraitEffect.targetProviders) can hold references to score
        /// providers for a whole run, and a destroyed one would throw inside the game's own code. If the score manager is
        /// recreated per level, that leaks one small ScriptableObject per level, which is acceptable.
        /// </summary>
        internal static void ForgetNative() { _native = null; _nativeOwner = null; _scoreManager = null; _castFor = null; _cast = null; }
    }
}
