# Keyed IntegrityHash and audit-log checkpoint chain (verification path for #468)

- Status: accepted
- Date: 2026-07-25

## Context and Problem Statement

`IntegrityHash` is an unsalted SHA-256 over canonical JSON of `{tenant, title, body, entryType, severity}` (`IntegrityHashService`), stored in the same mutable row it describes. The audit log's `BeforeHash`/`AfterHash` reuse the same computation. Nothing ever recomputes or compares any of these hashes — the `rehash` CLI only backfills nulls. The #333 OWASP-AI review (Finding 3, deferred to #468) called this out as decorative tamper-evidence: any actor who can write the row can trivially recompute a matching hash, and deleting an audit row is undetectable. Triage decided to build a real verification path, not downgrade the claim.

The design must answer three questions: (1) what makes the hash unforgeable by a database-only writer; (2) what makes audit-row deletion detectable; (3) where verification runs. A constraint discovered during design: deduplication does **not** compare hash values (`DeduplicationService` does a literal title+body string compare; no `WHERE IntegrityHash = …` query exists anywhere), so the hash *primitive* can change freely as long as the 5-field canonical set — which `BackupRecordHash`'s doc contract references — stays fixed.

## Threat model (stated honestly)

These mechanisms detect **post-commit tampering by database-only writers**: SQL injection through the app's DB credential, a stolen DB credential, a rogue DBA in a deployment where DB and app-host access are separated (Compose/Helm), or a popped Postgres container on an A2 host. They do **not** defend against:

- **A compromised app process** — it holds the HMAC key to compute legitimate writes, so it can forge consistently from the moment of compromise. HMAC/chaining prove *content has not changed since commit*, never that the commit was truthful.
- **Host root** on a single-host A2 install — root reads the key file and rewrites any local anchor. The only mitigation is an anchor that has already left the host (the ADR-012 signed backup manifest, when copied off-host).

On A2 the operator *is* the DB admin; the meaningful A2 attacker is one who reaches the DB without reaching the host (SQLi, container escape into Postgres only). In Helm/Compose deployments where the key is a secret mounted only into the API workload, HMAC delivers its full value: a DB-side writer was never issued the key and cannot forge a matching MAC.

## Considered Options

### 1. Hash strength

- **A. Keyed HMAC-SHA256, replacing the primitive in place** — same field, same canonical JSON, output `k<id>:<hex>`.
- **B. Additive `ContentMac` columns beside the existing hash** — no change to existing values.
- **C. Row-to-row chaining of entries** — rejected outright: entries are mutable (PATCH), chaining fits append-only data only.

### 2. Audit-log deletion detection

- **A. Strict per-row chain** (`PrevHash`/`RowHash` computed inside every mutating transaction).
- **B. Checkpoint (epoch) chain** — periodic checkpoint rows, each holding an RFC 6962 Merkle root over the audit-row range since the previous checkpoint plus the previous checkpoint's hash.
- **C. Rely on the ADR-012 backup Merkle manifest alone.**

### 3. Where verification runs

- **A. Operator CLI (`verify`) scheduled by an OS timer / CronJob.**
- **B. In-process `BackgroundService` sweep.**
- **C. On-read verification (fail-closed per response).**

## Decision Outcome

**1A — keyed HMAC in place.** `IntegrityHashService` computes `HMACSHA256(key, canonicalBytes)` over the *identical* 5-field canonical JSON, stored as `{keyId}:{lowercase hex}` in the existing `IntegrityHash` column and audit `BeforeHash`/`AfterHash`. Chosen over 1B because dedup never hash-compares (verified in code), the response field stays a plain string (zero OpenAPI change), and no new columns or dual bookkeeping are needed. The key-id prefix makes rotation cheap: retired keys stay resolvable for verification; a bare 64-hex value (no colon) is recognized as the legacy unkeyed format.

**2B — checkpoint chain.** A strict per-row chain (2A) forces every mutating transaction to read the current chain tip — a global write-serialization point that is fragile under PgBouncer transaction pooling and multi-replica Helm, and couples every endpoint's write path to the integrity feature. The checkpoint chain reuses the existing `MerkleTree` (RFC 6962, ADR-012) over `Id`-ranges of audit rows, adds zero write-path contention, and detects any edit or deletion inside a sealed range (the recomputed root diverges). Accepted cost: rows newer than the latest checkpoint are not yet committed to the chain — the detection gap equals the verification cadence and is monitored via checkpoint-staleness metrics. 2C alone is insufficient: backups are operator-cadence and heavier; the live chain gives per-sweep detection independent of backup timing. The two compose: checkpoints are included in backups, so the signed, off-host manifest (ADR-012 Amendment 1) anchors the chain head against a local rewrite.

**3A — CLI + OS timer.** `dotnet run -- verify` (wrapped as `expertise-apictl verify`) recomputes every entry MAC (cursor-paged, `AsNoTracking`, cross-tenant via `IgnoreQueryFilters`), verifies every sealed checkpoint by recomputing its Merkle root and chain link, then seals a new checkpoint through the latest audit row. Exit 0 clean / 1 mismatch / 2 precondition. Scheduling is an OS concern (systemd timer / launchd on A2, CronJob on k8s), matching how `backup` and `reembed` already run. The verification logic lives in an `IntegrityVerificationService` so 3B remains a future option without rework. 3C is rejected as a default: it puts recompute cost and a fail-closed failure mode on the read-hot path to catch what the sweep already catches within one interval; a narrowly scoped opt-in admin endpoint may be added later if a use case appears.

