using System;
using System.Collections.Generic;
using HarmonyLib;
using Game.Runtime.Data;
using Game.Runtime.Manager;

namespace Sandbox
{
    /// <summary>
    /// Keeps sandbox runs off the player's records. Every target's RVA was looked up in dump.cs: all appear once (own code)
    /// except one, which IDA shows is safe to share (below). Il2CppInterop patches the native body, so a prefix on a method
    /// whose body is shared with another method also runs (and skips) for that other method.
    ///   - PATCHED although shared: PlayerProgressionManager.CheckBossCompletion (0x90FC80) has the same body as
    ///     PlayerProgressionManager.OnLevelCompleted. IDA: the body only moves currentBossLevel (0x40) on by one when the
    ///     current boss is beaten; its only references are data (method tables / the level-completed event), so both
    ///     sharers are the boss-progress write and both should be skipped in a sandbox race (accepted by the player).
    ///   - NOT patched: ACardSO.IncreaseCardAcquiredStat (0x9BE770) = the virtual base ACardSO.OnCardAcquired (called by
    ///     CardEffectSO.OnCardAcquired 0x9E3EF0, CardUsableSO.OnCardAcquired 0x9EDA20 and through vtables): skipping it
    ///     would break every card's on-acquire effect, so the per-card "times acquired" counter still counts (README).
    /// The demo gates (MissionsDisabled, XpLockedForDemo, AchievementsDisabled, CreditsLockedForDemo) are NOT patched:
    /// they share 0x68D930 / 0x8AAA40 with hundreds of methods. BossProfileSO.SetDiscovered is not patched either (shared code).
    ///
    /// PlayerProgressionManager.TrySpendCredits (0x911CD0) is skipped in a sandbox race and reports success without
    /// spending: its only game-scene caller is DefeatScreenPanel.OnCheckpointRestoreButton (the garage TryPurchase callers
    /// run in the main menu, where the guard is off), and AddCredits is blocked, so real credits spent there could never
    /// come back.
    ///
    /// Leaderboard prefixes skip while the stored run is a sandbox run in any single-player scene (the upload belongs to
    /// the run that just ended), and in any scene while an agreed multiplayer sandbox run is on or was the latest
    /// multiplayer run, until the next run starts (Multiplayer.LeaderboardBlocked). Every other prefix skips only in a
    /// sandbox race: a single-player game scene of a sandbox run (Plugin.GuardNow / GuardFast), or the multiplayer race
    /// scene / the host's tile pick of an agreed multiplayer sandbox run (Multiplayer.GuardNow / GuardFast), so claiming
    /// mission / level rewards in the main menu after a sandbox run still works. Each skip is logged once per run.
    /// Installed by hand so a missing method can't break loading; a failure is recorded and the SANDBOX button stays off.
    /// </summary>
    internal static class Guards
    {
        private static readonly HashSet<string> LoggedThisRun = new HashSet<string>();

        internal static void NewRun() => LoggedThisRun.Clear();

