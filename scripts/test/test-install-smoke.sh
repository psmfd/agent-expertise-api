#!/usr/bin/env bash
#
# test-install-smoke.sh — E1 of #166. End-to-end smoke test for scripts/
# install.sh against a real Postgres on Linux. Runs INSIDE the install-
# smoke Debian 13 + systemd container (scripts/test/Dockerfile.install-
# smoke). Designed to be reusable from the macOS leg (E2 / #259) and the
# --from-release leg (E3 / #260) by shelling out only to install.sh +
# apictl, never to OS-specific commands directly.
#
# What it asserts (each assertion adds 1 to PASS or FAIL):
#
#   1. Postgres is reachable on 127.0.0.1:5432 before install begins.
#   2. SECRETS_FILE gets written with the connection string (precondition
#      for install.sh's migrate step).
#   3. scripts/install.sh exits 0 (full stage → migrate → swap → service-
#      install loop).
#   4. ${PREFIX}/.install-mode and ${PREFIX}/.install-version-semver
#      exist post-install (D3 post-install markers).
#   5. `systemctl --user is-active expertise-api.service` reports active.
#   6. /health/live returns 200 within READY_TIMEOUT seconds.
#   7. /health/ready returns 200 (proves DB + ONNX + migrations are all
#      green, per #143's readiness aggregation).
#   8. `expertise-apictl restart` completes and /health/ready returns
#      200 again post-restart (regression guard for #141 / PR #164).
#   9. `expertise-apictl stop` completes cleanly.
#  10. No orphan dotnet ExpertiseApi process remains after stop.
#
# Exit codes:
#   0  — all assertions passed
#   1+ — at least one assertion failed
#
# Usage (inside the container):
#   bash scripts/test/test-install-smoke.sh
#
# Required env (defaulted):
#   PREFIX                  — install prefix (default: ${HOME}/expertise-api)
#   BIND_PORT               — port to bind the service to (default: 18080)
#   READY_TIMEOUT_SECONDS   — max wait for /health/ready (default: 60)
#   POSTGRES_HOST           — default 127.0.0.1
#   POSTGRES_PORT           — default 5432
#   POSTGRES_DB             — default expertise_smoke
#   POSTGRES_USER           — default expertise_smoke
#   POSTGRES_PASSWORD       — default smoke-password-not-secret
#
# Postgres bootstrap (Linux container only): the harness invokes
# `start_postgres_linux` which calls `sudo systemctl start postgresql`
# and creates the role + database if missing. On macOS (E2) the harness
# expects the caller to have done this via `brew services start`; the
# bootstrap function detects OS and dispatches.

set -uo pipefail

# Pre-emptively tighten umask so any directory we create later (CONFIG_DIR,
# PREFIX, log paths) starts mode 0700, not the runner's inherited 0022.
# Per shell-expert E1 pre-PR review: secrets.env itself is chmod 600 but
# the directory listing would otherwise leak filenames on shared runners.
umask 0077

# ---------------------------------------------------------------------------
# Locate the repo root. Resilient to being invoked from a copy of the tree
# rather than the bind-mounted /workspace original.
# ---------------------------------------------------------------------------
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TEST_NAME="$(basename "$0")"
PASS=0
FAIL=0

PREFIX="${PREFIX:-${HOME}/expertise-api}"
BIND_PORT="${BIND_PORT:-18080}"
BIND_ADDR="127.0.0.1:${BIND_PORT}"
# Note: each wait_for_http iteration is up to 3s wall clock (curl --max-time 2
# + sleep 1), so the effective wall-clock budget is 60-180s. Treat the
# variable as a maximum attempt count, not a strict second budget. Per
# shell-expert E1 pre-PR review.
READY_TIMEOUT_SECONDS="${READY_TIMEOUT_SECONDS:-60}"
POSTGRES_HOST="${POSTGRES_HOST:-127.0.0.1}"
POSTGRES_PORT="${POSTGRES_PORT:-5432}"
POSTGRES_DB="${POSTGRES_DB:-expertise_smoke}"
POSTGRES_USER="${POSTGRES_USER:-expertise_smoke}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-smoke-password-not-secret}"
BASE_URL="http://${BIND_ADDR}"

