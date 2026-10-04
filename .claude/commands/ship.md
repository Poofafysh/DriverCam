---
description: Ship my change - pull --rebase, audit until PASS, bump version if needed, commit, push-check; pushes only if the person said "push" (never force)
argument-hint: "[commit message or what the change does]"
---
Ship the current work to `origin/main` of this shared repo. Person's note / commit message hint: `$ARGUMENTS`

Work through these steps in order. Stop and report at the first step that cannot be completed cleanly.
The person usually pushes themselves: step 7 pushes only if the person's own message that started this `/ship` (or a
later message of theirs in this conversation, after seeing the push-check result) contains the word "push".
`$ARGUMENTS` counts as their message. Otherwise stop after step 6 and print the push command for them.

1. **Look.** `git status`, `git diff HEAD --stat`, `git log origin/main..HEAD --oneline`. If there is nothing to ship
   (clean tree and nothing ahead of origin), say so and stop. Split the changed files into two lists:
   - **Mine**: files this conversation created or edited (from your own tool calls), plus files the person named in
     `$ARGUMENTS`.
   - **Not mine**: every other changed or untracked file. Other sessions work in this tree at the same time: never
     stage, revert, stash, format or "fix" these, and keep them out of the audit scope. List them once.
   - A **Mine** file whose `git diff` has hunks you didn't write (another session edited it too): stop and ask whether
     to ship it with those hunks; never split or drop someone else's hunks yourself.

   - Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ownership.ps1 -Check -Paths "<Mine files,
     ;-separated>"`. Exit 1 = another session has claimed one of them (`/claims`): stop, show its `CONFLICT` lines and
     ask the person; exit 2 = show its `ERROR` line and go on. `.claude/ownership.md` itself is never shipped.

   Show the **Mine** list as the files that will go out. If anything in it must never be committed (`*.dll`, `bin/`,
   `obj/`, `BepInEx/`, `dotnet/`, `source/local.props`, `backup/`, game assets or dumps, zips), stop and tell the
   person.
2. **Pull.** `git pull --rebase` (add `--autostash` if there are uncommitted changes). Never a merge, never `reset`,
   never discard local work. `--autostash` briefly stashes the **Not mine** files too: if it ends with
   `conflicts in the stash` / the stash isn't re-applied, stop, show `git stash list` and `git status --short`, and
   tell the person (another session's files are in that stash); don't drop or pop it yourself. If the rebase hits
   conflicts: show the conflicted files and the other developer's commits for them
   (`git log HEAD..origin/main --format="%h %an %s" -- <file>`), resolve only if the right answer is obvious and
   mechanical (version numbers: take the higher and re-bump, see step 4), otherwise stop and ask.
3. **Audit plugin code.** If a non-`.md` file under `source/<Plugin>/` or `source/Shared/` in **Mine** differs from
   `origin/main`, follow `/audit` steps 2-5 (scope = the **Mine** plugins, approvals = only what the person said).
   On FAIL fix every numbered item and audit again until PASS; never accept "almost", and never touch the other
   developer's plugin to make it pass without asking.
4. **Version.** For every plugin whose code, assets or shipped `SavedSettings/` changed (every plugin linking a
   changed `source/Shared/*.cs` too; docs-only needs nothing): if its `Plugin.cs` version is not above
   `git show origin/main:source/<Plugin>/Plugin.cs`, follow `/bump <Plugin> <level>` (level by the CLAUDE.md version
   rules; ask if unsure). Never hand-edit a version.
5. **Commit.** Stage only the **Mine** files (plus the files `bump-version.ps1` changed for them) by name
   (`git add -- <paths>`; never `git add -A`, `git add .` or `git commit -a`). Check `git diff --cached --name-only`
   equals that list before committing. Commit message:
   first line `<Plugin> <new version>: <what changed>` (or a plain summary for repo/tool/doc changes), a short body
   with the user-visible changes and any renamed/removed config keys, and end with the attribution line(s) from the
   current system instructions if there are any. If the commit changes a plugin the other developer owns (see the
   table in `CLAUDE.md`), say in the body who approved it and what changed, e.g.
   `Approved by Poofafysh: DriverCam reads HeadLook's head angle (HeadLookLink.cs)`. push-check FAILs a commit to
   someone else's plugin without "approved" in its message. Several plugins changed? Prefer one commit per plugin so
   each message stays accurate.
6. **Push check.** Run the **push-check** subagent (`.claude/agents/push-check.md`; it also runs
   `tools/il2cpp-check.ps1`). If it says BLOCKED (`RESULT: FAIL`), fix and repeat from the relevant step (behind
   origin -> step 2). If OK WITH WARNINGS (`RESULT: WARN`), show each warning's exact text; a warning is accepted only
   when the person answers yes to it after seeing it (quote the warning next to their yes).
7. **Push.** Only if the "push" rule at the top is met, and only on `PUSH CHECK: OK` (or warnings the person
   accepted): `git push origin main`. Otherwise print `Ready to push: git push origin main` and stop.
   **Never** `--force`, `--force-with-lease`, `reset --hard` or any history rewrite. If the push is rejected because
   origin moved, go back to step 2.
8. **Summarize** in exactly this shape:
   ```
   SHIP: PUSHED | READY TO PUSH | STOPPED at step <n> (<reason>)
   Commits: <hash> <first line>, one per commit
   Versions: <Plugin> old -> new, one per plugin
   Audit: <RESULT line> | not needed (no plugin code)
   Push check: <PUSH CHECK line>; accepted warnings: <quoted WARN + the person's yes> | none
   Left out (not mine): <n> file(s) | nothing
   Next: the other developer runs /whats-new then /sync | git push origin main
   RESULT: OK | WARN | FAIL
   ```
   `OK` = pushed, or ready to push with no open warnings; `WARN` = ready but a warning still needs the person's yes;
   `FAIL` = stopped.