        internal static int Install(Harmony h)
        {
            int n = 0;
            // harmony-target: LeaderboardsManager.PublishEntry (cooperates with PitStop)
            n += P(h, typeof(LeaderboardsManager), "PublishEntry", nameof(SkipLeaderboard));
            // harmony-target: SteamLeaderboardsManager.PublishEntry
            n += P(h, typeof(Game.Runtime.Steamworks.SteamLeaderboardsManager), "PublishEntry", nameof(SkipSteamLeaderboard));
            // harmony-target: AchievementManager.CompleteAchievement
            n += P(h, typeof(AchievementManager), "CompleteAchievement", nameof(SkipAchievement));
            // harmony-target: MissionManager.CompleteMission
            n += P(h, typeof(MissionManager), "CompleteMission", nameof(SkipMission));
            // harmony-target: AObjective.SetCompleted (0x6EBFD0, own code, 29 direct callers incl. AObjective.Update and
            // TrySetCompletedNextFrame's check). Without it a sandbox run would leave mission / achievement objectives marked
            // completed in memory, and a later normal run in the same session could complete the mission on that progress.
            n += P(h, typeof(AObjective), "SetCompleted", nameof(SkipObjective));
            // harmony-target: PlayerProgressionManager.AddExpPoints, PlayerProgressionManager.AddCredits
            n += P(h, typeof(PlayerProgressionManager), "AddExpPoints", nameof(SkipXp));
            n += P(h, typeof(PlayerProgressionManager), "AddCredits", nameof(SkipCredits));
            // harmony-target: PlayerProgressionManager.CheckBossCompletion (0x90FC80, body shared with
            // PlayerProgressionManager.OnLevelCompleted: both are the same boss-progress check, both skipped on purpose)
            n += P(h, typeof(PlayerProgressionManager), "CheckBossCompletion", nameof(SkipBossProgress));
            // harmony-target: PlayerProgressionManager.TrySpendCredits (sandbox perk: reports success, spends nothing)
            n += P(h, typeof(PlayerProgressionManager), "TrySpendCredits", nameof(SkipSpendCredits));
            // harmony-target: ACardSO.IncreaseCardSoldStat, ACardSO.IncreaseCardDestroyedStat
            n += P(h, typeof(ACardSO), "IncreaseCardSoldStat", nameof(SkipCardSoldStat));
            n += P(h, typeof(ACardSO), "IncreaseCardDestroyedStat", nameof(SkipCardDestroyedStat));
            // harmony-target: CardEquipmentManager.UnlockCard, CardEquipmentManager.AcquireCardPermanently, CardEquipmentManager.BuyAllCardsDebug
            n += P(h, typeof(CardEquipmentManager), "UnlockCard", nameof(SkipCardUnlock));
            n += P(h, typeof(CardEquipmentManager), "AcquireCardPermanently", nameof(SkipCardPermanent));
            n += P(h, typeof(CardEquipmentManager), "BuyAllCardsDebug", nameof(SkipBuyAll));
            // harmony-target: VehicleGarageManager.UnlockVehicle, VehicleGarageManager.UnlockVinyl, VehicleGarageManager.UnlockPart, VehicleGarageManager.UnlockDriverTitle
            n += P(h, typeof(VehicleGarageManager), "UnlockVehicle", nameof(SkipVehicleUnlock));
            n += P(h, typeof(VehicleGarageManager), "UnlockVinyl", nameof(SkipVinylUnlock));
            n += P(h, typeof(VehicleGarageManager), "UnlockPart", nameof(SkipPartUnlock));
            n += P(h, typeof(VehicleGarageManager), "UnlockDriverTitle", nameof(SkipTitleUnlock));
            // harmony-target: GameStatisticsManager.OnEnterGasStation, GameStatisticsManager.OnSlipstreamCompleted, GameStatisticsManager.OnTopSpeedCompleted, GameStatisticsManager.OnDriftEndsDuration, GameStatisticsManager.OnRunCurrencyAdded, GameStatisticsManager.OnRunCurrencySpent
            foreach (var m in new[] { "OnEnterGasStation", "OnSlipstreamCompleted", "OnTopSpeedCompleted", "OnDriftEndsDuration", "OnRunCurrencyAdded", "OnRunCurrencySpent" })
                n += P(h, typeof(GameStatisticsManager), m, nameof(SkipGameStat));
            // harmony-target: RaceStatisticsManager.OnLevelCompleted, RaceStatisticsManager.OnLevelFailed, RaceStatisticsManager.OnRunEnded, RaceStatisticsManager.TryRegisterHighScore, RaceStatisticsManager.TryRegisterHighestLevelAchieved, RaceStatisticsManager.TryRegisterCoinHighScores
            foreach (var m in new[] { "OnLevelCompleted", "OnLevelFailed", "OnRunEnded", "TryRegisterHighScore", "TryRegisterHighestLevelAchieved", "TryRegisterCoinHighScores" })
                n += P(h, typeof(RaceStatisticsManager), m, nameof(SkipRaceStat));
            return n;
        }

