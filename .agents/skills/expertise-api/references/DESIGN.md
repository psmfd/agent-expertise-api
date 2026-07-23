# expertise-api — design reference

Loaded on demand from `SKILL.md`. Covers data model, scopes, approval state
machine, audit log, and authentication modes. Action-oriented usage — the
curl toolkit and when to invoke it — lives in `SKILL.md`.

---

## Design Decisions

| Concern | Decision |
|---------|----------|
| Runtime | .NET 10 (LTS) |
| API framework | ASP.NET Core Minimal APIs |
| OpenAPI | Scalar (`Scalar.AspNetCore`) |
| Data access | Repository pattern — `IExpertiseRepository` |
| Database | PostgreSQL 17 (single backend, no MongoDB) |
| PostgreSQL image | `pgvector/pgvector:pg17` |
| Connection pooling | PgBouncer 1.21+ sidecar (`edoburu/pgbouncer`, transaction mode) |
| Vector search | pgvector extension — `vector(512)` column with HNSW index (`vector_cosine_ops`) |
| Keyword search | PostgreSQL stored generated `tsvector` column with GIN index |
| Embeddings | In-process ONNX via `Microsoft.SemanticKernel.Connectors.Onnx` |
| Embedding model | `jina-embeddings-v2-small-en` (~130MB FP32, 512-dim, 6144-token ceiling, bundled in Docker image — ADR-017) |
| Embedding abstraction | `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI) |
| Embedding input | `EmbeddingService.BuildInputText(title, body)` — single source of truth |
| Auth | Multi-issuer OIDC (Entra + Authentik) via `JwtBearer` per issuer behind a `Bearer` policy scheme; `ApiKey`/`LocalDev`/`Hybrid` modes for Development only |
| Tags storage | PostgreSQL `text[]` with GIN index (not JSONB — avoids EF Core 10 `Contains()` bug) |
| Deployment | k3s — personal and business clusters |
| Local dev | Docker Compose (not a deployment target) |
| Ingress | ingress-nginx |
| TLS | cert-manager + Route53 DNS-01 ACME |
| DNS | DDNS cron script (no external-dns controller) |
| Secrets | SOPS + age (no in-cluster controller) |
| Container registry | GitHub Container Registry (`ghcr.io`, private) |
| Manifests | Helm chart — shared templates, per-environment values |
| Backup | Out-of-chart sidecar (custom image, deployed from the infrastructure repo) |
| CLI | `reembed` — regenerate all embeddings for model migration |

## Data Model

```csharp
public class ExpertiseEntry
{
    public Guid Id { get; set; }
    public required string Domain { get; set; }     // "azure-devops", "iac", "shared"
    public List<string> Tags { get; set; } = [];     // PostgreSQL text[] with GIN index
    public required string Title { get; set; }
    public required string Body { get; set; }        // markdown
    public EntryType EntryType { get; set; }
    public Severity Severity { get; set; }
    public required string Source { get; set; }      // self-reported — informational only post-rebuild
    public string? SourceVersion { get; set; }       // e.g. "EF Core 10.0.1" — staleness signal
    public Vector? Embedding { get; set; }           // pgvector vector(512), nullable until embedded
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeprecatedAt { get; set; }      // soft delete
    public NpgsqlTsVector? SearchVector { get; set; } // stored generated tsvector column

    // Secure-rebuild additions (PR 1)
    public required string Tenant { get; set; }      // owning team; "shared" is first-class
    public Visibility Visibility { get; set; }       // Private (default) | Shared
    public required string AuthorPrincipal { get; set; } // OIDC sub of writer, server-set
    public string? AuthorAgent { get; set; }         // agent name if written by an agent
    public string? IntegrityHash { get; set; }       // SHA-256 hex; nullable until rehash CLI runs
    public ReviewState ReviewState { get; set; }     // Draft (default) | Approved | Rejected
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }

    // Aggregator up-sync attribution (ADR-013) — informational, excluded from
    // canonical hash and dedup equality (like Source/SourceVersion)
    public string? OriginInstanceId { get; set; }        // server-set from Sync:KnownInstances, never from body
    public string? OriginAuthorPrincipal { get; set; }   // origin-side author, body-supplied, 256-char cap
}

