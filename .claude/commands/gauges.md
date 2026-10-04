---
description: Check that every DriverCam cockpit dash matches the game - speedo and tach ranges vs specs.py, faces present, HUD speed multiplier, real top speed and RPM source from the log (gauge-check agent, read-only)
argument-hint: "[cars A,B] [no-log]"
---
Arguments: `$ARGUMENTS`

1. Launch the **gauge-check** subagent (`.claude/agents/gauge-check.md`), passing the car names if given (it then
   reports only those) and `no-log` if given (skip step 5 of the agent).
2. Relay its report unchanged from `GAUGES` to `RESULT:`.
3. For a FAIL, add one plain-words line per car saying what the driver would see (e.g. "the speedo pegs at 280 while
   the HUD shows 300", "no needles: the cockpit was built before working gauges"). DriverCam is Aste-risks' plugin:
   offer `/assets interiors` (scratch rebuild) or a specs.py change only; edits need the person's approval.
