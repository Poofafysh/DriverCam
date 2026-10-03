---
description: What the other developer pushed since my last sync - incoming commits, version changes, config impact (read-only)
argument-hint: "[since <rev>, optional]"
---
Explain what is new on `origin/main` that the person doesn't have yet. Read-only: do not pull, build or install.
Optional starting point: `$ARGUMENTS` (a commit/rev; default = the current `HEAD`).

1. `git fetch origin --tags --quiet`. Then `git status -sb` to see ahead/behind.
   If nothing is incoming (`git rev-list --count HEAD..origin/main` is 0) and no rev was given, say "up to date" and
   also show whether the local game install matches the repo (step 3 still tells that) - then stop after step 3.
2. Incoming commits: `git log --format="%h %an %ad %s" --date=short <base>..origin/main` and
   `git log --stat <base>..origin/main`, where `<base>` is `$ARGUMENTS` or `HEAD`. Group by author; call out the other
   developer's commits (the user's own name is in `git config user.name`).
3. Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-install.ps1 -DryRun` (add `-FromRev <rev>` if
   a rev was given). It reports, without changing anything: plugin version changes (`a -> b` or new plugin), installed
   vs repo versions, config transitions (new keys, removed/renamed keys, changed defaults the person is still on) and
   tuned per-car `DriverCam_cars` setups that differ.
4. Read the actual diffs where it matters (`git diff <base>..origin/main -- source/`) and summarize per plugin in plain
   words: new features, behaviour/gameplay changes, new or changed hotkeys, renamed/removed config keys, changes to
   shipped car setups or cockpits, and changes to tools, agents, commands or `CLAUDE.md` that change the workflow.
5. Conflicts ahead: files changed both locally (uncommitted or local commits) and upstream
   (`git diff --name-only HEAD...origin/main` intersected with `git diff --name-only origin/main...HEAD` and
   `git status --short`). Name them.
6. End with a short "what to do" line: usually `/sync` to install it, or `git pull --rebase` first if they have local
   work.
