#!/usr/bin/env bash
# ===========================================================================
# scripts/lib/bootstrap-debian.sh
#
# Debian/Ubuntu apt bootstrap module for `scripts/install.sh --install-deps`
# (#246). Source me from install.sh; do not execute directly. Companion to
# bootstrap-macos.sh — same public contract, same shared bc_* helpers, so
# secrets.env handling, password generation, never-rotate semantics, and the
# audit trail are identical across platforms.
#
# Public functions (called by `bootstrap_deps` in install.sh):
#   bootstrap_debian_run            # full opt-in bootstrap sequence
#
# Module-private helpers (prefixed `_debian_`):
#   _debian_apt_update_once, _debian_apt_install,
#   _debian_ensure_cosign, _debian_ensure_dotnet_sdk,
#   _debian_ensure_postgres, _debian_ensure_pgvector,
#   _debian_ensure_database_and_role,
#   _debian_pg_installed_majors, _debian_pg_start, _debian_pg_wait_ready,
#   _debian_psql_super
#
# Design decisions (Debian 13 "trixie" reality, validated on-VM 2026-07-03):
#  - postgresql-17 (17.10), postgresql-17-pgvector (0.8.0), and cosign (2.5.0)
#    are all in Debian 13's OWN apt repos — no PGDG, no source build, no
#    GitHub-release binary. `apt` per rules/debian-baseline.
#  - .NET 10 is the only dep NOT in Debian apt. Primary channel is Microsoft's
#    packages.microsoft.com debian/13 feed (DEB822 via packages-microsoft-prod);
#    fallback is the official dotnet-install.sh into /usr/local/share/dotnet
#    with a /usr/local/bin symlink (a STANDARD root install.sh's wrapper
#    resolver accepts — it excludes ~/.dotnet, see write_wrapper).
#  - SDK, not runtime: matches bootstrap-macos.sh and supports both --from-source
#    (`dotnet publish`) and --from-release (FDD runtime) install modes.
#  - Unlike Homebrew (where the install user IS the cluster superuser), on Debian
#    the cluster is owned by the `postgres` system user with local peer auth, so
#    role/db/`CREATE EXTENSION vector` run via `sudo -u postgres psql`. The
#    SERVICE still connects over TCP loopback with scram — Debian's default
#    pg_hba already allows `host all all 127.0.0.1/32 scram-sha-256`.
#  - Major-version mismatch (a non-17 cluster present, 17 absent) refuses with
#    pg_upgradecluster guidance; never destructive (mirrors macOS).
# ===========================================================================

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  printf 'bootstrap-debian.sh is a library; source it from install.sh\n' >&2
  exit 64
fi

readonly _DEBIAN_PG_MAJOR=17
readonly _DEBIAN_DB_NAME="expertise"
readonly _DEBIAN_DB_USER="expertise"

# Aggregator string for the audit trail; helpers append `name:action` tokens.
_DEBIAN_TAKEN_SKIPPED=""

# ---------------------------------------------------------------------------
# apt helpers
# ---------------------------------------------------------------------------

_DEBIAN_APT_UPDATED=0
_debian_apt_update_once() {
  (( _DEBIAN_APT_UPDATED == 1 )) && return 0
  bc_sudo_ensure "apt-get update"
  log "apt-get update"
  sudo apt-get update -qq || err "apt-get update failed"
  _DEBIAN_APT_UPDATED=1
}

# _debian_apt_install <pkg...>: noninteractive install; caller has run update.
_debian_apt_install() {
  bc_sudo_ensure "installing packages: $*"
  DEBIAN_FRONTEND=noninteractive sudo -E apt-get install -y -qq "$@" \
    || err "apt-get install failed for: $*"
}

# ---------------------------------------------------------------------------
# cosign — required by install.sh --from-release (cosign verify-blob, ADR-011).
# Debian 13 ships cosign in its own repo. Honor any cosign already on PATH
# (go-install / manual / asdf) exactly like the macOS module.
# ---------------------------------------------------------------------------

_debian_ensure_cosign() {
  if command -v cosign >/dev/null 2>&1; then
    if ! dpkg -S "$(command -v cosign)" >/dev/null 2>&1; then
      log "cosign present on PATH (not dpkg-managed) — skip"
      _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}cosign:skip-external"
      return 0
    fi
    if (( ${UPGRADE_DEPS:-0} == 1 )); then
      _debian_apt_update_once
      _debian_apt_install cosign
      _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}cosign:upgraded"
    else
      log "cosign present (apt-managed) — skip"
      _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}cosign:skip"
    fi
    return 0
  fi
  log "installing cosign via apt"
  _debian_apt_update_once
  _debian_apt_install cosign
  command -v cosign >/dev/null 2>&1 || err "cosign not on PATH after apt install"
  _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}cosign:installed"
}

