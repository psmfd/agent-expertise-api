# Four-scope split with Draft/Approved review state for ASI06 mitigation

- Status: accepted (amended by [ADR-008](008-response-hygiene-and-actor-class.md) — adds `expertise.agent` scope; clarified 2026-05-19 — see "Draft visibility uses `GET /expertise/drafts`" and "Soft-delete on shared entries requires `expertise.write.approve`" subsections under Decision Outcome)
- Date: 2026-04-28

## Context and Problem Statement

The expertise API today issues two scopes: `expertise.read` and `expertise.write`. Every authenticated client receives both, and every successful `POST` immediately produces an entry that is auto-injected into every other agent's pre-flight context. A single compromised agent token — or a single agent that hallucinates a confident-sounding "fix" — propagates to every consumer's read path with no human in the loop. This is the ASI06 (autonomous self-injection) vulnerability: agents can rewrite the shared context that drives every other agent's behaviour, and the writer's identity, the writer's tenant, and the writer's intent are all asserted by the writer.

In addition, the binary write scope makes it impossible to distinguish "an agent recording a candidate observation" from "a curator promoting that observation to canonical knowledge." Without that distinction, every agent write lands at the highest trust tier by default.

How should the API gate writes so that compromised or hallucinating agents cannot escalate into a global injection vector, while still preserving low-friction draft submission as the agent's primary contribution path?

## Considered Options

- Keep two scopes; mitigate via an out-of-band review queue (current state, depends on framework-side enforcement)
- Split write into `expertise.write.draft` and `expertise.write.approve`, with a separate `expertise.admin` for cross-tenant operations and audit access
- Remove agent write entirely; only humans submit entries
- Per-entry signed approvals via a separate signing key, in addition to OIDC scopes

## Decision Outcome

Chosen option: **four-scope split** — `expertise.read`, `expertise.write.draft`, `expertise.write.approve`, `expertise.admin` — combined with a `ReviewState` enum (`Draft | Approved | Rejected`) on every entry.

Scope semantics:

| Scope | Permits |
|-------|---------|
| `expertise.read` | All `GET` endpoints. Reads are always filtered to `Tenant IN (caller, 'shared') AND ReviewState = 'Approved'`; reviewers see drafts via `GET /expertise/drafts` (requires `write.approve`). |
| `expertise.write.draft` | `POST`, `PATCH` on caller's own tenant only. `ReviewState` is forced to `Draft`. `Tenant` is forced to the caller's tenant. `Visibility` is forced to `Private`. The caller cannot override these by sending them in the request body. |
| `expertise.write.approve` | Everything `write.draft` permits, plus: calling `POST /expertise/{id}/approve` and `/reject`, setting `Tenant = 'shared'`, setting `Visibility = 'Shared'`, and editing Approved entries (which transitions them back to `Draft` for non-approvers, but stays `Approved` for approvers). |
| `expertise.admin` | Everything `write.approve` permits, plus: `GET /audit`, soft-delete on shared entries, and tenant reassignment. |

Scope expansion is **policy-side, not token-side**: a token carrying `expertise.admin` is treated as if it also carries `approve`, `draft`, and `read`. This keeps IdP configuration simple — operators issue exactly one scope per role — and centralizes the expansion logic in the authorization handler.

Reasons:

- Option 1 (status quo) depends on the framework's pre-flight queue and curator agent for enforcement. That enforcement lives outside the API's trust boundary; anything that bypasses the framework (e.g., a direct `curl` from a compromised agent token) lands directly in the canonical store. The API must enforce its own gate.
- Option 3 (humans only) breaks the agent self-improvement workflow entirely. The API exists specifically to capture agent-discovered fixes; removing agent write defeats the purpose.
- Option 4 (signed approvals) is overkill for the threat model. The blast radius is "an agent's hallucination becomes shared context," not "a malicious actor publishes signed lies." A scope-and-state model handles the realistic case at a fraction of the operational complexity (no signing key distribution, no signature verification path).
- Option 2 makes the trust boundary explicit at the API surface: a compromised `write.draft` token can dirty the caller's own tenant draft pile but cannot reach any other agent. A human or curator with `write.approve` then triages drafts before they enter the canonical read path.

`AuthorPrincipal` is always set from the token's `sub` claim, never from request body. `ReviewedBy` is always set from the token of the principal calling `/approve` or `/reject`. `IntegrityHash` is recomputed on every write. The audit log records `{Action, EntryId, Tenant, Principal, Agent?, BeforeHash, AfterHash}` for every state-changing operation.

