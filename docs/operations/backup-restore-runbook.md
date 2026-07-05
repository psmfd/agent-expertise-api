# Backup & Restore Runbook

Operator procedures for (A) seeding an instance from an existing `pg_dump` backup taken by an **older** deployed version of this API, and (B) ongoing provenance-verified backups via the `backup`/`restore` CLI (ADR-012).

Part A was validated by the 2026-07-05 schema-skew research (3-agent fan-out over the repo's migration history, EF Core migration semantics, and PostgreSQL restore mechanics). Key result: this repo's migration history contains no renames, squashes, or post-merge edits, so a dump from any earlier hosted version restores as a clean history *prefix* — exactly the case `migrate` is designed to close. The hazards are all outside `migrate`'s visibility, and this runbook exists to catch them.

---

## Part A — One-time seed from an existing pg_dump

### A.0 Identify the dump format

```bash
head -c 5 dump.file        # "PGDMP" => custom/tar archive: use pg_restore
                           # SQL text ("--" comments) => plain format: use psql
pg_restore --list dump.file  # confirms/rejects archive format cleanly
```

### A.1 Restore into a truly EMPTY database — before ever running migrations

Order matters: **restore first, migrate second.** Restoring into a database where EF migrations already ran produces object-already-exists collisions that `--clean --if-exists` only partially resolves.

```bash
# Fresh container/volume — do NOT run `dotnet ef database update` or `migrate` first
docker compose -f deploy/local/docker-compose.yml up -d postgres pgbouncer

# Truly empty target, cloned from template0 (template1 can carry local additions)
createdb -h localhost -U postgres --template=template0 expertise

# Archive formats — use PG17-native binaries regardless of the dump's source version:
pg_restore --no-owner --no-privileges --dbname=expertise --verbose dump.file

# Plain SQL format (no restore-time --no-owner equivalent exists):
psql -v ON_ERROR_STOP=1 -d expertise -f dump.sql
# Expect role errors if the dump carries ALTER OWNER/GRANT for roles that don't
# exist locally — pre-create the roles or strip those statements.
```

The HNSW index DDL in the dump requires pgvector ≥ 0.5.0 on the target — an older extension fails **at restore time** (unsupported access method), not at migrate time.

### A.2 Verify the restore BEFORE migrating

```sql
SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';
SELECT count(*) FROM "ExpertiseEntries";
SELECT count(*) FROM "ExpertiseAuditLogs";
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;  -- expect a prefix of the current migration list
SELECT indexname FROM pg_indexes WHERE tablename = 'ExpertiseEntries';
```

Compare the row counts against what you know about the source instance — `migrate` reasons only about `__EFMigrationsHistory` and has zero visibility into a truncated or partial dump.

### A.3 Apply pending migrations

```bash
dotnet run --project src/ExpertiseApi -- migrate
```

Capture the logged pending-migration list — it is the only structured record of how far behind the dump was. Exit 0 with an empty pending list on re-run confirms convergence.

### A.4 Tenant remediation — the one silent trap

If the dump predates the `AddTenantAuditFields` migration (2026-04-28), the migration backfills **every restored entry into tenant `legacy`**. Every read path filters `Tenant IN (caller_tenant, 'shared')`, so callers whose token maps to any other tenant see **zero rows** even though restore and migrate both succeeded.

```sql
SELECT "Tenant", count(*) FROM "ExpertiseEntries" GROUP BY "Tenant";
-- If 'legacy' rows exist and the real owning tenant is known:
UPDATE "ExpertiseEntries" SET "Tenant" = '<real-tenant>' WHERE "Tenant" = 'legacy';
```

This one-shot `UPDATE` bypasses the app and is **not audited** — an accepted, deliberate exception for data remediation. Record when you ran it (and against which backup) in your operations notes.

### A.5 Integrity hashes — run unconditionally

```bash
dotnet run --project src/ExpertiseApi -- rehash
```

Idempotent; only touches rows where `IntegrityHash IS NULL`. Costs nothing on a current dump, closes the gap on an old one.

### A.6 Embedding vintage — manual judgment (no automatic check exists)

`reembed` does **not** compare the stored model vintage against the deployed model (tracked: [#345](https://github.com/psmfd/agent-expertise-api/issues/345)); decide yourself:

```sql
SELECT "ModelName", "Dimensions", "LastReembedAt" FROM "EmbeddingMetadata";
```

If that row is absent, or disagrees with the deployed model files (`./scripts/download-models.sh` fetches bge-micro-v2, 384-dim), regenerate:

```bash
dotnet run --project src/ExpertiseApi -- reembed
```

When in doubt, reembed — it is safe to re-run.

### A.7 Smoke test

Run the CLAUDE.md quick-start sequence: `GET /health` → authenticated `POST /expertise` (with `Idempotency-Key`) → keyword search → semantic search. Semantic search round-trips pgvector + the tsvector path together, which is the cheapest end-to-end check of everything above.

### RPO note

A clean `migrate` exit proves the **schema** converged — not that no data was lost. Anything written to the source between the backup timestamp and now is simply absent.

---

## Part B — Ongoing provenance-verified backups (ADR-012)

> Ships with the `backup`/`restore` CLI feature ([#340](https://github.com/psmfd/agent-expertise-api/issues/340)). This section is finalized in that PR; the shape below is normative per [ADR-012](../../adrs/012-backup-artifact-format.md).

- **One-time setup:** `scripts/expertise-apictl backup-init` — installs cosign/age if missing (apt/brew), generates the cosign keypair and age identity into `~/.config/expertise-api/backup/` (700/600), writes `backup.env`. Re-running validates and reports; it never overwrites keys silently.
- **Take a backup:** `scripts/expertise-apictl backup` — preflight → NDJSON export → gzip → age-encrypt → manifest + `cosign sign-blob --key` → single `.tar` artifact, chmod 600.
- **Restore:** `scripts/expertise-apictl restore <artifact>` — signature verify (fail-closed) → payload SHA check → decrypt → `restore` verb with pending-migrations and empty-target preconditions; per-record hash mismatches quarantine as Draft; foreign backups land entirely as Draft.
- **Key discipline:** losing the age identity means losing the backups; losing the cosign key means future backups need a new trust root. Configure a second age recipient (offline/escrow copy) in `backup.env`. Backup artifacts contain Drafts, Rejected entries, and the audit log — treat the artifact itself as sensitive even though it is encrypted.