log() { printf '[smoke] %s\n' "$1"; }
warn() { printf '[smoke] WARN: %s\n' "$1" >&2; }
err_exit() { printf '[smoke] FATAL: %s\n' "$1" >&2; exit 1; }

assert() {
  local name="$1"; shift
  if "$@"; then
    PASS=$((PASS + 1))
    printf 'PASS: %s\n' "$name"
  else
    FAIL=$((FAIL + 1))
    printf 'FAIL: %s\n' "$name" >&2
  fi
}

# ---------------------------------------------------------------------------
# OS-aware Postgres bootstrap. Linux: systemctl start + role/db create
# via the postgres superuser. macOS: assumes brew services started it and
# the smoke caller has psql on PATH; we only role/db create.
# ---------------------------------------------------------------------------
start_postgres_linux() {
  log "starting postgresql via systemd"
  sudo systemctl start postgresql || err_exit "systemctl start postgresql failed"
  # Wait for ready (PGDG-installed cluster on Debian uses port 5432 by default).
  # pg_isready is an unauthenticated TCP startup probe — no sudo needed even
  # though postgres is started as root. Try unprivileged first; fall back to
  # sudo only if PATH is incomplete.
  local i
  for i in $(seq 1 30); do
    if pg_isready -h "${POSTGRES_HOST}" -p "${POSTGRES_PORT}" >/dev/null 2>&1 \
       || sudo pg_isready -h "${POSTGRES_HOST}" -p "${POSTGRES_PORT}" >/dev/null 2>&1; then
      log "postgresql ready after ${i}s"
      return 0
    fi
    sleep 1
  done
  err_exit "postgresql did not become ready within 30s"
}

start_postgres_macos() {
  log "macOS: expecting postgres already running via brew services"
  local i
  for i in $(seq 1 30); do
    if pg_isready -h "${POSTGRES_HOST}" -p "${POSTGRES_PORT}" >/dev/null 2>&1; then
      log "postgresql reachable after ${i}s"
      return 0
    fi
    sleep 1
  done
  err_exit "postgresql is not reachable at ${POSTGRES_HOST}:${POSTGRES_PORT}"
}

create_postgres_role_and_db() {
  # On Linux we sudo to postgres; on macOS the brew install runs as the
  # current user so plain psql works.
  local psql_cmd
  case "$(uname -s)" in
    Linux)  psql_cmd=(sudo -u postgres psql -h "${POSTGRES_HOST}" -p "${POSTGRES_PORT}") ;;
    Darwin) psql_cmd=(psql -h "${POSTGRES_HOST}" -p "${POSTGRES_PORT}" -d postgres) ;;
    *)      err_exit "unsupported OS for psql dispatch: $(uname -s)" ;;
  esac
  log "creating role/db ${POSTGRES_USER}/${POSTGRES_DB} (idempotent)"
  "${psql_cmd[@]}" <<SQL || warn "role/db create returned non-zero (may be pre-existing)"
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '${POSTGRES_USER}') THEN
    CREATE ROLE ${POSTGRES_USER} WITH LOGIN PASSWORD '${POSTGRES_PASSWORD}';
  END IF;
END
\$\$;
SQL
  "${psql_cmd[@]}" -tAc "SELECT 1 FROM pg_database WHERE datname='${POSTGRES_DB}'" \
    | grep -q 1 \
    || "${psql_cmd[@]}" -c "CREATE DATABASE ${POSTGRES_DB} OWNER ${POSTGRES_USER}"
  # pgvector extension — install.sh's migrate path expects it.
  "${psql_cmd[@]}" -d "${POSTGRES_DB}" -c "CREATE EXTENSION IF NOT EXISTS vector" \
    || warn "CREATE EXTENSION vector failed — pgvector apt package may be missing; embeddings will fail but smoke continues"
}

# ---------------------------------------------------------------------------
# 1. Postgres bootstrap + reachability assertion
# ---------------------------------------------------------------------------
case "$(uname -s)" in
  Linux)  start_postgres_linux ;;
  Darwin) start_postgres_macos ;;
  *) err_exit "unsupported OS: $(uname -s)" ;;
esac

