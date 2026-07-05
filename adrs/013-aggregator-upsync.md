# Hub-and-spoke aggregator: up-sync v1 via existing batch endpoint and draft-only scope

- Status: accepted
- Date: 2026-07-05
- Companion: [ADR-001](001-tenancy-model.md) (tenancy, unmodified), [ADR-003](003-scope-split.md) (scope semantics as the enforcement mechanism), [ADR-005](005-multi-issuer-jwt-policy-scheme.md) (auth plumbing reused), [ADR-008](008-response-hygiene-and-actor-class.md) (service actor class), [ADR-010](010-idempotency-key-handling.md) (batch exclusion), [ADR-012](012-backup-artifact-format.md) (Merkle construction reused later)
- Tracking issue: [#341](https://github.com/psmfd/agent-expertise-api/issues/341)
- Surfaced by: 2026-07-05 backup/restore + aggregator design session (3-agent fan-out); user decisions locked the same day: OIDC `client_credentials` on a shared IdP, up-sync only for v1.

## Context and Problem Statement

Several independent A2 instances ("spokes", typically NAT'd solo-developer boxes) accumulate expertise entries. Teams want approved, shareable knowledge aggregated on one central instance (the "hub") where a curator controls what becomes visible to other teams. How do spokes ship entries to the hub without new write surface, without a new auth mode, and without letting any spoke poison the hub's shared knowledge base (OWASP ASI04/ASI06 — knowledge-supply-chain poisoning, the exact threat class in `docs/security/integration-threat-model.md`)?

## Considered Options

- **A. New dedicated sync endpoint + new service-token auth mode.** Maximum flexibility; maximum new attack surface: a second write path to review, a non-OIDC credential class outside the existing startup guards, and a new ADR-003 bypass risk.
- **B. Reuse `POST /expertise/batch` + OIDC `client_credentials` on a shared IdP, spoke credential scoped `expertise.write.draft` only.** Zero new write endpoints; zero new auth code (ADR-005 multi-issuer plumbing and the `TenantSource` mapping already handle service principals); the Draft gate is enforced by scope semantics that already exist and are already tested.
- **C. Pull model (hub polls spokes).** Requires every spoke to be reachable from the hub — false for NAT'd A2 boxes; also inverts the credential direction so the hub holds a credential for every spoke (worse blast radius).

## Decision Outcome

**Chosen: Option B.** Hub-and-spoke; **each spoke is a tenant on the hub** (ADR-001 unmodified). All connections are spoke-initiated. v1 is **up-sync only**; down-sync is deferred with an explicit scope list ([#342](https://github.com/psmfd/agent-expertise-api/issues/342)).

The non-negotiable control: the spoke's hub credential carries **`expertise.write.draft` and nothing else — never `.approve`**. ADR-003's scope semantics then force every synced entry to land as `Draft` in the spoke's own hub-side tenant, invisible to every other tenant until the hub curator independently approves it. No sync code enforces this; the existing authorization layer does. A compromised or malicious spoke can spam its own tenant's draft queue — it cannot place content in front of other teams.

### Sub-decisions

**Spoke side: `ExpertiseSyncWorker` (`BackgroundService`).** Modeled on `IdempotencyGcService`/`MigrationStateRefresher` (PeriodicTimer, `IServiceScopeFactory` scope per tick, two-tier narrowed exception handling, `internal static` interval override for tests). It pages newly Approved + `shared`-tenant local entries after a persisted cursor and POSTs them to the hub's existing `POST /expertise/batch` in ≤100-item chunks (the endpoint's `MaxBatchSize`). Data access goes through a new `IExpertiseRepository` method (cursor-paged `(UpdatedAt, Id)` keyset) — the DbContext-encapsulation architecture test forbids constructor-injecting `ExpertiseDbContext` outside `Data/`/`Cli/`, and the worker is not CLI.

**Sync cursor: new `SyncStates` table.** Singleton-row pattern copied from `EmbeddingMetadata` (POCO + `DbSet` + get-or-create at the call site): `LastSyncedUpdatedAt`, `LastSyncedId`, `LastSuccessAt`. The cursor advances only after the hub acknowledges a batch (200/207 with per-item `Created`/`Duplicate` results); `Failed` items are retried next tick.

**Auth: OIDC `client_credentials` on the shared IdP; hub-side tenancy is config-only.** The hub derives the spoke's tenant from the token via the existing `TenantSource.CompoundRole` (roles encoded `{tenant}:{scope}`) or `GroupToTenantMapping` machinery — whichever the shared IdP supports for service clients. The payload's tenant field is never trusted (ADR-003 "never from request body"). No new auth code on either side; provisioning a spoke = IdP client + role/group config + hub `Auth:Oidc` config.

**Actor class: `Service`, for free.** `client_credentials` tokens where `sub == azp` already resolve to `ActorClass = Service` (ADR-008), so synced writes are distinguishable in the hub audit log with no sync-specific code.

**Origin attribution: two nullable informational columns.** `OriginInstanceId` — set **server-side on the hub** from the authenticated client identity (azp/client_id → configured instance mapping), never from the payload (same principle as tenant). `OriginAuthorPrincipal` — accepted from the request body as informational reviewer context. Both are excluded from `IntegrityHashService.Compute`'s canonical fields (like `Source`/`SourceVersion`) and from dedup equality. Adding an optional request field is an additive OpenAPI change (breaking-change gate unaffected).

**Retry safety: at-least-once via dedup, not header idempotency.** `POST /expertise/batch` is explicitly outside Idempotency-Key scope (ADR-010: "revisiting batch requires amending this ADR") — confirmed in code: the route chains no `.RequireIdempotency()`. Sync retries therefore rely on the tenant-scoped `DeduplicationService` (exact + semantic) to absorb replays: a re-POSTed entry returns `Duplicate`, which the worker treats as success. This is recorded here deliberately; if strict idempotency is later required, that is an ADR-010 amendment, not a sync-side workaround.

**Resilience: named HttpClient + `Microsoft.Extensions.Http.Resilience`** (first HttpClient in the codebase), plus a minimal `client_credentials` token client caching tokens until expiry−60s. Sync failure must never affect API availability: the worker degrades to logging + Prometheus counters (`pushed`/`duplicate`/`failed`/cycle metrics) and retries on the next tick.

### Explicitly out of scope for v1

- **Down-sync (hub → spoke)** — deferred with its scope enumerated in [#342](https://github.com/psmfd/agent-expertise-api/issues/342): keyset cursor on `GET /expertise`, ADR-008 hygiene-envelope stripping in the client, monotonic `sourceUpdatedAt` anti-rollback, tombstone propagation + signed-Merkle-root reconciliation (reusing ADR-012's construction), and a draft re-gate on the receiving spoke.
- **Cross-spoke dedup at the hub** — two spokes submitting the same tip land in two tenants and do not collapse; `DeduplicationService` is deliberately tenant-scoped (ADR-001, prevents cross-tenant leakage via 409 bodies). Curator-facing hints tracked in [#344](https://github.com/psmfd/agent-expertise-api/issues/344).
- **mTLS PKI between instances, per-entry cryptographic signatures on sync, hub re-signing, hash-chained audit log, SLSA-level provenance** — rejected as disproportionate at this scale by all three design agents; revisit only if the hub becomes an untrusted third party.
- **EFCore.BulkExtensions** for hub-side upsert volume — rejected (revenue-gated dual license since 2023); plain EF load-then-save, raw `ON CONFLICT` SQL if volume ever demands.

## Consequences

- **Good** — zero new write endpoints and zero new auth code; the supply-chain control is an existing, tested authorization behavior (ADR-003), not new sync logic.
- **Good** — spokes work from behind NAT; the hub holds no spoke credentials.
- **Good** — synced writes are audit-distinguishable (`ActorClass = Service`) and origin-attributed (`OriginInstanceId` server-set) with minimal schema change.
- **Good** — at-least-once delivery with dedup absorption means the worker needs no distributed-transaction machinery.
- **Bad** — requires a shared IdP between hub and spokes; two organizations without one cannot federate in v1 (accepted per user decision — revisit only with a concrete second-party need).
- **Bad** — semantic dedup as the replay absorber is heuristic: a replay after the original was *edited* on the hub may create a near-duplicate draft for the curator to reject. Accepted; curator review is the backstop by design.
- **Bad** — the hub's draft queue is spammable by a compromised spoke (rate-limited by the existing `expertise-write` policy, 10/min per principal; blast radius is the spoke's own tenant).
- **Bad** — `Visibility` vs tenant-`shared` vocabulary remains subtle: "Approved + shared" for sync purposes means `Tenant == "shared" && ReviewState == Approved` on the spoke. The implementation and docs must be precise or the worker syncs the wrong slice.
- **Revisit if** — a second write-heavy consumer of `/batch` appears (idempotency amendment pressure), down-sync is scheduled (#342), or hub trust assumptions change (per-entry signatures moot).

## Implementation notes (non-normative)

- Spoke config: `Sync` section (`Enabled`, `HubUrl`, `TokenEndpoint`, `ClientId`, secret via env, `Interval`, `BatchSize`), manual startup guard in the repo's convention (no `ValidateOnStart`): `Enabled` ⇒ all connection fields present, else fail at boot.
- Hub config: `Sync:KnownInstances` map (client_id → instance id) feeding `OriginInstanceId`.
- Config surfaces: `appsettings.json` (`_comment` convention), compose `.env.example`, Helm `values.yaml` + `values.schema.json` + secret via existing `secretRef` pattern, `install.sh` secrets-stub heredoc + `SECRETS_SCHEMA_VERSION` bump.
- Tests: unit (cursor advance, chunking, token cache); integration (new repository method's tenant/review filtering; batch endpoint origin handling; worker end-to-end against an in-proc stub hub via `WebApplicationFactory`).