### Key management

- Config: `Integrity:HmacKey` (inline base64) / `Integrity:HmacKeyFile` (path to a base64 key file) — inline wins when both are set, mirroring the `EXPERTISE_API_TOKEN`/`_FILE` idiom (#464). `Integrity:ActiveKeyId` (default `k1`) names the key; `Integrity:RetiredKeys` (id → base64, added with `verify` in PR 2) keeps old keys resolvable for verification after rotation.
- Key material: 256-bit minimum (decoded length ≥ 32 bytes). Loaded once at startup into a singleton (`IIntegrityKeyProvider`); never hot-reloaded.
- **Fail-closed when configured**: a configured-but-unusable key (missing/unreadable file, invalid base64, short key, malformed key id) aborts boot — the ADR-015 JWKS posture. Silent fallback would quietly write forgeable hashes.
- **Soft-require when unconfigured** (phased rollout, the ADR-010 precedent): no key configured → legacy unkeyed SHA-256 plus a loud startup warning and an `expertise_integrity_unkeyed` gauge (1/0). `install.sh` generates a key automatically on A2, so native installs are keyed from the first post-upgrade boot; Compose/Helm operators add the secret at their own pace. The flip to hard-require outside Development is tracked in #490 and gated on fleet observability showing the gauge at zero.
- Per archetype: A2 — `install.sh`-generated file under `~/.config/expertise-api/` (600/700 perms) referenced via `Integrity__HmacKeyFile` in the service environment; Compose — `INTEGRITY__HMACKEY` in the gitignored `.env`; Helm — a key in the existing `auth.secretName` Secret via the established `envFrom` path (zero new chart plumbing).

### Migration and operational notes

- **One-time rekey**: `rehash --force` recomputes every row unconditionally (not just nulls). It ships in the same PR as the HMAC switch — without it, the first `verify` after an upgrade reports a false 100% mismatch. The upgrade runbook orders it: upgrade → key present → `rehash --force` → schedule `verify`.
- **Restore** (`restore` CLI) copies `IntegrityHash` verbatim from the backup payload; after restoring a backup taken under a different key (or pre-HMAC), run `rehash --force` before trusting `verify` output. Restore's own tamper detection is unaffected — it verifies `BackupRecordHash` + Merkle roots, which remain unkeyed by design (the backup manifest is signed; ADR-012).
- Audit `BeforeHash`/`AfterHash` written before the switch remain legacy-format; `verify` treats the format prefix as authoritative per value and reports (metric-labelled, non-fatal) how many legacy values remain.
- MAC comparison uses `CryptographicOperations.FixedTimeEquals`; hashing uses the static `HMACSHA256.HashData`/`SHA256.HashData` one-shots.

### Delivery phasing

- **PR 1**: this ADR; keyed `IntegrityHashService` + `IntegrityKeyProvider` + startup guard + unkeyed gauge; `rehash --force`; tests.
- **PR 2**: `AuditCheckpoints` table + migration; `IntegrityVerificationService` + `verify` CLI + `expertise-apictl verify`; `expertise_integrity_verify_runs_total{result}` / `expertise_integrity_mismatches_total{kind}` metrics; tamper-detection integration tests; checkpoint export in `backup`.
- **PR 3**: deployment surfaces — `install.sh` keygen, Compose/Helm docs and secret wiring, timer/CronJob templates, upgrade runbook.

### Rejected / deferred hardening (recorded so they are decisions, not omissions)

- **On-read verification** — rejected as default (cost/blast-radius, above).
- **RFC 3161 timestamping of checkpoint heads** — genuinely defeats host-root for pre-timestamp state, but adds a network dependency and token handling disproportionate to a first cut. Revisit only on demand.
- **Postgres-native mechanisms** (`pgcrypto` `hmac()`, logical decoding) — pushes key material into SQL text and breaks the portable, Testcontainers-testable app-level pattern.
- **`REVOKE UPDATE, DELETE ON "ExpertiseAuditLog"` from the app role** — cheap defense-in-depth against SQLi-class writers, but the single-role A2 install (app role owns the schema and can re-grant) blunts it; noted for operators who run split-role deployments, not implemented here.
- **ASP.NET Core Data Protection API as the key mechanism** — wrong primitive: nondeterministic protect/unprotect with auto-rotating key ring, no stable MAC surface (first-party DP docs).

### Consequences

- Good, because a database-only writer can no longer forge `IntegrityHash` (keyed), and deletion or edit of any checkpointed audit row is detectable on the next sweep.
- Good, because the write path is untouched: no new locks, no serialization point, no API-shape change, dedup unaffected.
- Good, because rollout is non-breaking (soft-require + auto-keying on A2) with a tracked flip (#490).
- Bad, because a detection gap exists for audit rows newer than the last checkpoint (bounded by sweep cadence) and for anything a compromised app process writes after compromise — both stated in the threat model above.
- Bad, because the API host now holds a symmetric secret (a bounded exception to ADR-015's no-server-keys posture; the backup signing key set the precedent in ADR-012 Amendment 1).

## Related

- #468 (this issue), #333 Finding 3 (origin), #490 (hard-require flip)
- ADR-012 (backup Merkle manifest — the off-host anchor), ADR-015 (fail-closed key loading posture), ADR-010 (soft→hard require precedent)
- `Services/IntegrityHashService.cs`, `Services/BackupRecordHash.cs`, `Services/MerkleTree.cs`
