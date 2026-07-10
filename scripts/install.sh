#!/usr/bin/env bash
#
# install.sh — install agent-expertise-api as a native OS service on
# macOS (launchd LaunchAgent) or Linux (systemd --user unit).
#
# This is the Archetype A2 installer (no Docker). For the Compose flow see
# deploy/local/docker-compose.yml; for k8s see helm/expertise-api/.
#
# Usage:
#   scripts/install.sh [--prefix DIR] [--bind ADDR:PORT] [--system]
#                      [--publish-mode fdd|scd] [--skip-preflight]
#                      [--rid RID] [--fix-line-endings]
#                      [--allow-system-prefix]
#                      [--install-deps] [--upgrade-deps]
#                      [--from-release | --from-source --i-accept-unverified-source]
#                      [--version vX.Y.Z|latest]
#                      [--allow-downgrade] [--accept-republished-version]
#                      [--skip-release-api-crosscheck]
#                      [--migrate-timeout SECONDS]
#                      [--help]
#
# Defaults: per-user install, fdd publish, bind 127.0.0.1:8080.
#
# Install mode (ADR-011, issue #249):
#
#   * --from-release   DEFAULT (D4 flip, #249). Fetch cosign-signed portable
#                      tarball from GitHub Releases. Cosign verifies the
#                      manifest; the manifest's sha256 binds the tarball.
#                      Requires --version on the FIRST release-mode install
#                      (--version latest only permitted on upgrades).
#   * --from-source    Opt-in escape hatch: build from the local source tree
#                      (no cosign chain). REQUIRES --i-accept-unverified-source
#                      to acknowledge the reduced trust posture. For devs
#                      iterating on a clone and air-gapped operators.
#   * --version vX.Y.Z Pin the release tag (release-mode only). Required
#                      on first install. "latest" permitted on upgrades.
#   * --allow-downgrade  Permit ${incoming} < ${prior}. Cosign verify still
#                      applies. Use to recover from a bad version bump.
#   * --accept-republished-version  Permit reinstalling the same
#                      ${version} with a different manifest sha (republish
#                      scenario). Investigate provenance first.
#   * --skip-release-api-crosscheck  Disable the independent GitHub
#                      Releases API tag→assets check (air-gap escape
#                      hatch). Sole trust anchor becomes the cosign sig.
#
# Dependency bootstrap (issue #241, PR C1):
#
#   * --install-deps installs missing host deps (.NET 10 SDK, PostgreSQL 17,
#     pgvector) and creates the `expertise` role + database. Off by default.
#     Currently macOS only via Homebrew; #246 (Debian) and #247 (RHEL) follow.
#   * --upgrade-deps bumps already-present deps within the allowed band
#     (minor only; never crosses major). Requires --install-deps.
#   * Generated DB password is never echoed to stdout/stderr/logs; only
#     written via psql parameter binding into secrets.env (mode 600).
#   * Re-runs are idempotent (detect-then-skip); never rotate the password
#     if secrets.env already has a connection string.
#   * One-line-per-run audit appended to ${PREFIX}/.install-deps-history.
#
# Upgrade safety (issue #223):
#
#   * Concurrent installs blocked by a `mkdir`-based lock at
#     ${PREFIX}/.install.lock — portable across Linux + macOS (no flock).
#   * Publish output staged to ${BIN_DIR}.new; live ${BIN_DIR} is only
#     touched once both publish AND migrate succeed.
#   * Migrate runs against the STAGED binaries (via migrate.sh --bin-dir)
#     so a migrate failure leaves the live tree, the running service, and
#     the DB-from-the-operator's perspective unchanged. EF migrations are
#     transactional per-migration, so partial DB advance from a failed
#     batch is retry-safe on the next install run.
#   * Service wrapper lives at ${PREFIX}/launch-expertise-api.sh (NOT
#     inside ${BIN_DIR}) so it survives binary swaps. Service templates
#     are updated to reference the new location.
#   * Version marker written to ${PREFIX}/.install-version after every
#     successful install; main() reads it at entry to log fresh /
#     reinstall / upgrade-from-X-to-Y.
#   * secrets.env carries a schema-version header. Absent header = v0
#     (legacy) and is not auto-mutated; v1 is the current shape.
#   * CRLF detector fails fast in preflight before any publish work;
#     --fix-line-endings converts in place preserving mode + owner.
#   * --migrate-timeout SECS (default 300): wall-time limit for the migrate
#     verb. 0 disables the bound. On timeout the install exits non-zero
#     with a clear message; live binaries are NOT swapped and the service
#     is NOT touched. Passed through to scripts/migrate.sh unchanged.
#

set -euo pipefail

# ---------------------------------------------------------------------------
# Helpers (style matches scripts/download-models.sh)
# ---------------------------------------------------------------------------
log()  { printf '[install] %s\n' "$1"; }
warn() { printf '[install] WARN: %s\n' "$1" >&2; }
err()  { printf '[install] ERROR: %s\n' "$1" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Pinned secrets-file schema version. Bump when the format changes; add
# a per-version fixup in `migrate_secrets_schema` and re-export.
# v2 adds the documented Sync__* (ADR-013 up-sync) block to the stub. Existing
# v1 files keep working — detect_secrets_schema_version warns, never rewrites.
SECRETS_SCHEMA_VERSION=2

# ---------------------------------------------------------------------------
# Defaults & arg parsing
# ---------------------------------------------------------------------------
INSTALL_SCOPE="user"          # user | system
PUBLISH_MODE="fdd"            # fdd | scd
BIND_ADDR="127.0.0.1:8080"
SKIP_PREFLIGHT=0
EXPLICIT_RID=""
PREFIX_OVERRIDE=""
FIX_LINE_ENDINGS=0
ALLOW_SYSTEM_PREFIX=0
INSTALL_DEPS=0
UPGRADE_DEPS=0
# Release-mode args (#249). Since the D4 default-flip an unset INSTALL_MODE
# resolves to release (cosign-verified tarball); --from-source is opt-in and
# requires --i-accept-unverified-source. See ADR-011.
INSTALL_MODE=""               # "" (resolves to release, D4 default-flip) | release | source
REQUESTED_VERSION=""          # vX.Y.Z, X.Y.Z, or "latest" — release mode only
ALLOW_DOWNGRADE=0
ACCEPT_REPUBLISHED_VERSION=0
SKIP_RELEASE_API_CROSSCHECK=0
ACCEPT_UNVERIFIED_SOURCE=0    # mandatory with --from-source since the D4 default-flip (#249)
MIGRATE_TIMEOUT=300           # wall-time limit for the migrate verb; 0 = unbounded

usage() { sed -n '2,80p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --prefix)            PREFIX_OVERRIDE="${2:?--prefix needs a path}"; shift 2 ;;
    --bind)              BIND_ADDR="${2:?--bind needs ADDR:PORT}"; shift 2 ;;
    --system)            INSTALL_SCOPE="system"; shift ;;
    --publish-mode)      PUBLISH_MODE="${2:?--publish-mode needs fdd|scd}"; shift 2 ;;
    --skip-preflight)    SKIP_PREFLIGHT=1; shift ;;
    --rid)               EXPLICIT_RID="${2:?--rid needs a runtime identifier}"; shift 2 ;;
    --fix-line-endings)  FIX_LINE_ENDINGS=1; shift ;;
    --allow-system-prefix) ALLOW_SYSTEM_PREFIX=1; shift ;;
    --install-deps)      INSTALL_DEPS=1; shift ;;
    --upgrade-deps)      UPGRADE_DEPS=1; shift ;;
    --from-release)              
      if [[ "${INSTALL_MODE}" == "source" ]]; then err "--from-release and --from-source are mutually exclusive"; fi
      INSTALL_MODE="release"; shift ;;
    --from-source)               
      if [[ "${INSTALL_MODE}" == "release" ]]; then err "--from-release and --from-source are mutually exclusive"; fi
      INSTALL_MODE="source"; shift ;;
    --i-accept-unverified-source) ACCEPT_UNVERIFIED_SOURCE=1; shift ;;
    --version)                   REQUESTED_VERSION="${2:?--version needs vX.Y.Z or latest}"; shift 2 ;;
    --allow-downgrade)           ALLOW_DOWNGRADE=1; shift ;;
    --accept-republished-version) ACCEPT_REPUBLISHED_VERSION=1; shift ;;
    --skip-release-api-crosscheck) SKIP_RELEASE_API_CROSSCHECK=1; shift ;;
    --migrate-timeout)   MIGRATE_TIMEOUT="${2:?--migrate-timeout needs a number}"; shift 2 ;;
    --help|-h)           usage; exit 0 ;;
    *)                   err "unknown flag: $1 (try --help)" ;;
  esac
