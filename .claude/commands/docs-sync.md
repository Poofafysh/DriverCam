---
description: Check plugin READMEs against the code - log checklist lines vs real Log calls, settings vs Config.Bind (docs-sync agent, read-only)
argument-hint: "[plugins A,B]"
---
Arguments: `$ARGUMENTS`

1. Launch the **docs-sync** subagent (`.claude/agents/docs-sync.md`) with the plugins named (none -> all).
2. Relay its report unchanged from `DOCS` to `RESULT:`.
3. Offer to apply the `REAL` README fixes for plugins this session may edit (its own claims in `/claims`, owner per
   the CLAUDE.md table). README-only changes need no version bump.
