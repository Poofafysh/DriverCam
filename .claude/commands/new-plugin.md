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
     Choose keys no plugin uses (currently F1-F5, F11; avoid F12 = Steam screenshot; the game itself may bind some)
     and ask the person to confirm them.
     Use the Input System (`Keyboard.current.f11Key` or a `Key.F11` default in a config entry), because that is what
     push-check scans for clashes.
2. **`source/<Name>/<Name>.csproj`**: copy `source/CurbFeel/CurbFeel.csproj` and change only `AssemblyName` and
   `RootNamespace`. Keep: `net6.0`, `DisableImplicitFrameworkReferences`, the `GameDir` default
   (`<GameDir Condition="'$(GameDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\Driving Rogue</GameDir>`,
   real paths come from `source/local.props` via `source/Directory.Build.props`), the same `HintPath` references
   (`$(GameDir)\dotnet\`, `BepInEx\core\`, `BepInEx\interop\`, all `<Private>false</Private>`; drop references the
   plugin doesn't need, add other interop DLLs the same way), and the `DeployToGame` target
   (`AfterTargets="Build"`, `Copy` of `$(TargetPath)` into `$(GameDir)\BepInEx\plugins`, `ContinueOnError="true"`).
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
   Only add Harmony (`using HarmonyLib; new Harmony(Guid).PatchAll(...)`) if the plugin patches game methods.
4. **`source/<Name>/README.md`**: title, one-paragraph description, the line `Current version: **0.1.0**.`, sections
   "What it changes" (say honestly if it changes gameplay), "In game" (hotkeys), "Tuning (<guid>.cfg)" (table of every
   config key), "Build" (`dotnet build -c Release`, needs `source/local.props`), "Notes".
5. **Root `README.md`**: add a row to the plugin table in the same format (`| **<Name>** | 0.1.0 | <what it does>. See [...](source/<Name>/README.md) |`)
   so `tools/bump-version.ps1` can update it, add its keys to the Controls table, and add its DLL to Uninstall.
   Add the new plugin, GUID and hotkeys to the registries in `CLAUDE.md`.
6. **Build** it: `dotnet build -c Release source/<Name>` (MSB3021/3027 = game running, compile still counts). Fix errors.
7. **Push check**: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/push-check.ps1`. Expect warnings only for
   the new uncommitted files; fix any FAIL (GUID/name/DLL duplicate, hotkey clash, version written differently).
8. Show the created files and the push-check result. Do not commit or push unless asked; suggest `/audit` then `/ship`.