done
# Touch ALLOW_SYSTEM_PREFIX so shellcheck sees it as referenced; the
# real consumer is scripts/lib/prefix-validation.sh sourced below.
: "${ALLOW_SYSTEM_PREFIX}"

# Hard-error on --upgrade-deps without --install-deps. Surfaced as a
# pre-design HIGH (shell-expert): silent-no-op footgun for operator typos
# that imply intent to mutate.
#
# This is also enforced inside scripts/lib/bootstrap-common.sh via
# bc_require_install_deps_flag() — belt-and-suspenders so a future
# refactor that bypasses arg-parse-time validation still trips the
# library-side guard before any state mutation.
if (( UPGRADE_DEPS == 1 && INSTALL_DEPS == 0 )); then
  err "--upgrade-deps requires --install-deps (refusing silent no-op; pass both or neither)"
fi

[[ "${PUBLISH_MODE}" == "fdd" || "${PUBLISH_MODE}" == "scd" ]] \
  || err "--publish-mode must be 'fdd' or 'scd'"

# Install-mode validation (#249). Since the D4 default-flip an unset
# INSTALL_MODE resolves to release (cosign-verified); --from-source is the
# opt-in escape hatch and requires --i-accept-unverified-source.
#
# D3 pre-PR (shell-expert LOW): the arg-parse loop currently lets
# `--from-release --from-source` (or vice versa) silently take the LAST
# value of INSTALL_MODE. Refuse the conflict so the operator's intent is
# never resolved silently.
# D4 default-flip (#249, ADR-011): the default install mode is now the
# cosign-verified release tarball. Resolve an unset INSTALL_MODE to release
# FIRST — so the accept-flag guard below also catches the implicit-default
# case (--i-accept-unverified-source with no mode flag), not just explicit
# --from-release. --from-source is the opt-in escape hatch (else branch).
if [[ -z "${INSTALL_MODE}" ]]; then
  INSTALL_MODE="release"
fi
# --i-accept-unverified-source is meaningful only with --from-source. Reject it
# in any non-source (cosign-verified) mode rather than silently ignoring intent.
if [[ "${INSTALL_MODE}" != "source" ]] && [[ "${ACCEPT_UNVERIFIED_SOURCE}" == "1" ]]; then
  err "--i-accept-unverified-source is meaningful only with --from-source (release mode is cosign-verified)"
fi
if [[ "${INSTALL_MODE}" == "release" ]]; then
  if [[ -z "${REQUESTED_VERSION}" ]]; then
    REQUESTED_VERSION="latest"   # first-install policy enforced in rc_publish_from_release
  fi
  # --version is meaningful only for release mode; reject in source mode
  # to avoid silently ignoring the operator's intent.
  :
else
  # source mode (explicit "--from-source" or unset). Reject --version so the
  # operator does not assume it pins source-build behavior.
  if [[ -n "${REQUESTED_VERSION}" ]]; then
    err "--version is only meaningful with --from-release (got: --version ${REQUESTED_VERSION} without --from-release)"
  fi
  if (( ALLOW_DOWNGRADE == 1 || ACCEPT_REPUBLISHED_VERSION == 1 || SKIP_RELEASE_API_CROSSCHECK == 1 )); then
    err "--allow-downgrade / --accept-republished-version / --skip-release-api-crosscheck are release-mode-only flags"
  fi
  # D4 default-flip (#249): --from-source now builds from an unverified local
  # tree (no cosign chain), so it must be an explicit, acknowledged choice.
  if (( ACCEPT_UNVERIFIED_SOURCE != 1 )); then
    err "--from-source builds from an unverified local source tree (no cosign verification). Pass --i-accept-unverified-source to acknowledge the reduced trust posture, or omit --from-source to use the default cosign-verified --from-release path."
  fi
fi

# ---------------------------------------------------------------------------
# OS detection
# ---------------------------------------------------------------------------
detect_os() {
  case "$(uname -s)" in
    Darwin)  echo macos ;;
    Linux)
      if grep -qiE '(microsoft|wsl)' /proc/version 2>/dev/null; then
        echo wsl
      else
        echo linux
      fi ;;
    MINGW*|MSYS*|CYGWIN*)
      err "Use scripts/install.ps1 from native PowerShell 7+; Git-Bash cannot create Windows Services." ;;
    *) err "unsupported OS: $(uname -s)" ;;
  esac
}

detect_rid() {
  local arch; arch="$(uname -m)"
  case "${OS}" in
    macos)
      case "${arch}" in
        arm64)  echo osx-arm64 ;;
        x86_64) echo osx-x64 ;;
        *)      err "unsupported macOS arch: ${arch}" ;;
      esac ;;
    linux|wsl)
      case "${arch}" in
        x86_64)         echo linux-x64 ;;
        aarch64|arm64)  echo linux-arm64 ;;
        *)              err "unsupported Linux arch: ${arch}" ;;
      esac ;;
  esac
}

OS="$(detect_os)"
RID="${EXPLICIT_RID:-$(detect_rid)}"

# ---------------------------------------------------------------------------
# Prefix validation (shared with uninstall.sh)
#
# Runs BEFORE path layout so a hostile --prefix can never reach BIN_DIR /
# STAGE_DIR / OLD_DIR / LOCK_DIR derivation. Closes the install/uninstall
# asymmetry surfaced by the PR B (#223) pre-PR security review.
# ---------------------------------------------------------------------------
# shellcheck source=lib/prefix-validation.sh disable=SC1091
. "${SCRIPT_DIR}/lib/prefix-validation.sh"
# D3 (#249) — release-consumer helpers. Sourcing release-consumer.sh also
# sources verify-release.sh (via rc_source_verify_release) on first use.
# shellcheck source=lib/release-consumer.sh disable=SC1091
. "${SCRIPT_DIR}/lib/release-consumer.sh"
# ---------------------------------------------------------------------------
# macOS --system gate (#145). Top-level — NOT inside preflight() — so
# --skip-preflight cannot bypass it: the SUDO_USER value below feeds a
# root-context tilde-expansion eval (write_install_env) and the daemon
# plist UserName key, so the validation must be unskippable.
# ---------------------------------------------------------------------------
if [[ "${OS}" == "macos" && "${INSTALL_SCOPE}" == "system" ]]; then
  if [[ "$(id -u)" != "0" ]]; then
    printf '[install] ERROR: --system on macOS requires root (run: sudo scripts/install.sh --system ...).\n' >&2
    exit 2
  fi
  if [[ -z "${SUDO_USER:-}" ]]; then
    printf '[install] ERROR: --system on macOS must be run via sudo (SUDO_USER not set). The service will run as the invoking user; SUDO_USER identifies that user.\n' >&2
    exit 2
  fi
  if ! printf '%s' "${SUDO_USER}" | grep -Eq '^[A-Za-z_][A-Za-z0-9._-]*$'; then
    printf '[install] ERROR: SUDO_USER %q contains unexpected characters — refusing --system install.\n' "${SUDO_USER}" >&2
    exit 2
  fi
fi

if [[ -n "${PREFIX_OVERRIDE}" ]]; then
  PREFIX_OVERRIDE="$(normalize_prefix "${PREFIX_OVERRIDE}")"
  validate_prefix "${PREFIX_OVERRIDE}"
fi

# ---------------------------------------------------------------------------
# Path layout per OS (matches notes/agent-expertise-api-hosting.md §A2)
# ---------------------------------------------------------------------------
if [[ -n "${PREFIX_OVERRIDE}" ]]; then
  PREFIX="${PREFIX_OVERRIDE}"
  # When --prefix overrides the default, co-locate config + logs under it
  # so the layout stays self-contained. Operators who want split locations
  # can set XDG_* explicitly before invoking install.sh.
  LOG_DIR="${PREFIX}/logs"
  CONFIG_DIR="${PREFIX}"
