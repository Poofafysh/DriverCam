# Driving Rogue mods (shared repo)

Two developers push to this repo: **Poofafysh** and **Aste-risks**. It is **source only** (no DLLs, no BepInEx, no zips).

- `source/DriverCam/`: first-person driver camera plugin
- `source/CurbFeel/`: curbs, sidewalks and lane-splitting plugin
- `tools/push-check.ps1`: pre-push checks

## Rules for Claude in this repo

- **After changing plugin code, run the `code-auditor` subagent** (`.claude/agents/code-auditor.md`). It is a strict,
  read-only PASS/FAIL review of the diff. Fix what it lists, then re-run it until PASS.
- **Before any `git push`, run the `push-check` subagent** (`.claude/agents/push-check.md`) and only push if it reports
  OK or the person accepts the warnings. Never force-push.
- **To update a local game install from the repo, use the `repo-sync` subagent** (`.claude/agents/repo-sync.md`,
  backed by `tools/sync-install.ps1`). It backs up, fast-forward pulls, builds and installs, then verifies DLLs,
  shipped assets, stale or duplicate plugins and config changes, with rollback (`-Rollback latest`). Don't hand-copy DLLs.
- Pull with `git pull --rebase` before starting work and before pushing.
- When a plugin's code changes, bump its version in `source/<Plugin>/Plugin.cs` and in its `README.md`
  ("Current version"). Never reuse a version number the other developer already pushed.
- Personal paths go in `source/local.props` (git-ignored; copy `source/local.props.example`), never in a `.csproj`.
- Don't commit game assets (AssetRipper output, Il2Cpp dumps, exported car models) or build output.
- Hotkeys: DriverCam uses F6/F7, CurbFeel uses F8/F9/F10. Pick unused keys for new features.
