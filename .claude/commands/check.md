---
description: Run tools/push-check.ps1 and explain every FAIL/WARN with the exact fix
argument-hint: "[-NoFetch] [-NoGitHub] [-AllowIdentityChange]"
---
Run the deterministic pre-push gate and explain the result. Extra flags: `$ARGUMENTS`

1. From the repo root run:
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/push-check.ps1 $ARGUMENTS`
   Only pass flags the person gave (`-NoFetch` offline, `-NoGitHub` skip the GitHub releases lookup,
   `-AllowIdentityChange` when a GUID/name/DLL rename is intentional).
2. Read the full output. Quote the `RESULT:` line.
3. For **every** `FAIL` and `WARN` line, give one short bullet: what it means in plain words and the exact fix
   (command or file + edit). Common ones:
   - behind / diverged from origin -> `git pull --rebase`, resolve conflicts, re-run.
   - files both people changed -> name the file and the other developer's commits
     (`git log HEAD..origin/main --format="%h %an %s" -- <file>`); talk before overwriting.
   - code changed without a version bump / version lower than or equal to origin / version already tagged ->
     `powershell -NoProfile -ExecutionPolicy Bypass -File tools/bump-version.ps1 -Plugin <Name> -To patch` (it counts
     up from the higher of local and origin).
   - version written differently in Plugin.cs / README / root README / log string -> make the stale spot match
     `Plugin.cs` (bump-version doesn't touch hard-coded log strings, e.g. DriverCam's "DriverCam x.y.z loaded").
   - uncommitted / untracked files -> commit them or leave them out on purpose (fine before committing).
   - binaries, build output, `local.props`, game assets staged -> `git rm --cached <path>` and check `.gitignore`.
   - duplicate GUID/name/DLL, hotkey clash, same Harmony target in two plugins -> name both plugins and the fix.
   - conflict markers -> the file and line.
4. End with one line: OK to push / OK with warnings (list which need a decision) / blocked. Do not push, commit or edit
   anything unless the person asks.