elif [[ "${OS}" == "macos" && "${INSTALL_SCOPE}" == "system" ]]; then
  # macOS --system (LaunchDaemon): root-owned paths, mirrors Linux system-scope.
  # Log dir is NOT under any user home (the service may start before login).
  PREFIX="/opt/expertise-api"
  LOG_DIR="/var/log/expertise-api"
  CONFIG_DIR="/etc/expertise-api"
elif [[ "${OS}" == "macos" ]]; then
  PREFIX="${HOME}/Library/Application Support/expertise-api"
  LOG_DIR="${HOME}/Library/Logs/expertise-api"
  CONFIG_DIR="${HOME}/Library/Application Support/expertise-api"
else
  # Linux / WSL — XDG
  if [[ "${INSTALL_SCOPE}" == "system" ]]; then
    PREFIX="/opt/expertise-api"
    LOG_DIR="/var/log/expertise-api"
    CONFIG_DIR="/etc/expertise-api"
  else
    PREFIX="${HOME}/.local/share/expertise-api"
    LOG_DIR="${XDG_STATE_HOME:-${HOME}/.local/state}/expertise-api"
    CONFIG_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api"
  fi
fi

BIN_DIR="${PREFIX}/bin"
STAGE_DIR="${BIN_DIR}.new"
OLD_DIR="${BIN_DIR}.old"
MODEL_DIR="${PREFIX}/models"
SECRETS_FILE="${CONFIG_DIR}/secrets.env"
# Wrapper relocated outside BIN_DIR so it survives the atomic swap (#223).
WRAPPER_SCRIPT="${PREFIX}/launch-expertise-api.sh"
WRAPPER_LEGACY="${PREFIX}/bin/launch-expertise-api.sh"
VERSION_MARKER="${PREFIX}/.install-version"
LOCK_DIR="${PREFIX}/.install.lock"

# ---------------------------------------------------------------------------
# Ancestor-walk TOCTOU guard (--system only, issue #242 / #145)
#
# Mirror of the guard in uninstall.sh: for system-scope installs (both
# Linux and macOS LaunchDaemon), walk every ancestor of PREFIX and refuse
# if any ancestor is non-root-owned, group/world-writable without sticky,
# or a symlink. User-mode operators own $HOME — the check is unnecessary
# there and would fire on any user install.
# ---------------------------------------------------------------------------
if [[ "${INSTALL_SCOPE}" == "system" ]]; then
  validate_prefix_ancestors "${PREFIX}"
fi

# ---------------------------------------------------------------------------
# Cleanup / trap — staged STAGE variable drives rollback granularity
# ---------------------------------------------------------------------------
STAGE="init"
SUCCESS=0

cleanup() {
  local rc=$?
  trap - ERR EXIT INT TERM
  # Cleanup must not itself trip set -e — guard each op with || true.
  if (( SUCCESS == 1 )); then
    # Steady-state cleanup of the rollback runway (deferred from
    # atomic_swap so install_service / write_install_version_marker
    # failures still have something to restore from).
    if [[ -d "${OLD_DIR}" && ! -L "${OLD_DIR}" ]]; then
      rm -rf -- "${OLD_DIR}" 2>/dev/null || true
    fi
    release_lock || true
    exit 0
  fi
  warn "install failed at stage=${STAGE} (rc=${rc}) — performing rollback"
  case "${STAGE}" in
    init|preflight|version|models|config|staged|migrated)
      # Live ${BIN_DIR} untouched. Drop the staged tree if it exists.
      # CI / debug escape hatch: EXPERTISE_API_PRESERVE_STAGE_ON_FAILURE=1
      # keeps STAGE_DIR and the quarantine in place so the caller can
      # inspect what install.sh staged before the failure. Production
      # installs never set this; only the smoke harness (E1 / #166).
      if [[ "${EXPERTISE_API_PRESERVE_STAGE_ON_FAILURE:-0}" = "1" ]]; then
        warn "EXPERTISE_API_PRESERVE_STAGE_ON_FAILURE=1 — leaving ${STAGE_DIR} in place for diagnostic"
      else
        if [[ -d "${STAGE_DIR}" && ! -L "${STAGE_DIR}" ]]; then
          rm -rf -- "${STAGE_DIR}" 2>/dev/null || true
          warn "removed staged tree: ${STAGE_DIR}"
        fi
        # D3 pre-PR (shell + security MED): also mop up the release-mode
        # quarantine sibling and the download stash so a refused install
        # does not leave libarchive-extracted bytes under ${PREFIX}.
        if [[ -d "${STAGE_DIR}.unpack" && ! -L "${STAGE_DIR}.unpack" ]]; then
          rm -rf -- "${STAGE_DIR}.unpack" 2>/dev/null || true
          warn "removed quarantine tree: ${STAGE_DIR}.unpack"
        fi
        if [[ -d "${PREFIX}/.release-download" && ! -L "${PREFIX}/.release-download" ]]; then
          rm -rf -- "${PREFIX}/.release-download" 2>/dev/null || true
          warn "removed release download stash: ${PREFIX}/.release-download"
        fi
      fi
      ;;
    swapped)
      # Swap completed but a later step failed. Restore old binaries.
      if [[ -d "${OLD_DIR}" && ! -L "${OLD_DIR}" ]]; then
        if [[ -e "${BIN_DIR}" ]]; then
          mv -- "${BIN_DIR}" "${STAGE_DIR}" 2>/dev/null || true
        fi
        if mv -- "${OLD_DIR}" "${BIN_DIR}" 2>/dev/null; then
          warn "restored prior binaries from ${OLD_DIR} -> ${BIN_DIR}"
          warn "Operator: verify with 'scripts/expertise-apictl status' and 'scripts/expertise-apictl health'"
          rm -rf -- "${STAGE_DIR}" 2>/dev/null || true
        else
          warn "AUTOMATIC ROLLBACK FAILED. Manual recovery required:"
          warn "  1. mv ${OLD_DIR} ${BIN_DIR}"
          warn "  2. rm -rf ${STAGE_DIR}"
          warn "  3. scripts/expertise-apictl restart"
        fi
      else
        warn "no ${OLD_DIR} present — cannot auto-restore; investigate ${BIN_DIR} state"
      fi
      ;;
  esac
  release_lock || true
  exit "${rc}"
}
trap cleanup ERR EXIT INT TERM

# ---------------------------------------------------------------------------
# Concurrent-install lock (portable: mkdir is atomic per POSIX.1-2017)
# ---------------------------------------------------------------------------
acquire_lock() {
  mkdir -p "${PREFIX}" 2>/dev/null || err "cannot create ${PREFIX}"
  if ! mkdir "${LOCK_DIR}" 2>/dev/null; then
    err "another install in progress (lock dir ${LOCK_DIR} exists). If no install is running, remove it manually and retry."
  fi
  printf '%s\n' "$$" > "${LOCK_DIR}/pid" 2>/dev/null || true
}

release_lock() {
  if [[ -d "${LOCK_DIR}" ]]; then
    rm -f -- "${LOCK_DIR}/pid" 2>/dev/null || true
    rmdir -- "${LOCK_DIR}" 2>/dev/null || true
  fi
}

