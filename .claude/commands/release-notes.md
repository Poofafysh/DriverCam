---
description: Fixed-format release notes per plugin (versions, commits, settings, hotkeys) between two git points (release-notes agent, read-only)
argument-hint: "[since <rev>] [until <rev>] [worktree]"
---
Arguments: `$ARGUMENTS`

1. Launch the **release-notes** subagent (`.claude/agents/release-notes.md`) with `since=`, `until=` and `worktree`
   exactly as given (none -> its defaults: `origin/main..HEAD`).
2. Relay its report unchanged from `RELEASE NOTES` to `RESULT:`.
3. If it says `NOT BUMPED`, point to `/bump <Plugin> patch|minor|major`. Never tag, commit or push from here.
