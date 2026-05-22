# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Self-hosted .NET 10 REST API for storing and serving expertise entries consumed by AI agents (GitHub Copilot, Claude). Entries are a running log of issues/fixes, workarounds, caveats, and requirements — either domain-specific or shared across agent domains.

## Tech Stack

- **.NET 10** (LTS) with **ASP.NET Core Minimal APIs**
- **PostgreSQL 17** with **pgvector** extension for semantic search
- **EF Core** for data access (repository pattern via `IExpertiseRepository`)
- **PgBouncer 1.21+** sidecar for connection pooling (transaction mode)
- **In-process ONNX** embeddings via `Microsoft.SemanticKernel.Connectors.Onnx` (bge-micro-v2, 384-dim)
- **Serilog** for structured logging (`Serilog.AspNetCore`)
- **prometheus-net** for Prometheus metrics endpoint (`/metrics`)
- **OpenAPI** docs via Scalar (`Scalar.AspNetCore`)
- **Docker Compose** for local dev; **Helm** chart for k8s deployment

## Prerequisites

```bash
# .NET 10 SDK (verify with: dotnet --version)
# The repo pins the SDK band via global.json (10.0.1xx, latestFeature rollforward).
# Docker + Docker Compose
# EF Core CLI tool is pinned in .config/dotnet-tools.json — install via:
dotnet tool restore
```

## Build & Run Commands

```bash
# Build
dotnet build src/ExpertiseApi/ExpertiseApi.csproj

# Run locally (requires PostgreSQL via Docker Compose)
dotnet run --project src/ExpertiseApi/ExpertiseApi.csproj

# EF Core migrations
dotnet ef migrations add <MigrationName> --project src/ExpertiseApi
dotnet ef database update --project src/ExpertiseApi

# Run tests (requires Docker for integration tests)
dotnet test ExpertiseApi.slnx

# Docker Compose local dev stack (database only)
docker compose -f deploy/local/docker-compose.yml up postgres pgbouncer

# Docker Compose full stack (database + API)
docker compose -f deploy/local/docker-compose.yml up

# Regenerate all embeddings (CLI command)
dotnet run --project src/ExpertiseApi -- reembed [--batch-size 50]

# Backfill IntegrityHash for entries created before the secure-rebuild data model
# (entries with IntegrityHash = NULL). Idempotent — only touches null rows.
dotnet run --project src/ExpertiseApi -- rehash [--batch-size 50]
```

## Model Download

The ONNX model files are not tracked in git. Download them before running locally or building Docker images:

```bash
# Download bge-micro-v2 quantized model (~17.4 MB) and vocab.txt
./scripts/download-models.sh

# Force re-download (e.g., after model version bump)
FORCE=1 ./scripts/download-models.sh
```

Files land in `src/ExpertiseApi/models/` (gitignored). In CI, this step is cached and only runs when `scripts/download-models.sh` changes.

## Local Development Quick Start

```bash
# 1. Start the database layer
cp deploy/local/.env.example deploy/local/.env
# Edit deploy/local/.env — set POSTGRES_PASSWORD and AUTH__APIKEY
docker compose -f deploy/local/docker-compose.yml up -d postgres pgbouncer

# 2. Apply EF Core migrations
dotnet ef database update --project src/ExpertiseApi

# 2b. Download ONNX model files (required for embeddings and semantic search)
./scripts/download-models.sh

# 3. Run the API
dotnet run --project src/ExpertiseApi

# 4. Verify — health check (no auth required)
curl http://localhost:5000/health

# 5. Create an entry (under Hybrid mode in Development — accepts the API key from .env)
curl -X POST http://localhost:5000/expertise \
  -H "Authorization: Bearer dev-api-key-change-me" \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "shared",
    "title": "Example expertise entry",
    "body": "This is a test entry for local development.",
    "entryType": "Pattern",
    "severity": "Info",
    "source": "human"
  }'

# 6. List entries
curl http://localhost:5000/expertise \
  -H "Authorization: Bearer dev-api-key-change-me"

# 7. Keyword search
curl "http://localhost:5000/expertise/search?q=test" \
  -H "Authorization: Bearer dev-api-key-change-me"

# 8. Semantic search (requires ONNX model files in src/ExpertiseApi/models/)
curl "http://localhost:5000/expertise/search/semantic?q=test&limit=5" \
  -H "Authorization: Bearer dev-api-key-change-me"

# 9. OpenAPI docs
# Browse to http://localhost:5000/scalar/v1 (Development only — gated on IsDevelopment())

# 10. Query page (interactive browser UI for read-only browsing and search)
# Browse to http://localhost:5000/query
```

