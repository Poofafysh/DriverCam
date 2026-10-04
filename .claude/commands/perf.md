---
description: Read-only performance report - per-frame code costs, [Perf] runtime timings and model budgets, ranked by plugin (perf-budget agent)
argument-hint: "[plugins A,B] [since=<n>] [install dir] [scan]"
---
Arguments: `$ARGUMENTS`

- `scan` alone -> no agent: run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/perf-scan.ps1` (with
  `-Plugin <A,B>` if plugins were named) and show its `PLUGIN` lines and `RESULT:` line; exit 2 = could not run.
  Stop.
- Otherwise say `ETA: ~2-4 min`, launch the **perf-budget** subagent (`.claude/agents/perf-budget.md`) with
  `plugins=`, `since=` and the install folder as given, and relay its report unchanged from `PERF` to `RESULT:`.
- To measure an optimisation: run `/perf` before, make the change, install, drive the same track for ~1 min with
  `[Perf] Enabled = true` (rogue.trafficdensity.cfg), then `/perf since=<line count before the drive>`, and compare
  the two `RUNTIME` lines. Only plugins whose owner allows it get edited (CLAUDE.md table).
