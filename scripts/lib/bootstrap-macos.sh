#!/usr/bin/env bash
# ===========================================================================
# scripts/lib/bootstrap-macos.sh
#
# macOS Homebrew bootstrap module for `scripts/install.sh --install-deps`.
# Source me from install.sh; do not execute directly.
#
# Public functions (called by `bootstrap_deps` in install.sh):
#   bootstrap_macos_run             # full opt-in bootstrap sequence
#
# Module-private helpers (prefixed `_macos_`):
#   _macos_require_brew, _macos_refuse_root, _macos_brew_prefix,
#   _macos_ensure_dotnet_sdk, _macos_ensure_postgres,
#   _macos_ensure_pgvector, _macos_ensure_cosign,
#   _macos_ensure_database_and_role,
#   _macos_pg_psql, _macos_pg_running, _macos_pg_port,
#   _macos_pg_installed_major
#
# Design decisions (locked by PR C1 pre-design review 2026-05-22):
#  - SDK install, not runtime (install.sh runs `dotnet publish`; tracked by #245).
#  - Homebrew required; we never auto-install brew itself.
#  - `postgresql@17` is keg-only; never assume `psql` is on PATH.
#  - Major-version mismatch refuses with `pg_upgrade` guidance; never destructive.
#  - `pgvector` Homebrew bottle is preferred; the build-from-source fallback
#    is out of scope for C1 (macOS bottle availability is reliable).
#  - `CREATE EXTENSION vector` requires SUPERUSER. Under Homebrew the
#    install user IS the cluster superuser, so we don't need `sudo -u postgres`.
# ===========================================================================

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  printf 'bootstrap-macos.sh is a library; source it from install.sh\n' >&2
  exit 64
fi

# Target Postgres major. Bumping this is a deliberate operator decision and
# pairs with the major-version mismatch refusal branch.
readonly _MACOS_PG_MAJOR=17
readonly _MACOS_PG_FORMULA="postgresql@${_MACOS_PG_MAJOR}"
readonly _MACOS_DB_NAME="expertise"
readonly _MACOS_DB_USER="expertise"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_macos_refuse_root() {
  # Distinct from bc_refuse_root: Homebrew's own refusal-to-run-as-root is
  # load-bearing for the entire macOS path; surface a brew-specific message.
  [[ "${EUID:-$(id -u)}" -ne 0 ]] \
    || err "Homebrew refuses to run as root on macOS; re-run --install-deps as your normal user"
}

_macos_require_brew() {
  command -v brew >/dev/null 2>&1 \
    || err "Homebrew not found; install it from https://brew.sh and re-run --install-deps (we intentionally do not auto-install brew)"
}

# Resolve a brew formula's prefix without assuming it is symlinked.
# echo "" if the formula is not installed.
_macos_brew_prefix() {
  local formula="$1"
  brew --prefix "${formula}" 2>/dev/null || true
}

# Pick a psql binary from the keg-only formula. brew does not link
# postgresql@17 into $(brew --prefix)/bin, so PATH lookups miss it.
_macos_pg_bin() {
  local prefix
  prefix="$(_macos_brew_prefix "${_MACOS_PG_FORMULA}")"
  [[ -n "${prefix}" ]] || return 1
  printf '%s/bin/%s' "${prefix}" "$1"
}

_macos_pg_psql() {
  local psql_bin
  psql_bin="$(_macos_pg_bin psql)" || return 1
  [[ -x "${psql_bin}" ]] || return 1
  printf '%s' "${psql_bin}"
}

_macos_pg_running() {
  # `brew services list` columns: NAME STATUS USER PLIST/FILE
  brew services list 2>/dev/null \
    | awk -v n="${_MACOS_PG_FORMULA}" '$1 == n && $2 == "started" { found=1 } END { exit !found }'
}

_macos_pg_port() {
  local prefix conf
  prefix="$(_macos_brew_prefix "${_MACOS_PG_FORMULA}")"
  [[ -n "${prefix}" ]] || { printf '5432'; return; }
  conf="${prefix}/var/postgres@${_MACOS_PG_MAJOR}/postgresql.conf"
  if [[ ! -f "${conf}" ]]; then
    conf="${prefix}/share/postgresql@${_MACOS_PG_MAJOR}/postgresql.conf"
  fi
  if [[ -f "${conf}" ]] && grep -qE '^[[:space:]]*port[[:space:]]*=' "${conf}"; then
    awk -F'[=#]' '/^[[:space:]]*port[[:space:]]*=/ { gsub(/[[:space:]]/,"",$2); print $2; exit }' "${conf}"
  else
    printf '5432'
  fi
}