# ---------------------------------------------------------------------------
# Pre-flight
# ---------------------------------------------------------------------------
preflight() {
  STAGE="preflight"
  log "pre-flight: OS=${OS} RID=${RID} scope=${INSTALL_SCOPE} mode=${PUBLISH_MODE}"

  # macOS --system root/SUDO_USER gate lives at TOP LEVEL (after lib sourcing),
  # not here — preflight() is skippable via --skip-preflight and the gate
  # must not be (#145).

  if [[ "${PUBLISH_MODE}" == "fdd" ]]; then
    if (( INSTALL_DEPS == 1 )) && ! command -v dotnet >/dev/null 2>&1; then
      # --install-deps runs AFTER preflight (main) and installs the .NET SDK;
      # do not hard-fail here for a dep the bootstrap will provide. The per-OS
      # bootstrap module verifies a 10.x SDK is present before returning, and
      # write_wrapper/run_migrate_staged use dotnet only after bootstrap.
      log "dotnet not yet present — deferring to --install-deps bootstrap"
    else
      command -v dotnet >/dev/null 2>&1 \
        || err "dotnet CLI not found in PATH (required for fdd; pass --publish-mode scd, or --install-deps to install it)"
      if ! dotnet --list-runtimes 2>/dev/null | grep -qE '^Microsoft\.AspNetCore\.App 10\.'; then
        if (( INSTALL_DEPS == 1 )); then
          log "ASP.NET Core 10 runtime not yet present — deferring to --install-deps bootstrap"
        else
          err "ASP.NET Core 10 runtime not installed (need 'Microsoft.AspNetCore.App 10.x'; install via https://dot.net, or pass --install-deps)"
        fi
      else
        log "dotnet runtime: OK"
      fi
    fi
  fi

  local port="${BIND_ADDR##*:}"
  if command -v lsof >/dev/null 2>&1; then
    if lsof -iTCP:"${port}" -sTCP:LISTEN -n -P >/dev/null 2>&1; then
      err "port ${port} already in use"
    fi
    log "port ${port}: free"
  elif command -v ss >/dev/null 2>&1; then
    if ss -ltn "sport = :${port}" 2>/dev/null | grep -q LISTEN; then
      err "port ${port} already in use"
    fi
    log "port ${port}: free"
  else
    warn "skipping port-in-use check (no lsof or ss)"
  fi

  local pg_host="${PG_HOST:-127.0.0.1}" pg_port="${PG_PORT:-5432}"
  if command -v nc >/dev/null 2>&1; then
    if nc -z -w 2 "${pg_host}" "${pg_port}" 2>/dev/null; then
      log "postgres ${pg_host}:${pg_port}: reachable"
    else
      warn "postgres ${pg_host}:${pg_port} NOT reachable — service will start but health checks will fail"
    fi
  elif (exec 3<>"/dev/tcp/${pg_host}/${pg_port}") 2>/dev/null; then
    exec 3<&-; exec 3>&-
    log "postgres ${pg_host}:${pg_port}: reachable (via /dev/tcp)"
  else
    warn "postgres reachability skipped (no nc, no bash /dev/tcp)"
  fi

  local need_mib=200 avail_kib avail_mib
  avail_kib=$(df -P "$(dirname "${PREFIX}")" 2>/dev/null | awk 'NR==2 {print $4}' || echo 0)
  avail_mib=$(( avail_kib / 1024 ))
  if (( avail_mib > 0 )) && (( avail_mib < need_mib )); then
    err "insufficient disk space at ${PREFIX}: ${avail_mib} MiB free, need ${need_mib}"
  fi
  log "disk space: ${avail_mib} MiB"

  check_secrets_line_endings
}

# ---------------------------------------------------------------------------
# CRLF detector — fail fast before any publish work. Only filename and
# line number are echoed; never the matched content (which may contain
# secret values).
# ---------------------------------------------------------------------------
check_secrets_line_endings() {
  [[ -f "${SECRETS_FILE}" ]] || return 0
  # No `grep -U`: not in POSIX; busybox grep (Alpine, minimal containers)
  # rejects it silently, which would let CRLF slip past on exactly the
  # platforms this hardening is meant to cover. `-U` is a no-op outside
  # Windows-host grep anyway.
  if LC_ALL=C grep -l $'\r' "${SECRETS_FILE}" >/dev/null 2>&1; then
    local line
    line=$(LC_ALL=C grep -n -m1 $'\r' "${SECRETS_FILE}" 2>/dev/null | cut -d: -f1 || echo "?")
    if (( FIX_LINE_ENDINGS == 1 )); then
      fix_secrets_line_endings
      log "CRLF detected at ${SECRETS_FILE}:${line} — fixed in place (mode + owner preserved)"
    else
      err "CRLF line endings detected at ${SECRETS_FILE}:${line}. Rerun with --fix-line-endings, or remediate manually: tr -d '\\r' < ${SECRETS_FILE} > ${SECRETS_FILE}.tmp && mv ${SECRETS_FILE}.tmp ${SECRETS_FILE} && chmod 600 ${SECRETS_FILE}"
    fi
  fi
}

# In-place CRLF fix preserving mode 600 and original owner (matters under
# sudo where the effective uid is 0 but the secrets file is operator-owned).
fix_secrets_line_endings() {
  local tmp orig_mode orig_uid orig_gid umask_old
  umask_old=$(umask)
  umask 077
  tmp=$(mktemp "${SECRETS_FILE}.XXXXXX") || err "mktemp failed for ${SECRETS_FILE}"
  # Read existing mode + ownership via stat (BSD-vs-GNU branch).
  if stat -f '%p' /dev/null >/dev/null 2>&1; then
    orig_mode=$(stat -f '%Lp' "${SECRETS_FILE}")
    orig_uid=$(stat -f '%u' "${SECRETS_FILE}")
    orig_gid=$(stat -f '%g' "${SECRETS_FILE}")
  else
    orig_mode=$(stat -c '%a' "${SECRETS_FILE}")
    orig_uid=$(stat -c '%u' "${SECRETS_FILE}")
    orig_gid=$(stat -c '%g' "${SECRETS_FILE}")
  fi
  tr -d '\r' < "${SECRETS_FILE}" > "${tmp}"
  chmod "${orig_mode:-600}" "${tmp}"
  # Restore owner only if we have privilege (root or same uid).
  if [[ "$(id -u)" == "0" ]] || [[ "$(id -u)" == "${orig_uid}" ]]; then
    chown "${orig_uid}:${orig_gid}" "${tmp}" 2>/dev/null || true
  fi
  mv -f -- "${tmp}" "${SECRETS_FILE}"
  umask "${umask_old}"
}

# ---------------------------------------------------------------------------
# Version resolution + marker
# ---------------------------------------------------------------------------
resolve_install_version() {
  STAGE="version"
  local v=""
  if command -v git >/dev/null 2>&1 && git -C "${REPO_ROOT}" rev-parse --git-dir >/dev/null 2>&1; then
    v=$(git -C "${REPO_ROOT}" describe --tags --always --dirty 2>/dev/null || true)
  fi
  # Sanitize: git refnames permit characters wider than [:alnum:]._+-;
  # clamp to a known-safe alphabet and length to prevent injection into
  # the marker file (which is later echoed in logs). Allow `_` so tags
  # like `release_2026_05` are not silently mangled.
  v=$(printf '%s' "${v:-unknown}" | LC_ALL=C tr -cd '[:alnum:]._+-' | cut -c1-64)
  [[ -z "${v}" ]] && v="unknown"
  NEW_VERSION="${v}"

  local prior=""
  # Refuse to read through a symlink — a hostile pre-existing symlink
  # at the marker path under --system could leak content fragments via
  # the log line below.
  if [[ -L "${VERSION_MARKER}" ]]; then
    err "${VERSION_MARKER} exists as a symlink — refusing to read (potential information disclosure)"
  fi
  if [[ -r "${VERSION_MARKER}" ]]; then
    prior=$(LC_ALL=C tr -cd '[:alnum:]._+-' < "${VERSION_MARKER}" | cut -c1-64 || true)
  fi
  # shellcheck disable=SC2034  # PRIOR_VERSION reserved for future doc/log surfaces
  PRIOR_VERSION="${prior}"

  if [[ -z "${prior}" ]]; then
    log "version: fresh install (${NEW_VERSION})"
  elif [[ "${prior}" == "${NEW_VERSION}" ]]; then
    log "version: reinstall (${NEW_VERSION})"
  else
    log "version: upgrade ${prior} -> ${NEW_VERSION}"
  fi
}

write_install_version_marker() {
  # Refuse to write through a hostile symlink (parity with the read-side
  # guard in resolve_install_version).
  if [[ -L "${VERSION_MARKER}" ]]; then
    err "${VERSION_MARKER} exists as a symlink — refusing to write"
  fi
  printf '%s\n' "${NEW_VERSION}" > "${VERSION_MARKER}.tmp"
  mv -f -- "${VERSION_MARKER}.tmp" "${VERSION_MARKER}"
  chmod 644 "${VERSION_MARKER}"
}

