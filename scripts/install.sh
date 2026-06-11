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
#                      [--from-release | --from-source [--i-accept-unverified-source]]
#                      [--version vX.Y.Z|latest]
#                      [--allow-downgrade] [--accept-republished-version]
#                      [--skip-release-api-crosscheck]
#                      [--help]
#
# Defaults: per-user install, fdd publish, bind 127.0.0.1:8080.
#
# Install mode (ADR-011, issue #249):
#
#   * --from-release   Fetch cosign-signed portable tarball from GHCR
#                      Releases. Cosign verifies the manifest; manifest's
#                      sha256 binds the tarball. Default mode after the
#                      D4 flip gate clears. Requires --version on FIRST
#                      release-mode install (--version latest only
#                      permitted on upgrades).
#   * --from-source    Build from local source tree (current default for
#                      back-compat with pre-D3 callers). Becomes opt-in
#                      after the D4 flip; --i-accept-unverified-source
#                      will be mandatory then. Accepted silently in D3.
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
SECRETS_SCHEMA_VERSION=1

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
# D3 (#249) — release-mode args. Defaults preserve pre-D3 behavior (source
# mode silent default) until the D4 flip gate clears (one release cycle of
# D2 green + D3 smoke test green on macOS+Linux). See ADR-011.
INSTALL_MODE=""               # "" (= source, silent default in D3) | release | source
REQUESTED_VERSION=""          # vX.Y.Z, X.Y.Z, or "latest" — release mode only
ALLOW_DOWNGRADE=0
ACCEPT_REPUBLISHED_VERSION=0
SKIP_RELEASE_API_CROSSCHECK=0
ACCEPT_UNVERIFIED_SOURCE=0    # no-op in D3; mandatory after D4 default-flip

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

# D3 (#249) — install-mode validation. Silent default in D3 = source (= "")
# for back-compat. After the D4 flip gate clears, the default becomes
# release and --from-source will require --i-accept-unverified-source.
#
# D3 pre-PR (shell-expert LOW): the arg-parse loop currently lets
# `--from-release --from-source` (or vice versa) silently take the LAST
# value of INSTALL_MODE. Refuse the conflict so the operator's intent is
# never resolved silently.
if [[ "${INSTALL_MODE}" == "release" ]] && [[ "${ACCEPT_UNVERIFIED_SOURCE}" == "1" ]]; then
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
  # Touch ACCEPT_UNVERIFIED_SOURCE so shellcheck sees it referenced; will
  # become load-bearing after the D4 default-flip.
  : "${ACCEPT_UNVERIFIED_SOURCE}"
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

  # Guard: macOS system-scope is not yet implemented. LaunchDaemon support
  # (root-owned /Library/LaunchDaemons/, reboot survival) is tracked by #145.
  # Hard-error here — before any filesystem mutation — so no partial install
  # artifacts exist when this exit path is taken. Exit 2 = precondition
  # failure per scripts/script-output-conventions.md.
  if [[ "${OS}" == "macos" && "${INSTALL_SCOPE}" == "system" ]]; then
    printf '[install] ERROR: --system is not supported on macOS (see #145 for LaunchDaemon support). Use the default user-LaunchAgent mode (omit --system).\n' >&2
    exit 2
  fi

  if [[ "${PUBLISH_MODE}" == "fdd" ]]; then
    command -v dotnet >/dev/null 2>&1 \
      || err "dotnet CLI not found in PATH (required for fdd; pass --publish-mode scd to skip)"
    if ! dotnet --list-runtimes 2>/dev/null | grep -qE '^Microsoft\.AspNetCore\.App 10\.'; then
      err "ASP.NET Core 10 runtime not installed (need 'Microsoft.AspNetCore.App 10.x'; install via https://dot.net)"
    fi
    log "dotnet runtime: OK"
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
# The double quotes are REQUIRED: bash sources this file via \`set -a; . file\`
# from the launch wrapper (and scripts/migrate.sh), and an unquoted value
# containing \`;\` would be split as separate commands. systemd's own
# EnvironmentFile= parser is literal and tolerates the unquoted form, but
# the launchd-on-macOS path and the migrate scripts both use the bash sourcer.
#
# When Auth:Mode=Oidc, populate per-issuer overrides via Auth__Oidc__Issuers__N__*
# environment variables — see appsettings.json for the shape.
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
  cat > "${WRAPPER_SCRIPT}.tmp" <<EOF
#!/usr/bin/env bash
# launch-expertise-api.sh — service entrypoint.
# Sources secrets.env then exec's the API. Generated by scripts/install.sh.
set -euo pipefail

SECRETS_FILE="${SECRETS_FILE}"
BIN_DIR="${BIN_DIR}"

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
export Onnx__ModelPath="${MODEL_DIR}/model.onnx"
export Onnx__VocabPath="${MODEL_DIR}/vocab.txt"

if [[ -x "\${BIN_DIR}/ExpertiseApi" ]]; then
  exec "\${BIN_DIR}/ExpertiseApi"
else
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
  if ! "${SCRIPT_DIR}/migrate.sh" --bin-dir "${STAGE_DIR}" --secrets-file "${SECRETS_FILE}"; then
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

  # Lingering — service survives logout
  if command -v loginctl >/dev/null 2>&1; then
    if ! loginctl show-user "$(id -un)" 2>/dev/null | grep -q 'Linger=yes'; then
      warn "user lingering not enabled — service stops at logout. Enable with: sudo loginctl enable-linger \$USER"
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
  launchctl enable "${domain}/${label}"
  launchctl kickstart -k "${domain}/${label}"
  log "launchd: bootstrapped + kickstarted"
}

install_service() {
  case "${OS}" in
    linux|wsl)
      [[ "${INSTALL_SCOPE}" == "user" ]] \
        || err "--system install not yet supported by this script (use systemd unit at /etc/systemd/system/ manually)"
      install_systemd_user ;;
    macos)
      install_launchd ;;
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
# PR C1: macOS only. Debian/RHEL modules land via #246 / #247.
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
      err "--install-deps for Linux is not yet implemented (tracked by #246 Debian, #247 RHEL). For now, install dotnet SDK 10, postgresql 17, and pgvector via your package manager and re-run without --install-deps."
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
  # D3 (#249) — post-swap mode/semver/history markers. Written AFTER
  # atomic_swap so they only reflect committed installs (a rollback path
  # that runs cleanup() before SUCCESS=1 will not have touched them).
  rc_write_post_install_markers "${INSTALL_MODE:-source}"
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
