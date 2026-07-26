# Integrity Verification Runbook (ADR-020)

Operator procedures for the keyed integrity hash and the audit checkpoint
chain: key provisioning per deployment path, the upgrade/rekey sequence, the
`verify` schedule, key rotation, and alerting. Design rationale lives in
[ADR-020](../../adrs/020-integrity-verification.md); backup/restore
interactions in the
[backup & restore runbook](backup-restore-runbook.md).

## What verification covers

- **Entry MACs** — every entry's `IntegrityHash` is an HMAC-SHA256
  (`{keyId}:{hex}`) over the canonical content fields, keyed by
  `Integrity:HmacKey`. A database-level writer without the key cannot forge a
  matching value. Bare 64-hex values are legacy unkeyed SHA-256 (counted, not
  failed, so a partially-rekeyed corpus stays verifiable). The key itself is
  **hard-required outside Development** since #490 (ADR-020 Amendment 1): an
  instance with no key configured fails at boot unless the
  `Integrity:RequireKey=false` rollback overlay is set — see
  [Rollback overlay](#rollback-overlay-integrityrequirekeyfalse) below.
- **Audit checkpoint chain** — `verify` seals MAC'd, chained RFC 6962 Merkle
  checkpoints over audit-log ranges. Deleting or editing an audit row inside
  a sealed range, or rewriting a checkpoint, breaks the root, the MAC, or the
  chain link.

`verify` exit codes: **0** clean, **1** integrity mismatch (**alert**),
**2** precondition failure (config/DB unreachable). A clean run also seals
the next checkpoint (unless `--no-seal`).

## Key provisioning by deployment path

The key is base64 of at least 32 random bytes. Never commit it; never log it.

| Path | Where the key lives | Who wires it |
| --- | --- | --- |
| A2 native (macOS/Linux/WSL) | `${CONFIG_DIR}/integrity-hmac.key`, mode 600 | `scripts/install.sh` generates it and (new installs) writes `Integrity__HmacKeyFile=` into the `secrets.env` stub. Existing installs: add the line yourself — see below. |
| A2 Windows | `%ProgramData%\ExpertiseApi\config\integrity-hmac.key` | `scripts/install.ps1` generates it and sets `Integrity__HmacKeyFile` in the service's registry `Environment` value. |
| Helm / k8s | `Integrity__HmacKey` entry in the secret named by `auth.secretName` | Operator adds the entry; the API Deployment and the verify CronJob both `envFrom` that secret. |
| Docker Compose (local dev) | `INTEGRITY__HMACKEY` in `deploy/local/.env` | Required — the container runs as Production, so an empty key fails at boot (#490). `INTEGRITY__REQUIREKEY=false` is the temporary rollback overlay. |

Generate a key manually when needed:

```sh
openssl rand -base64 32
```

## Upgrading an existing instance (key not yet configured)

Order matters: the service must hold the key **before** the rekey, and the
rekey must complete **before** you trust `verify` output.

Since the #490 hard-require flip, an unkeyed instance fails at boot — and at
the installer's `migrate` step — the moment it runs the flip version outside
Development. Provision the key (step 1) **as part of the same upgrade**, or
set the `Integrity:RequireKey=false` overlay first and remove it after step 3
if you need to stage the key separately.

1. **Provision the key** per the table above. On A2, re-running
   `scripts/install.sh` generates the key file if absent and prints the exact
   `secrets.env` line to add (the installer never edits an existing
   `secrets.env`):

   ```sh
   Integrity__HmacKeyFile="/path/to/integrity-hmac.key"
   ```

2. **Restart the service** so the API writes keyed hashes from now on:
   `expertise-apictl restart` (A2) / `helm upgrade` rollout (k8s).

3. **One-time rekey** — recompute every entry hash under the active key:

   ```sh
   expertise-apictl rehash --force            # A2
   kubectl exec deploy/<release> -- dotnet ExpertiseApi.dll rehash --force   # k8s
   ```

   Until this completes, `verify` counts the remaining bare-hex rows as
   legacy (non-fatal, reported in its log output).

4. **Seal the first checkpoint** — run `expertise-apictl verify` (or wait for
   the schedule). The first run verifies all entry MACs and seals checkpoint
   #1; the chain grows from there.

5. **Confirm the schedule exists** (next section) and that
   `expertise_integrity_last_verify_age_seconds` starts reporting.

## Rollback overlay: `Integrity:RequireKey=false`

The hard-require guard (#490, ADR-020 Amendment 1) has one sanctioned escape
hatch, mirroring `Idempotency:RequireKey` (ADR-010 Amendment 1): setting
`Integrity:RequireKey=false` in an environment overlay
(`Integrity__RequireKey=false` as an env var; `INTEGRITY__REQUIREKEY=false` in
the Compose `.env`) lets the instance boot unkeyed.

- **It is a rollback ramp, not an operating mode.** Unkeyed hashes are
  forgeable by any database-level writer — the exact threat ADR-020 closes.
  While the overlay is active, `expertise_integrity_unkeyed` reads 1 and the
  startup log carries a `Running UNKEYED` warning; alert on either.
- **When to use it:** an upgrade to the flip version failed at boot because
  the key was not staged, and service availability matters more than the
  hours it takes to provision the key properly.
- **Exit path:** provision the key per the table above, run
  `rehash --force`, run `verify`, then remove the overlay and restart. Do not
  leave the overlay in place after the key is wired — it silently downgrades
  the next misconfiguration from a loud boot failure to forgeable hashes.

## Scheduling `verify`

- **A2 Linux** — `install.sh` installs and enables
  `expertise-api-verify.timer` (daily, `Persistent=true`) next to the main
  unit. Inspect with `systemctl --user list-timers expertise-api-verify.timer`.
  Opt out with `--no-verify-timer`.
- **A2 macOS** — `install.sh` bootstraps
  `com.thesemicolon.expertise-api.verify` (LaunchAgent, or LaunchDaemon for
  `--system`) at 03:17 daily. Logs land in `verify-stdout.log` /
  `verify-stderr.log` under the install's log dir.
- **k8s** — the Helm chart's verify CronJob (`integrity.verify.enabled`,
  default on, daily). `concurrencyPolicy: Forbid` and `backoffLimit: 0` are
  deliberate: sweeps must not overlap, and exit 1 is a finding, not a
  transient error to retry.
- **Docker Compose** — no native timer; use host cron:

  ```cron
  17 3 * * * cd /path/to/repo && docker compose -f deploy/local/docker-compose.yml exec -T api dotnet ExpertiseApi.dll verify
  ```

- **Windows** — scheduled task running the binary with the service's env:

  ```powershell
  $action  = New-ScheduledTaskAction -Execute 'C:\Program Files\ExpertiseApi\bin\ExpertiseApi.exe' -Argument 'verify'
  $trigger = New-ScheduledTaskTrigger -Daily -At 03:17
  Register-ScheduledTask -TaskName 'expertise-api-verify' -Action $action -Trigger $trigger -User 'SYSTEM'
  ```

  Ensure `ConnectionStrings__DefaultConnection` and `Integrity__HmacKeyFile`
  are visible to the task (machine-level env vars, or a small wrapper script
  that sets them from the config dir).

## Key rotation

1. Generate a new key; give it a **new key id** (`Integrity:ActiveKeyId`,
   e.g. `k2` — on A2 set `Integrity__ActiveKeyId` in `secrets.env`).
2. Move the old id → base64 into `Integrity:RetiredKeys`
   (`Integrity__RetiredKeys__k1=<old-base64>`), so `verify` can still resolve
   values written under it. A retired id must not shadow the active id — the
   API fails closed at boot on that misconfiguration.
3. Restart, then run `rehash --force` to rewrite every entry under the new
   key.
4. After a clean `verify`, the old entry hashes are gone; keep the retired
   key configured until the **checkpoint chain** contains no MACs under the
   old id (checkpoints are never rewritten — retired keys generally stay
   configured indefinitely, which is cheap and safe).

## Alerting

Wire at least one of:

- **Exit code** — the scheduled unit/CronJob fails on exit 1. systemd:
  `systemctl --user list-units --failed`; k8s: alert on CronJob job failures.
- **Gauges** (API process, `/metrics`, `Metrics__Enabled=true`):
  - `expertise_integrity_last_verify_mismatches` — **> 0 is the alert.**
  - `expertise_integrity_last_verify_age_seconds` — a dead timer/CronJob
    (e.g. > 2× the schedule interval).
  - `expertise_integrity_checkpoint_age_seconds` — the sealed-chain
    staleness bound.
  - `expertise_integrity_unkeyed` — 1 means this instance is writing legacy
    unkeyed hashes. Since the #490 hard-require flip this is only reachable in
    Development or under the `Integrity:RequireKey=false` rollback overlay —
    a non-zero value in production means the overlay is active and should be
    alerted on until the key is wired and the overlay removed.

Example Prometheus rules:

```yaml
groups:
  - name: expertise-integrity
    rules:
      - alert: ExpertiseIntegrityMismatch
        expr: expertise_integrity_last_verify_mismatches > 0
        labels: { severity: critical }
        annotations:
          summary: "Integrity verification found mismatches — investigate before the next write"
      - alert: ExpertiseVerifyStale
        expr: expertise_integrity_last_verify_age_seconds > 172800
        labels: { severity: warning }
        annotations:
          summary: "No integrity verify run in 48h — the schedule is dead"
```

## Responding to a mismatch (exit 1)

1. **Do not re-run `rehash --force`.** It would re-seal tampered content
   under the active key and destroy the evidence.
2. Read the `verify` log output: it names the mismatch kind (`entry_mac`,
   `entry_unknown_key`, `checkpoint_root`, `checkpoint_mac`,
   `checkpoint_chain`) and the affected ids/ranges.
3. Cross-check the affected entries against the audit log
   (`GET /audit?entryId=...`, admin scope) and against your most recent
   signed backup (`expertise-apictl backup` artifacts embed the checkpoint
   chain as an off-host anchor).
4. Restore affected content from a trusted backup if warranted (see the
   [backup & restore runbook](backup-restore-runbook.md)); a restore
   re-sequences the audit log and the chain restarts at the next `verify`.
5. Rotate the HMAC key if you suspect key compromise (a forged *matching*
   MAC implies the writer had the key).

## Interaction with backup/restore

- `backup` exports the checkpoint chain (`checkpoints.jsonl` + manifest
  root) as an off-host anchor; `restore` deliberately never imports it.
- After restoring a backup taken under a different (or no) key, run
  `rehash --force` before trusting `verify` — see the backup runbook's
  post-restore checklist.