# ---------------------------------------------------------------------------
# Install-env marker — records resolved path layout so expertise-apictl can
# discover prefix-aware paths (e.g. LOG_DIR) without re-deriving them.
#
# Written to a STABLE, non-prefix-dependent location so apictl can probe it
# without knowing which --prefix was used at install time:
#   ${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api/install.env
#
# For a single-user install (the only supported mode) there is one active
# install at a time, so the "last install wins" property is correct.
#
# File contains plain key=value assignments (no quoting, no subshells).
# apictl reads it with grep+sed, not source, so no shell-execution risk.
# chmod 644: no secrets — paths only.
# ---------------------------------------------------------------------------
write_install_env() {
  # For --system installs on macOS (LaunchDaemon path), the XDG config dir
  # is under SUDO_USER's home, not root's. resolve_install_env_dir() picks
  # the right location based on scope.
  local xdg_config_dir env_dir env_file
  if [[ "${INSTALL_SCOPE}" == "system" && "${OS}" == "macos" && -n "${SUDO_USER:-}" ]]; then
    # Write under the invoking user's config dir so apictl (running as that
    # user) can find the install.env without sudo.
    xdg_config_dir="$(eval printf '%s' "~${SUDO_USER}/.config")"
  else
    xdg_config_dir="${XDG_CONFIG_HOME:-${HOME}/.config}"
  fi
  env_dir="${xdg_config_dir}/expertise-api"
  env_file="${env_dir}/install.env"
  mkdir -p "${env_dir}"
  if [[ -L "${env_file}" ]]; then
    err "${env_file} exists as a symlink — refusing to write"
  fi
  printf 'EXPERTISE_API_LOG_DIR=%s\n' "${LOG_DIR}"       > "${env_file}.tmp"
  printf 'EXPERTISE_API_PREFIX=%s\n'  "${PREFIX}"        >> "${env_file}.tmp"
  printf 'EXPERTISE_API_SCOPE=%s\n'   "${INSTALL_SCOPE}" >> "${env_file}.tmp"
  mv -f -- "${env_file}.tmp" "${env_file}"
  chmod 644 "${env_file}"
  # For --system on macOS the file was written as root; chown it to SUDO_USER
  # so apictl (running as that user) can read it without sudo.
  if [[ "${INSTALL_SCOPE}" == "system" && "${OS}" == "macos" && -n "${SUDO_USER:-}" ]]; then
    chown "${SUDO_USER}" "${env_file}" 2>/dev/null || true
  fi
  log "install-env: ${env_file}"
}

# ---------------------------------------------------------------------------
# Publish (staged)
# ---------------------------------------------------------------------------
publish_app_staged() {
  STAGE="staged"
  # Defense-in-depth: refuse stage/old paths that exist as symlinks (a
  # local attacker with write access to ${PREFIX} could pre-create them
  # to redirect rm -rf or the rename swap).
  if [[ -L "${STAGE_DIR}" ]]; then err "${STAGE_DIR} exists as a symlink — refusing to overwrite (potential TOCTOU)"; fi
  if [[ -L "${OLD_DIR}"   ]]; then err "${OLD_DIR} exists as a symlink — refusing to overwrite (potential TOCTOU)"; fi
  # Clean any leftover staged tree from a prior failed run.
  if [[ -d "${STAGE_DIR}" ]]; then
    log "removing leftover staged tree at ${STAGE_DIR}"
    rm -rf -- "${STAGE_DIR}"
  fi

  log "publishing to ${STAGE_DIR} (rid=${RID}, mode=${PUBLISH_MODE})"
  mkdir -p "${STAGE_DIR}"
  local self_contained="false"
  [[ "${PUBLISH_MODE}" == "scd" ]] && self_contained="true"
  ( cd "${REPO_ROOT}" && dotnet publish src/ExpertiseApi/ExpertiseApi.csproj \
      --configuration Release \
      --runtime "${RID}" \
      --self-contained "${self_contained}" \
      -p:UseAppHost=true \
      --output "${STAGE_DIR}" )
  log "publish: complete (staged)"
}

# ---------------------------------------------------------------------------
# Models
# ---------------------------------------------------------------------------
ensure_models() {
  STAGE="models"
  if [[ -f "${MODEL_DIR}/model.onnx" && -f "${MODEL_DIR}/vocab.txt" ]]; then
    log "ONNX models present at ${MODEL_DIR}"
    return
  fi
  log "downloading ONNX models to ${MODEL_DIR}"
  mkdir -p "${MODEL_DIR}"
  DEST_DIR="${MODEL_DIR}" "${REPO_ROOT}/scripts/download-models.sh"
}

# ---------------------------------------------------------------------------
# Config / secrets stub
#
# Wrapped in umask 077 to close the ~10ms window where the tmp file
# inherited the process umask (typically 0022 → mode 644 → world-readable)
# before the explicit chmod 600. Belt-and-suspenders: the chmod stays.
# ---------------------------------------------------------------------------
ensure_config_stubs() {
  STAGE="config"
  mkdir -p "${CONFIG_DIR}" "${LOG_DIR}"
  if [[ ! -f "${SECRETS_FILE}" ]]; then
    log "creating secrets stub at ${SECRETS_FILE} (chmod 600) — edit before starting service"
    local umask_old; umask_old=$(umask)
    umask 077
    cat > "${SECRETS_FILE}.tmp" <<EOF
# expertise-api-secrets-version=${SECRETS_SCHEMA_VERSION}
# secrets.env — sourced by launch-expertise-api.sh / EnvironmentFile= directive.
# chmod 600. Do NOT commit. Do NOT log.
#
# Set the connection string after install. Example for native Postgres:
#   ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=5432;Database=expertise;Username=expertise;Password=CHANGE_ME"
#
# This points DIRECTLY at PostgreSQL on 5432 — a single-workstation install
# does NOT need the PgBouncer sidecar (that is a k8s/Compose concern for pooling
# many concurrent clients). The appsettings.json default of Port=6432 +
# "No Reset On Close=true" is PgBouncer-specific; neither is needed here. Only
# add "No Reset On Close=true" if you deliberately front Postgres with PgBouncer.
#
# The double quotes are REQUIRED: bash sources this file via \`set -a; . file\`
# from the launch wrapper (and scripts/migrate.sh), and an unquoted value
# containing \`;\` would be split as separate commands. systemd's own
# EnvironmentFile= parser is literal and tolerates the unquoted form, but
# the launchd-on-macOS path and the migrate scripts both use the bash sourcer.
#
# When Auth:Mode=Oidc (the default outside Development), populate per-issuer
# overrides via Auth__Oidc__Issuers__N__* env vars — see appsettings.json for the
# shape. For the shipped LAN static issuer (index 2 = "LanStatic", ADR-015 embedded
# JWKS), a networked A2 instance consumed by LAN clients needs:
#   Auth__Oidc__Issuers__2__Issuer="https://auth.lan.example/"   # byte-exact w/ token iss
#   Auth__Oidc__Issuers__2__JwksPath="${CONFIG_DIR}/jwks.json"   # local public JWKS; no HTTPS fetch
#   AllowedHosts="your-lan-hostname"                             # else HostFiltering 400s the LAN Host
#   ForwardedHeaders__KnownNetworks__0="10.0.0.0/24"             # proxy CIDR; else audit IP = the proxy
# and a non-loopback bind (default is 127.0.0.1:8080): scripts/install.sh --bind 0.0.0.0:8080
# Full runbook: deploy/lan-static-oidc/RUNBOOK.md
#
# Aggregator up-sync (ADR-013, spoke role only — leave unset for a standalone
# instance). The hub credential must carry expertise.write.draft ONLY:
#   Sync__Enabled=true
#   Sync__HubUrl="https://hub.example.com"
#   Sync__TokenEndpoint="https://idp.example.com/oauth2/token"
#   Sync__ClientId="spoke-client-id"
#   Sync__ClientSecret="CHANGE_ME"
EOF
    chmod 600 "${SECRETS_FILE}.tmp"
    mv -f -- "${SECRETS_FILE}.tmp" "${SECRETS_FILE}"
    umask "${umask_old}"
  else
    log "secrets file present at ${SECRETS_FILE} (preserved)"
    detect_secrets_schema_version
  fi
}

# Read schema-version header. Absent = v0 (legacy); do NOT auto-rewrite.
# Future v0→v1 migrations should land behind an explicit --upgrade-secrets
# flag to avoid mutating operator-owned credential files implicitly.
detect_secrets_schema_version() {
  local ver
  ver=$(LC_ALL=C grep -m1 -E '^# expertise-api-secrets-version=' "${SECRETS_FILE}" 2>/dev/null \
          | sed 's/^# expertise-api-secrets-version=//; s/[^0-9].*//' || true)
  if [[ -z "${ver}" ]]; then
    warn "${SECRETS_FILE} predates schema-version header (treating as v0). No action required today; future schema migrations may require manual upgrade."
  elif [[ "${ver}" != "${SECRETS_SCHEMA_VERSION}" ]]; then
    warn "${SECRETS_FILE} declares schema version ${ver}; installer expects ${SECRETS_SCHEMA_VERSION}. Inspect appsettings.json for any format change."
  fi
}

