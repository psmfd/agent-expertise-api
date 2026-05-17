#!/usr/bin/env bash
# migrate.sh — apply pending EF Core migrations to the configured Postgres.
#
# Sources secrets.env (the same way the launch wrapper does), then exec's
# `ExpertiseApi[.dll] migrate`. Idempotent: a no-op when no migrations
# are pending; non-zero exit when the apply fails.
#
# Invoked by:
#   * scripts/install.sh between publish and service install (issue #144).
#   * The operator directly, post-secrets-edit on fresh-install:
#       scripts/migrate.sh
#   * The operator directly, before manual restart on schema-evolving
#     upgrades when not using install.sh:
#       scripts/migrate.sh && scripts/expertise-apictl restart
#
# Defaults match scripts/install.sh:
#   PREFIX     = ${HOME}/.local/share/expertise-api
#   BIN_DIR    = ${PREFIX}/bin
#   CONFIG_DIR = ${HOME}/.config/expertise-api
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/migrate.sh [--prefix DIR] [--secrets-file FILE]

Options:
  --prefix DIR         Install root (default: $HOME/.local/share/expertise-api)
  --secrets-file FILE  Override path to secrets.env (default: $XDG_CONFIG_HOME/expertise-api/secrets.env)
  -h, --help           Show this help

Exit codes:
  0  success — migrations applied or none pending
  1  failure — see logs for details (connection refused, schema conflict, ...)
  2  bad invocation (missing binary, missing connection string)
EOF
}

PREFIX="${HOME}/.local/share/expertise-api"
SECRETS_FILE="${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api/secrets.env"

while (($#)); do
  case "$1" in
    --prefix)        PREFIX="${2:?--prefix needs a directory}"; shift 2 ;;
    --secrets-file)  SECRETS_FILE="${2:?--secrets-file needs a path}"; shift 2 ;;
    -h|--help)       usage; exit 0 ;;
    *)               printf '[migrate] ERROR: unknown arg: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

BIN_DIR="${PREFIX}/bin"

log()  { printf '[migrate] %s\n' "$1"; }
warn() { printf '[migrate] WARN: %s\n' "$1" >&2; }
err()  { printf '[migrate] ERROR: %s\n' "$1" >&2; exit 2; }

# Load secrets — required because the connection string is sensitive and
# never embedded in the wrapper or service unit on the filesystem.
if [[ -f "${SECRETS_FILE}" ]]; then
  log "sourcing secrets from ${SECRETS_FILE}"
  set -a
  # shellcheck disable=SC1090
  . "${SECRETS_FILE}"
  set +a
else
  warn "no secrets file at ${SECRETS_FILE} — relying on environment"
fi

# Fail-fast guard: a missing or placeholder connection string would cause
# the migrate verb to hang on connection-establishment (Npgsql default
# Timeout=15s) and print a generic Npgsql error. Better to surface the
# operator-facing root cause here.
CONN="${ConnectionStrings__DefaultConnection:-}"
if [[ -z "${CONN}" ]]; then
  err "ConnectionStrings__DefaultConnection is not set — edit ${SECRETS_FILE} first"
fi
if [[ "${CONN}" == *CHANGE_ME* ]]; then
  err "ConnectionStrings__DefaultConnection still contains CHANGE_ME placeholder — edit ${SECRETS_FILE} first"
fi

# Prefer the SCD-published native binary when present (no `dotnet` runtime
# required); fall back to the framework-dependent DLL.
if [[ -x "${BIN_DIR}/ExpertiseApi" ]]; then
  log "invoking native binary: ${BIN_DIR}/ExpertiseApi migrate"
  exec "${BIN_DIR}/ExpertiseApi" migrate
elif [[ -f "${BIN_DIR}/ExpertiseApi.dll" ]]; then
  command -v dotnet >/dev/null 2>&1 \
    || err "dotnet CLI not found in PATH (required for fdd publish layout at ${BIN_DIR})"
  log "invoking framework-dependent: dotnet ${BIN_DIR}/ExpertiseApi.dll migrate"
  exec dotnet "${BIN_DIR}/ExpertiseApi.dll" migrate
else
  err "no ExpertiseApi binary at ${BIN_DIR} — run scripts/install.sh first"
fi
