---
description: Scaffold a new BepInEx 6 IL2CPP plugin in source/<Name>/ following the repo conventions, then run push-check
argument-hint: "<Name> [one-line description]"
---
Create a new plugin. Arguments: `$ARGUMENTS` (first word = PascalCase plugin name, rest = what it does).
If no name was given, ask for one. Refuse a name that already exists under `source/` (case-insensitive).

1. **Pick identity, check it's unique.**
   - GUID: `rogue.<lowercasename>` (CurbFeel uses `rogue.curbfeel`; DriverCam's older `drivingrogue.drivercam` stays).
     `git grep -n "<guid>"` must find nothing. DLL / AssemblyName = `<Name>`.
   - Hotkeys: read the registry in `CLAUDE.md` and grep all plugins (`git grep -nE "f[0-9]{1,2}Key|Key\.F[0-9]{1,2}" -- source`).
     F1-F11 are all taken and F12 is Steam's screenshot key, so a new hotkey needs a modifier (e.g. Ctrl+F2). Check
     non-key inputs too (right stick, mouse buttons: see the registry). The game itself may bind some. Ask the person
     to confirm them.
     Use the Input System (`Keyboard.current.f11Key` or a `Key.F11` default in a config entry), because that is what
     push-check scans for clashes.
2. **`source/<Name>/<Name>.csproj`**: copy `source/CurbFeel/CurbFeel.csproj` and change only `AssemblyName` and
   `RootNamespace`. Keep: `net6.0`, `DisableImplicitFrameworkReferences`, the `GameDir` default
   (`<GameDir Condition="'$(GameDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\Driving Rogue</GameDir>`,
   real paths come from `source/local.props` via `source/Directory.Build.props`), the same `HintPath` references
   (`$(GameDir)\dotnet\`, `BepInEx\core\`, `BepInEx\interop\`, all `<Private>false</Private>`; drop references the
   plugin doesn't need, add other interop DLLs the same way), and the `DeployToGame` target
   (`AfterTargets="Build"`, `Condition="'$(SkipDeploy)' != 'true'"` - every check builds with SkipDeploy -, `Copy`
   of `$(TargetPath)` into `$(GameDir)\BepInEx\plugins`, `ContinueOnError="true"`). Drop CurbFeel's hot-module parts
   (the `Hot` property groups, the HotReload `ProjectReference` and the `BepInEx\hot` copies).
   No personal paths, no `<Version>` tag (the version lives in `Plugin.cs`).
3. **`source/<Name>/Plugin.cs`**, modeled on `source/CurbFeel/Plugin.cs`:
   ```csharp
   using BepInEx;
   using BepInEx.Logging;
   using BepInEx.Unity.IL2CPP;
   using Il2CppInterop.Runtime.Injection;

   namespace <Name>
   {
       [BepInPlugin(Guid, "<Name>", Version)]
       public class Plugin : BasePlugin
       {
           public const string Guid = "rogue.<lowercasename>";
           public const string Version = "0.1.0";
           internal static new ManualLogSource Log;

           public override void Load()
           {
               Log = base.Log;
               // Config.Bind(...) entries here
               ClassInjector.RegisterTypeInIl2Cpp<<Name>Runner>();   // always BEFORE AddComponent
               AddComponent<<Name>Runner>();
               Log.LogInfo($"<Name> {Version} loaded.");            // use the const, never a literal version
           }
       }
   }
   ```
   plus `source/<Name>/<Name>Runner.cs`: a `MonoBehaviour` with `public <Name>Runner(System.IntPtr ptr) : base(ptr) { }`,
   an `Update()` that polls the hotkey(s) and wraps its body in try/catch with a throttled `LogWarning`.
   Only add Harmony (`using HarmonyLib; new Harmony(Guid).PatchAll(...)`) if the plugin patches game methods. Patches
   installed by hand (`harmony.Patch(...)`) get a `// harmony-target: Type.Method` comment next to them, so
   push-check can compare targets across plugins.
   Reach game members through a small `GameApi` class with a startup check (`Has()` by name) so a game update turns
   the feature off with a log line instead of crashing, and give the runner an error breaker (N errors -> restore
   everything, log once, stay off).
4. **`source/<Name>/README.md`**: title, one-paragraph description, the line `Current version: **0.1.0**.`, sections
   "What it changes" (say honestly if it changes gameplay), "In game" (hotkeys), "Tuning (<guid>.cfg)" (table of every
   config key), "Build" (`dotnet build -c Release`, needs `source/local.props`), "Notes", and
   "Log (`/game-log <Name>`)": the lines it prints at startup and in play, which the log-reader agent checks for.
5. **Root `README.md`**: add a row to the plugin table in the same format (`| **<Name>** | 0.1.0 | <what it does>. See [...](source/<Name>/README.md) |`)
   so `tools/bump-version.ps1` can update it, add its keys to the Controls table, and add its DLL to Uninstall.
   Add the new plugin, GUID and hotkeys to the registries in `CLAUDE.md`.
6. **Build** it: `dotnet build -c Release -p:SkipDeploy=true source/<Name>`, then
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/il2cpp-check.ps1 -NoBuild -Plugin <Name>`. Fix errors.
7. **Push check**: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/push-check.ps1`. Expect warnings only for
   the new uncommitted files; fix any FAIL (GUID/name/DLL duplicate, hotkey clash, version written differently).
8. Do not commit or push unless asked; suggest `/audit` then `/ship`. Report in exactly this shape:
   ```
   NEW PLUGIN <Name>: guid <guid>, hotkeys <keys or "none">, owner <who asked for it>
   Files: <each created or edited file>
   Build: OK | FAILED (<first error>); il2cpp-check: <RESULT line> (exit <n>)
   push-check.ps1: <RESULT line>; FAIL lines: <each, or "none">
   RESULT: OK | WARN | FAIL
   ```
   `FAIL` = build failed, il2cpp-check exit 1/2, or any push-check FAIL; `WARN` = push-check warnings other than
   the new files being uncommitted / untracked; else `OK`.