# ---------------------------------------------------------------------------
# Wrapper script — sources secrets, then exec's dotnet (or native binary)
# Wrapper lives at ${PREFIX}/launch-expertise-api.sh (NOT in ${BIN_DIR})
# so the atomic swap of ${BIN_DIR} does not destroy it.
# ---------------------------------------------------------------------------
write_wrapper() {
  # Resolve dotnet to an ABSOLUTE path at install time. The service manager's
  # environment (launchd path_helper PATH on macOS, systemd user units on
  # Linux) does not include non-standard dotnet roots — e.g. ~/.dotnet from
  # dotnet-install.sh / actions/setup-dotnet, or the brew formula layout —
  # so a bare `exec dotnet` works in the install shell but fails at service
  # start for the FDD/portable layout (#288; caught by the E3 from-release
  # smoke, where the portable tarball has no apphost). Preflight already
  # requires dotnet on PATH for FDD installs, so an empty resolution here
  # only happens on SCD installs, where the apphost branch runs instead.
  local dotnet_bin dotnet_real dotnet_root=""
  dotnet_bin="$(command -v dotnet 2>/dev/null || true)"
  if [[ -n "${dotnet_bin}" ]]; then
    # DOTNET_ROOT is the directory of the REAL muxer binary — follow the
    # cask's /usr/local/bin/dotnet -> /usr/local/share/dotnet/dotnet symlink.
    # readlink -f exists on macOS 13+ (the .NET 10 floor) and all Linux
    # targets; fall back to the unresolved path if it is unavailable.
    dotnet_real="$(readlink -f "${dotnet_bin}" 2>/dev/null || printf '%s' "${dotnet_bin}")"
    dotnet_root="$(dirname "${dotnet_real}")"
  fi
  cat > "${WRAPPER_SCRIPT}.tmp" <<EOF
#!/usr/bin/env bash
# launch-expertise-api.sh — service entrypoint.
# Sources secrets.env then exec's the API. Generated by scripts/install.sh.
set -euo pipefail

SECRETS_FILE="${SECRETS_FILE}"
BIN_DIR="${BIN_DIR}"
DOTNET_BIN="${dotnet_bin}"

if [[ -f "\${SECRETS_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  . "\${SECRETS_FILE}"
  set +a
fi

export ASPNETCORE_URLS="http://${BIND_ADDR}"
export ASPNETCORE_ENVIRONMENT="\${ASPNETCORE_ENVIRONMENT:-Production}"
export DOTNET_NOLOGO=true
export DOTNET_PRINT_TELEMETRY_MESSAGE=false

# --- Lightweight local-workstation runtime tuning (A2 native install ONLY) ---
# These are set here in the wrapper, NOT in the csproj/appsettings, so they
# never reach the Docker image, Helm chart, or \`dotnet run\` — the container /
# k8s path keeps the production defaults (Server GC, metrics on).
#
# Server GC (the Microsoft.NET.Sdk.Web default) allocates one managed heap and
# background GC thread PER logical core and reserves memory aggressively — the
# right trade for a multi-tenant pod under concurrent load, but pure idle-RSS
# and thread overhead for a single-user, low-traffic local service. Workstation
# GC uses a single heap and cuts idle working set substantially on a many-core
# workstation. Concurrent (background) GC stays on to keep pauses short.
#
# Prometheus metrics default off locally: a solo workstation has no scraper, so
# \`/metrics\` + per-request histogram bookkeeping is dead weight.
#
# Every value uses \`:-\` defaults so an operator can override any of them by
# setting the same variable in secrets.env (sourced above) — e.g. set
# DOTNET_gcServer=1 or Metrics__Enabled=true to restore production behaviour.
export DOTNET_gcServer="\${DOTNET_gcServer:-0}"
export DOTNET_gcConcurrent="\${DOTNET_gcConcurrent:-1}"
export Metrics__Enabled="\${Metrics__Enabled:-false}"

export Onnx__ModelPath="${MODEL_DIR}/model.onnx"
export Onnx__VocabPath="${MODEL_DIR}/vocab.txt"

# DOTNET_ROOT must be exported for BOTH exec branches: an fdd APPHOST
# resolves the runtime exactly like the muxer path does, and on hosts where
# dotnet lives outside the default /usr/local/share/dotnet location (e.g.
# ~/.dotnet from dotnet-install.sh) the apphost fails with
# "app-launch-failed / missing_runtime" without it. Previously only the
# DLL branch exported it, so an fdd publish that emitted an apphost crash-
# looped at service start on such hosts.
if [[ -n "${dotnet_root}" ]]; then
  export DOTNET_ROOT="\${DOTNET_ROOT:-${dotnet_root}}"
fi

if [[ -x "\${BIN_DIR}/ExpertiseApi" ]]; then
  exec "\${BIN_DIR}/ExpertiseApi"
elif [[ -n "\${DOTNET_BIN}" && -x "\${DOTNET_BIN}" ]]; then
  # Absolute path baked at install time: the service manager's PATH
  # (launchd path_helper, systemd user units) does not include
  # non-standard dotnet roots, so bare \`dotnet\` is not resolvable here.
  exec "\${DOTNET_BIN}" "\${BIN_DIR}/ExpertiseApi.dll"
else
  # Last resort: PATH lookup (pre-existing behavior).
  exec dotnet "\${BIN_DIR}/ExpertiseApi.dll"
fi
EOF
  mv -f -- "${WRAPPER_SCRIPT}.tmp" "${WRAPPER_SCRIPT}"
  chmod 755 "${WRAPPER_SCRIPT}"
  log "wrapper: ${WRAPPER_SCRIPT}"
  # Remove the legacy in-BIN_DIR wrapper if present (back-compat sweep).
  if [[ -f "${WRAPPER_LEGACY}" && "${WRAPPER_LEGACY}" != "${WRAPPER_SCRIPT}" ]]; then
    rm -f -- "${WRAPPER_LEGACY}" 2>/dev/null || true
  fi
}

# ---------------------------------------------------------------------------
# Migrate against the STAGED binaries (#223). Failure leaves the live tree
# untouched; trap cleans the stage dir.
# ---------------------------------------------------------------------------
run_migrate_staged() {
  STAGE="migrated"
  local conn=""
  if [[ -f "${SECRETS_FILE}" ]]; then
    # shellcheck disable=SC1090
    conn=$( (set -a; . "${SECRETS_FILE}"; set +a; printf '%s' "${ConnectionStrings__DefaultConnection:-}") )
  fi

  if [[ -z "${conn}" || "${conn}" == *CHANGE_ME* ]]; then
    warn "skipping migrate — ConnectionStrings__DefaultConnection unset or placeholder in ${SECRETS_FILE}"
    warn "After editing the secrets file, run: ${SCRIPT_DIR}/migrate.sh"
    warn "Then start the service: ${SCRIPT_DIR}/expertise-apictl start"
    return 0
  fi

  # Legacy upgrade footgun: pre-#144 stub examples showed the connection
  # string UNQUOTED, which under `set -a; . file` gets split on `;`. See
  # PR #157 review for the smoking-gun heuristic.
  if [[ "${conn}" == *Host=* && "${conn}" != *\;* ]] \
      && grep -qE '^[[:space:]]*Port=' "${SECRETS_FILE}" 2>/dev/null; then
    err "detected legacy unquoted ConnectionStrings__DefaultConnection in ${SECRETS_FILE}: the bash sourcer split on ';' and only kept the Host= segment. Wrap the value in double quotes (e.g., ConnectionStrings__DefaultConnection=\"Host=...;Port=...;Database=...;Username=...;Password=...\") and re-run scripts/install.sh."
  fi

  log "running migrate against staged binaries (${STAGE_DIR})"
  if ! "${SCRIPT_DIR}/migrate.sh" --bin-dir "${STAGE_DIR}" --secrets-file "${SECRETS_FILE}" --migrate-timeout "${MIGRATE_TIMEOUT}"; then
    err "migrate failed — live binaries NOT swapped; service NOT restarted; prior state intact. EF migrations are transactional per-migration; retry by fixing the schema/DB issue and re-running scripts/install.sh."
  fi
}

# ---------------------------------------------------------------------------
# Atomic swap — two-mv pattern. POSIX rename(2) cannot replace a non-empty
# directory, so we use:
#   1. mv ${BIN_DIR}   -> ${OLD_DIR}   (atomic; only if ${BIN_DIR} exists)
#   2. mv ${STAGE_DIR} -> ${BIN_DIR}   (atomic)
# We do NOT remove ${OLD_DIR} here — it is the rollback runway for any
# failure between this swap and final SUCCESS=1 in main() (e.g., a
# systemd `daemon-reload` or `launchctl bootstrap` failure during
# install_service). Cleanup of ${OLD_DIR} is deferred to the SUCCESS
# branch of cleanup() so the rollback path remains valid.
#
# There is a small window between (1) and (2) where ${BIN_DIR} does not
# exist. The running service holds an open fd to its binary inode and is
# unaffected by the parent-directory rename (POSIX inode-by-handle).
# ---------------------------------------------------------------------------
atomic_swap() {
  # Re-check symlink trap defense immediately before the destructive step.
  if [[ -L "${BIN_DIR}" ]]; then err "${BIN_DIR} exists as a symlink — refusing to swap"; fi
  if [[ -L "${STAGE_DIR}" ]]; then err "${STAGE_DIR} exists as a symlink — refusing to swap"; fi
  if [[ -L "${OLD_DIR}" ]]; then err "${OLD_DIR} exists as a symlink — refusing to swap"; fi
  # Clean any leftover .old from a prior failed run (steady-state cleanup
  # also happens in cleanup()'s success branch and at the next swap).
  if [[ -d "${OLD_DIR}" ]]; then rm -rf -- "${OLD_DIR}"; fi

  if [[ -d "${BIN_DIR}" ]]; then
    mv -- "${BIN_DIR}" "${OLD_DIR}"
  fi
  mv -- "${STAGE_DIR}" "${BIN_DIR}"
  STAGE="swapped"
  log "atomic swap: ${STAGE_DIR} -> ${BIN_DIR} (rollback runway preserved at ${OLD_DIR})"
}

# ---------------------------------------------------------------------------
# Service file installation per OS
# ---------------------------------------------------------------------------
install_systemd_user() {
  local unit_dir unit_path
  unit_dir="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"
  unit_path="${unit_dir}/expertise-api.service"
  mkdir -p "${unit_dir}"

  local tpl="${SCRIPT_DIR}/service-templates/expertise-api.service.tmpl"
  [[ -f "${tpl}" ]] || err "missing template: ${tpl}"

  # sed substitutions (avoid envsubst dependency)
  sed \
    -e "s|@@WRAPPER@@|${WRAPPER_SCRIPT}|g" \
    -e "s|@@WORKDIR@@|${PREFIX}|g" \
    -e "s|@@SECRETS_FILE@@|${SECRETS_FILE}|g" \
    -e "s|@@LOG_DIR@@|${LOG_DIR}|g" \
    "${tpl}" > "${unit_path}.tmp"
  # CI escape hatch: strip the strict-hardening directives that require
  # CAP_SYS_ADMIN to enforce. user-mode systemd inside a privileged Docker
  # container does not have permission to drop these capabilities and
  # rejects the unit with "Failed to drop capabilities: Operation not
  # permitted". Production installs on real Linux hosts (with proper
  # cgroup-delegation to user@1000.service) honor these directives and
  # do not set this env var. E1 (#166) smoke harness sets it.
  if [[ "${EXPERTISE_API_RELAXED_HARDENING:-0}" = "1" ]]; then
    warn "EXPERTISE_API_RELAXED_HARDENING=1 — stripping container-incompatible hardening directives from unit"
    sed -i.bak -E '/^(ProtectSystem|ProtectHome|ProtectKernelTunables|ProtectKernelModules|ProtectControlGroups|PrivateDevices|RestrictNamespaces|LockPersonality|RestrictAddressFamilies|SystemCallArchitectures|PrivateTmp)=/d' "${unit_path}.tmp"
    rm -f -- "${unit_path}.tmp.bak"
  fi
  mv -f -- "${unit_path}.tmp" "${unit_path}"
  log "systemd user unit: ${unit_path}"

  systemctl --user daemon-reload
  if systemctl --user is-enabled expertise-api.service >/dev/null 2>&1; then
    systemctl --user restart expertise-api.service
    log "systemd: restarted"
  else
    systemctl --user enable --now expertise-api.service
    log "systemd: enabled + started"
  fi

  # Lingering — service survives logout (#145).
  # Attempt `loginctl enable-linger` so the --user unit activates at boot
  # without requiring an interactive login session. The call is idempotent:
  # if linger is already on it is a no-op. It can fail under polkit
  # restrictions (e.g. some container / CI environments, or deployments
  # where the admin has restricted lingering). On failure, fall back to a
  # warning so the install still completes.
  if command -v loginctl >/dev/null 2>&1; then
    if loginctl show-user "$(id -un)" 2>/dev/null | grep -q 'Linger=yes'; then
      log "linger: already enabled for $(id -un) — service survives logout"
    else
      log "linger: attempting loginctl enable-linger $(id -un)"
      if loginctl enable-linger "$(id -un)" 2>/dev/null; then
        log "linger: enabled — user sessions persist after logout (service survives reboot)"
        log "linger: note: all user processes for $(id -un) will persist after logout"
      else
        warn "linger: loginctl enable-linger failed (polkit restriction?) — service will stop at logout"
        warn "linger: to enable manually: sudo loginctl enable-linger $(id -un)"
      fi
    fi
  fi
}

install_launchd() {
  local plist_dir plist_path label="com.thesemicolon.expertise-api"
  plist_dir="${HOME}/Library/LaunchAgents"
  plist_path="${plist_dir}/${label}.plist"
  mkdir -p "${plist_dir}"

  local tpl="${SCRIPT_DIR}/service-templates/expertise-api.plist.tmpl"
  [[ -f "${tpl}" ]] || err "missing template: ${tpl}"

  sed \
    -e "s|@@LABEL@@|${label}|g" \
    -e "s|@@WRAPPER@@|${WRAPPER_SCRIPT}|g" \
    -e "s|@@WORKDIR@@|${PREFIX}|g" \
    -e "s|@@LOG_DIR@@|${LOG_DIR}|g" \
    "${tpl}" > "${plist_path}.tmp"
  mv -f -- "${plist_path}.tmp" "${plist_path}"
  log "launchd plist: ${plist_path}"

  local domain
  domain="gui/$(id -u)"
  launchctl bootout "${domain}/${label}" 2>/dev/null || true
  launchctl bootstrap "${domain}" "${plist_path}"
  # `launchctl enable` writes a PERSISTENT "=> enabled" override entry into
  # launchd's LaunchDatabase — it does NOT merely flip runtime state, and no
  # launchctl subcommand removes an override entry without root. An
  # unconditional `enable` here therefore leaves a stale entry in
  # `launchctl print-disabled gui/UID` that survives uninstall forever
  # (observed on a CI macOS runner — #286). Only run it when the label is
  # actually disabled (recovering from a manual `launchctl disable`), so a
  # normal install→uninstall cycle never touches the LaunchDatabase.
  if launchctl print-disabled "${domain}" 2>/dev/null \
      | grep -F "\"${label}\"" | grep -qw disabled; then
    launchctl enable "${domain}/${label}"
    log "launchd: cleared disabled-override for ${label}"
  fi
  launchctl kickstart -k "${domain}/${label}"
  log "launchd: bootstrapped + kickstarted"
}

install_launchd_system() {
  # macOS --system: LaunchDaemon in /Library/LaunchDaemons/ (#145).
  # Runs as root. The service drops privileges to SUDO_USER via the UserName
  # plist key — root only spawns the process, the OS then sets uid/gid before
  # exec so the service never runs as root.
  #
  # launchctl override caveat (#301): only run `launchctl enable` when the
  # label is actually listed as disabled in the system domain, to avoid
  # writing a persistent override entry that survives uninstall.
  local label="com.thesemicolon.expertise-api"
  local plist_dir="/Library/LaunchDaemons"
  local plist_path="${plist_dir}/${label}.plist"
  mkdir -p "${plist_dir}"

  local tpl="${SCRIPT_DIR}/service-templates/expertise-api-daemon.plist.tmpl"
  [[ -f "${tpl}" ]] || err "missing template: ${tpl}"

  # Resolve the uid/gid of the service user (the original invoker, not root).
  local service_user service_uid service_gid service_group
  service_user="${SUDO_USER}"
  service_uid="$(id -u "${service_user}" 2>/dev/null || true)"
  [[ -n "${service_uid}" ]] || err "cannot resolve uid for user '${service_user}'"
  service_gid="$(id -g "${service_user}" 2>/dev/null || true)"
  [[ -n "${service_gid}" ]] || err "cannot resolve gid for user '${service_user}'"
  # Lookup group name: `id -gn` works on both macOS and Linux.
  service_group="$(id -gn "${service_user}" 2>/dev/null || true)"
  [[ -n "${service_group}" ]] || err "cannot resolve group name for user '${service_user}'"

  sed \
    -e "s|@@LABEL@@|${label}|g" \
    -e "s|@@WRAPPER@@|${WRAPPER_SCRIPT}|g" \
    -e "s|@@WORKDIR@@|${PREFIX}|g" \
    -e "s|@@LOG_DIR@@|${LOG_DIR}|g" \
    -e "s|@@SERVICE_USER@@|${service_user}|g" \
    -e "s|@@SERVICE_GROUP@@|${service_group}|g" \
    "${tpl}" > "${plist_path}.tmp"
  # Root must own the plist (launchd requires it for daemons).
  chown root:wheel "${plist_path}.tmp"
  chmod 644 "${plist_path}.tmp"
  mv -f -- "${plist_path}.tmp" "${plist_path}"
  log "launchd daemon plist: ${plist_path}"
  log "launchd daemon: service will run as ${service_user}:${service_group} (uid=${service_uid})"

  # Ensure log dir is owned by the service user (the daemon writes stdout/stderr
  # there via the plist; creating it as root then running the process as another
  # user would cause write failures).
  mkdir -p "${LOG_DIR}"
  chown "${service_user}:${service_group}" "${LOG_DIR}"
  chmod 755 "${LOG_DIR}"

  # The secrets file in /etc/expertise-api is typically created by root, but
  # the launch wrapper sources it AFTER launchd drops to ${service_user} — a
  # root-owned 600 file is unreadable there and the service dies at startup.
  # Hand ownership to the service user, keep 600.
  if [[ -f "${SECRETS_FILE}" ]]; then
    chown "${service_user}" "${SECRETS_FILE}"
    chmod 600 "${SECRETS_FILE}"
  fi

  # The user-scope install hardening leaves PREFIX, bin/, models/, and
  # CONFIG_DIR at mode 700 — correct when the owner IS the service user,
  # fatal here: after the UserName privilege drop the service cannot even
  # chdir into WorkingDirectory (launchd: "Unable to set current working
  # directory ... Permission denied", exit 78 crash-loop — caught by the
  # system-scope CI smoke). Daemon layout follows the /usr/local model:
  # root OWNS the tree (tamper resistance — the service user cannot modify
  # binaries), everyone may read+traverse, and the only protected object is
  # the secrets file (600, service-user-owned, in a 755 root-owned /etc dir
  # — same pattern as /etc/ssh).
  chmod 755 "${PREFIX}"
  chmod -R go+rX "${PREFIX}/bin" "${MODEL_DIR}" 2>/dev/null || true
  chmod 755 "${CONFIG_DIR}" 2>/dev/null || true

  launchctl bootout "system/${label}" 2>/dev/null || true
  launchctl bootstrap system "${plist_path}"
  # Only run `launchctl enable` when the label is actually listed as disabled
  # in the system domain — same pattern as the user LaunchAgent path (#301).
  if launchctl print-disabled system 2>/dev/null \
      | grep -F "\"${label}\"" | grep -qw disabled; then
    launchctl enable "system/${label}"
    log "launchd: cleared disabled-override for system/${label}"
  fi
  launchctl kickstart -k "system/${label}"
  log "launchd daemon: bootstrapped + kickstarted (survives reboot)"
}

install_service() {
  case "${OS}" in
    linux|wsl)
      [[ "${INSTALL_SCOPE}" == "user" ]] \
        || err "--system install not yet supported by this script (use systemd unit at /etc/systemd/system/ manually)"
      install_systemd_user ;;
    macos)
      if [[ "${INSTALL_SCOPE}" == "system" ]]; then
        install_launchd_system
      else
        install_launchd
      fi ;;
  esac
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# Dependency bootstrap dispatch (issue #241 PR C1)
#
# Sources scripts/lib/bootstrap-common.sh + the per-OS module on demand.
# Modules contain function definitions only (sentinel-guarded against
# direct execution). The dispatch happens AFTER arg parsing and AFTER
# preflight()/resolve_install_version() so the modules can rely on
# INSTALL_DEPS / UPGRADE_DEPS / OS / NEW_VERSION / PREFIX / SECRETS_FILE.
#
# PR C1: macOS. Debian lands via #246; RHEL via #247.
# ---------------------------------------------------------------------------
bootstrap_deps() {
  STAGE="bootstrap"
  # shellcheck source=lib/bootstrap-common.sh disable=SC1091
  . "${SCRIPT_DIR}/lib/bootstrap-common.sh"
  bc_require_install_deps_flag
  bc_refuse_xtrace
  case "${OS}" in
    macos)
      # shellcheck source=lib/bootstrap-macos.sh disable=SC1091
      . "${SCRIPT_DIR}/lib/bootstrap-macos.sh"
      bootstrap_macos_run
      ;;
    linux)
      # Debian/Ubuntu (apt) — #246. RHEL (#247) is still a separate module;
      # gate on the apt package manager so a non-apt distro gets a clear
      # pointer rather than a confusing apt-not-found failure.
      if command -v apt-get >/dev/null 2>&1; then
        # shellcheck source=lib/bootstrap-debian.sh disable=SC1091
        . "${SCRIPT_DIR}/lib/bootstrap-debian.sh"
        bootstrap_debian_run
      else
        err "--install-deps on Linux currently supports apt-based distros (Debian/Ubuntu, #246); RHEL is tracked by #247. Install dotnet SDK 10, postgresql ${_DEBIAN_PG_MAJOR:-17}, and pgvector via your package manager and re-run without --install-deps."
      fi
      ;;
    wsl)
      if command -v apt-get >/dev/null 2>&1; then
        # shellcheck source=lib/bootstrap-debian.sh disable=SC1091
        . "${SCRIPT_DIR}/lib/bootstrap-debian.sh"
        bootstrap_debian_run
      else
        err "--install-deps under WSL currently supports apt-based distros (#246)."
      fi
      ;;
    *)
      err "--install-deps does not support OS=${OS}"
      ;;
  esac
}

