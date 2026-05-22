# Install / Uninstall / Upgrade Hardening — Session Tracker

Persistent TODO for the multi-PR effort that gets the native-service install path (Archetype A2) ready for pi-agent integration. Updated at the end of every working session.

**Last updated:** 2026-05-22
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
| 0 | File dependency-bootstrap issue | #241 | ☑ filed 2026-05-22 | Per-OS modules + upgrade band table baked into ACs. |
| 1 | Update #223 description to add upgrade-safety ACs | #223 | ☑ updated 2026-05-22 | Atomic-publish rollback on migrate failure, version marker, secrets schema-version header. |
| A | PR A — uninstall hardening | #222 | ◐ in progress — code + tests done, awaiting PR | `eval` removal, `--prefix` guard, `--allow-system-prefix` escape, smoke test. |
| B | PR B — install atomicity + upgrade safety + CRLF | #223 | ☐ blocked-by-A | Re-framed per ACs above. |
| C | PR C — host-dependency bootstrap | #241 | ☐ blocked-by-A,B | Per-OS modules under `scripts/lib/`. `--install-deps` + `--upgrade-deps` flags. |
| T | Upgrade-roundtrip test | (lands w/ B + C) | ☐ blocked-by-B | `scripts/test/test-upgrade-roundtrip.sh`. Covers atomic publish, migrate-fail rollback, version-marker, dep upgrade band. |

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

- **2026-05-22** — Plan drafted (this file). #223 ACs updated; new issue #241 filed for dependency bootstrap.
- **2026-05-22** — `shell-expert` pre-design review on PR A: verdict PASS_WITH_WARNINGS. Folded `..` rejection + lexical normalization (no `realpath`); two-tier blocklist; first-class `--dry-run`; test fixtures under `tests/uninstall/`.
- **2026-05-22** — Pre-PR fan-out on PR A: 3-way parallel (`shell-expert` + `security-review-expert` + `linter`). Aggregate verdict PASS_WITH_WARNINGS (most-severe-wins). Linter PASS. Shell-expert 3 Medium / 4 Low/Info. Security 1 Warning / multiple Info. Citation-currency caveat: both reviewers flagged they could not live-fetch first-party docs in-session; substantive POSIX/Bash claims (`rm -rf` symlink semantics, `"$@"` argv preservation) are well-known foundational behavior. Folded in: `daemon-reload` `|| true` regression; expanded blocklist (`/usr/{bin,sbin,lib,libexec,share,include}`, `/var/lib`, `/snap` promoted to prefix-block); whitespace/CR rejection; dropped `[[ -d ]]` precheck (TOCTOU window); 17 new test assertions (now 49 total); invariant-grep test forbidding absolute-path destructive binaries. Filed #242 for the multi-user-safety PREFIX-parent TOCTOU (out of scope for PR A; naturally part of #145 LaunchDaemon thread). README updated with expanded blocklist precision and #242 caveat.
