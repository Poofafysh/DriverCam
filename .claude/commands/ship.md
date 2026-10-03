---
description: Ship my change - pull --rebase, audit until PASS, bump version if needed, commit, push-check, push (never force)
argument-hint: "[commit message or what the change does]"
---
Ship the current work to `origin/main` of this shared repo. Person's note / commit message hint: `$ARGUMENTS`

Work through these steps in order. Stop and report at the first step that cannot be completed cleanly.

1. **Look.** `git status`, `git diff HEAD --stat`, `git log origin/main..HEAD --oneline`. If there is nothing to ship
   (clean tree and nothing ahead of origin), say so and stop. List the files that will go out. If anything looks like
   it must never be committed (`*.dll`, `bin/`, `obj/`, `BepInEx/`, `dotnet/`, `source/local.props`, `backup/`, game
   assets or dumps, zips), stop and tell the person.
2. **Pull.** `git pull --rebase` (add `--autostash` if there are uncommitted changes). Never a merge, never `reset`,
   never discard local work. If the rebase hits
   conflicts: show the conflicted files and the other developer's commits for them
   (`git log HEAD..origin/main --format="%h %an %s" -- <file>`), resolve only if the right answer is obvious and
   mechanical (version numbers: take the higher and re-bump, see step 4), otherwise stop and ask.
3. **Audit plugin code.** If any file under `source/<Plugin>/` other than `*.md` changed (compare against `origin/main`),
   run the **code-auditor** subagent (`.claude/agents/code-auditor.md`) on the change. If it answers FAIL, fix every
   numbered item, then run the auditor again. Repeat until PASS. Do not skip this or accept "almost". Do not
   touch the other developer's plugin to make the audit pass without asking.
4. **Version.** For every plugin whose code, assets or shipped `SavedSettings/` changed (docs-only changes need no bump):
   check whether its version in `Plugin.cs` is already higher than on `origin/main`
   (`git show origin/main:source/<Plugin>/Plugin.cs`). If not, run
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/bump-version.ps1 -Plugin <Plugin> -To <level>`
   with `patch` for fixes/tuning, `minor` for new features or settings, `major` for breaking config/behaviour changes
   (ask if unsure). Never hand-edit versions, never reuse or go below origin's version.
5. **Commit.** Stage only the intended files by name (`git add <paths>`, never `git add -A` blindly). Commit message:
   first line `<Plugin> <new version>: <what changed>` (or a plain summary for repo/tool/doc changes), a short body
   with the user-visible changes and any renamed/removed config keys, and end with the attribution line(s) from the
   current system instructions if there are any.
6. **Push check.** Run the **push-check** subagent (`.claude/agents/push-check.md`). If it says BLOCKED, fix and repeat
   from the relevant step (behind origin -> step 2). If OK WITH WARNINGS, show the warnings and ask the person whether
   to push anyway.
7. **Push.** Only on `PUSH CHECK: OK` (or warnings the person accepted): `git push origin main`.
   **Never** `--force`, `--force-with-lease`, `reset --hard` or any history rewrite. If the push is rejected because
   origin moved, go back to step 2.
8. **Summarize** in a few lines: commit hash(es) pushed, plugin versions `old -> new`, audit result, push-check verdict,
   and a reminder that the other developer can run `/sync` (or `/whats-new`) to get it.
