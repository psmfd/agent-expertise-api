# Install / Uninstall / Upgrade Hardening — Session Tracker

Persistent TODO for the multi-PR effort that gets the native-service install path (Archetype A2) ready for pi-agent integration. Updated at the end of every working session.

**Last updated:** 2026-05-23 (PRs A+B+C1 merged; ADR-011 merged; D1+D2+D2.1+D3 merged; E1+E2 merged; D4-macOS-cosign in flight; #166 + #215 closed)
**Driver issue:** [#230 readiness sweep](https://github.com/TheSemicolon/agent-expertise-api/issues/230)
**Goal:** make `scripts/install.sh` safe, upgrade-aware, and capable of bootstrapping host dependencies on macOS (primary) and Linux, so the pi-agent integration session has a turn-key target.

## Out of scope for this track

- `scripts/install.ps1` / `uninstall.ps1` mirror parity — separate follow-up issue, file when PR C lands.
- `#145` macOS LaunchDaemon opt-in — separate session.
- `#166` CI smoke test — naturally consumes everything below; pursue after PR C lands.
- `#242` PREFIX-parent TOCTOU in `--system` mode — surfaced by security review on PR A; multi-user safety is naturally addressed alongside #145 LaunchDaemon work.

## Workstream status

| # | Workstream | Issue | Status | Notes |
|---|---|---|---|---|
| 0 | File dependency-bootstrap issue | #241 | ☑ filed 2026-05-22 | Re-scoped 2026-05-22 to PR C1 (macOS only) after pre-design review. |
| 1 | Update #223 description to add upgrade-safety ACs | #223 | ☑ updated 2026-05-22 | Closed by PR B (#244). |
| A | PR A — uninstall hardening | #222 | ☑ merged as `411bcad` (#243) | `eval` removal, `--prefix` guard, `--allow-system-prefix` escape, 49-assertion smoke test. |
| B | PR B — install atomicity + upgrade safety + CRLF | #223 | ☑ merged as `44fd668` (#244) | Atomic stage-then-swap, migrate-against-staged, version marker, CRLF detector, shared `scripts/lib/prefix-validation.sh`. 93/93 tests pass. |
| **C1** | PR C1 — macOS Homebrew bootstrap + shared library | #241 | ☑ merged as `afaba45` (#248) | `bootstrap-common.sh` + `bootstrap-macos.sh`. ‘Install the SDK’ short-term default per #245; ADR-011 records the long-term migration. 139/139 tests pass. |
| C2 | PR C2 — Debian/Ubuntu bootstrap | #246 | ☐ unblocked, scope updated by ADR-011 | Under ADR-011 Option B: bootstrap surface reduces from ‘Microsoft feed + GPG pinning’ to ‘cosign + bsdtar’. WSL warn-and-refuse for Postgres unchanged. |
| C3 | PR C3 — RHEL/Fedora bootstrap | #247 | ☐ unblocked, scope updated by ADR-011 | Same scope contraction as C2 under ADR-011 Option B. |
| ADR | ADR for deployment artifact format | #245 | ☑ merged as `fcb3c19` (#250) | `adrs/011-deployment-artifact-format.md`. Endorses Option B (CI publishes a portable cosign-signed tarball; install.sh cosign-verifies + bsdtar-extracts) with Option A retained as `--from-source`. Implementation tracked as #249. |
| D1 | csproj `<RuntimeIdentifiers>` + `<RollForward>` precondition | #249 | ☑ merged as `a54aabf` (#251) | One-line csproj addition. Hard precondition for D2 portable publish (ONNX RID natives). Zero behavior change to docker/helm/`dotnet run`/existing A2 installs. |
| D2 | release.yml portable publish + manifest + cosign sign-blob | #249 | ☑ merged as `6929bdb` (#252) | `scripts/build/generate-manifest.sh` + 30-assertion test suite; release.yml publish/manifest/sign/upload steps; README §Supply-chain verification stanza for the tarball-via-manifest recipe; CI `release-manifest-generator` job seeds #166 incrementally. First D2 production run gates the D4 default-flip per ADR-011. |
| D3 | install.sh `--from-release` opt-in (cosign verify + bsdtar extract + downgrade defense + runtime semver tighten) | #249 | ☑ merged as `88d20b1` (#258) | Load-bearing PR. Ships `scripts/verify-release.sh` (sanctioned cosign-verify entrypoint), `scripts/lib/release-consumer.sh` (fetch/verify/inspect/extract/markers), install.sh wiring (`--from-release` / `--from-source` / `--version` / `--allow-downgrade` / `--accept-republished-version` / `--skip-release-api-crosscheck`), and `tests/install/test-release-tarball-flow.sh` (39 assertions). Pre-PR fan-out folded 2 HIGH + 4 MED + 5 LOW. Follow-up flags deferred to #255 (`--tarball-url`) and #256 (`--allow-offline-verify`); SemVer §11 comparator deferred to #257. |
| D4 | Cosign as managed dep in `bootstrap-*.sh`; default flip `--from-source` → `--from-release` | #249 | ◐ macOS half in flight (`feat/macos-bootstrap-cosign`); Linux halves bundle with C2/C3; default-flip still gated | macOS half adds `_macos_ensure_cosign` to `bootstrap-macos.sh` (Homebrew, idempotent, honors any cosign on PATH — not just brew-managed). bsdtar intentionally not installed (macOS `/usr/bin/tar` IS bsdtar; release-consumer.sh::rc_select_tar already auto-detects). Linux halves still pending in C2 (#246) / C3 (#247). **Default flip itself still gated** on (a) one release cycle of D2 green publishing manifest+sig artifacts; (b) E3 (#260) `--from-release` smoke green on macOS + Ubuntu runners — the macOS-cosign-in-bootstrap PR explicitly does NOT flip the default. ADR-011 binding-flip rule. |
| E1 | install.sh end-to-end smoke (Linux / systemd-user / postgres-17 in privileged container) | #166 | ☑ merged as `122542d` (#261) | Ships `scripts/test/Dockerfile.install-smoke` (debian13 + systemd + postgres17 + dotnet-sdk-10) + `scripts/test/test-install-smoke.sh` (12-assertion harness) + new `install-smoke-linux` job in `ci.yml`. Covers the primary AC of #166 (Linux + Postgres + service install + restart + health + orphan check). Container approach mirrors the existing apictl-restart-race-debian13 precedent (privileged systemd-in-container with cgroup v2 unified mount + host-uid 1000 user); deviates from #166's suggested GHA `services:` shape because the smoke needs systemd-user for service-install/restart parity with real hosts, which GHA's host runner does not give cleanly. Harness is OS-agnostic by design so E2 (macOS) reuses it without modification. First run on dev: 1m58s, all 12 assertions PASS. |
| E2 | macOS install.sh smoke (`brew install postgresql@17`, reuses E1 harness) | #259 | ☑ merged as `731cba3` (#264) | Closes the stretch AC of #166. Adds `install-smoke-macos` to `ci.yml` (macos-latest / arm64, brew-installed postgresql@17 + pgvector, no privileged container). Reuses `scripts/test/test-install-smoke.sh` — the macOS branches seeded in E1 (`start_postgres_macos`, Darwin psql dispatch, BSD-pgrep guard) ran for the first time and exposed one harness bug: hardcoded `DOTNET_ROOT=/usr/share/dotnet` overrode the GHA-runner's `/Users/runner/.dotnet`, breaking the FDD apphost. Fixed in the same PR (honor caller `DOTNET_ROOT`; per-OS fallback). Pre-PR fan-out (code-review + security + linter) PASS; two Info-level hardening notes folded in (postgresql@14 collision check + pgvector hard-fail probe). First green run on dev: 1m58s wall clock, 12/12 assertions PASS. |
| E3 | `--from-release` install.sh smoke (both OSes; gates D4 default-flip) | #260 | ☐ deferred (depends on E1 + a real signed release on main) | Runs `install.sh --from-release --version vX.Y.Z` against the latest cosign-signed release. Path-filter trigger on `scripts/install.sh`, `scripts/verify-release.sh`, `scripts/lib/release-consumer.sh`, `.github/workflows/release.yml`. Asserts D3 post-install markers (`.install-mode == release`, `.install-version-semver` non-empty). Required green for one release cycle on `dev` to unblock D4. |
| T | Upgrade-roundtrip test | (landed w/ B) | ☑ in PR B | `tests/install/test-upgrade-roundtrip.sh` (39 assertions). Extend in C1 for `--install-deps` paths. |

Status legend: ☐ todo · ◐ in progress · ☑ merged · ✗ abandoned.

## PR A detail — #222 uninstall hardening

**Design locked 2026-05-22 via `shell-expert` pre-review (verdict: PASS_WITH_WARNINGS).** Two High-severity corrections folded in below: `..` rejection + lexical normalization (no `realpath`), and **prefix-match** blocklist semantics (not exact-match).

**Files:** `scripts/uninstall.sh`, `tests/uninstall/test-prefix-guard.sh` (new — note path change, fixtures live under `tests/` not `scripts/test/`).

### Implementation order (per shell-expert)

1. [ ] Add `--dry-run` flag as a first-class option. `do_action` honors it via `printf 'DRY-RUN: %s\n' "$*"; return 0`. Independent of guard work; could ship alone if needed.
2. [ ] Audit `do_action` call sites for hidden re-parsing dependencies (literal `&&`, `||`, `;`, globs, variables needing re-expansion). **Do not mechanically swap until audit done.** Then replace `eval "$@"` → `"$@"`; lift `2>/dev/null` and `|| true` to call sites as caller policy.
3. [ ] Add `normalize_prefix()`:
   - Reject any `..` component outright (`*"/../"*`, `*"/.."`, `"../"*`, `".."`).
   - Collapse runs of `//` to `/`.
   - Strip trailing slash **after** the `p != "/"` check.
   - **Do not call `realpath` / `readlink -f`** — non-portable on macOS, false-secure on non-existent paths, hides symlink attacks rather than surfacing them.
4. [ ] Add `validate_prefix()`:
   - Absolute path required.
   - `p != "/"`, `p != "$HOME"`.
   - **Prefix-match blocklist** (`[[ "$p/" == "$b/"* ]]`), expanded per shell-expert Q6:
     - Universal: `/`, `/bin`, `/sbin`, `/etc`, `/usr`, `/var`, `/lib`, `/lib64`, `/boot`, `/dev`, `/proc`, `/sys`, `/tmp`, `/home`, `/root`, `/opt`, `/srv`, `/run`.
     - Linux extras: `/mnt`, `/media`, `/snap`, `/usr/local`.
     - macOS: `/Library`, `/System`, `/Applications`, `/private`, `/Volumes`, `/Users`, `/Network`, `/cores`, `/.vol`.
     - WSL: `/mnt/c`, `/mnt/wsl`.
     - Containers: `/host`, `/rootfs`.
     - User-fat-finger: `$HOME/Desktop`, `$HOME/Documents`.
   - Component check: `[[ "/$p/" == *"/expertise-api/"* ]]` (after normalization), bypassable only by `--allow-system-prefix`.
   - For `--system` mode: refuse symlinked prefix (`[[ -L "$p" ]] && err`).
5. [ ] Add `--allow-system-prefix` flag (off by default). **Scoped to component check only** — blocked-roots list stays unconditional. Document the asymmetry in `--help` text.
6. [ ] Post-validation log line listing every path slated for removal (free forensics, free user sanity check) before any `rm -rf`.
7. [ ] `tests/uninstall/test-prefix-guard.sh`:
   - Primary assertion: `--dry-run` output never contains `DRY-RUN: rm -rf` for any blocked prefix.
   - Secondary assertion: run without `--dry-run` under PATH-shimmed `rm` / `launchctl` / `systemctl`; assert exit non-zero and zero-byte shim log.
   - Coverage: `/`, `/usr`, `/home/foo` (no expertise-api component), `/Users/x/.local/../../etc` (must be rejected at `..` check), `/opt/expertise-api/` (trailing slash, must normalize and pass).
   - Stage as `tests/uninstall/`, not `scripts/test/`.
8. [ ] `shellcheck scripts/uninstall.sh` clean.
9. [ ] README Archetype A2 → Uninstall subsection: document `--dry-run`, `--allow-system-prefix`, blocked-roots policy, symlink restriction.
10. [ ] `shell-expert` final review pre-PR (confirm design corrections were applied correctly).

### Open design points resolved

- **Realpath:** rejected. Lexical normalization + `..` rejection only.
- **Trailing slash:** stripped during normalization, after `p != "/"` check.
- **Symlinks:** `rm -rf` does not follow directory symlinks (POSIX). Only `--system` mode adds an `[[ -L "$p" ]]` precheck; user mode is fine.
- **Two-flag composition:** `--allow-system-prefix` and the blocklist are independent layers.
- **Test harness:** `--dry-run` is the primary mechanism; PATH shims are the second line.

## PR B detail — #223 install atomicity + upgrade safety + CRLF

**Files:** `scripts/install.sh`, `scripts/test/test-install-crlf-detector.sh` (new), `scripts/test/test-upgrade-roundtrip.sh` (new).

- [ ] Atomic `publish_app`: stage `${BIN_DIR}.new`, swap via rename, retain `${BIN_DIR}.old` until migrate+restart success.
- [ ] Migrate-failure rollback: restore `${BIN_DIR}.old → ${BIN_DIR}` on `maybe_migrate` non-zero.
- [ ] `${PREFIX}/.install-version` marker — write post-success; read at `main()` entry; log fresh-vs-upgrade-vs-reinstall.
- [ ] `secrets.env` schema-version header line + read/upgrade hook (no-op today, reserves the seam).
- [ ] CRLF detector for `secrets.env`; actionable error; optional `--fix-line-endings` flag.
- [ ] CRLF smoke test.
- [ ] Upgrade-roundtrip test (no-op upgrade, version bump, publish-fail rollback, migrate-fail rollback).
- [ ] README Archetype A2 → Upgrade + Rollback subsections.
- [ ] `shell-expert` review pre-PR.

## PR C detail — dependency bootstrap (new issue)

**Files:** `scripts/install.sh` (add `--install-deps` / `--upgrade-deps`), `scripts/lib/detect-platform.sh`, `scripts/lib/bootstrap-mac.sh`, `scripts/lib/bootstrap-debian.sh`, `scripts/lib/bootstrap-rhel.sh`, `scripts/test/test-bootstrap-deps-*.sh` (CI-gated).

- [ ] File the new issue with the per-dependency upgrade policy table (see plan).
- [ ] `--install-deps` flag (off by default).
- [ ] `--upgrade-deps` flag (off by default; requires `--install-deps`).
- [ ] Per-OS module functions: `ensure_dotnet_runtime`, `ensure_postgres`, `ensure_pgvector`, `ensure_database_and_role`, `write_local_connection_string`.
- [ ] Mac: require Homebrew present (do not auto-install brew); pin Postgres major to 17.
- [ ] Linux Debian/Ubuntu: Microsoft pkg feed dispatch; `apt install dotnet-runtime-10.0 aspnetcore-runtime-10.0 postgresql-17 postgresql-17-pgvector`.
- [ ] Linux Fedora/RHEL: `dnf` equivalents.
- [ ] Generated Postgres password via `openssl rand -base64 24`; writes to `secrets.env` (chmod 600); never echoed.
- [ ] Refuse Postgres major-version upgrade; print `pg_upgrade` instructions.
- [ ] Extend upgrade-roundtrip test with dep-upgrade band assertions.
- [ ] README Archetype A2 → Quick start + "What gets installed" per-OS table.
- [ ] CLAUDE.md note on `--install-deps` for A2.
- [ ] Parallel review: `shell-expert` + `dotnet-expert` + `security-review-expert`.

## Upgrade policy reference (PR C)

| Dependency | Fresh install | Upgrade existing (`--upgrade-deps`) | Major-version mismatch |
|---|---|---|---|
| .NET runtime (10.x) | install latest 10.x | bump to latest 10.x | refuse — print and exit |
| Postgres | install pinned major (17) | minor-bump only | refuse — print `pg_upgrade` |
| pgvector | install latest from pkg mgr | upgrade + `ALTER EXTENSION vector UPDATE` | refuse on known breaks |
| coreutils (mac) | install | leave alone | n/a |
| `expertise` DB / role | create | **never drop or alter** | n/a |

## Cross-cutting notes

- All three PRs squash-merge to `dev` per github-flow rule; semantic-release picks up on merge to `main`.
- Each PR uses Conventional Commits: PR A `fix(install)`, PR B `feat(install)`, PR C `feat(install)`.
- ADR not required for any (pattern-following). Document inline + README.
- Doc-sync pairs touched: README Archetype A2 section, CLAUDE.md Quick Start.

## Session log

- **2026-05-23** — PR E2 (#264) merged to dev as `731cba3`. Closes #259 and #166 (both primary + stretch ACs now covered). Post-merge CI green: CI 1m54s / CodeQL 2m40s / Security 24s. macOS smoke first run: 1m58s, 12/12 PASS. Bug surfaced & fixed in the same PR: harness's hardcoded `DOTNET_ROOT=/usr/share/dotnet` (Linux container path) broke FDD apphost on macOS GHA runners; fix honors caller `DOTNET_ROOT` with per-OS fallback. Unblocks #260 (E3 `--from-release` smoke on both OSes, gates D4 default-flip per ADR-011).
- **2026-05-23** — PR E1 (#261) merged to dev as `122542d`. #166 primary AC closed (Linux). Post-merge CI green: CI 1m58s / CodeQL 2m39s / Security 26s. Unblocks E2 (#259 macOS smoke, reuses harness unchanged) and E3 (#260 `--from-release` smoke, gates D4 default-flip).
- **2026-05-22** — Plan drafted.
- **2026-05-22** — PR A (#243) merged to dev as `411bcad`. #222 closed.
- **2026-05-22** — PR B (#244) merged to dev as `44fd668`. #223 closed. 93/93 tests across three suites; shared `scripts/lib/prefix-validation.sh` extracted.
- **2026-05-22** — PR C pre-design fan-out: shell-expert + security-review-expert + dotnet-expert. All NEEDS_CHANGES. Convergent finding (dotnet-expert HIGH): install host runs `dotnet publish` which requires SDK, not just runtime — the brief's 'runtime-only' framing is broken and is also a latent preflight bug. Resolution: PR C1 installs the SDK (pragmatic short-term); #245 owns the ADR-eligible refactor to pre-publish + cosign artifact path. PR C scope-split: C1 (macOS, this round, #241), C2 (Debian, #246), C3 (RHEL, #247). C4 (WSL) folded into C2 as warn-and-refuse for Postgres. Critical findings folded into C1 implementation plan: CREATE EXTENSION SUPERUSER + IF NOT EXISTS; `--upgrade-deps` requires `--install-deps` (hard-error); password via psql `-v pw=:$pw -f -` parameter binding (never argv); single-quoted connection string; secrets.env never-overwrite-existing-connection-string; loopback-only PG bind; audit trail to `${PREFIX}/.install-deps-history`; brew keg-only path handling; `/dev/urandom | base64` primary password path.
- **2026-05-22** — PR C1 in flight on `feat/install-deps-bootstrap`: shared `scripts/lib/bootstrap-common.sh` (password gen w/ /dev/urandom primary + openssl fallback, secrets.env atomic injection, audit trail, sudo discipline, xtrace refusal) + `scripts/lib/bootstrap-macos.sh` (Homebrew bootstrap: SDK + PG17 + pgvector + role/db/extension). `--install-deps` / `--upgrade-deps` flags wired into install.sh; `--upgrade-deps` alone hard-errors per shell-expert HIGH. New tests `test-install-deps-flag.sh` (7) + `test-bootstrap-common.sh` (32); all 5 install-track suites pass (132/132 assertions). shellcheck clean across 8 files. Live install deferred to CI smoke per #241 ACs.
- **2026-05-22** — `shell-expert` pre-design review on PR A: verdict PASS_WITH_WARNINGS. Folded `..` rejection + lexical normalization (no `realpath`); two-tier blocklist; first-class `--dry-run`; test fixtures under `tests/uninstall/`.
- **2026-05-22** — Pre-PR fan-out on PR A: 3-way parallel (`shell-expert` + `security-review-expert` + `linter`). Aggregate verdict PASS_WITH_WARNINGS (most-severe-wins). Linter PASS. Shell-expert 3 Medium / 4 Low/Info. Security 1 Warning / multiple Info. Citation-currency caveat: both reviewers flagged they could not live-fetch first-party docs in-session. Folded in: `daemon-reload` `|| true` regression; expanded blocklist (`/usr/{bin,sbin,lib,libexec,share,include}`, `/var/lib`, `/snap` promoted to prefix-block); whitespace/CR rejection; dropped `[[ -d ]]` precheck (TOCTOU window); 17 new test assertions (now 49 total); invariant-grep test. Filed #242 for the multi-user-safety PREFIX-parent TOCTOU. Filed pi_config#151 for the web_fetch enablement gap.
- **2026-05-22** — PR A (#243) merged to dev as `411bcad`. #222 closed.: 3-way parallel (`shell-expert` + `security-review-expert` + `dotnet-expert`). All three NEEDS_CHANGES with a converging redesign. Critical finding: `migrate.sh` execs the published binary's `migrate` verb (not `dotnet ef`), so the original publish→swap→migrate→rollback shape was inverted. Correct shape: publish-staged → migrate-against-staged → swap on success. Additional findings: wrapper-script clobber (relocate outside BIN_DIR), `migrate.sh` hardcodes `${BIN_DIR}` (needs `--bin-dir` flag), trap-with-SUCCESS-flag, CRLF detector moves to preflight, secrets-stub umask window, schema-header opt-in only, portable `mkdir` lock (not flock), `git describe` byte-filter, CRLF output line-number-only.

## PR B redesign (locked 2026-05-22)

Final operation order in `main()`:

```text
acquire_install_lock                    # mkdir ${PREFIX}/.install.lock
preflight                               # + secrets CRLF detector (early)
resolve_install_version                 # git describe → sanitized $NEW_VERSION
ensure_models                           # idempotent, outside swap surface
ensure_config_stubs                     # umask 077 wrap; v1 header on fresh
publish_app_staged                      # → ${BIN_DIR}.new (symlink-trap checked)
write_wrapper_to_prefix                 # → ${PREFIX}/launch-expertise-api.sh
run_migrate_against_staged              # migrate.sh --bin-dir ${BIN_DIR}.new
atomic_swap                             # rename current→old; rename new→current; rm old
install_service                         # templates updated for new wrapper path
write_install_version_marker            # ${PREFIX}/.install-version
SUCCESS=1
```

Trap cleanup (single function, branches on `STAGE`):

- `STAGE=init`: release lock only.
- `STAGE=staged` or `STAGE=migrated`: remove `${BIN_DIR}.new`, release lock. Live tree untouched.
- `STAGE=swapped`: best-effort restore from `${BIN_DIR}.old`; emit operator recovery checklist; release lock.
- `STAGE=done` (SUCCESS=1): trap is no-op.

Wrapper script lives at `${PREFIX}/launch-expertise-api.sh` (NOT inside `${BIN_DIR}`) so it survives binary swaps. Service templates updated. Uninstall guard already protects PREFIX root.

Not in PR B (deferred):

- Destructive-migration convention guardrail (doc-only follow-up commit).
- Multi-user ancestor-ownership check on `${PREFIX}` — #242.
- `--system` permissions hardening on `.install-version` — #145 LaunchDaemon thread.