# Returns the installed Postgres MAJOR via brew's formula listing.
# Output examples: "17", "16", "" (none).
_macos_pg_installed_major() {
  local list majors
  list=$(brew list --formula 2>/dev/null) || return 0
  majors=$(printf '%s\n' "${list}" \
    | awk '/^postgresql(@|$)/ { if ($0 ~ /@/) { split($0, a, "@"); print a[2] } else { print "legacy" } }' \
    | sort -nr)
  # `legacy` (un-versioned `postgresql` formula, pre-versioned-formulas era)
  # is treated as an unknown/non-matching major by the caller so the
  # pg_upgrade-guidance refusal fires (shell-expert LOW S4).
  printf '%s\n' "${majors}" | head -1
}

# ---------------------------------------------------------------------------
# ensure_dotnet_sdk
#
# Install the .NET 10 SDK via Homebrew. The cask `dotnet-sdk` is the
# stable, runtime-included path on macOS; brew's `dotnet` formula has a
# fragmentation history (formula vs cask, full SDK vs runtime-only) that
# we sidestep entirely here. SDK install includes the ASP.NET Core
# runtime so the runtime preflight downstream is satisfied transitively.
#
# Major-version pinning: the cask tracks the current GA SDK. PR C1
# explicitly accepts whatever 10.x SDK the cask currently provides; the
# ADR follow-up (#245) addresses tighter version pinning via tarball.
# ---------------------------------------------------------------------------

_macos_ensure_dotnet_sdk() {
  # Idempotency: SDK already present?
  if command -v dotnet >/dev/null 2>&1 \
     && dotnet --list-sdks 2>/dev/null | awk '$1 ~ /^10\./ { found=1 } END { exit !found }'; then
    local sdk_ver
    sdk_ver=$(dotnet --list-sdks 2>/dev/null | awk '$1 ~ /^10\./ { print $1; exit }')
    if (( ${UPGRADE_DEPS:-0} == 0 )); then
      log "dotnet SDK 10.x present (${sdk_ver}) \u2014 skip"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}dotnet-sdk:skip"
      return 0
    fi
    log "dotnet SDK 10.x present (${sdk_ver}) \u2014 --upgrade-deps will refresh via brew"
  fi
  log "installing .NET 10 SDK via Homebrew cask (dotnet-sdk)"
  if brew list --cask dotnet-sdk >/dev/null 2>&1; then
    if (( ${UPGRADE_DEPS:-0} == 1 )); then
      brew upgrade --cask dotnet-sdk \
        || warn "brew upgrade --cask dotnet-sdk failed (continuing; may already be at latest)"
    fi
  else
    brew install --cask dotnet-sdk \
      || err "brew install --cask dotnet-sdk failed; install manually from https://dot.net and re-run without --install-deps"
  fi
  command -v dotnet >/dev/null 2>&1 \
    || err "dotnet still not on PATH after brew install \u2014 check 'brew doctor' and ensure /usr/local/share/dotnet or /opt/homebrew/* is in PATH"
  dotnet --list-sdks 2>/dev/null | awk '$1 ~ /^10\./ { found=1 } END { exit !found }' \
    || err "no .NET 10.x SDK detected after install (dotnet --list-sdks shows: $(dotnet --list-sdks 2>&1))"
  _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}dotnet-sdk:installed"
}

# ---------------------------------------------------------------------------
# ensure_postgres
#
# Install postgresql@17 via Homebrew, start the service, refuse on
# major-version mismatch with `pg_upgrade` guidance. Side-by-side
# installs (PG16 + PG17) are first-class; we only refuse if the *target*
# major is absent and a non-target major is present (operator likely
# wants pg_upgrade, not a fresh install).
# ---------------------------------------------------------------------------

