# Sandbox

Current version: 0.2.0

A separate run mode for trying builds: a **SANDBOX** button on the main menu (right under Singleplayer) starts a run
in which every card is free and you can have 20 mods. Sandbox runs stay off your records (except the known gaps under Risks and limits).

> **Back up `player.dat` before your first sandbox test.** It is in
> `%USERPROFILE%\AppData\LocalLow\<game company>\Driving Rogue\` (copy the whole folder). The guards below are built to keep a sandbox run out of the save, but this
> is version 0.2.0: the sandbox maps and multiplayer have not been played yet.

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
- **Road length** x2 by default, x1 to x5 (`LengthMultiplier`): each race asks the game's road generator for that many
  times its usual length. The race timer follows the real road length by itself; the score target does not (longer = easier).
- **Sandbox maps**: every sandbox race is on a **6-lane, 30 m road** (the game's are 4 lanes, 20 m) with all buildings
  and props removed; only the road and what is built around it stays (curbs, sidewalks, guardrails, street lights). See below.
- A **SANDBOX** tag top-left on the HUD; on the results screens it reads **"Not uploaded (sandbox)"**.

**Continue**, **Retry** (after a defeat or from the run summary), **Restart** and **Next race** keep the run in sandbox
mode. Every other run start is a normal run: Singleplayer, the tutorials, multiplayer, and restoring a normal run
snapshot or checkpoint. The Racing Line and Pursuit score categories are unchanged.

**Multiplayer.** A multiplayer run is a sandbox run when the host turns it on and every player agrees (the
multiplayer part, `Multiplayer*.cs`; `Plugin.Recompute` / `ActiveNow` call into it). The road is then the host's: the
length and the tile choice come with the host's tile list (clients never pick tiles), and the map settings (on/off,
width, lanes) come from the host's run settings (`Multiplayer.Wide` / `Width` / `Lanes`, read by `WideRoads.Apply` /
`ClampW` / `ClampN`) before each client loads its tiles. **Every player needs the same Sandbox version**: a client
without it would see AI cars on the wide lanes (+-12.5 m) driving through the game's buildings.

## Sandbox maps (`Maps.cs` = class `WideRoads`, `Maps.Build.cs`, `Maps.Runner.cs`, 0.2.0)

With `[Maps] WideRoads` on (the default), every sandbox race is driven on a wide road built at runtime along each road tile's own path, with the tile's
buildings, trees, props and old road hidden. The race path itself never changes, so traffic, AI racers, obstacles, the
timer, Racing Line and Police keep working on it; only the width does. Together with the x2 length (RoadLength.cs).

- **Width and lanes.** The game's road width and lane count are global (`RoadTileContainerSO.roadWidth` /
  `roadLaneCount`, 20 m / 4). Before each race's tiles exist they are set to `Width` / `Lanes` (30 m / 6; the lane count
  is lowered so each lane is at least 3.5 m), and
  `RunWorldManager.UpdateLaneInfo` rebuilds the lane list (the game only does that at run start). Lane centres at 30 m /
  6 lanes: -12.5, -7.5, -2.5, 2.5, 7.5, 12.5 m. Where it is set: the host / single-player in the `GetRandomTiles` prefix,
  a multiplayer client in Multiplayer.cs's `LevelGenerator.GenerateMultiplayerLevelAsClient` prefix (host's values),
  and every other level load in a `LevelGenerator.SpawnTilesCoroutine` prefix (idempotent). The first race of a run is picked
  while the main menu is still loaded (`GameCoordinatorManager.SetupGameInfo` from `StartNewSingleplayerGame`), so the
  tile-pick hooks check the run flag, not the scene (`Plugin.TilePickNow`); length x2 applies to that race too. AI racers read the container when they
  start, so it stays wide for the whole race; it is put back when the sandbox race ends (also a multiplayer race end or
  disconnect), on a normal run, on the next race that isn't a sandbox race, on the error breaker and when the plugin unloads.
- **The road.** When the race path is built (`RoadPathGenerator.GeneratePath`, after every tile loaded, still on the
  loading screen), the tiles' waypoints are joined into one centre line, resampled every 2.5 m, with max(40 m, 1.5 x
  width) of straight road added before the start and after the end (if the waypoint line is more than 0.5 m off the
  race spline, the spline is sampled instead). Every tile is then built at once, under `fx_WideRoad` in its Road Network
  (it unloads with the tile): one mesh with 9 materials (asphalt; white lane dashes 3 m on / 9 m off; a double yellow
  centre line when the lane count is even; solid edge lines; 0.15 m curbs; 4 m sidewalks; a ground band out to 80 m that
  slopes gently down; a **guardrail** 0.45-0.8 m high with posts every 5 m on the wall line; **street lights**: a pole every
  40 m, alternating sides, 8 m tall with an arm and a lit lamp head over the road), a Street-layer collider (flat, with a
  bevelled curb, out to 6 m past the road edge) and invisible walls `fx_WideWall` (Guardrail layer, 4.5 m past the road
  edge, 3 m high, plus a cross wall at both ends of the road). The stock tile's physics materials are reused.
- **Street lights** (`StreetLights`): besides the lamp heads, a pool of 8 real point lights moves to the 8 poles nearest
  the camera every 0.5 s.
- **The old tile is hidden, not removed.** At once (loading screen): every non-trigger collider under Biomes and Road
  Network is disabled (old road, guardrails, buildings, cones), so no car meets an old wall or building, and every
  `Light` there is switched off, and every renderer there is listed. Then each listed renderer gets `forceRenderingOff`
  (buildings, scenery, the 20 m road, tunnels, bridges, overhead cliffs): the first two tiles at once, the rest a slice
  per frame (`BudgetMs`; skipped only while the pause menu is open). Triggers (finish line, reverb zones) stay. Colliders
  added later (CurbFeel's `CurbFeel_Ramp`) are switched off on the tile the camera is on and the next one within
  0.25 s (from the build until 10 s after the race is ready; after that each of the two about every 1 s, which also
  covers CurbFeel's F9 / F10 reset). A round-robin re-check of every tile (one tile per 0.15 s, so with ~20 tiles each
  tile about every 3 s; after 15 s each tile at most every 5 s, then also renderers and lights) catches the rest. Sandbox
  scenery (Scenery.cs) leaves these tiles alone and gives back anything it hid there first.
- **Hairpins.** Where the road comes back within reach of itself (more than max(60 m, 2 x width) further along the
  path), the walls, guardrail, raised sidewalks and street lights are left out there, so they never cut across the
  other part of the road.
- **No gas stations or car washes** in sandbox races (`RunWorldManager.ShouldSpawnGasStation` / `ShouldSpawnCarWash`
  answer false; the host decides, clients get its tile list): their side lanes would be walled off, cards are free in
  sandbox, and PitStop's F2 refills health. If either hook fails to install, sandbox races keep the game's road.
- **Finish line.** It is spawned for a 20 m road; its trigger is measured and the finish object stretched sideways to
  the road width + 4 m, and put back afterwards.
- **Other plugins.** Racing Line and TrafficDensity read the live road width and lane list, so they follow. Police
  daredevils follow Racing Line's line; Police patrols keep to their fixed +-7.5 m (`DriveLimit`, Police's own file),
  so they don't use the outer lanes yet. CurbFeel only moves walls named Guardrail Regular/Wider, so `fx_WideWall` is
  left alone; its `CurbFeel_Ramp` colliders (built at a tile's first scan, +1 s and +4 s) are switched off on the
  camera's tile and the next one within 0.25 s, elsewhere by the round-robin re-check (about every 3 s per tile).
- **Limits.** The city is a plain road: no buildings at all. Where two road sections overlap (tight bends, crossings),
  their ground strips can flicker against each other (z-fighting). The game's overhead tunnels and bridges are hidden
  with the rest. `Assets/maps/build_tile.py` / `extract_waypoints.py` are reference only; the `.wtl` meshes they make
  stay local (git-ignored, see MAPS.md).

## Multiplayer (`Multiplayer.cs`, `MultiplayerNet.cs`, `MultiplayerLobby.cs`)

The host switches SANDBOX on in the lobby panel (bottom-left of the main-menu lobby, uGUI sorting 471) or with `[Multiplayer] Sandbox`. Every player needs Sandbox, the same version, and all record guards installed; otherwise the run is a normal run, and the log and a toast say why. A sandbox multiplayer run uses the host's settings for everyone: road length, mod slots, the all-cards picker, the sandbox map (width and lanes) and scenery stripping. Every player gets free cards and stays off every record, leaderboards included. Only the host picks the road; clients load the host's tile list (so the length multiplier syncs by itself). Each client sets the host's width and lanes before its tiles spawn and reports back, and the host warns on a mismatch. Sandbox talks only over its own Steam networking channel 7742 (Police uses 7741), never the game's Mirror network state. LAN games can't be sandbox.

## Scenery (`Scenery.cs`)

With `[Scenery] StripBuildings` on (default), every sandbox race tile that the maps did not take over (only when the
wide build fails: the maps hide their own tiles) is
stripped down to the road and what is built around it:

- **Kept**: the whole "Road Network" (road mesh, ground collider, invisible guardrail walls, the maps' `fx_Wide*`), and
  under "Biomes" anything whose own name or a parent's contains road, sidewalk, walkway, kerb, curb, sides_, base_h,
  Ground_Easy/Normal/Hard/Pro, Moss_Plane, guardrail, guard_rail, railing, railgenerator, barrier, bridge, tunnel, or a
  street-light word (light, lamp, pole), plus anything on the Street / Guardrail / WeatherBlocker layers (11 / 15 / 28).
- **Removed**: everything else under "Biomes" (buildings, trees, props, ads, walls, fences, cones, water):
  `Renderer.forceRenderingOff` (its `enabled` flag untouched, so CurbFeel and LODGroups see the same tile), and every
  enabled non-trigger collider on those objects is disabled, except ground (names with ground / moss / floor / terrain,
  layer 11, terrain colliders).
- **Lights** (`HideLights`): lights under "Biomes" are switched off except street lights (a parent named light / lamp /
  pole, or the light's own object named lamp / pole / streetlight) and tunnel / bridge lights.
- No stand-in blocks any more (0.1.x put a grey block where each building stood); a leftover `fx_SandboxBlocks` is removed.
- Work is spread over frames (`BudgetMs`), one tile opened per poll; renderers, colliders and lights all come back when the
  sandbox race ends, `StripBuildings` is switched off, after repeated errors, or when the maps take a tile over.

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
  `CardShop_AllCardsSelector.Awake`, `CardDisplayGroup.UpdateCards`, `LevelGeneratorTileSelector.GetRandomTiles`
  (also sets the race's road width), `RoadPathGenerator.GeneratePath` (starts the road build),
  `RunWorldManager.ShouldSpawnGasStation`, `ShouldSpawnCarWash` (postfixes, false only in wide races),
  `LevelGenerator.SpawnTilesCoroutine` (prefix: sets the road width; never skips) (12). The multiplayer hooks are
  listed in `Multiplayer.cs`.
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
| `[Run] LengthMultiplier` | 2 | Road length of each sandbox race, x1-x5 (next race; the host's in multiplayer). A config from 0.1.x still at x1 is moved to x2 once. |
| `[Maps] WideRoads` | true | Sandbox races on the sandbox map (wide road, scenery hidden); off = the game's own road, where Scenery strips the buildings (next race; the host's in multiplayer). |
| `[Maps] Width` | 30 | Width of the sandbox road, 20-40 m (next race; the host's in multiplayer). |
| `[Maps] Lanes` | 6 | Lanes on the sandbox road, 4-8, lowered so each lane is at least 3.5 m (next race; the host's in multiplayer). |
| `[Maps] StreetLights` | true | Street-light poles every 40 m and 8 real lights that follow you (next race). |
| `[Maps] BudgetMs` | 3 | Milliseconds per frame spent hiding the old scenery after the race loads, 0.5-10. |
| `[Multiplayer] Sandbox` | false | Host only: offer SANDBOX for the next multiplayer run (same as the lobby panel switch). Every player needs the same Sandbox version. |
| `[State] RunIsSandbox`, `[State] SnapshotMarks`, `[State] DefaultsVersion` | | Written by the plugin (hidden in Rogue Hub): whether the stored run is a sandbox run, which of the game's recent run snapshots were sandbox runs, and which default changes were applied. Don't edit. |
| `[Scenery] StripBuildings` | true | Sandbox races: remove everything around the roads that isn't road, sidewalk, curb, ground, guardrail, railing, barrier, bridge, tunnel or street light (renderers and their colliders; see Scenery above). |
| `[Scenery] HideLights` | true | Also switch off the lights of the removed scenery; street, tunnel and bridge lights stay on (the host's in multiplayer). |
| `[Scenery] BudgetMs` | 2 | Milliseconds per frame spent stripping a newly loaded tile, 0.5-10. |

## Log lines

- `Sandbox x.y.z loaded. N record guards, N run-state hooks, N sandbox perks installed; the SANDBOX button is on the main menu.`
- `Sandbox x.y.z loaded WITHOUT the SANDBOX button: these guard / run-state patches failed, ...` (with the list)
- `[Sandbox] SANDBOX button added under Singleplayer (layout group)` / `(placed under the last button)`
- `[Sandbox] SANDBOX button not added: ...`
- `[Sandbox] SANDBOX button not added ([General] Enabled is off)`
- `[Sandbox] SANDBOX chosen: the next car you start with begins a sandbox run`
- `[Sandbox] SANDBOX run: started from the SANDBOX button` / `retry / restart of the sandbox run` / `restored run snapshot (recorded)`
- `[Sandbox] normal run (sandbox off): ...` (only when a sandbox run was stored before)
- `[Sandbox] sandbox race: free cards, 20 mod slots, road x2, 6 lanes / 30 m, records guarded` / `[Sandbox] left the sandbox race`
- `[Sandbox] settings updated to the 0.2.0 defaults: road length x1 -> x2 ...` (once, on a config from 0.1.x)
- `[Sandbox] shop: every card is free in this sandbox run`
- `[Sandbox] mod slots: 20 (10 extra in sandbox runs)`
- `[Sandbox] all-cards picker shown in the shop` / `hidden`
- `[Sandbox] Current Mods: 12 mods, shown in 2 rows`
- `[Sandbox] road length x3: 31 tiles, 11234 m (about 255 s of 255 s asked), picked in 4 ms; loading the tiles...`
- `[Sandbox] road ready: 11180 m to the finish line, 9.4 s after the tiles were picked (tile scenes loaded)`
- `[Sandbox] skipped (sandbox run): leaderboard upload (LeaderboardsManager.PublishEntry): not uploaded (sandbox)` (and one line per other record, once per run)
- `[Sandbox] perk ... not installed: ...` (that perk is off; the guards are unaffected)
- Scenery (playtest checklist: no buildings or props left, no invisible obstacles, street lights still lit, everything back after the race):
  - `[Sandbox] scenery: tile Hard_7.1: 1180 renderers hidden (240 road renderers kept), 36 colliders off, 95 lights off (12 street / tunnel / bridge lights kept)` (`, 1 old block set(s) removed` after a hot reload from 0.1.x)
  - `[Sandbox] scenery restored (Sandbox ended): ... renderers, ... colliders and ... lights back on in ... tiles` (or `StripBuildings off`, `errors`, `plugin unloaded`)
  - `[Sandbox] scenery: tile ... left as it is after an error: ...` / `[Sandbox] scenery error (n/3): ...` / `scenery switched off for this session after repeated errors (the tiles are restored): ...`
- Maps (playtest checklist: the lane list, RacingLine's "+-14 m" limit, no out-of-bounds respawns, no buildings, lit lamp heads, width back in the menu; in multiplayer the client's own lines with the same offsets; on hairpin tiles (`pinched` > 0) no fall off the street collider's edge into the void; obstacles, pickups and traffic stay visible and solid; `road built` and `race ready` appear and `build failed` does not):
  - `Sandbox x.y.z loaded. ... Sandbox maps: 6 lanes, 30 m, road x2, scenery hidden, street lights.` / `Sandbox maps off ([Maps] WideRoads): ...` / `Sandbox maps unavailable (a hook failed): ...`
  - `[Sandbox] maps: road 20 m / 4 lanes -> 30 m / 6 lanes (stock 20 m / 4); lane offsets [-12.5, -7.5, -2.5, 2.5, 7.5, 12.5], road 30 m`
  - `[Sandbox] maps: 8 lanes asked, 5 used (lanes at least 3.5 m on a 20 m road)`
  - `[Sandbox] maps: no finish line found to widen`
  - `[Sandbox] maps: finish trigger width unreadable (0.0 m), left as it is`
  - `[Sandbox] maps: no gas station this race (its side lane would be walled off; cards are free in sandbox and PitStop's F2 refills)` (or `no car wash`)
  - `[Sandbox] maps: road container found by search (1)` (only if GeneralReferencesData didn't give it)
  - `[Sandbox] maps: 12 tiles, 431 waypoints, 1790 samples (4402 m) from waypoints (Catmull-Rom), racer-to-spline gap 0.12 m, 0 pinched samples; building the road now` (or `from the race spline (...)`)
  - `[Sandbox] maps: finish line 'FinishLine(Clone)' 22.0 m -> 34.0 m wide (scale x 1 -> 1.55)`
  - `[Sandbox] maps: finish trigger 40.0 m wide, already covers 34 m`
  - `[Sandbox] maps: road built in 180 ms (110 street lights); hiding the scenery of 10 more tiles over the next frames`
  - `[Sandbox] maps: tile 3 Hard_7.1 (500): 160 waypoints, samples 412-1056, 12 pinched, 30000 visual / 3800 street / 1300 wall triangles, 16 street lights, 2950 renderers hidden, 41 colliders and 30 lights off, materials street 'Road' wall 'Wall', 9.1 ms`
  - `[Sandbox] maps: race ready, 12 tiles in 2.1 s (110 ms of work), ... visual triangles, ... renderers hidden, ... colliders and ... lights off`
  - `[Sandbox] maps: tile 3 ...: re-check caught 4 colliders, 0 renderers and 0 lights added later` (CurbFeel ramps)
  - `[Sandbox] maps: tile 3 ...: near-car check caught 4 colliders added later` (CurbFeel ramps on the camera's tile or the next)
  - `[Sandbox] maps restored (race not wide): ...` (a wide race followed by one with the maps off: the old road and its light pool go)
  - `[Sandbox] maps: road path built for a race that is not a sandbox race; width put back` (should not appear; the width never stays wide without the wide road)
  - `[Sandbox] scenery: tile ... handed to wide roads (...)` (Scenery.cs)
  - `[Sandbox] maps restored (left the sandbox race): 12 tiles, ... renderers, ... colliders and ... lights back on, 8 pooled street lights removed`
  - `[Sandbox] maps: road width restored to 20 m / 4 lanes; lane offsets [-7.5, -2.5, 2.5, 7.5], road 20 m`
  - `[Sandbox] maps: build failed, this race keeps the game's road: ...` / `maps switched off for this session after repeated errors ...` / `maps error (n/3): ...`

## Risks and limits (not yet played)

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
- The maps (0.2.0) are not played yet: check the playtest lines above. Police patrols keep to the middle 15 m of a
  wide road (their own fixed limit). Multiplayer sandbox needs the multiplayer part (Multiplayer*.cs) and the same
  Sandbox version on every player.
- If the game ever restores a run snapshot the plugin did not see being taken, it is treated like the most recent
  snapshot it did see (sandbox or not).