public enum EntryType  { IssueFix, Caveat, Requirement, Pattern }
public enum Severity   { Info, Warning, Critical }
public enum Visibility { Private, Shared }
public enum ReviewState { Draft, Approved, Rejected }
public enum AuditAction { Created, Updated, Approved, Rejected, Deleted }

public class ExpertiseAuditLog
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public AuditAction Action { get; set; }
    public Guid EntryId { get; set; }                // FK to ExpertiseEntries with ON DELETE RESTRICT
    public required string Tenant { get; set; }
    public required string Principal { get; set; }
    public string? Agent { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string? IpAddress { get; set; }
}
```

Trust decisions are based on `AuthorPrincipal` (token-asserted) and `ReviewState` (gate-enforced) — `Source` is self-reported and informational only post-rebuild.

`IntegrityHash` is SHA-256 over canonical JSON of `{tenant, title, body, entryType, severity}` with alphabetical key order. Computed via `IntegrityHashService.Compute`. The `rehash` CLI command backfills `IntegrityHash` for any entry with a null hash (pre-rebuild rows or rows created before the audit-write path lands).

Indexes on `ExpertiseEntries`: standalone B-tree on `Tenant`; covering composite on `(Tenant, ReviewState)` `INCLUDE (Id, EntryType, Severity)`. Existing GIN on `Tags`, GIN on `SearchVector`, HNSW on `Embedding`, B-tree on `Domain` and `DeprecatedAt`, expression index on `LOWER("Title")` are unchanged.

Indexes on `ExpertiseAuditLogs`: covering composite `(EntryId, Timestamp)` `INCLUDE (Action)`; composite `(Principal, Timestamp)`.

Single-row `EmbeddingMetadata` table tracks model name, dimensions, and `LastReembedAt`.

## API Surface

| Method | Endpoint | Scope | Purpose | Optional params |
|--------|----------|-------|---------|-----------------|
| GET | `/expertise` | `expertise.read` | Filter by domain, tags, type, severity | `domain`, `tags` (comma-separated), `entryType`, `severity`, `includeDeprecated` |
| GET | `/expertise/{id}` | `expertise.read` | Single entry | |
| POST | `/expertise` | `expertise.write.draft` | Create entry (generates embedding) | |
| POST | `/expertise/batch` | `expertise.write.draft` | Create up to 100 entries (generates embeddings, deduplicates) | Max 100 entries per batch |
| PATCH | `/expertise/{id}` | `expertise.write.draft` | Update entry (regenerates embedding if title/body changed). Changing `visibility` — or PATCHing a `shared` entry at all (#330) — requires `expertise.write.approve`. | Title ≤ 200 / Body ≤ 1500 chars |
| DELETE | `/expertise/{id}` | `expertise.write.draft` | Soft delete (sets DeprecatedAt). Shared entries require `expertise.write.approve`. | |
| GET | `/expertise/drafts` | `expertise.write.approve` | List Draft + Rejected entries in caller's tenant | |
| POST | `/expertise/{id}/approve` | `expertise.write.approve` | Transition Draft → Approved | |
| POST | `/expertise/{id}/reject` | `expertise.write.approve` | Transition Draft → Rejected (requires reason) | |
| GET | `/expertise/search?q=` | `expertise.read` | Keyword full-text search (`websearch_to_tsquery` over tsvector, `ts_rank_cd` ranked; supports quoted phrases, OR, `-negation`) | `domain`, `tags` (comma-separated, all must match), `entryType`, `severity`, `limit` (1-100, default 50), `includeDeprecated` |
| GET | `/expertise/search/semantic?q=` | `expertise.read` | Semantic vector search (pgvector) | `domain`, `tags` (comma-separated, all must match), `entryType`, `severity`, `limit` (1-100, default 10), `includeDeprecated` |
| GET | `/expertise/search/hybrid?q=` | `expertise.read` | Hybrid search — keyword + semantic arms fused with Reciprocal Rank Fusion (k=60, ties newest-first; ADR-016). Recommended default for agent callers | `domain`, `tags` (comma-separated, all must match), `entryType`, `severity`, `limit` (1-100, default 10), `includeDeprecated` |
| GET | `/audit` | `expertise.admin` | Cross-tenant audit log | `entryId`, `principal`, `action`, `from`, `to`, `limit` (1-200, default 50), cursor (`afterTimestamp` + `afterId`) |
| GET | `/health` | none | Liveness probe | |
| GET | `/metrics` | none | Prometheus scrape endpoint | |
| GET | `/query` | none | Interactive browser UI for read-only API exploration | |

CLI:

- `dotnet run --project src/ExpertiseApi -- reembed [--batch-size 50]` — regenerate all embeddings.
- `dotnet run --project src/ExpertiseApi -- rehash [--batch-size 50]` — backfill `IntegrityHash` for entries with a null hash. Idempotent.
- `dotnet run --project src/ExpertiseApi -- backup --output <dir> [--instance-id <id>]` — export all entries (every tenant + review state) + audit log as NDJSON with an RFC 6962 Merkle manifest (ADR-012). Plain files; signing/encryption is `scripts/expertise-apictl`'s job. Deliberately CLI-only — a backup is a full-fidelity cross-tenant extract, more privileged than `GET /audit/{id}/raw`, so it must never be reachable through a bearer token.
- `dotnet run --project src/ExpertiseApi -- restore --input <dir> [--force-draft]` — import a decrypted backup payload. Replace mode only (empty target, pending migrations empty); verifies per-record `BackupRecordHash` + Merkle roots against the manifest (root mismatch → abort; single-record mismatch → quarantine as Draft + `RestoreQuarantined` audit row); `--force-draft` re-gates every entry for foreign-backup seeds.

## Authentication

`Auth:Mode` config switch drives scheme registration. `Oidc` is the only mode permitted outside Development; `LocalDev`, `ApiKey`, and `Hybrid` hard-fail on startup in any non-Development environment. `Hybrid` is the default in Development. `Auth:Mode=Oidc` with zero valid `Auth:Oidc:Issuers` entries also hard-fails on startup (any environment) — without this guard the API boots cleanly but 500s every protected request.

### Modes

| Mode | Accepts | Default scheme behavior |
| --- | --- | --- |
| `Oidc` | Validated JWT from configured issuers | One named `JwtBearer` scheme per issuer behind a `Bearer` policy scheme that routes by token's `iss` |
| `LocalDev` | `Bearer dev:{tenant}:{scope1}+{scope2}` ad-hoc tokens | Custom `LocalDevAuthHandler` |
| `ApiKey` | Legacy static API key via `Auth:ApiKey` | `ApiKeyAuthHandler` (mints `expertise.write.draft` and `expertise.read`) |
| `Hybrid` | All of the above | Policy scheme routes by token shape: `dev:` → LocalDev; `xxx.yyy.zzz` → JWT (per matching `iss`); else → ApiKey |

### Multi-issuer JWT

`Auth:Oidc:Issuers[]` carries one entry per IdP. Each is registered as its own named `JwtBearer` scheme so audience validation is pinned per issuer (a flat `ValidIssuers`/`ValidAudiences` list would allow cross-issuer audience contamination). The `Bearer` policy scheme uses `ForwardDefaultSelector` to route incoming tokens to the right named scheme.

### Tenant derivation

`OidcIssuerOptions.TenantSource`:

- **`Groups`** — walk the principal's group claims through `GroupToTenantMapping`. Used for delegated flows and Authentik (which emits groups for both flows).
- **`CompoundRole`** — parse each scope-claim entry as `{tenant}{separator}{scope}` (default separator: `:`). Required for Entra `client_credentials`, which does not emit `groups` for service principals.

### Scope claims

Per-issuer `ScopeClaims[]`:

- Entra: `["scp", "roles"]` — `scp` for delegated, `roles` for `client_credentials`. Both unioned.
- Authentik: `["scope"]` — RFC 9068 standard.

### Scopes and policies

Four scopes with hierarchical implication (`admin ⊇ approve ⊇ draft ⊇ read`). Scope expansion is precomputed on the principal during `OnTokenValidated` (`JwtTenantContextEvents.ExpandScopeClosure`) — the `ScopeAuthorizationHandler` is then a simple `Contains` check. The legacy `expertise.write` scope is normalized to `expertise.write.draft` for one transition cycle.

| Scope | Policy |
| --- | --- |
| `expertise.read` | `ReadAccess` |
| `expertise.write.draft` | `WriteAccess` |
| `expertise.write.approve` | `WriteApproveAccess` |
| `expertise.admin` | `AdminAccess` |

### TenantContext

A `TenantContext { Tenant, Principal, Agent?, Scopes[] }` is built per request and stashed on `HttpContext.Features`. All authentication paths (JWT, ApiKey, LocalDev) populate it. Endpoints read it via `HttpContext.RequireTenantContext()`. Per ADR-001 every `IExpertiseRepository` method takes a `TenantContext` argument and constructs explicit `WHERE Tenant IN (ctx.Tenant, "shared") AND ReviewState = Approved` predicates. The default review state filter is lifted via `GET /expertise/drafts` for callers carrying `expertise.write.approve` (caller's tenant only); the tenant filter is unconditional.

When the principal authenticates successfully but no tenant maps (e.g. group not in `GroupToTenantMapping`), `TenantContext.Tenant` is `null` and the authorization handler returns 403.

### Tenant filtering — defense layers

1. **Endpoint** — every protected endpoint reads `httpContext.RequireTenantContext()` and threads it to the repository.
2. **Repository** — primary safeguard. Each method applies an explicit `WHERE Tenant IN (ctx.Tenant, "shared")`; `GetByIdAsync`, `UpdateAsync`, and `SoftDeleteAsync` use `Where(id) + FirstOrDefaultAsync` rather than `FindAsync` so the filter cannot be bypassed via the EF identity map.
3. **EF global query filter** — `HasQueryFilter` on `ExpertiseEntry` reads `ITenantContextAccessor.Tenant` (HTTP-backed). Defense-in-depth: when a future query forgets the explicit `WHERE`, this still applies. Short-circuits to no filter when the accessor returns null (CLI / design-time / direct test seeding) — those paths rely on the explicit repository `WHERE` for correctness.
4. **CLI bypass** — `reembed` and `rehash` commands call `IgnoreQueryFilters()` explicitly so they process every tenant.
5. **Dedup tenant scoping** — `FindExactMatchAsync`, `FindExactMatchesAsync`, `FindNearestInDomainAsync`, and `FindAllEmbeddingsInDomainAsync` are tenant-scoped so a `409 Conflict` on `POST /expertise` cannot leak another tenant's entry contents in the response body.

Cross-tenant operations return **404, not 403**, on `GET`, `PATCH`, and `DELETE` so existence is not disclosed.

### Approval workflow

Reads default to `ReviewState = Approved`. The previous `?includeDrafts=true` query parameter on `/expertise` and `/expertise/search*` was replaced by a dedicated `GET /expertise/drafts` endpoint that returns `Draft` and `Rejected` entries in the caller's tenant only (no `shared` for the draft queue). Requires `expertise.write.approve`.

`POST /expertise/{id}/approve` and `POST /expertise/{id}/reject` move entries between states. Both:

- Require `expertise.write.approve`.
- Are tenant-scoped — cross-tenant returns 404, even for admins.
- Validate the source state is `Draft`; otherwise 409.
- Use a Postgres `xmin` system column as an EF Core RowVersion concurrency token. Concurrent approve+reject and concurrent PATCH races resolve to one 200 + one 409 (no schema migration; `xmin` already exists on every Postgres table). `UpdateAsync` catches `DbUpdateConcurrencyException` and maps it to 409 alongside `ApproveAsync`/`RejectAsync`.
- Write an `ExpertiseAuditLog` row in the same `SaveChangesAsync` as the state mutation — atomic by construction.
- `/reject` requires a non-empty `RejectionReason` body field, max 2000 chars.

PATCH state regression (ADR-003): a `write.draft`-only caller editing an `Approved` or `Rejected` entry resets it to `Draft` and clears review metadata (`ReviewedBy`, `ReviewedAt`, `RejectionReason`) so it requires re-review. A `write.approve` caller preserves the source state. The Approved branch ensures content changes post-approval cannot bypass review (ASI06 mitigation); the Rejected branch enables resubmission after the author addresses the rejection reason.

Dedup queries (exact-match and semantic) exclude `Rejected` entries — otherwise a Rejected entry would permanently block resubmission of identical content. Drafts and Approved entries still dedup as before.

Mutating a `Tenant = "shared"` entry — content PATCH or soft-delete — requires `expertise.write.approve` (returns 403 otherwise — 404 would mislead since the caller can read the entry). The PATCH gate (#330) prevents a `write.draft` caller in any tenant from editing a shared Approved entry, which would regress it to `Draft + Tenant="shared"` — a state no tenant's draft queue surfaces, stranding the entry. Changing `Visibility` on PATCH (Private ↔ Shared, either direction) is the symmetric inverse of `/approve`'s Visibility selection and also requires `expertise.write.approve`; the check is value-based, so a no-op PATCH that supplies the current Visibility does not escalate.

### Audit log

Every write path writes one `ExpertiseAuditLog` row in the same transaction as the entry mutation. The repository owns this — `ExpertiseRepository.BuildAuditRow` constructs the row using `IHttpContextAccessor` for `IpAddress`, falling back to `null` when no `HttpContext` (CLI). `BeforeHash` / `AfterHash` are SHA-256 over the canonical content fields (`IntegrityHashService`); approve/reject leave content unchanged so before == after, but the `Action` discriminates the transition.

`GET /audit` is `expertise.admin`-only and cross-tenant. Query parameters: `entryId`, `principal`, `action`, `from`, `to`, `limit` (1-200, default 50), plus cursor pagination via `afterTimestamp` + `afterId`.

### Aggregator up-sync (ADR-013)

Hub-and-spoke; each spoke is a tenant on the hub (ADR-001 unmodified). The spoke's `ExpertiseSyncWorker` (`BackgroundService`, `Sync` config section, disabled by default) pages Approved + `shared`-tenant entries past a `SyncStates` keyset cursor `(LastSyncedUpdatedAt, LastSyncedId)` via `IExpertiseRepository.ListSharedApprovedUpdatedAfterAsync` and POSTs ≤100-item batches to the hub's existing `POST /expertise/batch` under OIDC `client_credentials`. The spoke's hub credential carries `expertise.write.draft` ONLY, so ADR-003 semantics land every synced entry as Draft in the spoke's hub-side tenant — the supply-chain control is the existing authorization layer, not sync code. At-least-once delivery: `/batch` is outside Idempotency-Key scope (ADR-010); replays come back `Duplicate` (success). The cursor advances only when a page lands entirely as Created/Duplicate/Rejected; any transient `Failed` (or HTTP/token failure) retries the whole page next tick. `Rejected` is permanent — logged and skipped. Hub-side `Sync:KnownInstances` (client id → instance id) drives server-set `OriginInstanceId`.

### ForwardedHeaders middleware

`Program.cs` registers `UseForwardedHeaders()` before `UseAuthentication()`. Configure `ForwardedHeaders:KnownNetworks` (CIDR list) so the middleware trusts only the actual proxy network — without an allowlist the middleware trusts only loopback and audit `IpAddress` records the ingress pod IP. In k8s the value is typically the cluster pod CIDR. Helm values should expose this as `ingress.trustedCidr` or equivalent.

## Embedding Architecture

In-process ONNX using `BertOnnxTextEmbeddingGenerationService` behind `IEmbeddingGenerator<string, Embedding<float>>`. Registered with `AddBertOnnxEmbeddingGenerator`. Requires `#pragma warning disable SKEXP0070`. Model/vocab paths configurable via `Onnx:ModelPath` and `Onnx:VocabPath` config keys (default: `models/model.onnx`, `models/vocab.txt`). The abstraction allows future substitution with Ollama or Azure OpenAI without changing application code.

