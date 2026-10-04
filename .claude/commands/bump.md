---
description: Bump a plugin's version everywhere via tools/bump-version.ps1 and show the diff
argument-hint: "<Plugin> [patch|minor|major|x.y.z]"
---
Bump a plugin version. Arguments: `$ARGUMENTS` (first word = plugin name, second = `patch` (default), `minor`, `major`
or an explicit `x.y.z`).

1. If no plugin name was given, list the plugins (`source/*/Plugin.cs`) with their current version and the version on
   `origin/main`, then ask which one. If the level is unclear, suggest one: `patch` = fix/tuning/asset change,
   `minor` = new feature or new setting, `major` = breaking change to config or behaviour.
2. Run from the repo root:
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/bump-version.ps1 -Plugin <Plugin> -To <level>`
   It fetches origin, counts up from the **higher** of the local and `origin/main` version, refuses versions that are
   not above origin or already tagged, and updates `Plugin.cs`, any `.csproj` version tags, `source/<Plugin>/README.md`
   "Current version" and the root `README.md` table. It does not commit.
3. If it refuses, explain why and what to pass instead. Do not hand-edit version numbers to get around it.
4. Show `git diff --stat` and the relevant hunks (`git diff -U1 -- source/<Plugin> README.md`). Check for any other
   place that still shows the old version (`git grep -n "<old version>" -- source/<Plugin> README.md`), for example a
   hard-coded "loaded" log string, and point it out.
5. Do not commit unless the person asks; suggest `/ship` next. Report in exactly this shape:
   ```
   BUMP <Plugin>: <old> -> <new> (origin/main has <x>) | REFUSED: <the script's "bump-version: ..." text>
   Files: <git diff --stat lines for the files it changed>
   Still old: <file:line of each remaining "<old version>" hit, or "none">
   RESULT: OK | WARN | FAIL
   ```
   `FAIL` = the script exited 1; `WARN` = a "Still old" hit (e.g. DriverCam's hard-coded `"DriverCam x.y.z loaded"`
   line, which must be fixed by hand); else `OK`. Another session may be editing the same plugin: if
   `git diff -- source/<Plugin>` shows changes besides the version lines that this conversation didn't make, say so
   on a `Note:` line (the bump is for the whole plugin folder, including their work).
