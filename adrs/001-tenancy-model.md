# Single-store tenancy with row-level tenant column and RBAC

- Status: accepted
- Date: 2026-04-28

## Context and Problem Statement

The expertise API today has a single keyspace shared by every authenticated client. With ASI06 (agent-written entries auto-injected into every other agent's context with no human in the loop), one client's writes silently influence every other client's read path. There is no isolation between teams, no notion of "shared" versus "private" expertise, and no way to scope a search to one team's data without losing the cross-cutting expertise that genuinely benefits everyone.

How should the API isolate expertise between consumers (teams, agents, individuals) while still allowing deliberate sharing of cross-cutting knowledge?

## Considered Options

- Per-tenant database instance (one Postgres database per consumer)
- Per-tenant schema in a single database (one schema per consumer)
- Single store with a row-level `Tenant` column, RBAC by tenant claim, and `shared` as a first-class tenant value
- Single store with PostgreSQL row-level security (RLS) policies enforced in the database

## Decision Outcome

Chosen option: **single store with a row-level `Tenant` column and RBAC by tenant claim**, with `shared` modelled as a first-class tenant value rather than a separate flag.

Reasons:

- One database, one set of migrations, one connection pool. Operationally simple at the scale the API targets.
- The HNSW vector index stays unified, so semantic search remains efficient across tenant + shared data when the caller is permitted to read both.
- `shared` as a tenant value makes the read filter explicit and uniform: every read uses `WHERE tenant IN (caller_tenant, 'shared') AND review_state = 'Approved'`. There is no special-case branch in the query path.
- RLS in the database (option 4) duplicates the policy in two places (app code and Postgres policies) and is hard to reason about under PgBouncer transaction-mode pooling, which loses session state between transactions.
- Per-tenant deployments (options 1 and 2) trade operational simplicity for stronger isolation that the threat model does not require — the tenants here are cooperating teams sharing the same trust boundary, not adversarial customers.

The trade-off — that a tenant-filter bug becomes a cross-tenant data leak — is mitigated with belt-and-braces:

1. Every `IExpertiseRepository` method is required to take a `TenantContext` argument. The `WHERE` clause is constructed from `TenantContext`, never from request input.
2. EF Core global query filter on `ExpertiseEntry.Tenant` as a defense-in-depth fallback, **not** the primary safeguard (filters can be silently bypassed with `IgnoreQueryFilters()`).
3. An architectural test fails the build if anything outside the `Data/` and `Cli/` namespaces takes `ExpertiseDbContext` as a constructor dependency.
4. Cross-tenant reads return **404, not 403** — never leak the existence of an entry the caller is not permitted to see.

### Consequences

- Good, because operational footprint stays small (one DB, one backup, one Helm chart, one migration history).
- Good, because shared-knowledge entries remain searchable from every tenant's read path without query duplication.
- Good, because adding a new tenant is a configuration change (group → tenant mapping in the IdP config block), not a deployment.
- Bad, because a single bug in tenant filtering can leak data across tenants. Mitigated by the four belt-and-braces measures above.
- Bad, because all tenants share the same blast radius for outages and bad migrations. Acceptable given the trust model and team scale.

## Related

- ADR-002 (multi-IdP OIDC) — defines how the `Tenant` claim is derived from group membership at each issuer.
- ADR-003 (four-scope split) — defines which scopes can write `Tenant = 'shared'` versus a caller's own tenant.
