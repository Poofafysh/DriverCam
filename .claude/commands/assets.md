---
description: Rebuild DriverCam interiors / gauge faces / Police models with Blender headless into a scratch folder and compare their triangle and draw-call budgets with the shipped ones (asset-builder agent)
argument-hint: "[interiors|gauges|police|all] [cars A,B] [previews] [write into the repo] [budget]"
---
Arguments: `$ARGUMENTS`

- `budget` alone -> no rebuild: run
  `powershell -NoProfile -ExecutionPolicy Bypass -File tools/asset-budget.ps1` and relay its `ASSET` / `TOTAL` /
  `RESULT:` lines unchanged (exit 2 = it could not run). Stop.
- Otherwise launch the **asset-builder** subagent (`.claude/agents/asset-builder.md`) with `what=`, `cars=`,
  `previews` and `write into the repo` exactly as given. Pass `approved: <quote>` only if the person approved writing
  DriverCam (Aste-risks' plugin) assets in this conversation, quoted. Relay its report unchanged.
- If it returns `NEEDS ANSWER:`, ask the person that exact question and relaunch with the answer.
- If it wrote files: say which plugin needs `/bump <Plugin> patch` and `/audit`. Never deploy: the cockpits reach the
  game through `/sync` (which puts the installed car setups back).