        /// <summary>One prefix on one method (no overloads among the targets). Returns 1, or records the failure and returns 0.</summary>
        private static int P(Harmony h, Type type, string method, string prefix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) throw new MissingMethodException(type.Name, method);
                h.Patch(target, prefix: new HarmonyMethod(typeof(Guards), prefix));
                return 1;
            }
            catch (Exception e) { Plugin.Fail($"{type.Name}.{method}", e); return 0; }
        }

        private static void Once(string what)
        {
            try { if (LoggedThisRun.Add(what)) Plugin.Log.LogInfo($"[Sandbox] skipped (sandbox run): {what}"); }
            catch { /* logging only */ }
        }

        /// <summary>A sandbox race: single-player (Plugin.GuardNow) or an agreed multiplayer sandbox run (Multiplayer.cs).</summary>
        private static bool G() => Plugin.GuardNow() || Multiplayer.GuardNow();
        /// <summary>The same for per-frame guards (cached scene checks).</summary>
        private static bool GF() => Plugin.GuardFast() || Multiplayer.GuardFast();

        /// <summary>Leaderboards: the stored run is a sandbox run and this is not multiplayer (any scene: the upload belongs to the run that ended), or the latest multiplayer run was sandbox.</summary>
        private static bool LbBlocked()
        {
            if (Multiplayer.LeaderboardBlocked()) return true;
            if (!Plugin.RunIsSandbox.Value) return false;
            try { return !Game.Runtime.GameState.IsMultiplayerMode; } catch { return true; }
        }

        // ---------------------------------------------------------------- prefixes (false = the game's method is skipped)

        private static bool SkipLeaderboard()
        {
            try { if (!LbBlocked()) return true; Once("leaderboard upload (LeaderboardsManager.PublishEntry): not uploaded (sandbox)"); return false; }
            catch { return false; }   // a sandbox check that throws must not let the upload through
        }

        private static bool SkipSteamLeaderboard()
        {
            try { if (!LbBlocked()) return true; Once("Steam leaderboard upload (SteamLeaderboardsManager.PublishEntry)"); return false; }
            catch { return false; }
        }

        private static bool SkipAchievement(ref bool __result)
        {
            if (!G()) return true;
            __result = false; Once("achievement (AchievementManager.CompleteAchievement)"); return false;
        }

        private static bool SkipMission(ref bool __result)
        {
            if (!G()) return true;
            __result = false; Once("mission completion (MissionManager.CompleteMission)"); return false;
        }

        /// <summary>
        /// The objective stays enabled and not completed (no flag, no OnDisable, no callback); it is re-checked by the game
        /// later. Per-frame in sandbox runs (ScoreDurationObjective / ComboChainActionCountObjective.OnUpdate call
        /// SetCompleted every frame while their condition holds), so it uses the cached check (GuardFast), not a scene-name read.
        /// </summary>
        private static bool SkipObjective() { if (!GF()) return true; Once("mission / achievement objective (AObjective.SetCompleted)"); return false; }

        private static bool SkipXp(ref bool __result)
        {
            if (!G()) return true;
            __result = false; Once("XP (PlayerProgressionManager.AddExpPoints)"); return false;
        }

        private static bool SkipCredits() { if (!G()) return true; Once("credits (PlayerProgressionManager.AddCredits)"); return false; }
        private static bool SkipSpendCredits(ref bool __result)
        {
            if (!G()) return true;
            __result = true; Once("credits spent (PlayerProgressionManager.TrySpendCredits): free in a sandbox run, nothing spent"); return false;
        }

        /// <summary>Also runs for PlayerProgressionManager.OnLevelCompleted (same native body, same boss check).</summary>
        private static bool SkipBossProgress() { if (!G()) return true; Once("boss progress (PlayerProgressionManager.CheckBossCompletion / OnLevelCompleted)"); return false; }

        private static bool SkipCardSoldStat() { if (!G()) return true; Once("card sold count (ACardSO.IncreaseCardSoldStat)"); return false; }
        private static bool SkipCardDestroyedStat() { if (!G()) return true; Once("card destroyed count (ACardSO.IncreaseCardDestroyedStat)"); return false; }
        private static bool SkipCardUnlock() { if (!G()) return true; Once("card unlock (CardEquipmentManager.UnlockCard)"); return false; }
        private static bool SkipCardPermanent() { if (!G()) return true; Once("permanent card (CardEquipmentManager.AcquireCardPermanently)"); return false; }
        private static bool SkipBuyAll() { if (!G()) return true; Once("buy-all cards (CardEquipmentManager.BuyAllCardsDebug)"); return false; }
        private static bool SkipVehicleUnlock() { if (!G()) return true; Once("vehicle unlock (VehicleGarageManager.UnlockVehicle)"); return false; }
        private static bool SkipVinylUnlock() { if (!G()) return true; Once("vinyl unlock (VehicleGarageManager.UnlockVinyl)"); return false; }
        private static bool SkipPartUnlock() { if (!G()) return true; Once("part unlock (VehicleGarageManager.UnlockPart)"); return false; }
        private static bool SkipTitleUnlock() { if (!G()) return true; Once("driver title unlock (VehicleGarageManager.UnlockDriverTitle)"); return false; }
        private static bool SkipGameStat() { if (!G()) return true; Once("game statistics (GameStatisticsManager)"); return false; }
        private static bool SkipRaceStat() { if (!G()) return true; Once("race / run statistics and high scores (RaceStatisticsManager)"); return false; }
    }
}
