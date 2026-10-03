---
description: Run the push-check scenario tests (tools/push-check.tests.ps1) and report
argument-hint: "[scenario name words to filter, e.g. collision lower]"
---
Run the tool test suite. Optional scenario filter: `$ARGUMENTS`

1. From the repo root run (it builds throwaway git repos in `%TEMP%`, never touches this repo or GitHub; it can take a
   minute or two):
   - no arguments: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/push-check.tests.ps1`
   - with words: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/push-check.tests.ps1 -Only <word1>,<word2>`
2. Report the pass/fail count and list every failing scenario with what it expected and what push-check actually
   reported. If all pass, one line is enough.
3. If something failed, say whether the cause looks like a real regression in `tools/push-check.ps1` (point at the
   check / line), a test-scenario problem, or the environment (git missing, PowerShell 5.1 parsing issue, temp folder
   permissions). Offer to fix; don't change `tools/` without a yes. If you add a check to `push-check.ps1`, add a
   scenario for it in `push-check.tests.ps1` too.
