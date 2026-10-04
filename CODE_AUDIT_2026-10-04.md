# Code audit - DriverCam repo plugins (2026-10-04)

**Scope.** Read-only audit of `source/` (about 23k lines: DriverCam, CurbFeel, TrafficDensity, RacingLine, Police, PitStop, EngineAudio, HeadLook, RogueHub, HotReload, Shared). No files were changed other than this report, and nothing was built.

**Method and limits.** The weekly usage budget was at 75% when the audit started, close to the 79% stop line. The audit was therefore pattern-driven (Grep over the whole tree, then targeted reads of the hits). It was not a line-by-line review. No web pages were fetched. Guidance used is from memory: Microsoft C# coding and performance conventions (avoid LINQ, allocations and string building in hot paths), Unity performance guidance (no per-frame Find/GetComponent, `Camera.main`, IMGUI cost, destroy created Texture2D/Material/Mesh/RenderTexture), and Il2CppInterop/BepInEx notes (inject types before AddComponent, Unity-object null semantics). The repo `CLAUDE.md` rules were checked as far as the greps allow. Another session was editing plugins at the time, so line numbers may drift.

**Overall.** The codebase is in good shape. Per-frame bodies are mostly throttled and try/caught, and Unity objects are held and destroyed in most places. Perf scopes are used (`Perf.Scope`). `RegisterTypeInIl2Cpp` is called before every `AddComponent<T>` found. The one thread (`TireSqueal.Prepare`) runs plain .NET only and hands results to the main thread. The findings below are mostly leaks on non-hot paths, a few Unity-null idiom slips and some IMGUI string churn.

---

## FAIL (real bugs / rule violations)

### F1. DriverCam - `DriverCam/CarExporter.cs:64-66` - baked mesh leaked
`new Mesh()` is created per SkinnedMeshRenderer and filled with `BakeMesh`. The file has no `Destroy` call at all, so every export leaks one native Mesh (CPU + GPU memory) per skinned renderer. This is a leak per export, not per frame, but meshes are large.
**Fix:** after the vertex/normal/triangle read (around line 100, once `verts`/`subTris` are copied), `if (baked) UnityEngine.Object.Destroy(mesh);`. Also destroy it on the `continue` paths (the GPU-read failure path at ~line 99).

### F2. DriverCam - `DriverCam/DriverCamBehaviour.cs:408` and `:118`, `:222`, `:211`, `DriverView.cs:116` - `?.` / `??` on Unity/Il2Cpp objects
`ctrl?.CurrentCameraMode?.cameraModeName`, `m?.cameraModeName ?? "?"`, `current?.cameraModeName`, `mgr.gameplaySettings?.SetCameraMode`, `_ctrl?.CurrentCamera`. `?.` only checks the managed reference. A destroyed Unity object (or an Il2Cpp wrapper with a dead native pointer) passes the check and then throws, or reads garbage, on the next access. Line 408 runs every OnGUI event, while the call at 211 is already inside a `try`.
**Fix:** use `if (ctrl == null) ... else ...` (Unity `==` handles destroyed objects) for `ctrl`, `mgr` and the mode objects. For the ScriptableObject-style types (`CameraModeSO` etc.), `== null` also works because they derive from `UnityEngine.Object`. Cache the mode name string when the mode changes rather than looking it up in OnGUI.

---

## WARN

### W1. DriverCam - `DriverCam/DriverCamBehaviour.cs:373-440` (OnGUI) - per-event string and Rect allocation
`DrawGui` runs on every IMGUI event (Layout + Repaint + input events, i.e. at least 2 per frame) while the panel or the button is visible. It builds `$"DriverCam   camera: {modeName}"`, `$"[ {tabs[t]} ]"`, `$"  {names[_mirrorIndex]}"`, `$"Move step: {step:0.###} m ..."` and similar each time, and it sets `GUI.skin.*.fontSize` for three styles each event.
**Why:** garbage per frame while the panel is open (and the always-on button path costs little). It is tolerable but easy to remove.
**Fix:** cache the strings and rebuild only when their inputs change (mode name, page, step, mirror index), and set the fonts only when `UiScale` changes. Optionally skip non-Repaint/non-input events early.

### W2. DriverCam - `DriverCam/DriverCamBehaviour.cs:32-40, 373-399` - exception handling
`Update` logs every exception (`Plugin.Logger.LogError(e)`) with no throttle, so a persistent fault gives a log line (plus a stack trace) every frame, which hurts fps and floods the log. OnGUI correctly logs once (`_guiErrorLogged`).
**Fix:** mirror the OnGUI approach in `Update`: log once, or at most every few seconds, and count repeats. `LateUpdate` (line 144) should be checked for the same.