_macos_ensure_postgres() {
  local present_major
  present_major=$(_macos_pg_installed_major)
  if [[ -n "${present_major}" && "${present_major}" != "${_MACOS_PG_MAJOR}" ]] \
     && ! brew list --formula "${_MACOS_PG_FORMULA}" >/dev/null 2>&1; then
    err "PostgreSQL ${present_major} is present but PostgreSQL ${_MACOS_PG_MAJOR} is not; refuse to install side-by-side without explicit operator intent. To upgrade run: brew install ${_MACOS_PG_FORMULA} && \$(brew --prefix ${_MACOS_PG_FORMULA})/bin/pg_upgrade --help"
  fi
  if brew list --formula "${_MACOS_PG_FORMULA}" >/dev/null 2>&1; then
    if (( ${UPGRADE_DEPS:-0} == 1 )); then
      log "${_MACOS_PG_FORMULA} present; --upgrade-deps will brew-upgrade (minor only)"
      brew upgrade "${_MACOS_PG_FORMULA}" 2>/dev/null \
        || log "${_MACOS_PG_FORMULA} already at latest (no-op upgrade)"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}postgres:upgraded"
    else
      log "${_MACOS_PG_FORMULA} present \u2014 skip"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}postgres:skip"
    fi
  else
    log "installing ${_MACOS_PG_FORMULA} via Homebrew"
    brew install "${_MACOS_PG_FORMULA}" \
      || err "brew install ${_MACOS_PG_FORMULA} failed"
    _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}postgres:installed"
  fi
  # Start (or no-op).
  if _macos_pg_running; then
    log "${_MACOS_PG_FORMULA} service already running"
  else
    log "starting ${_MACOS_PG_FORMULA} via brew services"
    brew services start "${_MACOS_PG_FORMULA}" \
      || err "brew services start ${_MACOS_PG_FORMULA} failed"
  fi
  # Wait for readiness. PG is generally accepting connections within
  # 1-2 seconds of `brew services start` but be patient.
  local psql_bin pg_isready_bin tries=0 max_tries=30
  psql_bin="$(_macos_pg_psql)" \
    || err "could not locate psql under \$(brew --prefix ${_MACOS_PG_FORMULA})/bin"
  pg_isready_bin="$(_macos_pg_bin pg_isready)" || pg_isready_bin=""
  while (( tries < max_tries )); do
    if [[ -n "${pg_isready_bin}" && -x "${pg_isready_bin}" ]]; then
      "${pg_isready_bin}" -h 127.0.0.1 -p "$(_macos_pg_port)" >/dev/null 2>&1 && break
    else
      "${psql_bin}" -h 127.0.0.1 -p "$(_macos_pg_port)" -d postgres -tAc 'SELECT 1' >/dev/null 2>&1 && break
    fi
    sleep 1
    tries=$((tries+1))
  done
  (( tries < max_tries )) \
    || err "PostgreSQL did not become ready within ${max_tries}s (check 'brew services list')"
  log "${_MACOS_PG_FORMULA} ready on 127.0.0.1:$(_macos_pg_port)"
}

# ---------------------------------------------------------------------------
# ensure_pgvector
#
# Homebrew has a `pgvector` bottle; install + idempotency check.
# The extension is created against the target database in
# _macos_ensure_database_and_role.
# ---------------------------------------------------------------------------

_macos_ensure_pgvector() {
  if brew list --formula pgvector >/dev/null 2>&1; then
    if (( ${UPGRADE_DEPS:-0} == 1 )); then
      brew upgrade pgvector 2>/dev/null || log "pgvector already at latest"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}pgvector:upgraded"
    else
      log "pgvector present \u2014 skip"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}pgvector:skip"
    fi
    return 0
  fi
  log "installing pgvector via Homebrew"
  brew install pgvector \
    || err "brew install pgvector failed"
  _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}pgvector:installed"
}

# ---------------------------------------------------------------------------
# ensure_cosign
#
# Install Sigstore cosign via Homebrew. cosign is required by
# install.sh's `--from-release` path (release-consumer.sh calls
# `cosign verify-blob` against the manifest signature per ADR-011).
# This is the macOS half of D4 — the Linux halves bundle with C2/C3
# (#246, #247). The `--from-release` default-flip itself remains gated
# on E3 (#260) per the tracker.
#
# bsdtar is intentionally NOT installed: macOS ships libarchive's
# bsdtar as `/usr/bin/tar`, and release-consumer.sh::rc_select_tar
# already detects this via `tar --version`.
#
# Deliberate divergence from `_macos_ensure_pgvector`: cosign has
# multiple legitimate install channels operators use in the wild
# (go install, asdf, direct GitHub release download, sigstore script);
# pgvector is brew-only on macOS. So this helper adds a `command -v`
# pre-check that honors any cosign on PATH, including under
# `--upgrade-deps`. Do not "harmonize" the two helpers by removing the
# pre-check — doing so would silently shadow operator-managed cosign
# with a brew copy.
# ---------------------------------------------------------------------------