# ---------------------------------------------------------------------------
# .NET 10 SDK — Microsoft apt feed primary, dotnet-install.sh fallback.
# ---------------------------------------------------------------------------

_debian_have_dotnet_10() {
  command -v dotnet >/dev/null 2>&1 \
    && dotnet --list-sdks 2>/dev/null | awk '$1 ~ /^10\./ { found=1 } END { exit !found }'
}

_debian_ensure_dotnet_sdk() {
  if _debian_have_dotnet_10; then
    local sdk_ver
    sdk_ver=$(dotnet --list-sdks 2>/dev/null | awk '$1 ~ /^10\./ { print $1; exit }')
    if (( ${UPGRADE_DEPS:-0} == 0 )); then
      log "dotnet SDK 10.x present (${sdk_ver}) — skip"
      _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}dotnet-sdk:skip"
      return 0
    fi
    log "dotnet SDK 10.x present (${sdk_ver}) — --upgrade-deps will refresh if apt-managed"
  fi

  # Primary: Microsoft apt feed (packages.microsoft.com/config/debian/<ver>).
  local os_ver=""
  [[ -r /etc/os-release ]] && os_ver="$(awk -F= '/^VERSION_ID=/{gsub(/"/,"",$2); print $2; exit}' /etc/os-release)"
  if [[ -n "${os_ver}" ]] && _debian_try_ms_feed_dotnet "${os_ver}"; then
    _debian_have_dotnet_10 \
      || err "dotnet-sdk-10.0 installed from the Microsoft feed but no 10.x SDK is visible (dotnet --list-sdks: $(dotnet --list-sdks 2>&1))"
    _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}dotnet-sdk:installed-apt"
    log "dotnet SDK 10.x installed via Microsoft apt feed"
    return 0
  fi

  # Fallback: official dotnet-install.sh into a standard system root.
  log "Microsoft feed did not yield dotnet-sdk-10.0; falling back to dotnet-install.sh"
  _debian_install_dotnet_script \
    || err "dotnet-install.sh fallback failed; install .NET 10 SDK manually from https://dot.net and re-run without --install-deps"
  _debian_have_dotnet_10 \
    || err "no .NET 10.x SDK detected after dotnet-install.sh (dotnet --list-sdks: $(dotnet --list-sdks 2>&1))"
  _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}dotnet-sdk:installed-script"
  log "dotnet SDK 10.x installed via dotnet-install.sh"
}

# Add the MS feed (idempotent) and try to install dotnet-sdk-10.0. Returns
# non-zero (without err) if the feed is unavailable or lacks the package, so
# the caller can fall back.
_debian_try_ms_feed_dotnet() {
  local os_ver="$1" deb_url tmp
  deb_url="https://packages.microsoft.com/config/debian/${os_ver}/packages-microsoft-prod.deb"
  if ! dpkg -s packages-microsoft-prod >/dev/null 2>&1; then
    tmp="$(mktemp /tmp/packages-microsoft-prod.XXXXXX.deb)" || return 1
    if ! curl -fsSL -o "${tmp}" "${deb_url}"; then
      rm -f "${tmp}"; log "Microsoft feed not published for debian/${os_ver}"; return 1
    fi
    bc_sudo_ensure "registering the Microsoft package feed"
    sudo dpkg -i "${tmp}" >/dev/null 2>&1 || { rm -f "${tmp}"; return 1; }
    rm -f "${tmp}"
    _DEBIAN_APT_UPDATED=0   # new source list; force a refresh
  fi
  _debian_apt_update_once
  # apt-cache first so a missing package is a clean fallback, not an err exit.
  apt-cache show dotnet-sdk-10.0 >/dev/null 2>&1 || return 1
  _debian_apt_install dotnet-sdk-10.0
}

# dotnet-install.sh into /usr/local/share/dotnet + /usr/local/bin symlink.
_debian_install_dotnet_script() {
  local script tmp_root="/usr/local/share/dotnet"
  script="$(mktemp /tmp/dotnet-install.XXXXXX.sh)" || return 1
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${script}" || { rm -f "${script}"; return 1; }
  bc_sudo_ensure "installing the .NET 10 SDK into ${tmp_root}"
  sudo bash "${script}" --channel 10.0 --install-dir "${tmp_root}" --no-path >/dev/null || { rm -f "${script}"; return 1; }
  rm -f "${script}"
  sudo ln -sf "${tmp_root}/dotnet" /usr/local/bin/dotnet
  hash -r 2>/dev/null || true
}

