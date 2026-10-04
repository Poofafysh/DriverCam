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
3. For `<GameDir>` and each `<ExtraGameDirs>` entry in `source/local.props` run
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-install.ps1 -DryRun -Force -GameDir "<dir>" -FromRev <base>`
   (`-Force` only stops the dry run at "uncommitted changes" from hiding the report; a dry run changes nothing). Its
   `info` / `WARN` lines give version changes (`<Plugin>: a -> b`), config transitions (new, removed / renamed keys,
   changed defaults still saved) and per-car setups that differ.
4. Read the actual diffs where it matters (`git diff <base>..origin/main -- source/`) and summarize per plugin in plain
   words: new features, behaviour/gameplay changes, new or changed hotkeys, renamed/removed config keys, changes to
   shipped car setups or cockpits, and changes to tools, agents, commands or `CLAUDE.md` that change the workflow.
5. Conflicts ahead: files changed both locally (uncommitted or local commits) and upstream
   (`git diff --name-only HEAD...origin/main` intersected with `git diff --name-only origin/main...HEAD` and
   `git status --short`). Name them.
6. Output in exactly this shape:
   ```
   Incoming: <n> commit(s) on origin/main since <base> (<author>: <n>, ...) | up to date
   <Author>: <hash> <subject>, one line per commit, the other developer's first
   Versions: <Plugin> a -> b | new plugin, one per plugin (from the dry run's info lines)
   Per plugin: <Plugin> - <plain-words summary: features, gameplay, hotkeys, config keys, car setups / cockpits>
   Workflow: <tools / agents / commands / CLAUDE.md changes, or "none">
   Config impact: <dry-run WARN lines in plain words, or "none">
   Conflicts ahead: <files changed on both sides, or "none">
   Install: <install leaf>: matches origin/main | behind (<Plugin> <installed> -> <origin>, ...) | dry run FAIL: <text>, one per install
   Next: /sync | git pull --rebase first (you have local commits) | nothing to do
   RESULT: OK | WARN | FAIL
   ```
   Installed versions = the last `Loading [<Name> <version>]` per plugin in `<dir>\BepInEx\LogOutput.log` (the
   log-reader agent's version snippet), compared with `git show origin/main:source/<Plugin>/Plugin.cs`. A dry run
   that FAILs in preflight (diverged branch, BepInEx missing) still leaves steps 2, 4 and 5 to show; its FAIL text
   goes on that install's `Install:` entry. `RESULT: WARN` = conflicts ahead or a config-impact line; `FAIL` = a dry
   run FAILed; else `OK`.
