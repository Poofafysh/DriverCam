---
description: Look up how a game system works (types, fields, methods) - RESEARCH.md first, then the real BepInEx interop assemblies
argument-hint: "<game type, field, method or question>"
---
Answer a question about the game's code: `$ARGUMENTS`

Read-only. Never commit anything produced here; game assets and dumps must not enter the repo.

1. **Existing notes first.** Search `source/CurbFeel/RESEARCH.md` (walls, curbs, collision layers, `VehicleDamage`,
   traffic contacts, with offsets and decompiled formulas), `source/CurbFeel/README.md`, `source/README.md` (DriverCam
   architecture) and the plugin sources (`source/*/*.cs`) for the name or topic. Existing code that already uses a type
   is the strongest evidence of its real API.
2. **Ground truth = the interop.** Find `<GameDir>` from `source/local.props`. The callable API is in
   `<GameDir>\BepInEx\interop\` (`Assembly-CSharp.dll` for game code; `UnityEngine.*.dll`, `Unity.InputSystem.dll`,
   `Mirror.dll`, `Il2Cppmscorlib.dll`). Inspect it without modifying or copying it into the repo: `ilspycmd` if it is
   installed, otherwise a throwaway `dotnet` console project in the scratchpad that reads the metadata with
   `System.Reflection.Metadata` (loading interop DLLs with `Assembly.LoadFile` in Windows PowerShell 5.1 fails because
   they target .NET 6). A raw string search of the DLL bytes only proves a name exists, not its signature. Report the
   exact type name, namespace (interop types may be under `Il2Cpp...`), and the members/signatures that exist.
3. **Dumps are hints only.** Names seen only in an Il2Cpp dump, AssetRipper export or decompiler output must be confirmed
   in the interop (and runtime object names / layers in the running game's log) before code relies on them. Say clearly
   which facts are verified and which are not.
4. **Networking check.** If the type is a Mirror `NetworkBehaviour` or the member is a SyncVar / Command / ClientRpc,
   warn: our plugins must only change the local player's game.
5. Answer concisely: what it is, where it is used, the members that matter, any gotchas (IL2CPP arrays, wrappers,
   structs passed by value). If the finding is new and useful, offer to add a short section to the relevant plugin's
   RESEARCH/README notes (facts and names only, no ripped content).