main() {
  acquire_lock
  if (( SKIP_PREFLIGHT == 0 )); then preflight; fi
  resolve_install_version
  ensure_config_stubs
  ensure_models
  if (( INSTALL_DEPS == 1 )); then
    bootstrap_deps
  fi
  if [[ "${INSTALL_MODE}" == "release" ]]; then
    rc_publish_from_release "${REQUESTED_VERSION}"
  else
    publish_app_staged
  fi
  write_wrapper
  run_migrate_staged
  atomic_swap
  install_service
  write_install_version_marker
  write_install_env
  # D3 (#249) — post-swap mode/semver/history markers. Written AFTER
  # atomic_swap so they only reflect committed installs (a rollback path
  # that runs cleanup() before SUCCESS=1 will not have touched them).
  rc_write_post_install_markers "${INSTALL_MODE:-release}"
  SUCCESS=1

  log "install complete"
  log "  version:  ${NEW_VERSION}"
  log "  binary:   ${BIN_DIR}"
  log "  wrapper:  ${WRAPPER_SCRIPT}"
  log "  models:   ${MODEL_DIR}"
  log "  config:   ${CONFIG_DIR}"
  log "  logs:     ${LOG_DIR}"
  log "  bind:     http://${BIND_ADDR}"
  log ""
  log "Edit ${SECRETS_FILE} to set the database connection string,"
  log "then check the service with: scripts/expertise-apictl status"
}

main "$@"