assert "postgres reachable at ${POSTGRES_HOST}:${POSTGRES_PORT}" \
  bash -c "pg_isready -h '${POSTGRES_HOST}' -p '${POSTGRES_PORT}' >/dev/null 2>&1 \
           || sudo pg_isready -h '${POSTGRES_HOST}' -p '${POSTGRES_PORT}' >/dev/null 2>&1"

create_postgres_role_and_db

# ---------------------------------------------------------------------------
# 2. Pre-stage SECRETS_FILE so install.sh's migrate step succeeds. We do
#    this manually rather than via --install-deps to keep this test focused
#    on install.sh (bootstrap coverage lives in tests/install/test-
#    bootstrap-common.sh).
# ---------------------------------------------------------------------------
# install.sh's CONFIG_DIR derivation: when --prefix is overridden (which
# the smoke harness ALWAYS does), CONFIG_DIR == ${PREFIX} on every OS
# (install.sh:262-268 PREFIX_OVERRIDE branch wins over the OS branch).
# Per shell-expert E1 pre-PR review: the previous OS-dispatched derivation
# silently false-passed on macOS — install.sh would look in ${PREFIX}/
# secrets.env while we wrote to ~/Library/Application Support/…, the
# migrate step would skip with "ConnectionStrings__DefaultConnection
# unset", and /health/ready would never go green.
CONFIG_DIR="${PREFIX}"
SECRETS_FILE="${CONFIG_DIR}/secrets.env"

mkdir -p "${PREFIX}"
mkdir -p "${CONFIG_DIR}"
chmod 700 "${CONFIG_DIR}"
# Heredoc with unquoted delimiter intentionally expands ${POSTGRES_PASSWORD}.
# The default password is shell-safe; callers overriding POSTGRES_PASSWORD
# must avoid characters that need quoting ($, `, ", \). Documented contract.
cat > "${SECRETS_FILE}" <<EOF
ConnectionStrings__DefaultConnection="Host=${POSTGRES_HOST};Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
EOF
chmod 600 "${SECRETS_FILE}"
assert "secrets file written with conn string" test -s "${SECRETS_FILE}"

# ---------------------------------------------------------------------------
# 3. install.sh — full stage → publish → models → migrate → swap → service
#    install. Bind to a non-default port so the smoke run doesn't collide
#    with anything else on the host. --skip-preflight bypasses the human-
#    facing host preflight (CI is not a human-facing host).
# ---------------------------------------------------------------------------
log "running install.sh with prefix=${PREFIX} bind=${BIND_ADDR}"
if bash "${ROOT}/scripts/install.sh" \
     --skip-preflight \
     --prefix "${PREFIX}" \
     --bind "${BIND_ADDR}" \
     > "${PREFIX}/.smoke-install.log" 2>&1; then
  PASS=$((PASS + 1))
  printf 'PASS: install.sh exit 0\n'
else
  rc=$?
  FAIL=$((FAIL + 1))
  printf 'FAIL: install.sh exit %d
' "$rc" >&2
  # Use `--` to terminate option parsing: argv starting with `-----` would
  # otherwise be parsed as a flag by bash's printf builtin.
  printf -- '\n----- install.sh log (last 80 lines) -----\n' >&2
  tail -n 80 "${PREFIX}/.smoke-install.log" >&2 || true
  printf -- '----- end install.sh log -----\n\n' >&2
fi

# ---------------------------------------------------------------------------
# 4. D3 post-install markers present
# ---------------------------------------------------------------------------
assert "post-install marker .install-mode exists" test -s "${PREFIX}/.install-mode"
assert "post-install marker .install-version-semver exists or source-mode" \
  bash -c "[ -s '${PREFIX}/.install-version-semver' ] \
           || [ \"\$(cat '${PREFIX}/.install-mode' 2>/dev/null)\" = 'source' ]"

# ---------------------------------------------------------------------------
# 5. Service is active under systemd-user (Linux) / launchd (macOS).
#    apictl status is the OS-agnostic shim.
# ---------------------------------------------------------------------------
APICTL="${PREFIX}/expertise-apictl"
[ -x "${APICTL}" ] || APICTL="${ROOT}/scripts/expertise-apictl"
export EXPERTISE_API_URL="${BASE_URL}"

