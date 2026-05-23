---
description: Draft a new expertise entry capturing what was just learned
argument-hint: "<title> [summary]"
---
Draft a new expertise entry titled **$1**.

Additional context from the invoker (if any): ${@:2}

Before drafting, briefly answer these questions to yourself so the entry is useful to the next agent:

- **Domain** — which logical grouping does this belong to? (e.g. `shared`, `azure-devops`, `iac`, or a more specific one observed in recent search results)
- **Entry type** — `IssueFix` (root-cause + fix for a real bug), `Caveat` (warning about a footgun), `Requirement` (rule that must be followed), or `Pattern` (recommended approach)
- **Severity** — `Info`, `Warning`, or `Critical` based on the blast radius of getting it wrong
- **Source version** — if this is tied to a specific tool/library version (e.g. `PgBouncer 1.21.0`, `EF Core 10.0.1`), include it as `sourceVersion` so the entry can be flagged stale later

Then call `expertise_create` with:

- `title: "$1"` (short imperative — e.g. "PgBouncer transaction mode breaks advisory locks")
- `body`: a markdown body covering **Problem**, **Root cause**, **Fix**, and (if relevant) **Verification**. Keep it tight; cite commits/issues by id where possible.
- `domain`, `entryType`, `severity`, `source`, optional `sourceVersion`, optional `tags[]` as decided above.
- `source`: a self-reported origin string identifying this session (e.g. `agent-session-YYYY-MM`).

If the server returns 409 (duplicate), surface the existing entry id and ask the invoker whether to update it via `expertise_update` instead.

The entry is created in **Draft** state by default and requires `/expertise-approve` (with the `expertise.write.approve` scope) to become visible to other agents.
