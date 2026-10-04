# Sandbox

Current version: 0.1.1

A separate run mode for trying builds: a **SANDBOX** button on the main menu (right under Singleplayer) starts a run
in which every card is free and you can have 20 mods. Sandbox runs stay off your records (except the known gaps under Risks and limits).

> **Back up `player.dat` before your first sandbox test.** It is in
> `%USERPROFILE%\AppData\LocalLow\<game company>\Driving Rogue\` (copy the whole folder). The guards below are built to keep a sandbox run out of the save, but this
> is version 0.1.0 and has not been played yet.

## What a sandbox run does

Start it with **SANDBOX** on the main menu (play section, under Singleplayer; the d-pad reaches it), then choose a car
as usual. While that run lasts:

- **Every card is free**: shop prices show 0, rerolls cost 0, and selling a card pays 0 (free cards are not a cash machine).
- **Checkpoint restores are free (a sandbox perk)**: during a sandbox race `PlayerProgressionManager.TrySpendCredits`
  reports success without spending anything, so the defeat screen's checkpoint restore costs none of your real credits.
  Its only in-race caller is that restore button (garage purchases run in the main menu, where this is off). It is
  free on purpose: credits can't be earned in a sandbox run, so real credits spent there could never come back.
- **Locked mods are offered** in the shop and boxes as if unlocked. Nothing is saved as unlocked.
- **All-cards picker**: the game ships a hidden developer picker in the card shop (search and a category filter;
  picking a card adds it for free). Sandbox shows it; its "buy all" button stays hidden (it would overflow the slots).
- **20 mod slots** (10 + `ExtraSlots`). Car and card slot modifiers still stack; the game's own cap is 25 free slots.
  When you hold more than 10 mods, the Current Mods row is laid out in 2 rows.
- **Road length** x1 to x5 (`LengthMultiplier`): each race asks the game's road generator for that many times its
  usual length. The race timer follows the real road length by itself; the score target does not (longer = easier).
- A **SANDBOX** tag top-left on the HUD; on the results screens it reads **"Not uploaded (sandbox)"**.

**Continue**, **Retry** (after a defeat or from the run summary), **Restart** and **Next race** keep the run in sandbox
mode. Every other run start is a normal run: Singleplayer, the tutorials, multiplayer, and restoring a normal run
snapshot or checkpoint. Multiplayer is never sandbox. The Racing Line and Pursuit score categories are unchanged.

## What a sandbox run never touches

Each is blocked at the game method that writes it (a Harmony prefix that skips it during a sandbox race):

| Record | Game method blocked |
|---|---|
| Steam leaderboards | `LeaderboardsManager.PublishEntry` (PitStop skips the same method for refilled runs; both are skip-prefixes) and `SteamLeaderboardsManager.PublishEntry` |
| Achievements | `AchievementManager.CompleteAchievement` |
| Missions | `MissionManager.CompleteMission` |
| Mission / achievement objectives | `AObjective.SetCompleted` (the objective stays open, so a later normal run in the same game session can't complete a mission on sandbox progress; an objective's own counters may still have moved) |
| XP, credits | `PlayerProgressionManager.AddExpPoints`, `AddCredits` (and `TrySpendCredits` spends nothing, see the perk above) |
| Boss progress | `PlayerProgressionManager.CheckBossCompletion`. Its game code is shared with `PlayerProgressionManager.OnLevelCompleted`, so that one is skipped too; checked in the game binary: both are the same boss-progress step (move to the next boss when the current one is beaten), nothing else |
| Card unlocks, permanent cards | `CardEquipmentManager.UnlockCard`, `AcquireCardPermanently`, `BuyAllCardsDebug` |
| Per-card times sold / destroyed | `ACardSO.IncreaseCardSoldStat`, `IncreaseCardDestroyedStat` |
| Car, vinyl, part, title unlocks | `VehicleGarageManager.UnlockVehicle`, `UnlockVinyl`, `UnlockPart`, `UnlockDriverTitle` |
| Statistics (Steam stats come from these) | `GameStatisticsManager` gas station / slipstream / top speed / drift / coins earned / coins spent handlers |
| Race and run stats, high scores, per-car stats | `RaceStatisticsManager.OnLevelCompleted`, `OnLevelFailed`, `OnRunEnded`, `TryRegisterHighScore`, `TryRegisterHighestLevelAchieved`, `TryRegisterCoinHighScores` |

If **any** of these (or the run-start hooks) fails to install, the SANDBOX button is not added and the load line says
why. Claiming mission or level rewards in the main menu after a sandbox run still works (those guards only act during
the sandbox race itself; the leaderboard guards act in any scene while the stored run is a sandbox run).

## Every game method patched

Checked in the game's `dump.cs`: each of these has its own game code (no other method shares it), except
`CheckBossCompletion` (shared with `PlayerProgressionManager.OnLevelCompleted`, the same boss-progress step; see above).

- **Record guards** (skip-prefixes, only during a sandbox race; leaderboards while the stored run is a sandbox run):
  `LeaderboardsManager.PublishEntry`, `SteamLeaderboardsManager.PublishEntry`, `AchievementManager.CompleteAchievement`,
  `MissionManager.CompleteMission`, `AObjective.SetCompleted`, `PlayerProgressionManager.AddExpPoints`, `AddCredits`,
  `CheckBossCompletion`, `TrySpendCredits`, `ACardSO.IncreaseCardSoldStat`, `IncreaseCardDestroyedStat`,
  `CardEquipmentManager.UnlockCard`, `AcquireCardPermanently`, `BuyAllCardsDebug`, `VehicleGarageManager.UnlockVehicle`,
  `UnlockVinyl`, `UnlockPart`, `UnlockDriverTitle`, `GameStatisticsManager.OnEnterGasStation`, `OnSlipstreamCompleted`,
  `OnTopSpeedCompleted`, `OnDriftEndsDuration`, `OnRunCurrencyAdded`, `OnRunCurrencySpent`,
  `RaceStatisticsManager.OnLevelCompleted`, `OnLevelFailed`, `OnRunEnded`, `TryRegisterHighScore`,
  `TryRegisterHighestLevelAchieved`, `TryRegisterCoinHighScores` (30).
- **Run state** (which run is a sandbox run; they never skip the game's method): `MainMenuPanel.OnSingleplayerButton`,
  `OnTutorialButton`, `OnMultiplayerButton`, `MainMenuManager.OpenVehicleSelection`,
  `ReturnToMainMenuFromVehicleSelection`, `StartGameWithVehicle`, `GameCoordinatorManager.StartNewSingleplayerGame`,
  `TryRestoreSingleplayerGame`, `StartFullTutorial`, `StartQuickTutorial`, `StartNPCTestingArea`,
  `StartMultiplayerFromHost`, `SessionBackup.CaptureSnapshot` (13).
- **Perks** (only change anything in a sandbox race): `CardShopInfo.get_CardPrice`, `CardContainerSO.GetRerollPrice`,
  `ACardSO.get_SellingPrice`, `CardExtensions.IsCardAvailable`, `CardEquipmentManager.get_CurrentModSlotCount`,
  `CardShop_AllCardsSelector.Awake`, `CardDisplayGroup.UpdateCards`, `LevelGeneratorTileSelector.GetRandomTiles`,
  `RoadPathGenerator.GeneratePath` (9).
- **SANDBOX button**: `MainMenuPanel.Awake` (postfix).
- Sandbox scenery (Scenery.cs) installs no Harmony patches.

Not patched on purpose: `ACardSO.IncreaseCardAcquiredStat` (see Risks and limits), the demo gates
(`MissionsDisabled`, `XpLockedForDemo`, `AchievementsDisabled`, `CreditsLockedForDemo`) and
`BossProfileSO.SetDiscovered`, whose code is shared with many unrelated methods.

## Settings (`BepInEx/config/rogue.sandbox.cfg`, also in Rogue Hub)

| Setting | Default | What it does |
|---|---|---|
| `[General] Enabled` | true | Adds the SANDBOX button to the main menu (takes effect on the next main menu). |
| `[Mods] ExtraSlots` | 10 | Mod slots added in sandbox runs (0-15; 10 = 20 slots). Applies at once. |
| `[Mods] AllCardsPicker` | true | Show the game's all-cards picker in the shop during sandbox runs. |
| `[Run] LengthMultiplier` | 1 | Road length of each sandbox race, x1-x5 (next race). |
| `[State] RunIsSandbox`, `[State] SnapshotMarks` | | Written by the plugin (hidden in Rogue Hub): whether the stored run is a sandbox run, and which of the game's recent run snapshots were sandbox runs. Don't edit. |
| `[Scenery] ...` | | Sandbox scenery (Scenery.cs). |

## Log lines

- `Sandbox x.y.z loaded. N record guards, N run-state hooks, N sandbox perks installed; the SANDBOX button is on the main menu.`
- `Sandbox x.y.z loaded WITHOUT the SANDBOX button: these guard / run-state patches failed, ...` (with the list)
- `[Sandbox] SANDBOX button added under Singleplayer (layout group)` / `(placed under the last button)`
- `[Sandbox] SANDBOX button not added: ...`
- `[Sandbox] SANDBOX button not added ([General] Enabled is off)`
- `[Sandbox] SANDBOX chosen: the next car you start with begins a sandbox run`
- `[Sandbox] SANDBOX run: started from the SANDBOX button` / `retry / restart of the sandbox run` / `restored run snapshot (recorded)`
- `[Sandbox] normal run (sandbox off): ...` (only when a sandbox run was stored before)
- `[Sandbox] sandbox race: free cards, 20 mod slots, road x1, records guarded` / `[Sandbox] left the sandbox race`
- `[Sandbox] shop: every card is free in this sandbox run`
- `[Sandbox] mod slots: 20 (10 extra in sandbox runs)`
- `[Sandbox] all-cards picker shown in the shop` / `hidden`
- `[Sandbox] Current Mods: 12 mods, shown in 2 rows`
- `[Sandbox] road length x3: 31 tiles, 11234 m (about 255 s of 255 s asked), picked in 4 ms; loading the tiles...`
- `[Sandbox] road ready: 11180 m to the finish line, 9.4 s after the tiles were picked (tile scenes loaded)`
- `[Sandbox] skipped (sandbox run): leaderboard upload (LeaderboardsManager.PublishEntry): not uploaded (sandbox)` (and one line per other record, once per run)
- `[Sandbox] perk ... not installed: ...` (that perk is off; the guards are unaffected)

## Risks and limits (0.1.0, not yet played)

- **Back up `player.dat` first** (see the top). A record path the guards miss would write into it.
- Known gap: bosses you meet in a sandbox run are marked as discovered in the compendium (`BossProfileSO.SetDiscovered`,
  whose code is shared, so it is not patched).
- **Never Continue a sandbox run without Sandbox installed** (or with it removed / disabled in BepInEx): the game
  would treat the restored run as a normal run and its records would be written.
- Known gap: the per-card **times acquired** count still counts in a sandbox run. `ACardSO.IncreaseCardAcquiredStat`
  is deliberately not patched: its game code is the same as `ACardSO.OnCardAcquired`, which every card's on-acquire
  effect runs through, so skipping it would break card effects.
- The two-row Current Mods layout and the cloned main-menu button are untested in game.
- Locked mods include boss or story cards that may expect a progression state; watch the log for errors.
- Longer roads keep every road tile loaded for the whole race: loading time and memory grow with the multiplier
  (the log line gives the load time). More tiles also mean more overlap fallbacks in the game's generator.
- If the game ever restores a run snapshot the plugin did not see being taken, it is treated like the most recent
  snapshot it did see (sandbox or not).