The embedding input text is constructed by `EmbeddingService.BuildInputText(title, body)` — this is the single source of truth for what text gets embedded, used by POST, PATCH, and reembed.

## Repository Structure

```text
src/ExpertiseApi/
  Program.cs               # Entry point, service registration, middleware
  wwwroot/                 # Static files — query page UI
  Endpoints/               # Minimal API endpoint definitions
  Models/                  # ExpertiseEntry, EmbeddingMetadata, enums (EntryType, Severity)
  Data/                    # DbContext, IExpertiseRepository, ExpertiseRepository, DesignTimeDbContextFactory
  Migrations/              # EF Core migrations (nine and counting — InitialCreate through the secure-rebuild/search additions)
  Services/                # EmbeddingService, DeduplicationService
  Auth/                    # ApiKeyAuthHandler, AuthExtensions, AuthConstants
  Cli/                     # ReembedCommand, RehashCommand, MigrateCommand, BackupCommand, RestoreCommand
  models/                  # ONNX model files (jina-v2-small) — not committed, needed at runtime
helm/expertise-api/        # Helm chart (shared templates, generic values)
deploy/local/              # Docker Compose, .env.example, pgvector init script
scripts/                   # download-models.sh
```

## Known Gotchas

- **Npgsql:** Pin to 10.0.1+. Avoid JSONB `Contains()` (issue #3745).
- **PgBouncer + Npgsql:** Connection string must include `No Reset On Close=true`. PgBouncer 1.21+ required for `max_prepared_statements`.
- **PgBouncer transaction mode:** Advisory locks, LISTEN/NOTIFY, session-level SET, SQL-level PREPARE/EXECUTE do not work across transactions.
- **PgBouncer `auth_dbname`:** Required in PgBouncer 1.21+ when using `auth_query` mode.
- **`/dev/shm`:** Default 64MB too small for PostgreSQL containers. Mount emptyDir Memory volume (128Mi).
- **PV reclaim policy:** k3s local-path defaults to `Delete`. Patch to `Retain` immediately.
- **pgvector init:** Use `.sh` script in `/docker-entrypoint-initdb.d/`, not `.sql` (pgvector issue #355).
- **Scalar:** Avoid deprecated-endpoint transformers until Scalar issue #6020 is resolved.
- **SKEXP0070:** `BertOnnxTextEmbeddingGenerationService` requires `#pragma warning disable SKEXP0070`.
- **No PDB** with `minAvailable: 1` for single-replica PostgreSQL — blocks node drain.
- **SOPS key:** Back up age private key separately — if lost, encrypted secrets are unrecoverable.
- **k3s:** Must disable Traefik with `--disable=traefik` at install time.
- **`reembed` CLI:** Run as a one-off k8s Job, not from a running API replica, to avoid concurrent row writes.
- **Keyword search uses raw SQL:** The stored `SearchVector` column cannot be queried via LINQ — `KeywordSearchAsync` uses `FromSqlInterpolated` (`websearch_to_tsquery` + `ts_rank_cd` + `LIMIT`; all filtering and ordering must stay inside the SQL string — composing LINQ on top wraps it in a subquery and can drop the inner ORDER BY).
- **Search `score` field semantics:** search responses carry a server-computed `score` per hit (#427) — keyword: raw `ts_rank_cd` (unbounded, comparable only within one response); semantic: cosine similarity (`1 - distance`, higher is closer); hybrid: the fused RRF sum (`Σ 1/(60+rank)` across arms, ADR-016). Never compare scores across modes; every score is comparable only within one response. Non-search reads emit no score.
- **No query-instruction prefix (ADR-017):** jina-embeddings-v2-small-en embeds queries and documents symmetrically — NO instruction prefix on either side. The bge-era query prefix (PR #431) was model-specific and was removed with the swap; `EmbeddingServiceTests` pins the no-prefix contract. Any future model swap must re-verify prefix requirements against the new model’s card (bge-family needs one, jina forbids the asymmetry).
- **6144-token embedding ceiling / `MaxBodyLength` (ADR-017):** the generator is wired with `MaximumTokens = 6144` (`EmbeddingModelInfo`) — the measured coverage plateau for this corpus (99.71%, identical to 8192, at 65% less peak RSS). The ONNX connector **silently truncates** beyond the ceiling — no exception, no log (#429). `BuildInputText` embeds `"{title} {body}"` in one pass, so Title and Body share the window. Writes enforce `MaxBodyLength = 16000` chars (hard 400; ≈5,390 worst-case body tokens at the measured 2.97 chars/token minimum density) and `MaxTitleLength = 200` (#436). Entries created before the model swap have NULL embeddings until `reembed` runs (invisible to semantic/hybrid search, still in keyword results). Ceiling-filling embeds transiently peak ~12 GB RSS — relevant to A2 host sizing, prohibitive for the current Helm limits (#458). Re-derive all constants on any model or ceiling change; ground-truth tables live on issue #437.
- **A2 binds are plaintext http; non-loopback requires an explicit override:** `install.sh` hardcodes `ASPNETCORE_URLS=http://…` (no TLS on this path), so `preflight` refuses a non-loopback `--bind` unless `--allow-plaintext-bind` is passed (#332). Keep the API on loopback behind a co-located TLS edge (the LAN runbook topology); the override exists only for a remote TLS edge where cleartext on that segment is accepted. `AllowedHosts` is not a mitigation — HostFiltering checks only the client-controlled Host header.
- **`secrets.env` is sourced, not parsed:** the wrapper, `migrate.sh`, and install flow all `set -a; . secrets.env` (deliberate — quoted `;`-bearing values survive). Consequence: write access to `secrets.env` is code-execution-equivalent in the service context at every start/restart/migrate, not merely config injection. It is the one 600-mode, owner-guarded object in the tree; treat it accordingly (#332, full deployment threat model tracked in #226).
- **launchd services lack systemd-grade sandboxing:** the systemd unit ships `ProtectSystem`/`ProtectHome`/`RestrictAddressFamilies` etc.; the launchd plists have no first-class equivalent without code-signing/entitlements. Accepted platform-parity gap on macOS A2 installs (#332/#226).
- **Content root follows the process CWD (A2 installs):** a framework-dependent `dotnet ExpertiseApi.dll` launch resolves `appsettings*.json` and `wwwroot/` from the CWD, not the binary directory. A service started with CWD = install root silently loads NO appsettings (the LanStatic issuer defaults vanish → `AddScheme` null-name crash under OIDC config; `/query` 404s its HTML) — and under launchd the fatal is invisible in the StandardErrorPath log. Fixed in the A2 layer (#404): all three service templates pin `WorkingDirectory` to `bin/` and the launch wrapper `cd`s there. If you launch the DLL by hand, do it from `bin/`.
- **ONNX model files not committed:** `src/ExpertiseApi/models/` is gitignored. Model files must be present at runtime for embedding generation and semantic search. CRUD and keyword search work without them.
- **`EmbeddingMetadata` not auto-updated:** The metadata row is only written by the `reembed` CLI command, not by normal POST/PATCH operations.

## Implementation Status

All 6 personal phase steps are complete:

| Step | Status | What |
|------|--------|------|
| 1 | Done | Data model + EF Core migrations (`InitialCreate`, `AddSearchVector`) |
| 2 | Done | API endpoints (CRUD + keyword search) + API key auth |
| 3 | Done | Docker Compose local dev (postgres + pgbouncer + API) |
| 4 | Done | Embedding service (ONNX) + semantic search + `reembed` CLI |
| 5 | Done | Helm chart + SOPS secrets + bootstrap manifests + DDNS script |
| 6 | Done | Backup mechanism (moved to out-of-chart sidecar in the infrastructure repo) |

## Production Hardening — outstanding

Shipped: multi-issuer OIDC (ADR-002 → ADR-005), four-scope split + audit log (ADR-003), Serilog + Prometheus metrics, CodeQL/Trivy/Hadolint security pipeline (ADR-004), semantic-release.

Outstanding:

- Rate limiting (per-principal + per-tenant)
- Production deployments (Entra-backed business cluster; Authentik-backed homelab)

## Full Design Document

For the complete design including PostgreSQL tuning parameters, Helm values structure, Kubernetes deployment details, and cluster bootstrap steps, see GitHub issue #1.