assert "apictl status reports active" \
  bash -c "'${APICTL}' status >/dev/null 2>&1 || '${APICTL}' status 2>&1 | grep -qiE 'active|running'"

# ---------------------------------------------------------------------------
# 6. /health/live returns 200 within READY_TIMEOUT_SECONDS
# ---------------------------------------------------------------------------
wait_for_http() {
  local url="$1"
  local timeout="$2"
  local i
  for i in $(seq 1 "$timeout"); do
    if curl --fail --silent --show-error --max-time 2 -o /dev/null "$url"; then
      return 0
    fi
    sleep 1
  done
  return 1
}

assert "/health/live returns 200 within ${READY_TIMEOUT_SECONDS}s" \
  wait_for_http "${BASE_URL}/health/live" "${READY_TIMEOUT_SECONDS}"

# ---------------------------------------------------------------------------
# 7. /health/ready returns 200 (DB + migrations + ONNX all green)
# ---------------------------------------------------------------------------
assert "/health/ready returns 200" \
  wait_for_http "${BASE_URL}/health/ready" 10

# ---------------------------------------------------------------------------
# 8. apictl restart preserves readiness (regression for #141 / PR #164)
# ---------------------------------------------------------------------------
log "restarting service via apictl"
if "${APICTL}" restart > "${PREFIX}/.smoke-restart.log" 2>&1; then
  PASS=$((PASS + 1))
  printf 'PASS: apictl restart exit 0\n'
else
  rc=$?
  FAIL=$((FAIL + 1))
  printf 'FAIL: apictl restart exit %d\n' "$rc" >&2
  tail -n 40 "${PREFIX}/.smoke-restart.log" >&2 || true
fi

assert "/health/ready returns 200 post-restart" \
  wait_for_http "${BASE_URL}/health/ready" "${READY_TIMEOUT_SECONDS}"

# ---------------------------------------------------------------------------
# 9. apictl stop completes cleanly
# ---------------------------------------------------------------------------
log "stopping service via apictl"
if "${APICTL}" stop > "${PREFIX}/.smoke-stop.log" 2>&1; then
  PASS=$((PASS + 1))
  printf 'PASS: apictl stop exit 0\n'
else
  rc=$?
  FAIL=$((FAIL + 1))
  printf 'FAIL: apictl stop exit %d\n' "$rc" >&2
  tail -n 40 "${PREFIX}/.smoke-stop.log" >&2 || true
fi

# Give the service-manager a moment to reap the dotnet PID. Poll rather
# than fixed sleep so slow .NET shutdown on a constrained CI runner does
# not produce a spurious orphan-check FAIL. Bounded to 30s (systemd's
# default TimeoutStopSec is 90s but ExpertiseApi targets 30s per #142).
# Per shell-expert E1 pre-PR review.
for _ in $(seq 1 30); do
  if ! pgrep -f "${PREFIX}.*ExpertiseApi" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

# ---------------------------------------------------------------------------
# 10. No orphan dotnet ExpertiseApi process remains. Match by the binary
#     path (which lives under ${PREFIX}) so we don't catch unrelated dotnet
#     processes that GHA's runner might have running.
#
#     Use `pgrep -lf` (not `-af`): `-a` is procps-only and macOS BSD pgrep
#     would silently no-op the option, producing a FALSE PASS for the
#     orphan check on E2 (#259). `-l` (long, prints command name) is in
#     both BSD and procps and is sufficient for our diagnostic output.
#     Per shell-expert E1 pre-PR review.
# ---------------------------------------------------------------------------
orphan_count=$(pgrep -lf "${PREFIX}.*ExpertiseApi" 2>/dev/null | wc -l | tr -d ' ')
if [ "${orphan_count}" = "0" ]; then
  PASS=$((PASS + 1))
  printf 'PASS: no orphan ExpertiseApi process under %s\n' "${PREFIX}"
else
  FAIL=$((FAIL + 1))
  printf 'FAIL: %d orphan ExpertiseApi process(es) under %s:\n' "${orphan_count}" "${PREFIX}" >&2
  pgrep -lf "${PREFIX}.*ExpertiseApi" >&2 || true
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo
echo "${TEST_NAME}: ${PASS} passed, ${FAIL} failed"
[ "${FAIL}" = 0 ]
