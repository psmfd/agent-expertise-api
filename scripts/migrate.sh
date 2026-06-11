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
Usage: scripts/migrate.sh [--prefix DIR] [--bin-dir DIR] [--secrets-file FILE]
                          [--migrate-timeout SECONDS]

Options:
  --prefix DIR             Install root (default: $HOME/.local/share/expertise-api)
  --bin-dir DIR            Target binary directory directly (overrides --prefix-derived
                           ${PREFIX}/bin). Used by install.sh during stage-then-swap
                           to migrate against the new binaries before they go live.
  --secrets-file FILE      Override path to secrets.env (default: $XDG_CONFIG_HOME/expertise-api/secrets.env)
  --migrate-timeout SECS   Wall-time limit for the migrate verb (default: 300).
                           0 disables the bound (unbounded). On timeout the script
                           exits non-zero with a clear message.
  -h, --help               Show this help

Exit codes:
  0  success — migrations applied or none pending
  1  failure — see logs for details (connection refused, schema conflict, ...)
  2  bad invocation (missing binary, missing connection string)
EOF
}

PREFIX="${HOME}/.local/share/expertise-api"
SECRETS_FILE="${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api/secrets.env"
BIN_DIR_OVERRIDE=""
MIGRATE_TIMEOUT=300

while (($#)); do
  case "$1" in
    --prefix)           PREFIX="${2:?--prefix needs a directory}"; shift 2 ;;
    --secrets-file)     SECRETS_FILE="${2:?--secrets-file needs a path}"; shift 2 ;;
    --bin-dir)          BIN_DIR_OVERRIDE="${2:?--bin-dir needs a directory}"; shift 2 ;;
    --migrate-timeout)  MIGRATE_TIMEOUT="${2:?--migrate-timeout needs a number}"; shift 2 ;;
    -h|--help)          usage; exit 0 ;;
    *)                  printf '[migrate] ERROR: unknown arg: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

# --bin-dir takes precedence over --prefix-derived path. Used by
# install.sh during the publish-staged → migrate-against-staged → swap
# flow so migrate runs against the new binaries without disturbing
# the live tree.
BIN_DIR="${BIN_DIR_OVERRIDE:-${PREFIX}/bin}"

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

# Mirror the launch wrapper's env exports so the migrate verb's DI graph
# matches the runtime graph exactly. Program.cs registers IEmbeddingGenerator
# only if File.Exists(Onnx:ModelPath) && File.Exists(Onnx:VocabPath); without
# these exports the default ${baseDir}/models/ path is wrong for a staged or
# installed layout, causing EmbeddingService DI validation to fail at startup
# before any migration runs (issue #262).
#
# Honour any value already in the environment (allows CI or the operator to
# override without editing this script). The fallback uses ${PREFIX}/models/
# which matches the install layout written by install.sh's write_wrapper().
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
MODEL_DIR="${PREFIX}/models"
export Onnx__ModelPath="${Onnx__ModelPath:-${MODEL_DIR}/model.onnx}"
export Onnx__VocabPath="${Onnx__VocabPath:-${MODEL_DIR}/vocab.txt}"

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

# ---------------------------------------------------------------------------
# Timeout wrapper — detect coreutils timeout (Linux) or gtimeout (macOS brew).
# Falls back to unbounded when neither is present and MIGRATE_TIMEOUT > 0.
# ---------------------------------------------------------------------------
TIMEOUT_BIN=""
if [[ "${MIGRATE_TIMEOUT}" -gt 0 ]] 2>/dev/null; then
  if command -v timeout >/dev/null 2>&1; then
    TIMEOUT_BIN="timeout"
  elif command -v gtimeout >/dev/null 2>&1; then
    TIMEOUT_BIN="gtimeout"
  else
    warn "timeout / gtimeout not found — migrate will run unbounded (install coreutils via brew to enable the bound)"
  fi
fi

# run_migrate CMD [ARGS...]: invoke the migrate command via the timeout wrapper
# (or directly when MIGRATE_TIMEOUT=0 or no timeout binary). Uses a child
# process rather than exec so we can inspect the exit code.
run_migrate() {
  local rc
  # `|| rc=$?` keeps a non-zero child exit from tripping `set -e` before we
  # can inspect it — without the guard, exit 124 aborted the script here and
  # the timeout diagnostic below was never printed (caught by CI on #242's
  # wiring of this test suite).
  rc=0
  if [[ -n "${TIMEOUT_BIN}" && "${MIGRATE_TIMEOUT}" -gt 0 ]]; then
    log "migrate timeout: ${MIGRATE_TIMEOUT}s (via ${TIMEOUT_BIN})"
    "${TIMEOUT_BIN}" "${MIGRATE_TIMEOUT}" "$@" || rc=$?
    if [[ "${rc}" -eq 124 ]]; then
      err "migration exceeded ${MIGRATE_TIMEOUT}s; live binaries NOT swapped; service NOT touched — check for advisory-lock contention or a runaway ALTER TABLE"
    fi
  else
    "$@" || rc=$?
  fi
  return "${rc}"
}

# Prefer the SCD-published native binary when present (no `dotnet` runtime
# required); fall back to the framework-dependent DLL.
if [[ -x "${BIN_DIR}/ExpertiseApi" ]]; then
  log "invoking native binary: ${BIN_DIR}/ExpertiseApi migrate"
  run_migrate "${BIN_DIR}/ExpertiseApi" migrate
elif [[ -f "${BIN_DIR}/ExpertiseApi.dll" ]]; then
  command -v dotnet >/dev/null 2>&1 \
    || err "dotnet CLI not found in PATH (required for fdd publish layout at ${BIN_DIR})"
  log "invoking framework-dependent: dotnet ${BIN_DIR}/ExpertiseApi.dll migrate"
  run_migrate dotnet "${BIN_DIR}/ExpertiseApi.dll" migrate
else
  err "no ExpertiseApi binary at ${BIN_DIR} — run scripts/install.sh first"
fi
