---
name: expertise-api-design
description: Deprecated shim. The action-oriented expertise-api skill now lives at .agents/skills/expertise-api/SKILL.md (portable across pi, Claude Code, and Codex CLI). The design reference moved to .agents/skills/expertise-api/references/DESIGN.md and is loaded on demand. Keep this file in place only so existing Claude Code installs do not 404; new installs should point at the new path.
user-invocable: false
---

## Moved

This skill has been split into:

- **Action skill:** [`.agents/skills/expertise-api/SKILL.md`](../../../.agents/skills/expertise-api/SKILL.md)
  — search / create / approve / reject operations against the running API,
  with a curl toolkit under `scripts/`.
- **Design reference:** [`.agents/skills/expertise-api/references/DESIGN.md`](../../../.agents/skills/expertise-api/references/DESIGN.md)
  — data model, scopes, approval state machine, audit log, authentication
  modes. Load on demand.

Existing Claude Code users should update their settings to add
`.agents/skills/expertise-api/` to the `skills` directory list. See the
project [README](../../../README.md#calling-from-agent-harnesses) for the
install one-liner.

This shim will be removed in a future release.