_macos_ensure_cosign() {
  # Audit-trail token taxonomy:
  #   cosign:skip-external  — PATH cosign present, not brew-managed (honor it)
  #   cosign:skip           — brew-managed cosign present, UPGRADE_DEPS=0
  #   cosign:upgraded       — brew-managed cosign present, UPGRADE_DEPS=1 (brew upgrade ran)
  #   cosign:installed      — absent before, brew install ran
  if command -v cosign >/dev/null 2>&1; then
    local v
    v="$(cosign version 2>/dev/null | awk '/GitVersion:/ { print $2; exit }')"
    # `v` empty on schema variance (cosign v2.x sometimes elides the
    # GitVersion line). Empty is acceptable; only used for log decoration.
    if ! brew list --formula cosign >/dev/null 2>&1; then
      # PATH cosign is not brew-managed (go-install / asdf / manual).
      # Honor it under BOTH UPGRADE_DEPS=0 and UPGRADE_DEPS=1 — the
      # operator owns the upgrade cadence for tools they manage out-
      # of-band. `--upgrade-deps` is scoped to brew-managed deps.
      log "cosign present on PATH (${v:-unknown version}, not brew-managed) \u2014 skip"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}cosign:skip-external"
      return 0
    fi
    # Brew-managed cosign present.
    if (( ${UPGRADE_DEPS:-0} == 1 )); then
      log "cosign brew-managed (${v:-unknown}) \u2014 --upgrade-deps will brew-upgrade"
      brew upgrade cosign 2>/dev/null || log "cosign already at latest (no-op upgrade)"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}cosign:upgraded"
    else
      log "cosign brew-managed and present (${v:-unknown version}) \u2014 skip"
      _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}cosign:skip"
    fi
    return 0
  fi
  log "installing cosign via Homebrew"
  brew install cosign \
    || err "brew install cosign failed; install manually from https://docs.sigstore.dev/cosign/installation/ and re-run without --install-deps"
  command -v cosign >/dev/null 2>&1 \
    || err "cosign not on PATH after brew install \u2014 check 'brew doctor' and ensure /usr/local/bin or /opt/homebrew/bin is in PATH"
  _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}cosign:installed"
}

# ---------------------------------------------------------------------------
# ensure_database_and_role
#
# Idempotent:
#   - role `expertise` LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
#   - db   `expertise` OWNER expertise
#   - extension vector (created as superuser; under brew, install user IS superuser)
#   - secrets.env `ConnectionStrings__DefaultConnection` (only if absent)
#
# Password generated ONLY when secrets.env does not already carry a
# connection string. If a connection string is already present, we do
# NOT touch the role's password (would break the running service).
# ---------------------------------------------------------------------------

_macos_ensure_database_and_role() {
  local psql_bin port
  psql_bin="$(_macos_pg_psql)" || err "psql not found under brew prefix"
  port="$(_macos_pg_port)"

  # Refuse a symlinked SECRETS_FILE *before* any PG mutation. Otherwise
  # we could ALTER the role's password in PG and then refuse to write
  # secrets.env, leaving the service unable to authenticate (review
  # finding security-review LOW D).
  if [[ -L "${SECRETS_FILE}" ]]; then
    err "${SECRETS_FILE} is a symlink — refusing to bootstrap (would leave PG role and secrets.env out of sync)"
  fi

  # Existing connection string? Skip everything (never rotate).
  if [[ -f "${SECRETS_FILE}" ]] \
     && grep -q '^ConnectionStrings__DefaultConnection=' "${SECRETS_FILE}"; then
    log "secrets.env already configured \u2014 skipping role/db/extension creation to avoid password rotation"
    _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}role:skip,db:skip,vector:skip,secrets:skip"
    return 0
  fi

  # Generate password (in a guarded subshell). NOT logged.
  local pw
  pw="$(bc_generate_db_password)"
  [[ -n "${pw}" ]] || err "bc_generate_db_password returned empty value"
  # Defense-in-depth: refuse if the generated value contains a single
  # quote (would break the literal-embedded SQL form below AND the
  # single-quoted secrets.env line). Today's generator outputs base64
  # which excludes `'`, but a future generator change must not silently
  # regress safety (review finding shell-expert LOW S3).
  case "${pw}" in
    *\'*) unset pw; err "generated DB password contains a single quote; refuse to proceed (would corrupt SQL/secrets.env quoting)" ;;
  esac

  # Build SQL with the password literal embedded in the heredoc and
  # feed through stdin. The literal NEVER enters argv (no `ps -ax` /
  # `/proc/<pid>/cmdline` leak) — closes the HIGH finding from
  # shell-expert + security-review pre-PR review which correctly
  # pointed out that `psql -v pw=...` is still argv.
  #
  # `SET LOCAL log_statement = 'none'` inside the same transaction
  # suppresses the ALTER ROLE line from PG's server log even on hosts
  # that have set `log_statement = ddl|all` for debugging
  # (review finding shell-expert MEDIUM S2).
  local sql_role
  sql_role="BEGIN;