### Cross-tenant read returns 404, not 403

A `GET /expertise/{id}` for an entry in another tenant returns 404 rather than 403, regardless of whether the entry exists. 403 leaks the existence of an entry the caller is not permitted to see; 404 does not. This applies to all read endpoints.

### Draft visibility uses `GET /expertise/drafts`, not `?includeDrafts=true`

*Clarification added 2026-05-19; closes #67. Implements decisions made in rebuild PR 4 (#60). Decision direction unchanged — this section makes an implicit choice explicit.*

Rebuild PR 3 (#59) introduced a `?includeDrafts=true` query parameter on `/expertise`, `/expertise/search`, and `/expertise/search/semantic`, gated by `expertise.write.approve`. Rebuild PR 4 (#60) **removed** the parameter entirely and replaced it with a dedicated `GET /expertise/drafts` endpoint.

Reasons:

- **`?includeDrafts=true` silently widened the result set to include `Rejected` entries** (including any `RejectionReason` content) in addition to `Draft`. Approvers asking “show me drafts” had no way to filter out rejected entries, and the parameter name did not advertise the rejection-inclusion behaviour. A caller could exfiltrate sensitive rejection commentary by reading the unfiltered output.
- **A dedicated endpoint makes the trust-boundary jump explicit.** `/expertise` returns canonical (Approved) entries; `/expertise/drafts` returns the review queue (Draft only). Two URLs, two purposes, no in-band mode-switching.
- **The audit story is cleaner.** `/expertise/drafts` access can be logged as a distinct read action without straining the existing read-audit semantics with an “or-was-it-a-draft-read?” disjunction.
- **`Rejected` entries are not exposed via any read endpoint.** They remain in the data store for audit purposes (visible via `/audit`) but are unreachable through the read API. Treat `Rejected` as a tombstone state, not a queryable category.

The parameter is **removed**, not deprecated. No backward-compatibility window applies because it shipped only on the dev branch between PR 3 and PR 4 and never appeared in a tagged release.

### Soft-delete on shared entries requires `expertise.write.approve`

*Clarification added 2026-05-19; closes #67. Implements the decision made in rebuild PR 4 (#60). Decision direction unchanged.*

The original Decision Outcome listed soft-delete on shared entries as an admin-tier action without specifying which scope satisfies it. PR 4 settled on **`expertise.write.approve`** (not `expertise.admin`) because soft-deleting a shared entry is the symmetric inverse of approving one into the shared keyspace, and the two operations belong at the same trust level.

- **Symmetry argument.** Promoting a draft into `Tenant = 'shared'` is a `write.approve` action; demoting (soft-deleting) a shared entry should be the same.
- **`expertise.admin` is reserved for audit-log read and operational operations** (`/audit` access, future bulk operations), not for the steady-state shared-keyspace lifecycle.
- **Tenant-scoped soft-delete (non-shared entries) requires only `expertise.write.draft`** for the writer's own entry. The scope escalation applies specifically when the entry's `Tenant = 'shared'`.

### Consequences

- Good, because a compromised agent token is now contained: drafts are visible only to approvers in the same tenant, never to other agents' read paths.
- Good, because `Source` (self-reported by the writer) is no longer a trust signal — `AuthorPrincipal` (token-asserted) and `ReviewState` (gate-enforced) drive trust decisions.
- Good, because the audit log gives the team a tamper-evident record of who promoted what and when, queryable via `/audit` for `expertise.admin` holders.
- Good, because shared entries (`Tenant = 'shared'`) require an explicit `write.approve` action — drift into the shared keyspace is no longer accidental.
- Bad, because curator workflow now requires a human-in-the-loop step for every entry that should reach canonical state. This is intentional; the framework's curator agent operates with `write.approve` scope to triage drafts on behalf of the operator. Operators must never grant `write.approve` to long-lived non-interactive service principals.
- Bad, because the legacy `expertise.write` scope is now ambiguous and must be deprecated cleanly. Removal is tracked as a future breaking change (Conventional Commits `!`).
- Bad, because the dedicated `GET /expertise/drafts` endpoint adds a UI/agent path that must be tested for tenant-scoping. Approvers see drafts in their own tenant only, never drafts in other tenants — even with `expertise.admin`.

## Related

- ADR-001 (tenancy model) — `write.approve` is what permits writing `Tenant = 'shared'`.
- ADR-002 (multi-IdP OIDC) — scope claim names differ per issuer (`scp` for Entra, `scope` for Authentik); the OIDC token-validated handler normalizes both into `TenantContext.Scopes`.