# ---------------------------------------------------------------------------
# PostgreSQL 17 (+ start + readiness + major-mismatch refusal)
# ---------------------------------------------------------------------------

# Installed Postgres majors, one per line (dirs under /usr/lib/postgresql).
_debian_pg_installed_majors() {
  [[ -d /usr/lib/postgresql ]] || return 0
  local d base
  for d in /usr/lib/postgresql/*/; do
    [[ -d "${d}" ]] || continue
    base="$(basename "${d}")"
    [[ "${base}" =~ ^[0-9]+$ ]] && printf '%s\n' "${base}"
  done | sort -nr
}

_debian_ensure_postgres() {
  local majors present_17=0 other=""
  majors="$(_debian_pg_installed_majors)"
  while IFS= read -r m; do
    [[ -z "${m}" ]] && continue
    if [[ "${m}" == "${_DEBIAN_PG_MAJOR}" ]]; then present_17=1; else other="${other:+${other},}${m}"; fi
  done <<< "${majors}"

  if (( present_17 == 0 )) && [[ -n "${other}" ]]; then
    err "PostgreSQL ${other} is present but PostgreSQL ${_DEBIAN_PG_MAJOR} is not; refuse to install side-by-side without explicit operator intent. To upgrade run: sudo apt-get install postgresql-${_DEBIAN_PG_MAJOR} && sudo pg_upgradecluster ${other%%,*} main"
  fi

  if (( present_17 == 1 )) && dpkg -s "postgresql-${_DEBIAN_PG_MAJOR}" >/dev/null 2>&1; then
    log "postgresql-${_DEBIAN_PG_MAJOR} present — skip"
    _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}postgres:skip"
  else
    log "installing postgresql-${_DEBIAN_PG_MAJOR} via apt"
    _debian_apt_update_once
    _debian_apt_install "postgresql-${_DEBIAN_PG_MAJOR}"
    _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}postgres:installed"
  fi

  _debian_pg_start
  _debian_pg_wait_ready
  log "postgresql-${_DEBIAN_PG_MAJOR} ready on 127.0.0.1:5432"
}

# Ensure the default cluster is running and enabled at boot. apt install
# auto-creates+starts `main` via postgresql-common, but be explicit and
# idempotent so a re-run on a stopped cluster recovers.
_debian_pg_start() {
  bc_sudo_ensure "starting the PostgreSQL service"
  # The postgresql@<major>-main unit is the per-cluster service; the plain
  # `postgresql` unit is a systemd target that starts all clusters.
  if sudo systemctl enable --now "postgresql@${_DEBIAN_PG_MAJOR}-main" >/dev/null 2>&1; then
    return 0
  fi
  if sudo systemctl enable --now postgresql >/dev/null 2>&1; then
    return 0
  fi
  # Fallback for non-systemd contexts (containers): pg_ctlcluster.
  sudo pg_ctlcluster "${_DEBIAN_PG_MAJOR}" main start >/dev/null 2>&1 || true
}

_debian_pg_wait_ready() {
  local tries=0 max_tries=30
  while (( tries < max_tries )); do
    if command -v pg_isready >/dev/null 2>&1; then
      pg_isready -h 127.0.0.1 -p 5432 >/dev/null 2>&1 && return 0
    else
      sudo -u postgres psql -h /var/run/postgresql -tAc 'SELECT 1' >/dev/null 2>&1 && return 0
    fi
    sleep 1; tries=$((tries+1))
  done
  err "PostgreSQL did not become ready within ${max_tries}s (check: sudo systemctl status postgresql@${_DEBIAN_PG_MAJOR}-main)"
}

# ---------------------------------------------------------------------------
# pgvector (Debian's postgresql-17-pgvector)
# ---------------------------------------------------------------------------

_debian_ensure_pgvector() {
  local pkg="postgresql-${_DEBIAN_PG_MAJOR}-pgvector"
  if dpkg -s "${pkg}" >/dev/null 2>&1; then
    if (( ${UPGRADE_DEPS:-0} == 1 )); then
      _debian_apt_update_once
      _debian_apt_install "${pkg}"
      _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}pgvector:upgraded"
    else
      log "${pkg} present — skip"
      _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}pgvector:skip"
    fi
    return 0
  fi
  log "installing ${pkg} via apt"
  _debian_apt_update_once
  _debian_apt_install "${pkg}"
  _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}pgvector:installed"
}

# ---------------------------------------------------------------------------
# role / db / extension / secrets — via `sudo -u postgres psql` (peer auth).
# Mirrors _macos_ensure_database_and_role; the password never enters argv
# (embedded in a heredoc fed to psql stdin) and SET LOCAL log_statement='none'
# suppresses the ALTER ROLE from the server log.
# ---------------------------------------------------------------------------

# Run privileged SQL as the postgres superuser over the local socket. Reads
# SQL from stdin. env-wrapped so PSQL_HISTORY/PGOPTIONS survive sudo's env reset.
_debian_psql_super() {
  sudo -u postgres env PSQL_HISTORY=/dev/null PGOPTIONS="--client-min-messages=warning" \
    psql -h /var/run/postgresql -v ON_ERROR_STOP=1 "$@"
}

_debian_ensure_database_and_role() {
  if [[ -L "${SECRETS_FILE}" ]]; then
    err "${SECRETS_FILE} is a symlink — refusing to bootstrap (would leave PG role and secrets.env out of sync)"
  fi
  if [[ -f "${SECRETS_FILE}" ]] \
     && grep -q '^ConnectionStrings__DefaultConnection=' "${SECRETS_FILE}"; then
    log "secrets.env already configured — skipping role/db/extension creation to avoid password rotation"
    _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}role:skip,db:skip,vector:skip,secrets:skip"
    return 0
  fi

  local pw
  pw="$(bc_generate_db_password)"
  [[ -n "${pw}" ]] || err "bc_generate_db_password returned empty value"
  case "${pw}" in
    *\'*) unset pw; err "generated DB password contains a single quote; refuse to proceed (would corrupt SQL/secrets.env quoting)" ;;
  esac

  local sql_role
  sql_role="BEGIN;
SET LOCAL log_statement = 'none';
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '${_DEBIAN_DB_USER}') THEN
    CREATE ROLE ${_DEBIAN_DB_USER} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
END
\$\$;
ALTER ROLE ${_DEBIAN_DB_USER} PASSWORD '${pw}';
COMMIT;
"
  _debian_psql_super -d postgres -f - <<<"${sql_role}" >/dev/null \
    || { unset pw sql_role; err "psql role creation failed"; }
  unset sql_role

  if ! _debian_psql_super -d postgres -tAc \
       "SELECT 1 FROM pg_database WHERE datname='${_DEBIAN_DB_NAME}'" 2>/dev/null | grep -q 1; then
    log "creating database ${_DEBIAN_DB_NAME} (owner=${_DEBIAN_DB_USER})"
    _debian_psql_super -d postgres \
      -c "CREATE DATABASE ${_DEBIAN_DB_NAME} OWNER ${_DEBIAN_DB_USER};" >/dev/null \
      || { unset pw; err "CREATE DATABASE failed"; }
  fi

  log "ensuring 'vector' extension in ${_DEBIAN_DB_NAME}"
  _debian_psql_super -d "${_DEBIAN_DB_NAME}" \
    -c 'CREATE EXTENSION IF NOT EXISTS vector;' >/dev/null \
    || { unset pw; err "CREATE EXTENSION vector failed (is postgresql-${_DEBIAN_PG_MAJOR}-pgvector installed?)"; }

  bc_write_connection_string_if_absent "127.0.0.1" "5432" \
    "${_DEBIAN_DB_NAME}" "${_DEBIAN_DB_USER}" "${pw}" \
    || warn "secrets.env injection skipped"
  unset pw

  _DEBIAN_TAKEN_SKIPPED+="${_DEBIAN_TAKEN_SKIPPED:+,}role:created,db:ensured,vector:ensured,secrets:wrote"
}

# ---------------------------------------------------------------------------
# bootstrap_debian_run — orchestrator
# ---------------------------------------------------------------------------

bootstrap_debian_run() {
  # shellcheck disable=SC2034  # STAGE is consumed by install.sh cleanup() trap
  STAGE="bootstrap"
  log "--install-deps: Debian apt bootstrap (upgrade-deps=${UPGRADE_DEPS:-0})"
  bc_refuse_root
  bc_sudo_probe
  bc_sudo_ensure "installing system packages (.NET SDK, PostgreSQL, pgvector, cosign)"
  command -v curl >/dev/null 2>&1 || err "curl is required for --install-deps on Debian; install it (sudo apt-get install -y curl) and re-run"
  _debian_ensure_cosign
  _debian_ensure_dotnet_sdk
  _debian_ensure_postgres
  _debian_ensure_pgvector
  _debian_ensure_database_and_role
  bc_append_install_deps_history "${_DEBIAN_TAKEN_SKIPPED}" ""
  log "--install-deps: Debian bootstrap complete"
}
