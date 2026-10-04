# RogueHub

BepInEx 6 IL2CPP plugin for **Driving Rogue**: one menu for every mod. Every installed plugin's settings appear in it
automatically (read from BepInEx, no change to the plugin needed), drawn in the game's own style: toggles, sliders with
a number box, lists, key binds and buttons, with a description of each setting.

Current version: **0.1.0**

Design doc: "Rogue Hub: one menu for every mod" (claude.ai doc 99d98c4b-7b01-4f2a-bf9f-12982034b3a3), version 1.

## Opening it

| | Controller | Keyboard and mouse |
|---|---|---|
| Hub (game paused) | MODS in the pause menu, or the quick menu's last row | MODS in the pause menu, or Shift + backtick (`` ` ``) |
| Quick menu (while driving, never pauses) | LB + RB together (both within 0.15 s) | backtick (`` ` ``) |

Outside a race (the game's menus), LB + RB or the backtick key open the hub straight away. Opening the hub while driving
pauses the race through the game's own pause; closing the hub then resumes it. Opened from the pause menu, it returns to
the pause menu.

## Hub

| | Controller | Keyboard and mouse |
|---|---|---|
| Move | D-pad, left stick | Arrows, click |
| Change a value | Left / right (hold to speed up) | Left / right (Shift = x10), drag the slider |
| Type an exact value | A opens a number pad | Type the digits, or Enter / click the number box |
| Switch, next item, run a button | A | Enter, click |
| Reset to default | Y | Delete |
| Add to / remove from the quick menu | X | Right-click |
| Tabs | LB / RB | Tab or Page Down (next), Page Up (previous), click |
| Search every setting | | Start typing (Backspace deletes, Esc clears) |
| Back, close | B | Esc |

- **Tabs** by area: Faves (the quick menu's rows), Driving (CurbFeel, PitStop), Camera (DriverCam, HeadLook), World
  (TrafficDensity, Police), Score (RacingLine), Audio (EngineAudio), System (Rogue Hub itself, HotReload, anything new).
- **Mod cards** show a live status line and an ON / OFF pill; X on a card switches the mod's master switch.
- **Numbers** with a known range get a slider and a number box; values typed outside the range are clamped (the box
  flashes red). A **GAME** (or **AUTO**) chip marks a value that means "keep the game's own value": step left past the
  minimum to switch to it, right to leave it, or click the chip.
- An orange dot marks a value changed from its default. Debug and calibration settings sit in a collapsed **ADVANCED**
  group per mod.
- Changes apply as soon as each mod reads them; the description panel says when a mod applies one later. A mod's
  .cfg file is written a second after the last change and when the hub closes (DriverCam's per-car preset files are
  written by DriverCam itself on each change).

## Quick menu

A short list on the left with the rows you picked (X in the hub); the defaults are Racing line, Police mode, Traffic
multiplier, Refill health and Engine sound, and the last row opens the hub. D-pad / arrows pick and change, A / Enter
use, B / Esc / backtick close; it closes itself after 6 seconds untouched. Only the d-pad, face buttons and arrow keys
drive it: the left stick and triggers stay the car's. While it is open, the game's d-pad, face-button and
arrow-key bindings are switched off so the menu doesn't also steer, brake or fire the ability; the triggers, the stick
and WASD keep driving. They're switched back on when it closes.

## Notifications

One stack at the top right for every mod (HubLink.Toast): PitStop's refill results (a refill pressed in the hub
while paused is queued until the race resumes), TrafficDensity's multiplier changes, button results. Repeats merge with a count. With notifications off, the mods show their own messages again.

## For plugin authors: HubLink (optional)

`source/Shared/HubLink.cs`, linked as source like `Perf.cs`, talks to the hub through AppDomain data `rogue.hub.v1`
(only built-in .NET types, no assembly reference):

- `HubLink.Meta(label, min, max, step, unit, scale, game, gameShows, advanced, applies, gameLabel, hidden)`: a tag
  string for a `ConfigDescription` (friendly name, slider range, units, a GAME / AUTO value, Advanced / hidden).
- `HubLink.Action(guid, id, label, description, Func<string>)`: a button; the returned line becomes a notification.
- `HubLink.Status(guid, Func<string>)`: the live line on the plugin's card.
- `HubLink.Toast(guid, title, detail, kind)`: a notification (`info`, `good`, `warn`, `bad`). `HubLink.HubPresent`
  tells a plugin whether to leave its own on-screen message out.

Used by TrafficDensity, CurbFeel and PitStop.

## What it changes in the game (all undone on close)

- While the hub is open over a game menu: the game's own popup lock (navigation, cancel and hotkeys of the menu
  underneath), a full-screen backdrop that catches the mouse, the cursor shown.
- While the quick menu is open: the binding overrides described above.
- The pause (only one it started itself).
- A MODS button added to the pause menu (a copy of Resume, with its hotkey removed).
- While the hub or the quick menu is up, CurbFeel hides its status panel (HubLink). While the hub is up, DriverCam's
  on-screen button is switched off through DriverCam's own `UI.ShowButton` setting (not saved) and switched back on, and
  DriverCam's file saved, when the hub closes; if you change that setting in the hub yourself, your value is kept. If the
  game quits or crashes with the hub open, the button can stay off: switch `UI Show button` back on in the hub's Camera
  tab. DriverCam's own settings panel, if you left it open, and other mods' debug overlays still draw over the hub. The
  quick menu sits right of DriverCam's button (at its default place) instead.

Five game parts (pause, race state, menu lock, driving-button block, MODS button) are checked at startup by name; a missing one switches only that part off (logged). Five errors switch
the hub off for the session and give the game back its menus, input, cursor and pause.

## Settings (`rogue.hub.cfg`)

| Key | Default | Notes |
|---|---|---|
| `General.Enabled` | true | master switch |
| `Open.QuickKey` | Backquote | quick menu key; Shift + it opens the hub. Letters, digits, arrows, Enter, Esc, Space, Tab and the like can't be used (they type or drive the menus): backtick is used instead |
| `Open.ControllerLBRB` | true | LB + RB opens the quick menu |
| `Open.PauseMenuButton` | true | MODS button in the pause menu (from the next time the pause menu is created) |
| `Quick.Items` | Racing line, Police mode, Traffic multiplier, Refill, Engine sound | the quick menu's rows (edit with X in the hub) |
| `Quick.AutoCloseSeconds` | 6 | 2-30 |
| `Notifications.Enabled` | true | the shared notification stack |
| `Notifications.Seconds` | 3.5 | 1-10 |

## Log (`/game-log RogueHub`)

- `RogueHub 0.1.0 loaded`, `[RogueHub] game check: pause OK, race state OK, menu lock OK, driving-button block OK, MODS button OK`,
  `MODS button hook installed`.
- `[RogueHub] MODS button added to the pause menu (layout group)` (or `placed under the last button`) the first time the pause menu is created. In the live log this is right after startup, at the main menu, not in a race.
- On first open: `[RogueHub] TMP fonts: ...`, `fonts: headings conthrax-sb SDF, text ...`, `catalog: N plugins, M settings and buttons`.
- Quick menu: `quick menu open (n rows, m game bindings paused)`.
