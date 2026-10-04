---
description: Release this session's claims in the ownership ledger (all, or only the paths given)
argument-hint: "[<path>;<path>...]"
---
Arguments: `$ARGUMENTS`

1. Run exactly:
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ownership.ps1 -Release [-Paths "<paths>"]`
   (`-Paths` only when paths were given; they must be written as they were claimed, see `/claims`).
2. Relay its `RELEASED` / `PRUNED` lines. `RELEASED nothing` -> show `/claims` so the person sees what is held.
3. Only this session's rows are touched; expired rows of any session are pruned. Release when the claimed work is
   committed, handed over, or abandoned.