**Note:** Semantic search and embedding generation require the bge-micro-v2 ONNX model files (`model.onnx` and `vocab.txt`) in `src/ExpertiseApi/models/`. Without them, the API will start but POST/PATCH and semantic search will fail. CRUD and keyword search work without the model.

## Agent Integration

AI agents (Claude Code, GitHub Copilot, Codex CLI, pi) consume this API via HTTP with a bearer token. Typical agent workflow:

1. **Search** existing expertise before solving a problem: `GET /expertise/search?q=<query>` or `GET /expertise/search/semantic?q=<query>`
2. **Create** a new entry when discovering a fix, caveat, or pattern: `POST /expertise`
3. **Update** an entry when information changes: `PATCH /expertise/{id}`

All endpoints except `/health`, `/query`, and `/metrics` require `Authorization: Bearer <token>`.

### Skill

An action-oriented skill ships in-tree at [`.agents/skills/expertise-api/`](.agents/skills/expertise-api/SKILL.md). It wraps each CRUD + review operation as a small curl-based script and is portable across pi, Claude Code, and Codex CLI.

```jsonc
// Claude Code — .claude/settings.json or ~/.claude/settings.json
{ "skills": [".agents/skills/expertise-api"] }
```

```jsonc
// pi — ~/.pi/settings.json (or symlink into ~/.pi/agent/skills/)
{ "skills": [".agents/skills/expertise-api"] }
```

pi users additionally get the in-tree extension at [`.pi/extensions/expertise-api/`](.pi/extensions/expertise-api/README.md) which registers eight typed tools (`expertise_search`, `expertise_search_semantic`, `expertise_get`, `expertise_create`, `expertise_update`, `expertise_approve`, `expertise_reject`, `expertise_delete`). The LLM calls them via `fetch()` so the bearer token does not appear in `ps`/`/proc`. Symlink `.pi/extensions/expertise-api/` into `~/.pi/agent/extensions/` to enable globally.

Three slash-command prompt templates at [`.pi/prompts/`](.pi/prompts/) layer on top: `/expertise-search <query>` (semantic-first), `/expertise-create <title> [summary]`, `/expertise-approve <id> [visibility]`. They appear in `/` autocomplete with `argument-hint` annotations.

Codex CLI: add the path to `skills = [...]` in `~/.codex/config.toml`.

The legacy skill at `.claude/skills/expertise-api-design/SKILL.md` is now a shim that redirects to the new path. The design reference (data model, scopes, approval state machine, audit-log shape, authentication modes) moved to [`.agents/skills/expertise-api/references/DESIGN.md`](.agents/skills/expertise-api/references/DESIGN.md) and is loaded on demand only.

Env contract used by every script in the skill:

```sh
mkdir -p ~/.config/expertise-api
cat > ~/.config/expertise-api/secrets.env <<'EOF'
EXPERTISE_API_BASE_URL=https://expertise.example.com
EXPERTISE_API_TOKEN=...
EOF
chmod 600 ~/.config/expertise-api/secrets.env
```

The scripts source that file automatically when present — no per-shell exports needed.

## Native OS service install (Archetype A2)

For a solo developer who wants the API as a persistent OS service rather
than `docker compose up`, see `scripts/install.sh` (macOS + Linux + WSL),
`scripts/install.ps1` (Windows), and the README's "Archetype A2" section.
The long-term artifact format for this install path (cosign-signed
tarball published by CI rather than `dotnet publish` on the install
host) is captured in [`adrs/011-deployment-artifact-format.md`](adrs/011-deployment-artifact-format.md).

