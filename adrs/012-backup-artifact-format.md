# Application-level signed and encrypted backup artifact over pg_dump

- Status: accepted
- Date: 2026-07-05
- Companion: [ADR-011](011-deployment-artifact-format.md) (signing precedent), `docs/operations/backup-restore-runbook.md`, [ADR-013](013-aggregator-upsync.md) (reuses the Merkle construction)
- Tracking issue: [#340](https://github.com/psmfd/agent-expertise-api/issues/340)
- Surfaced by: 2026-07-05 backup/restore + aggregator design session (3-agent fan-out: `expertise-api-owner`, `dotnet-expert`, `security-review-expert`); immediate driver is seeding a local A2 instance from an older hosted pg_dump and needing durable, portable backups thereafter.

## Context and Problem Statement

The only backup mechanism today is a raw `pg_dump`. A logical dump is coupled to the PostgreSQL/pgvector/EF-migration vintage of the moment it was taken, provides no per-record provenance, and its restore path was only validated as a one-time seeding runbook (see `docs/operations/backup-restore-runbook.md` Part A). The API needs a repeatable, provenance-verified backup and restore path that:

- survives PostgreSQL major-version, pgvector, and EF-migration skew between backup time and restore time;
- gives entry-granular tamper evidence, not just whole-file integrity;
- is trustworthy at disaster-recovery time with **no network access** and no CI identity;
- protects entry content at rest (backups leave the database's access-control envelope).

How should backup artifacts be formatted, signed, and encrypted, and what does restore trust?

## Considered Options

### Format

- **A. pg_dump (status quo).** Zero new code. Couples the artifact to DB engine vintage; no per-record hashing; restore of a partial/tampered dump is undetectable until data is served.
- **B. Application-level NDJSON export via new CLI verbs.** `backup`/`restore` verbs following the established `Cli/` pattern. Engine-agnostic (JSON survives any future PG/pgvector/EF change); per-record hashing is natural; restore re-enters through EF so column defaults, generated columns, and migrations behave identically to normal writes.
- **C. EF bulk export via a third-party library.** EFCore.BulkExtensions is revenue-gated dual-license (since 2023) — rejected on licensing grounds alone.

### Compression

- **gzip via `tar -czf` in the orchestration wrapper** — zero new NuGet or host dependencies (gzip ships everywhere cosign and age do); the wrapper streams tar → gzip → age in one pipeline, and the CLI verb emits plain NDJSON so the dev loop (`--i-accept-unsigned-backup`) needs no tooling at all.
- **gzip via .NET `GZipStream` inside the CLI verb** — also dependency-free, but double-buffers the payload through managed memory and splits compression across two layers once the wrapper tars the files anyway.
- **zstd** — better ratio/speed, but requires either a NuGet native-binding package or a host binary; a third external tool alongside cosign and age for marginal gain at this data volume.

### Signing

- **cosign local-keypair mode (`cosign sign-blob --key`)** — one signing tool across releases (ADR-011) and backups; works fully offline.
- **cosign keyless OIDC (release-pipeline mode)** — requires network + an OIDC identity at signing/verification time (unavailable at DR time for a solo operator) and publishes to the public Rekor transparency log (a metadata leak for private-tenant backups).
- **`ssh-keygen -Y sign/verify`** — acceptable lighter-weight alternative (no new tool if cosign were not already a host dependency), but cosign is already installed for A2 release verification.

### Encryption

- **age (encrypt payload, sign manifest in cleartext)** — modern, single-purpose file encryption; recipients model supports multiple keys; layered *under* the signature so verification is possible without the decryption key.
- **No encryption (signing only)** — acceptable only while backups never leave operator-controlled disks; rejected by user decision 2026-07-05: entries may contain sensitive prose, encrypt from day one.
- **GPG** — larger tool surface and worse UX than age for this single use case.

## Decision Outcome

**Chosen: application-level NDJSON (B), gzip via the wrapper's `tar -czf`, cosign local-keypair signing, age encryption of the payload only.**

Artifact layout — outer tar `expertise-backup-<instanceId>-<timestamp>.tar` containing:

```text
payload.tar.gz.age     # age-encrypted, gzipped tar of entries.jsonl + audit.jsonl
manifest.json          # cleartext — see schema below
manifest.json.sig      # detached cosign sign-blob --key signature over manifest.json only
```

Manifest schema (v1):

```jsonc
{
  "schemaVersion": 1,
  "instanceId": "…",             // operator-supplied or machine name
  "exportedAt": "…",             // UTC ISO-8601
  "entryCount": 0,
  "auditCount": 0,
  "entriesMerkleRoot": "…",      // RFC 6962 over per-entry BackupRecordHash leaves
  "auditMerkleRoot": "…",        // RFC 6962 over per-audit-row leaves
  "dbSchemaVersion": "…",        // last applied EF migration id at export time
  "embeddingModel": { "name": "bge-micro-v2", "dims": 384 },
  "payloadSha256": "…"           // SHA-256 of payload.tar.gz.age as stored
}
```

Because the manifest and signature are cleartext, an operator verifies the signature and the payload SHA **before** decrypting anything; the Merkle roots verify per-record integrity after decryption.

### Sub-decisions

**Record hashing: new `BackupRecordHash`, not a widened `IntegrityHashService.Compute`.** `Compute` hashes exactly `{tenant, title, body, entryType, severity}` and is load-bearing for the dedup-equality contract (`DeduplicationService`). The backup leaf hash must cover the full record: those 5 fields plus `Id` (binds the hash to the record's identity, so hashes cannot be swapped between records with identical content), `Domain`, sorted `Tags`, `Source`/`SourceVersion`, `Visibility`, `AuthorPrincipal`/`AuthorAgent`, `ReviewState`/`ReviewedBy`/`ReviewedAt`/`RejectionReason`, and `CreatedAt`/`UpdatedAt`/`DeprecatedAt`. It lives as a static sibling of `IntegrityHashService`, reusing the same `Utf8JsonWriter` canonical-JSON idiom. Leaves combine into an RFC 6962 Merkle tree (0x00 leaf / 0x01 node prefixes) so inclusion proofs are available later for down-sync reconciliation (ADR-013 deferral list).

**Embeddings ship inside the artifact but outside the trust boundary.** Embedding vectors are exported for restore speed, but are NOT covered by leaf hashes — they are derived data, regenerable via `reembed`. Restore compares the manifest's `embeddingModel` against the live `EmbeddingMetadata` row and **fails loudly on mismatch**, directing the operator to `reembed` (same explicit-compat-check posture as ADR-011's `requiredRuntime.minVersion`).

**Restore trust policy.**

| Scenario | Policy |
| --- | --- |
| DR restore of own backup | Fail closed on bad or missing signature |
| Single record fails its leaf hash | Quarantine that record as `Draft` + audit row; continue |
| Foreign backup (someone else's instance) | ALL entries forced to `Draft` regardless of signature validity |
| Local dev | `--i-accept-unsigned-backup` escape hatch (mirrors `--i-accept-unverified-source`, ADR-011) |

**Scope: everything reviewable, nothing ephemeral.** All `ReviewState`s (including Drafts — they are exactly the data the feature protects) and the full audit log are exported. Idempotency rows are excluded (24h-TTL ephemera; restoring them is meaningless). `SearchVector` (server-generated) and the `xmin` concurrency token are excluded from the DTO; `Id`/`CreatedAt`/`UpdatedAt` are preserved on import (explicit values win over `gen_random_uuid()`/`now()` column defaults — integration-tested invariant).

**v1 restore is `--mode replace` (empty target only).** Merge-mode restore has real conflict semantics (Id collisions, hash divergence, audit interleaving) and gets its own ADR — tracked in [#343](https://github.com/psmfd/agent-expertise-api/issues/343).

**CLI-only; no HTTP endpoints.** A backup is a full-fidelity cross-tenant extract — strictly more privileged than `GET /audit/{id}/raw`. It must not be reachable through any bearer token. Restore additionally requires `GetPendingMigrationsAsync()` to be empty, else it aborts (the `MigrateCommand` idiom).

**Key management and operator UX.** cosign keypair and age identity live in `~/.config/expertise-api/backup/` (directory 700, files 600). `scripts/expertise-apictl backup-init` bootstraps the whole surface: installs missing tools (apt on Debian, brew on macOS — mirroring `install.sh --install-deps`), generates keys if absent (never overwriting silently), and writes/validates `backup.env` (key paths, age recipients, output directory). Every `apictl backup`/`apictl restore` run re-validates preconditions with actionable errors pointing at `backup-init`. age recipients are configurable so a second (offline/escrow) key can decrypt.

## Consequences

- **Good** — artifact survives PG major, pgvector, and EF-migration skew by construction; restore re-enters through EF and the normal migration gate.
- **Good** — entry-granular tamper evidence; a single modified record is quarantined, not silently served.
- **Good** — verification requires no network, no CI identity, no Rekor: `cosign verify-blob --key` + SHA + Merkle recomputation are all local.
- **Good** — encrypted at rest from day one; signature verifiable without the decryption key.
- **Good** — one signing tool (cosign) across releases and backups; gzip adds zero dependencies.
- **Bad** — a second cosign *mode* (local keypair) now exists alongside the release pipeline's keyless mode; documentation must be explicit that the backup public key, not the Fulcio identity, is the backup trust root.
- **Bad** — key loss = backup loss (age) or unverifiable backups (cosign). Mitigated by the multi-recipient age configuration and the runbook's key-escrow note; accepted for a solo-operator scale.
- **Bad** — restore is replace-only in v1; operators with a populated target must wait for merge mode (#343) or wipe.
- **Bad** — backup artifacts contain Draft and Rejected content plus the audit log; the artifact itself is sensitive and the tooling chmods it 600, but off-disk handling is the operator's responsibility (runbook note).
- **Revisit if** — backup volume outgrows single-file NDJSON (streaming/chunked format), or a hub-side automated backup service materializes (would need non-interactive key handling).

## Implementation notes (non-normative)

- Export wraps keyset paging (`Id`-ordered, `IgnoreQueryFilters()`) in one `RepeatableRead` transaction for a consistent multi-page snapshot; import commits per batch.
- Import order: entries first, then audit rows (`ON DELETE RESTRICT` FK).
- Export DTOs get a source-generated `JsonSerializerContext` (repo-first; keeps `AnalysisMode=All` clean and the CLI trim-friendly).
- `Embedding` exports as `float[]` via `entry.Embedding?.ToArray()`.
- The .NET verbs own NDJSON+Merkle+manifest; `apictl` owns tar/gzip/age/cosign orchestration — so the binary alone produces a valid (plain, unsigned) payload under `--i-accept-unsigned-backup` for dev loops.
- tar extraction on restore follows ADR-011's hardening note (prefer `bsdtar`; GNU tar fallback flags).
