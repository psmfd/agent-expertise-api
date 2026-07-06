# Testing & Coverage Guide

Durable record of this project's testing conventions and the guardrails that protect
against **silent bugs** — code that compiles clean, passes analyzers, and fails only at
runtime. It captures decisions that would otherwise live only in test-file headers, PR
descriptions, or a maintainer's head. Read this before adding a repository query, a new
endpoint, or an enum member.

## The lesson that shaped these guardrails

`POST /expertise/batch` once shipped a runtime-fatal bug: `ExpertiseRepository`
`FindExactMatchesAsync` used `.ToLowerInvariant()` inside a LINQ-to-SQL predicate. EF Core
cannot translate `ToLowerInvariant()`, so the method threw `InvalidOperationException` on
its first real query — failing *every* valid batch item at the dedup phase. It passed the
compiler, `AnalysisMode=All` (0 warnings), and CodeQL. It survived because **no test ever
executed that query against a real database**, and the endpoint had no HTTP-level test at
all until one was finally written.

The generalizable finding: **EF Core query-translation failures have no compile-time or
analyzer detection — by design.** An untranslatable expression is only discovered when the
query pipeline runs against a real provider. The guards below exist to force that discovery
in CI instead of production.

## Running the tests

- **Unit tests** need no external services: `dotnet test ExpertiseApi.slnx --filter "FullyQualifiedName~Unit"`.
- **Integration tests** use [Testcontainers](https://dotnet.testcontainers.org/) to spin up
  a real PostgreSQL + pgvector container per run, so a **Docker-compatible runtime must be
  reachable**. Docker Desktop works out of the box. With **podman** (no Docker Desktop),
  point Testcontainers at the podman socket and disable the Ryuk reaper:

  ```sh
  podman machine start
  export DOCKER_HOST="unix://$(podman machine inspect --format '{{.ConnectionInfo.PodmanSocket.Path}}')"
  export TESTCONTAINERS_RYUK_DISABLED=true
  dotnet test ExpertiseApi.slnx
  ```

- CI (`ci.yml`, ubuntu-latest) has Docker available, so the **full suite including
  integration tests runs on every PR** — a coverage number measured there is meaningful.

Test layout and framework stack are documented in [CLAUDE.md](../CLAUDE.md#testing).

## Guardrails against silent bugs

### 1. Query-translation tests (`RepositoryQueryTranslationTests`)

`IQueryable.ToQueryString()` runs the full EF+Npgsql translation pipeline **without a
database or open connection** — a fast unit test (no Docker) that throws exactly as a real
execution would if a predicate cannot translate. Every distinctive predicate shape in
`ExpertiseRepository` has a `ToQueryString().Should().NotThrow()` assertion: array
containment (`tags.All`), `LOWER()` (`ToLower`), pgvector `CosineDistance` ordering, both
Guid keyset-cursor forms (`>` vs `CompareTo`), and `HasConversion<string>` enum comparisons.

**Same-PR expectation (mandatory convention):** any new conditional filter, `EF.Functions.*`
call, or query shape added to `ExpertiseRepository` ships with a translation assertion in
the same PR. The suite is DB-less and runs in milliseconds — there is no excuse to skip it.
The highest-risk method (`ListAsync`'s combinatorial filters) is tested against the exact
production expression tree via the extracted internal `BuildListQuery` seam, so the test
cannot drift from production.

### 2. Content-derived mock embeddings (`TestHelpers.CreateContentEmbedding`)

The test embedding generator seeds each vector from a **stable FNV hash of the input
content** (not `string.GetHashCode`, which is randomized per process). Identical content
yields the identical vector (cosine distance 0 → a real duplicate); distinct content yields
a near-orthogonal vector (distance ≈ 1.0, far above the 0.10 dedup threshold → not a
duplicate). This replaced an earlier mock that returned the *same* vector for every input,
which made semantic-dedup behaviour structurally unobservable and forced tests into
per-test workarounds.

**Limitation to remember:** hash-based vectors are all-or-nothing (identical vs orthogonal).
They eliminate *false* collisions but do not model *graded* semantic similarity. Testing the
0.10 threshold itself needs real embeddings and is out of scope for the mock.

### 3. Frozen enum-name guard (`EnumContractTests`)

`ReviewState`, `Visibility`, `EntryType`, `Severity`, `AuditAction`, and `ActorClass` are
**all stored as strings** (EF `HasConversion<string>()`) **and serialized as strings**
(`JsonStringEnumConverter`). A member name is therefore both the persisted DB value and the
wire contract. Renaming or removing a member silently breaks stored-row parsing and the JSON
contract — the `oasdiff` gate shows the schema change but a reviewer can wave it through. The
frozen-name assertions fail loudly so any change becomes a **deliberate, migration-aware
edit**: update the frozen set *and* account for existing persisted rows.

### 4. Coverage regression ratchet (`scripts/check-coverage.sh`)

The CI `Test` step collects coverage (`--collect:"XPlat Code Coverage"` with
`coverlet.runsettings`, which excludes `Migrations/`), and `scripts/check-coverage.sh` fails
the build if line or branch coverage drops below the floor in `.coverage-baseline`.

It is a **regression ratchet, not an aspirational target.** The floors sit a few points
below the measured value (currently `line=82.0` / `branch=68.0` against ≈84.6% / ≈70.9%) so
normal variation never trips CI, but removing a test file does.

- **Raise the floors** in `.coverage-baseline` when coverage improves. Never lower them
  without a recorded reason in the commit body.
- **Do not** treat the floor as the goal — a green ratchet means "no regression," not "well
  tested."
- **Mutation testing (Stryker.NET) was deliberately not adopted.** It strengthens *existing*
  tests rather than covering *unexecuted* paths (the actual failure class here), and its
  per-mutant cost is disproportionate against the Testcontainers-backed suite. Revisit only
  as a scheduled, file-scoped run if ever.

Coverage percentage is a weak proxy for this bug class: a line can show "covered" by a test
that mocks the very component whose real behaviour matters. The ratchet catches *unexecuted*
code; guards 1–3 catch *code executed only against fakes*.

### 5. Testability seams (`private → internal`)

Where a test needs to drive production logic deterministically — a query builder, a
background-service sweep, a state refresh — the production method is made `internal` (not
`private`) so the test shares the exact production code path rather than a reconstruction
that could drift. Current seams: `ApplyTenantFilter`, `ApplyApprovedReviewFilter`,
`BuildListQuery` (repository); `IdempotencyGcService.SweepOnceAsync`;
`MigrationStateRefresher.RefreshOnceAsync`. `InternalsVisibleTo("ExpertiseApi.Tests")` in
the csproj makes them reachable. Prefer this over re-deriving the logic in the test.

## Verifying a guard actually guards

When adding or changing a guard, prove it catches the failure it targets: reintroduce the
bug (e.g. swap `ToLower()` back to `ToLowerInvariant()`, revert the mock to
content-independent) and confirm the relevant test **fails**, then restore. Every coverage
guard in this repo was validated this way before merge. A test that cannot be made to fail
is not a guard.

## Related durable records

- **Backup/restore & aggregator design and decisions:** [ADR-012](../adrs/012-backup-artifact-format.md),
  [ADR-013](../adrs/013-aggregator-upsync.md), and the
  [operator runbook](operations/backup-restore-runbook.md) — these are the authoritative,
  durable home for that feature's rationale (not any session handoff note).
- **Security controls the tests protect:** [integration threat model](security/integration-threat-model.md).
