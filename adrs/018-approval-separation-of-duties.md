# Separation of duties on the approval gate (author ≠ reviewer)

- Status: accepted
- Date: 2026-07-24
- Companion: [`docs/security/integration-threat-model.md`](../docs/security/integration-threat-model.md) Part D (C6), OWASP ASI04 / ASI06
- Relates to: [ADR-003](003-scope-split.md) (scope split), [ADR-008](008-response-hygiene-and-actor-class.md) (actor class), [ADR-013](013-aggregator-upsync.md) (up-sync draft gating)

## Context and Problem Statement

The approval workflow exists to enforce one invariant: **machine-written content is reviewed by a human before it becomes Approved (and potentially `shared`)**. This is the knowledge-supply-chain control against OWASP ASI04 (agentic supply chain) and ASI06 (unverified content promotion).

A design fan-out for the service-mode human-review workflow found the invariant is not actually enforced. `ApproveAsync` / `RejectAsync` (`src/ExpertiseApi/Data/ExpertiseRepository.cs`) authorize solely on the `expertise.write.approve` scope and read the reviewer's `sub` claim **only to populate the audit row** — they never compare it to `entry.AuthorPrincipal`. Consequently any principal holding `write.approve` can approve or reject **its own** drafts, including a service/agent principal self-approving machine-written content. The gate was cosmetic.

A second, related finding: the `Human` vs `Service` actor-class tag that the audit log records is not cryptographically reliable evidence of a human. The offline A2 minting tool (`scripts/mint_token.py sign`) sets `sub = client` and emits no `azp`/`appid`/`client_id`, so every offline-minted token resolves `Human` by default; conversely a real-IdP `client_credentials` reviewer token collapses to `Service`. The audit trail records the tag but nothing consumes it, and it cannot be trusted to prove the review was human.

## Considered Options

**A. `sub`-equality gate with admin break-glass (chosen).** Block approve/reject with 403 when `reviewer_sub == entry.AuthorPrincipal`, unless the caller also holds `expertise.admin`.

**B. Machine-authored-only gate.** Block self-review only when the *author* was a non-human principal, leaving human curators free to self-publish. Requires the entry to persist the author's actor class (a schema change; today only `AuthorPrincipal`/`AuthorAgent` are stored, not the author's resolved `ActorClass`). More surface, more state, and no security gain over A in the deployment that matters: in service mode the agent's `sub` already differs from the human reviewer's `sub`, so A never blocks the legitimate flow.

**C. No server gate; rely on scope discipline** (never grant a service principal `write.approve`, per ADR-013's spoke credential). Rejected: a control that lives only in provisioning discipline (or only in a CLI client) is not a control — raw `curl`, a future web UI, or a misprovisioned token all bypass it. The gap is pre-existing and must be closed server-side.

## Decision Outcome

**Option A.** A new `WriteOutcome.SelfReviewForbidden` (→ **403**) is returned by `ApproveAsync`/`RejectAsync` when the caller's resolved reviewer identity equals `entry.AuthorPrincipal` and the caller does not hold `expertise.admin`. The check is evaluated **after** load/NotFound and **before** the Draft-state check, so the authorization boundary precedes business-logic state. Comparison is `Ordinal` (`sub` is an opaque, case-sensitive identifier).

- **`expertise.admin` is the audited break-glass** for the solo-operator case (author writes a manual entry *and* approves it). Every approve/reject already writes an audit row, so a break-glass self-approval is queryable after the fact.
- **Natural service-mode flow is unaffected**: an agent writes with a service `sub`; the human reviews with a different `sub`; the two are never equal, so the gate never fires in the intended path.
- **Up-sync is unaffected**: a hub-side synced draft's `AuthorPrincipal` is the spoke sync-credential's `sub`, distinct from the hub curator who reviews it.

**Attribution (companion, tracked in #484).** We do not attempt to make `Human` cryptographically provable — in the offline A2 issuer the same tool mints both human and machine tokens, so a self-asserted human claim would be exactly as forgeable as the `X-Actor-Class` header the system already refuses to trust (ADR-008). Instead: (1) a structured warning log + Prometheus counter fires when a non-`Human` actor class successfully approves/rejects, making the collapse scenario observable and alertable; (2) operational guidance requires the reviewer credential to be minted deliberately and separately from any agent token, backed by `mint_token.py`'s existing refusal to mint `write.approve` without `--allow-privileged`.

## Consequences

### Positive

- The self-approval hole (ASI04/ASI06) is closed at the server, the only place a control is authoritative across all clients (curl, CLI, future UI).
- The intended service-mode and up-sync review flows are untouched — the gate is invisible in the happy path.
- No schema change; the gate reuses existing `AuthorPrincipal` and `Scopes` state.

### Negative

- A solo operator who both authors and reviews a manual (human-written) entry with the same identity must either use an `expertise.admin` token or review under a distinct identity. Accepted: the admin break-glass covers it, and separation of duties is the correct default even for human-authored content.
- A single shared identity used for both writing and reviewing is blocked from self-review by design — this is the misconfiguration the gate is meant to catch, not a regression.

### Neutral

- `SelfReviewForbidden` maps to 403, mirroring the existing `InsufficientScope` outcome; both approve/reject routes already declare a 403 `ProducesProblem` response, so the OpenAPI contract is unchanged (no breaking-change flag).

## Links

- Issue #483 (this gate), #484 (non-Human-approve anomaly log/counter), #485 (review CLI), #486 (skill argv token leak)
- [ADR-003 — Four-scope split](003-scope-split.md), [ADR-008 — Response hygiene & actor class](008-response-hygiene-and-actor-class.md), [ADR-013 — Aggregator up-sync](013-aggregator-upsync.md)
- [Integration threat model](../docs/security/integration-threat-model.md) — Part D, ASI04/ASI06