The API host wires `builder.Host.UseSystemd().UseWindowsService(...)` in
`Program.cs` immediately after `WebApplication.CreateBuilder(args)`. Both
calls are no-ops outside their host environment, so Docker / Helm /
`dotnet run` paths remain identical.

Daily-use control: `scripts/expertise-apictl {start|stop|restart|status|logs|health}`.

## Authentication

`Auth:Mode` config switch governs which authentication scheme(s) are accepted:

| Mode | Tokens accepted | Permitted environments |
| --- | --- | --- |
| `Oidc` | Validated JWT from any configured issuer | All — required outside Development |
| `LocalDev` | Custom dev token `Bearer dev:{tenant}:{scope1}+{scope2}` | Development only |
| `ApiKey` | Static `Auth:ApiKey` value (legacy) | Development only |
| `Hybrid` | All of the above | Development only — default |

The startup guard hard-fails if (a) a non-`Oidc` mode is configured outside Development, or (b) `Auth:Mode=Oidc` is configured with zero valid `Auth:Oidc:Issuers` entries (any environment). Without (b) a misconfigured deployment boots cleanly, passes `/health` and `/metrics`, and returns 500 on the first protected request.

### Scopes

Four scopes drive the four authorization policies. The hierarchy is `admin ⊇ approve ⊇ draft ⊇ read` — a token carrying `expertise.admin` satisfies any policy:

| Scope | Policy | Required for |
| --- | --- | --- |
| `expertise.read` | `ReadAccess` | All `GET` endpoints |
| `expertise.write.draft` | `WriteAccess` | `POST`, `PATCH`, `DELETE` (drafts in caller's own tenant; **changing `Visibility` via PATCH escalates to `expertise.write.approve`** — see ADR-003) |
| `expertise.write.approve` | `WriteApproveAccess` | `/approve`, `/reject`, `PATCH` when `Visibility` changes, soft-delete on `shared` |
| `expertise.admin` | `AdminAccess` | `/audit`, cross-tenant ops |

The legacy `expertise.write` scope is normalized to `expertise.write.draft` during one transition cycle.

### Tenant filtering on reads

Every read path is scoped to `Tenant IN (caller_tenant, "shared") AND ReviewState = Approved`. Cross-tenant reads return 404, never 403, so existence is not disclosed. The filter is layered:

1. **Endpoint** — every read endpoint reads `httpContext.RequireTenantContext()` and passes it to the repository.
2. **Repository** — every `IExpertiseRepository` method takes a `TenantContext` and applies an explicit `WHERE` clause (primary safeguard per ADR-001).
3. **EF global query filter** — `HasQueryFilter` on `ExpertiseEntry` reads from `ITenantContextAccessor` as defense-in-depth. When the accessor returns null (CLI / design-time / direct DbContext access in tests) the filter short-circuits and the explicit repository `WHERE` drives correctness.
4. **CLI bypass** — `reembed` and `rehash` call `IgnoreQueryFilters()` explicitly to operate across all tenants.

`?includeDeprecated=true` lifts the `DeprecatedAt IS NULL` filter; tenant scoping still applies.

### Approval workflow

Reads default to `ReviewState = Approved`. Reviewers see `Draft` and `Rejected` entries via `GET /expertise/drafts` (requires `expertise.write.approve`, caller's tenant only — no cross-tenant or shared draft visibility). The previous `?includeDrafts=true` query parameter on `/expertise` and `/expertise/search*` was replaced by `/expertise/drafts`.

Approval transitions:

- `POST /expertise/{id}/approve` — `Draft → Approved`. Sets `ReviewedBy`, `ReviewedAt`, applies optional `Visibility` from request body (default `Private`), clears `RejectionReason`.
- `POST /expertise/{id}/reject` — `Draft → Rejected`. Body `{ "rejectionReason": "..." }` is required, max 2000 characters.
- Both require `expertise.write.approve`. Both return 409 if the entry is not in `Draft` state.
- Both use a Postgres `xmin` row-version concurrency token: a concurrent approve+reject race resolves to one 200 + one 409 instead of last-write-wins. The same applies to concurrent PATCHes — `UpdateAsync` catches `DbUpdateConcurrencyException` and returns 409.

PATCH state regression (per ADR-003): when a `write.draft`-only caller PATCHes an `Approved` or `Rejected` entry, the entry regresses to `Draft` and review metadata (`ReviewedBy`, `ReviewedAt`, `RejectionReason`) is cleared. A caller carrying `write.approve` preserves the source state. The Approved branch closes the ASI06 path where post-approval content edits would otherwise bypass review; the Rejected branch lets an author resubmit content after addressing the rejection reason.

Dedup queries (exact-match and semantic) exclude `Rejected` entries so a Rejected entry does not permanently block resubmission of identical content. Drafts and Approved entries still dedup as before.

Soft-deleting a `Tenant = "shared"` entry requires `expertise.write.approve`. A `write.draft` caller attempting to delete a shared entry receives 403.

### Audit log

Every state-changing operation (`POST`, `PATCH`, `DELETE`, `/approve`, `/reject`) writes a row to `ExpertiseAuditLog` in the same database transaction as the entry mutation — atomic by construction. Hashes (`BeforeHash`, `AfterHash`) are SHA-256 over the canonical content fields per `IntegrityHashService`; equal hashes mean content was not modified (e.g., approve/reject change `ReviewState` only, not content).

`GET /audit` is admin-only (`expertise.admin`), cross-tenant, cursor-paginated on `(Timestamp DESC, Id)`. Query parameters: `entryId`, `principal`, `action`, `from`, `to`, `limit` (1-200, default 50), `afterTimestamp` + `afterId` (cursor).

### ForwardedHeaders for IpAddress capture

The audit log records the client IP address. To get the real client IP behind an ingress controller, configure `ForwardedHeaders:KnownNetworks` (CIDR list) so the `UseForwardedHeaders` middleware trusts only the proxy network. Without explicit allowlist the middleware trusts only loopback and audit IpAddress will record the ingress pod IP. In Kubernetes the value is typically the cluster pod CIDR.

### OIDC issuers

`Auth:Oidc:Issuers[]` is an array of per-issuer configs. Each entry:

```jsonc
{
  "Name": "Entra",
  "Issuer": "https://login.microsoftonline.com/{tenant-id}/v2.0",
  "Audience": "{api-client-id-guid}",
  "AdditionalAudiences": ["api://expertise-api"],
  "ScopeClaims": ["scp", "roles"],
  "TenantSource": "CompoundRole",     // for client_credentials — parses "team:scope"
  "RoleSeparator": ":",
  "GroupClaim": "groups",
  "GroupToTenantMapping": { "<group-id>": "team-alpha" }
}
```

Notes:

- **Trailing slash on `Issuer`** must match the `iss` claim byte-exactly. Authentik includes one; Entra v2 does not. Copy from `.well-known/openid-configuration` verbatim.
- **`TenantSource = "CompoundRole"`** is for Entra `client_credentials` flow, which does not emit `groups` for service principals. Roles are encoded as `{tenant}:{scope}` and parsed at validation time.
- **`TenantSource = "Groups"`** is for delegated flows (and Authentik). Group claim values are mapped to tenant slugs.

### LocalDev token format

`Bearer dev:{tenant}:{scope1}+{scope2}+...`

Examples:

```text
Bearer dev:legacy:read
Bearer dev:team-alpha:draft+read
Bearer dev:shared:admin
```

Scope shorthand (`read`, `draft`, `approve`, `admin`) expands to full scope strings; any other value passes through verbatim. The handler is registered only when `Auth:Mode` is `LocalDev` or `Hybrid` AND the environment is Development.

## CI/CD

| Workflow | Trigger | What it does |
| -------- | ------- | ------------ |
| `ci.yml` | PR to main, push to dev | `dotnet build` + `dotnet test` |
| `release.yml` | Push to main | semantic-release version bump + tag, then Docker build linux/amd64+arm64, push to GHCR (only when a new version is released) |
| `lint-pr-title.yml` | PR to dev | Validates PR title follows Conventional Commits format |

GHCR image: `ghcr.io/thesemicolon/agent-expertise-api` (multi-arch: amd64 + arm64).

### Pre-flight PR validator

`scripts/validate-pr.sh` runs the same checks `lint-pr-title.yml` enforces (plus branch name + base branch from `agent-framework/rules/github-flow.md`) so failures surface locally before a PR is opened. Run it before every `gh pr create` / `gh pr edit --title`:

```bash
scripts/validate-pr.sh --title "fix: patch concurrency mapping" --branch fix/foo --base dev
```

`--branch` defaults to the current git branch, `--base` defaults to `dev`, `--title` is required. Exit codes: `0` PASS, `1` FAIL, `2` precondition failure (missing args). The most common trip-ups are uppercase first letters in the subject (acronyms like `PATCH`, `JWT`, `OIDC` and proper nouns like `PostgreSQL`/`GitHub` must lowercase at the start).

## Testing

### Test Prerequisites

- **Docker** must be running — integration tests use [Testcontainers](https://dotnet.testcontainers.org/) to spin up a PostgreSQL + pgvector instance per test run.
- Unit tests run without Docker.

### Commands

```bash
# Run all tests (unit + integration)
dotnet test ExpertiseApi.slnx

# Run unit tests only (no Docker required)
dotnet test ExpertiseApi.slnx --filter "FullyQualifiedName~Unit"

# Run integration tests only
dotnet test ExpertiseApi.slnx --filter "FullyQualifiedName~Integration"

# Helm chart render tests
bash helm/expertise-api/tests/test-render.sh
```

### Test Project Structure

```text
tests/ExpertiseApi.Tests/
  Infrastructure/     # Test fixtures, ApiFactory, helpers
  Unit/               # Fast tests, no external dependencies
  Integration/        # Full-stack tests via WebApplicationFactory + Testcontainers
  Architecture/       # Reflection-based architectural guards (e.g. DbContext encapsulation)
```

### Framework Stack

| Component | Package | Purpose |
|-----------|---------|---------|
| Test framework | xUnit | Test runner and assertions |
| Assertions | FluentAssertions | Readable assertion syntax |
| Mocking | NSubstitute | Interface mocking (embedding generator, etc.) |
| Database | Testcontainers.PostgreSql | Disposable PostgreSQL + pgvector container |
| HTTP testing | Microsoft.AspNetCore.Mvc.Testing | `WebApplicationFactory` for integration tests |
| Log assertions | Microsoft.Extensions.Diagnostics.Testing | `FakeLogCollector` for verifying log output |

### Test Expectations

- **New features and bug fixes must include tests.** Unit tests for logic, integration tests for endpoint behavior.
- **Helm chart changes** should be validated with the render test script.
- CI runs `dotnet test` on every PR and push to `dev`.

## Security Scanning

Findings flow into the GitHub Security tab (Code scanning + Dependabot + Secret scanning).

| Layer | Tool | Where |
| --- | --- | --- |
| SAST | CodeQL (C#, advanced setup) | `.github/workflows/codeql.yml` |
| SCA (build-time gate) | `dotnet list package --vulnerable` | step in `.github/workflows/ci.yml` |
| SCA (async alerts + PRs) | Dependabot | repo settings + `.github/dependabot.yml` |
| Secrets | GitHub secret scanning + push protection | repo settings (Settings → Code security) |
| Container/IaC misconfig | Trivy filesystem scan | `.github/workflows/security.yml` |
| Dockerfile lint | Hadolint | `.github/workflows/security.yml` |
| .NET analyzers | `<AnalysisMode>All</AnalysisMode>` | `Directory.Build.props` |

Triggers for CodeQL, Trivy, and Hadolint: push to `main`/`dev`, pull requests targeting `main`/`dev`, weekly schedule (Sunday 06:00 UTC), and manual dispatch.

The .NET analyzers run as **warnings** with `<AnalysisMode>All</AnalysisMode>`. The baseline reached **0 warnings** via issue #101's three-PR cleanup (CA1515 internal sweep + ADR-006; CA2007/CA1861 scoped suppressions; CA1062/CA1848 globally suppressed; CA1873 in `Cli/`; CA1308 in `Data/`; mechanical fixes for the residual culture/comparison/async family). The `Format gate` step in `ci.yml` runs `dotnet format analyzers --verify-no-changes` to prevent regression. The test project overrides `<AnalysisMode>Minimum</AnalysisMode>` to suppress xUnit conventions (underscore method names, `Random` for test data) that produce false-positive security findings.

See `adrs/004-security-scanning-stack.md` for the design rationale and known gaps (no reachability-aware SCA, no API security spec analysis).

## Data Model — Secure Rebuild

The `ExpertiseEntry` entity carries the original content fields (`Domain`, `Tags`, `Title`, `Body`, `EntryType`, `Severity`, `Source`, `SourceVersion`, `Embedding`, `CreatedAt`, `UpdatedAt`, `DeprecatedAt`, `SearchVector`) plus the secure-rebuild additions:

| Field | Type | Notes |
| --- | --- | --- |
| `Tenant` | `string`, required, indexed | Owning team. `shared` is a first-class tenant value. Migration backfills `legacy` for pre-rebuild rows; column-level default dropped post-backfill. |
| `Visibility` | `enum { Private, Shared }` | Stored as string. Defaults to `Private`. Setting `Shared` on create requires `expertise.write.approve`. Changing `Visibility` via PATCH (either direction) requires `expertise.write.approve`; no-op (PATCH supplies the current value) does not escalate. |
| `AuthorPrincipal` | `string`, required | OIDC `sub` of the writer. Server-set. Migration backfills `pre-rebuild`. |
| `AuthorAgent` | `string?` | Agent name when written via an agent. Distinct from `AuthorPrincipal`. |
| `IntegrityHash` | `string?` | SHA-256 hex over canonical JSON of `{tenant, title, body, entryType, severity}`. Backfilled by the `rehash` CLI. |
| `ReviewState` | `enum { Draft, Approved, Rejected }` | Stored as string. Defaults to `Draft`. |
| `ReviewedBy`, `ReviewedAt`, `RejectionReason` | `string?`, `DateTime?`, `string?` | Approval/rejection metadata, server-set on `/approve` or `/reject` (later PR). |

Indexes added: standalone B-tree on `Tenant`; composite B-tree on `(Tenant, ReviewState)` with `INCLUDE (Id, EntryType, Severity)` covering the hot read path.

A separate **`ExpertiseAuditLog`** table records every state-changing operation: `{Id, Timestamp, Action, EntryId, Tenant, Principal, Agent?, BeforeHash, AfterHash, IpAddress}`. FK to `ExpertiseEntries.Id` with `ON DELETE RESTRICT` (audit must survive entry deletion). Reads are not audited. Indexes on `(EntryId, Timestamp)` and `(Principal, Timestamp)`.

The `ExpertiseDbContext` exposes both as `DbSet<>`. **`IExpertiseRepository` is the only sanctioned consumer of `ExpertiseDbContext` for entry data** — the architectural test in `tests/ExpertiseApi.Tests/Architecture/` enforces this for everything outside `Data/` and `Cli/`. CLI commands (`reembed`, `rehash`) are intentional exceptions because they need cursor-based paging the repository interface does not expose.

## Architecture & Design

For API surface, authentication, embedding architecture, and known gotchas, see [`.agents/skills/expertise-api/references/DESIGN.md`](.agents/skills/expertise-api/references/DESIGN.md) (authoritative reference). Use the `expertise-api-owner` agent for design and implementation questions.

For the secure-rebuild design rationale, see `adrs/001-tenancy-model.md`, `adrs/002-multi-idp-oidc.md`, and `adrs/003-scope-split.md`.