### W3. DriverCam - `DriverCam/DriverCamBehaviour.cs` - no `OnDestroy` / restore path found
No `OnDestroy` was found in `DriverCamBehaviour.cs`, while RacingLine, EngineAudio, Police and HeadLook all have one. Resources are held with `Keep.Hold` (so they are not GC'd), but cockpit/mirror cameras, RenderTextures and the body-visibility changes are only restored on toggle-off paths. If `Plugin` is unloaded (hot reload) or the behaviour is destroyed (scene change), the cockpit root, mirror RenderTextures and materials can leak and renderers can stay hidden.
**Fix:** add `OnDestroy` that calls the same teardown as toggle-off (restore renderer visibility, destroy mirror cameras/RenderTextures, destroy cockpit root).

### W4. DriverCam - `DriverCam/TextureSaver.cs:17-18` - `RenderTexture`/`Texture2D` created before `try`
They are destroyed in `finally`, which is fine, but `new RenderTexture`/`Texture2D` sit outside the `try`: an exception in the second constructor would leak the first. Minor.
**Fix:** move both creations into the `try` (declare as null first) and null-check in `finally`.

### W5. Shared/RogueHub - `RogueHub/Ui.cs:216`, `Shared/UiKit.cs:75`, `Shared/ScoreRows.cs:115,234` - `Resources.FindObjectsOfTypeAll`
These walk every loaded object. They appear to be one-shot or gated lookups (comments in `CurbFeel/ScrapePatches.cs:28` and `UiKit.cs:20` say the result is cached and not repeated when nothing is found), which is acceptable. `ScoreRows.cs:115/234` should be confirmed to be called from a cached lookup, not from a per-frame row tick.
**Fix:** verify that the results screen path caches the controller, and keep a "retry after N seconds" throttle when the result is null (as ScrapePatches does).

### W6. CurbFeel - `CurbFeel/TrafficTuner.cs:64` - LINQ + string.Join + `ToString("0.##")` for the stats
`_lanes.Select(...)` plus `string.Join` allocates. If this runs per frame (UpdateStats is in `WallShifter.cs:36`, and `Stats.Lanes` is a display string), it is per-frame garbage.
**Fix:** rebuild only when `_lanes` changes (compare a version counter or the lane count/values), otherwise reuse the cached string.

### W7. CurbFeel - `CurbFeel/WallShifter.cs:725-783, 828` - created objects need explicit teardown
`CurbFeel_Ramp`, `CurbFeel_WallView`, the two meshes, `_wallMat` and `_debugMat` are created at runtime. Only `DestroyDebugMaterial` was seen. Confirm that the meshes (`mesh`, `wm`), `_wallMat` and the GameObjects are destroyed when the feature is toggled off or the map is left, and that they are held in fields (a Unity object created without a managed reference can be collected by the Il2Cpp GC).
**Fix:** one `Teardown()` that destroys mesh/mesh/material/GO and is called from `OnDestroy`, on map change and when the setting is switched off.

### W8. RacingLine - `RacingLine/Runner.cs:596` - `Camera.main` inside `Draw`
`FetchCamera` (line 533/866 pattern) correctly caches the camera and refreshes it every 2 s, but `Draw` calls `Camera.main` directly. Each call does a tag lookup (cheap in modern Unity, but it also creates an Il2Cpp wrapper); if `Draw` runs every frame that is avoidable.
**Fix:** use the cached `_cam`/`_camT` (already maintained by `FetchCamera`) in `Draw`.

### W9. Police - `Police/Runner.cs:872` - `Camera.main` (same pattern as W8, in a function that already caches)
Here the call sits inside the throttled `FetchCamera`, so it is fine. Listed only so nobody copies line 596's pattern. Police was audited many times, no further findings from this pass.

### W10. RogueHub - `RogueHub/HubView.cs:696, 711, 736, 904` - LINQ `Count/Any` over settings in view code
`_items.Count(x => ...)`, `m.Settings.Count(...)`, `Any(...)` with lambdas and interpolated status text. If these run in the per-frame draw/layout path (not only on tab or filter change), they allocate delegates and strings each frame.
**Fix:** compute these counts when the item list is rebuilt (tab, query or module change) and cache them. `Catalog.cs:170-171` and `HubView.cs:259` are build-time and fine.

### W11. RogueHub - `RogueHub/GameApi.cs:371, 380` - `GetComponentsInChildren<Component>(true)` / `<TMP_Text>(true)` on a cloned button
Done at clone time (one-shot). The `Component` overload returns every component and wraps each in an Il2Cpp object. Acceptable once, but confirm that it is not run on every hub open.
**Fix:** cache the cloned template and reuse it, or restrict to the specific component types needed.

### W12. HotReload - `HotReload/ModuleManager.cs:205-264` - lock + `DateTime` debounce across the FileSystemWatcher thread
The pattern (a lock around `Pending`, with the main thread draining it in `Update`) is correct. The only risk is `IndividualHotAssemblies` being read under lock at `:574` while enumerated elsewhere without it. Confirm every enumeration of `IndividualHotAssemblies` is under the same lock or a copy.
**Fix:** snapshot with `lock (...) arr = list.ToArray();` before iterating.

### W13. TrafficDensity - `TrafficDensity/Plugin.cs:116, 319-321` - LINQ on small arrays
`Distinct().OrderBy().ToArray()` and `FirstOrDefault`/`LastOrDefault` lambdas run on key presses only (setup and step buttons). Not hot. Safe, but `LastOrDefault(v => ...)` on a float array returns 0 when nothing matches, and the code at line 321 guards only one branch (`dir < 0 && next >= cur`). With `dir > 0` and no larger step, `FirstOrDefault` returns 0f, which would then be applied as the new multiplier unless it is clamped elsewhere.
**Fix:** treat "no match" explicitly (`if (next == 0f && ...) next = cur`), or clamp to the min/max step.

---

## INFO

- **I1 (all plugins).** 116 `catch { }` / `catch (Exception) { }` blocks with an empty body. Most are around optional game APIs and are consistent with the repo's "never crash the game" stance, but each swallowed exception inside a per-frame path hides a persistent bug. Where a swallow sits in `Update`/`LateUpdate`, add a once-only log.
- **I2 (EngineAudio).** `EngineAudio/Runner.cs:321-340` OnGUI is well built: it exits early for non-Repaint events, rebuilds the overlay string at 10 Hz and restores the shared `GUI.skin.label.fontSize`. Note that this is the pattern DriverCam's OnGUI (W1) should copy.
- **I3 (EngineAudio).** `EngineAudio/TireSqueal.cs:48` uses `Task.Run` for synthesis. It touches only plain .NET and stores results in statics read by the main thread via `EnsureClips()` (`Pending/Ready/Failed`). Make `s_data`/`s_dataError` `volatile` (or read them only after `s_prepare.IsCompleted`) for a clean memory-model story. The exception catch is inside the task, so there is no unobserved task exception.
- **I4 (Police).** `Police/Runner.cs:207-225` OnGUI follows the right pattern (cheap checks first, `Event.current` only then, Repaint only, perf scope). `Police/Lightbar.cs`, `Marker.cs`, `PursuitIcons.cs` and `RacingLine/Icons.cs` all have explicit `Destroy` paths. No new findings.
- **I5 (all).** Reflection lookup `AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(...)` is repeated in EngineAudio, HeadLook, PitStop, Police, RacingLine and RogueHub `GameApi`. It is a one-shot startup call, so only a duplication note: move it to `Shared` as one helper.
- **I6 (all).** `GetComponent<T>` hits are all in build/setup code (HullTrimmer, SidewalkMap, CarExporter, Cockpit, Lightbar, RogueHub layout), not in Update. `FindObjectsOfType`/`GameObject.Find` do not appear anywhere. `Resources.FindObjectsOfTypeAll` is used and (per comments) throttled.
- **I7 (all).** `ClassInjector.RegisterTypeInIl2Cpp<T>()` precedes every `AddComponent<T>()` for injected types (CurbFeel, DriverCam, EngineAudio, HeadLook, HotReload `HotHost`). HotReload's `HotModule.cs:11` documents "no injected types in hot modules", which matches the rule. The `(IntPtr)` constructor and the local-function rule were not exhaustively verified (EngineAudio `Runner.cs:351` has a comment showing the author is aware of the closure-struct trap).
- **I8 (threads).** The only threads are the FileSystemWatcher callback (HotReload, locked) and the TireSqueal worker. No other `Task.Run`/`async`/`Parallel` usage.
- **I9 (not checked).** Not verified because of the budget: `il2cpp-check`, the build, every `Update` body for a throttle, the per-plugin "restore state when switched off" paths for CurbFeel `HullTrimmer`/`ScrapePatches`, PitStop and HeadLook beyond the existence of `OnDestroy`, and RogueHub `Inputs.cs`/`ToastView.cs`.

---

## Per-plugin summary

| Plugin | FAIL | WARN | INFO | Notes |
|---|---|---|---|---|
| DriverCam | 2 (F1, F2) | 4 (W1-W4) | 0 | Baked-mesh leak in the exporter, `?.` on Unity objects in OnGUI, OnGUI strings, unthrottled Update error log, no OnDestroy |
| CurbFeel | 0 | 2 (W6, W7) | 0 | Stats string via LINQ, runtime mesh/material teardown to confirm |
| TrafficDensity | 0 | 1 (W13) | 0 | `FirstOrDefault` returning 0 when no step matches |
| RacingLine | 0 | 1 (W8) | 0 | `Camera.main` in Draw despite a cached camera |
| Police | 0 | 0 (W9 note) | 1 (I4) | Clean in this pass |
| PitStop | 0 | 0 | 0 | Only the shared reflection lookup (I5); not read in depth |
| EngineAudio | 0 | 0 | 2 (I2, I3) | Good OnGUI pattern; make worker results volatile |
| HeadLook | 0 | 0 | 0 | Has `OnDestroy`; not read in depth |
| RogueHub | 0 | 3 (W5, W10, W11) | 0 | LINQ counts and status strings in view code, clone-time component scans |
| HotReload | 0 | 1 (W12) | 0 | Lock coverage on `IndividualHotAssemblies` |
| Shared | 0 | 1 (W5) | 0 | `FindObjectsOfTypeAll` callers should be confirmed cached |