SET LOCAL log_statement = 'none';
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'expertise') THEN
    CREATE ROLE expertise LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
END
\$\$;
ALTER ROLE expertise PASSWORD '${pw}';
COMMIT;
"
  PGOPTIONS="--client-min-messages=warning" PSQL_HISTORY=/dev/null \
    "${psql_bin}" -h 127.0.0.1 -p "${port}" -d postgres \
      -v ON_ERROR_STOP=1 -f - <<<"${sql_role}" >/dev/null \
    || { unset pw sql_role; err "psql role creation failed"; }
  unset sql_role

  # Create database (cannot run inside DO block).
  if ! "${psql_bin}" -h 127.0.0.1 -p "${port}" -d postgres -tAc \
       "SELECT 1 FROM pg_database WHERE datname='${_MACOS_DB_NAME}'" 2>/dev/null | grep -q 1; then
    log "creating database ${_MACOS_DB_NAME} (owner=${_MACOS_DB_USER})"
    "${psql_bin}" -h 127.0.0.1 -p "${port}" -d postgres -v ON_ERROR_STOP=1 \
      -c "CREATE DATABASE ${_MACOS_DB_NAME} OWNER ${_MACOS_DB_USER};" >/dev/null \
      || { unset pw; err "CREATE DATABASE failed"; }
  fi

  # Vector extension (requires superuser; under brew install-user IS superuser).
  log "ensuring 'vector' extension in ${_MACOS_DB_NAME}"
  "${psql_bin}" -h 127.0.0.1 -p "${port}" -d "${_MACOS_DB_NAME}" -v ON_ERROR_STOP=1 \
    -c 'CREATE EXTENSION IF NOT EXISTS vector;' >/dev/null \
    || { unset pw; err "CREATE EXTENSION vector failed (is pgvector installed?)"; }

  # Write secrets.env (atomic; 600; original owner preserved if file existed).
  bc_write_connection_string_if_absent "127.0.0.1" "${port}" \
    "${_MACOS_DB_NAME}" "${_MACOS_DB_USER}" "${pw}" \
    || warn "secrets.env injection skipped"

  # Wipe the local password variable before returning. Bash doesn't
  # guarantee zeroing memory, but unsetting at least drops it from the
  # variable table for any later subshell that inspects env.
  unset pw

  _MACOS_TAKEN_SKIPPED+="${_MACOS_TAKEN_SKIPPED:+,}role:created,db:ensured,vector:ensured,secrets:wrote"
}

# ---------------------------------------------------------------------------
# bootstrap_macos_run \u2014 orchestrator
# ---------------------------------------------------------------------------

# Aggregator string for the audit trail. Modules append `name:action` tokens.
_MACOS_TAKEN_SKIPPED=""

bootstrap_macos_run() {
  # shellcheck disable=SC2034  # STAGE is consumed by install.sh cleanup() trap
  STAGE="bootstrap"
  log "--install-deps: macOS Homebrew bootstrap (upgrade-deps=${UPGRADE_DEPS:-0})"
  _macos_refuse_root
  _macos_require_brew
  _macos_ensure_cosign
  _macos_ensure_dotnet_sdk
  _macos_ensure_postgres
  _macos_ensure_pgvector
  _macos_ensure_database_and_role
  bc_append_install_deps_history "${_MACOS_TAKEN_SKIPPED}" ""
  log "--install-deps: macOS bootstrap complete"
}
