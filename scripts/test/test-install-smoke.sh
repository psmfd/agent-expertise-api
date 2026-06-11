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

# Suppress the dotnet first-run telemetry/HTTPS-cert banner so it does not
# consume our tail-N install-log window during failure diagnostics. Mirrors
# the launch wrapper's runtime defaults.
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true

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
  #
  # For the Linux leg we deliberately drop -h/-p and use the Unix socket
  # so pg_hba.conf's `peer` auth for the postgres role lets us connect
  # without a password. Connecting over TCP (`-h 127.0.0.1`) would route
  # through `local all postgres md5` (no password set) and fail with
  # FATAL: password authentication failed.
  local psql_cmd
  case "$(uname -s)" in
    Linux)  psql_cmd=(sudo -u postgres psql) ;;
    Darwin) psql_cmd=(psql -h "${POSTGRES_HOST}" -p "${POSTGRES_PORT}" -d postgres) ;;
    *)      err_exit "unsupported OS for psql dispatch: $(uname -s)" ;;
  esac
  log "creating role/db ${POSTGRES_USER}/${POSTGRES_DB} (idempotent)"
  # Use `err_exit` not `warn` for the role-create: if this fails the rest
  # of the harness silently runs against a missing db and migrate's real
  # error is buried under connection-refused noise. (CI run #26331919803
  # missed a sudoers misconfiguration this way.)
  "${psql_cmd[@]}" <<SQL || err_exit "role create failed"
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
    || "${psql_cmd[@]}" -c "CREATE DATABASE ${POSTGRES_DB} OWNER ${POSTGRES_USER}" \
    || err_exit "database create failed"
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
#
# Beyond the connection string, the smoke needs:
#   - ASPNETCORE_ENVIRONMENT=Development   so Auth:Mode=ApiKey is accepted
#                                          (Production / Staging refuse it)
#   - Auth__Mode=ApiKey                    short-circuits the OIDC issuers
#                                          requirement that defaults to Prod
#   - Auth__ApiKey=<not-secret>            the bearer the smoke does not
#                                          actually use today, but the
#                                          ApiKey provider refuses to start
#                                          without a non-empty value
#   - Onnx__ModelPath / Onnx__VocabPath    pinned to ${PREFIX}/models/. The
#                                          launch wrapper sets these at runtime
#                                          but migrate.sh bypasses the wrapper
#                                          and otherwise defaults to
#                                          ${baseDir}/models/ (= bin.new/models)
#                                          — which does not exist during a fresh
#                                          install, causing DI validation to
#                                          fail on the unconditionally-registered
#                                          EmbeddingService. Tracked as follow-up
#                                          (migrate.sh should mirror the wrapper).
# DOTNET_ROOT must point at a real .NET install. The path is OS-
# specific: Debian's apt package puts it at /usr/share/dotnet (Linux
# container path used by E1); GHA's setup-dotnet on macos-latest puts
# it at /Users/runner/.dotnet and exports DOTNET_ROOT in the job env.
# Honor the caller's DOTNET_ROOT when set; only fall back to per-OS
# defaults so the harness keeps working in the Linux container where
# DOTNET_ROOT is not pre-exported.
if [ -n "${DOTNET_ROOT:-}" ]; then
  effective_dotnet_root="${DOTNET_ROOT}"
else
  case "$(uname -s)" in
    Linux)  effective_dotnet_root="/usr/share/dotnet" ;;
    Darwin) effective_dotnet_root="/usr/local/share/dotnet" ;;
    *)      err_exit "unsupported OS for DOTNET_ROOT default: $(uname -s)" ;;
  esac
fi
cat > "${SECRETS_FILE}" <<EOF
ConnectionStrings__DefaultConnection="Host=${POSTGRES_HOST};Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
ASPNETCORE_ENVIRONMENT=Development
Auth__Mode=ApiKey
Auth__ApiKey=smoke-api-key-not-secret
Onnx__ModelPath=${PREFIX}/models/model.onnx
Onnx__VocabPath=${PREFIX}/models/vocab.txt
DOTNET_ROOT=${effective_dotnet_root}
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
# Preserve the staged tree on failure so the failure-handler below can
# re-run the migrate binary directly and capture its real stderr. The
# escape hatch is a CI / debug-only env that production installs never
# set. Same for EXPERTISE_API_RELAXED_HARDENING (#166 / E1) which strips
# the systemd hardening directives that require CAP_SYS_ADMIN to apply
# (incompatible with privileged-container user-systemd).
if EXPERTISE_API_PRESERVE_STAGE_ON_FAILURE=1 \
    EXPERTISE_API_RELAXED_HARDENING=1 \
    bash "${ROOT}/scripts/install.sh" \
     --skip-preflight \
     --prefix "${PREFIX}" \
     --bind "${BIND_ADDR}" \
     > "${PREFIX}/.smoke-install.log" 2>&1; then
  PASS=$((PASS + 1))
  printf 'PASS: install.sh exit 0\n'
else
  rc=$?
  FAIL=$((FAIL + 1))
  printf 'FAIL: install.sh exit %d\n' "$rc" >&2
  # Use `--` to terminate option parsing: argv starting with `-----` would
  # otherwise be parsed as a flag by bash's printf builtin. Tail 200 lines
  # not 80 so a fresh-CI dotnet first-run banner doesn't push the actual
  # migrate error out of the window.
  printf -- '\n----- install.sh log (last 200 lines) -----\n' >&2
  tail -n 200 "${PREFIX}/.smoke-install.log" >&2 || true
  printf -- '----- end install.sh log -----\n\n' >&2

  # If install.sh died at migrate, run the binary directly to capture
  # whatever stderr the migrate verb actually emitted. The launch wrapper
  # exports DOTNET_ROOT / Onnx__ModelPath / ASPNETCORE_ENVIRONMENT but
  # migrate.sh does not (#262); make them explicit here too so we get a
  # clean diagnostic regardless.
  if [ -x "${PREFIX}/bin.new/ExpertiseApi" ] || [ -f "${PREFIX}/bin.new/ExpertiseApi.dll" ]; then
    printf -- '\n----- direct binary migrate retry (diagnostic) -----\n' >&2
    printf -- 'PREFIX=%s\n' "${PREFIX}" >&2
    printf -- 'STAGE_BIN_DIR=%s\n' "${PREFIX}/bin.new" >&2
    ls -la "${PREFIX}/bin.new" 2>&1 | head -20 >&2 || true
    printf -- '\n[diag] dotnet --info via apphost env:\n' >&2
    DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}" "${DOTNET_ROOT:-/usr/share/dotnet}/dotnet" --info 2>&1 | head -20 >&2 || true
    printf -- '\n[diag] ldd of apphost binary:\n' >&2
    ldd "${PREFIX}/bin.new/ExpertiseApi" 2>&1 | head -10 >&2 || true
    printf -- '\n[diag] env that secrets file will set:\n' >&2
    (
      set -a
      # shellcheck disable=SC1090
      . "${SECRETS_FILE}"
      set +a
      env | grep -E '^(ASPNETCORE|Auth__|Onnx__|DOTNET|Connection)' | sed 's/Password=[^;]*/Password=***/' >&2
      printf -- '\n[diag] running binary with `migrate` arg; exit code captured separately\n' >&2
      export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"
      set +e
      if [ -x "${PREFIX}/bin.new/ExpertiseApi" ]; then
        "${PREFIX}/bin.new/ExpertiseApi" migrate \
          > "${PREFIX}/.diag-migrate.stdout" 2> "${PREFIX}/.diag-migrate.stderr"
      else
        dotnet "${PREFIX}/bin.new/ExpertiseApi.dll" migrate \
          > "${PREFIX}/.diag-migrate.stdout" 2> "${PREFIX}/.diag-migrate.stderr"
      fi
      bin_rc=$?
      set -e
      printf -- '\n[diag] binary exited rc=%d\n' "${bin_rc}" >&2
      printf -- '[diag] stdout (%d bytes):\n' "$(wc -c < "${PREFIX}/.diag-migrate.stdout")" >&2
      cat "${PREFIX}/.diag-migrate.stdout" >&2 || true
      printf -- '\n[diag] stderr (%d bytes):\n' "$(wc -c < "${PREFIX}/.diag-migrate.stderr")" >&2
      cat "${PREFIX}/.diag-migrate.stderr" >&2 || true
      # Probe the runtime resolution path with a DLL-via-dotnet invocation.
      # The apphost call above may be silenced by a Console-vs-pipe quirk;
      # `dotnet ... .dll` produces predictable Console output.
      printf -- '\n[diag] dotnet DLL invocation (catches apphost-vs-dotnet output divergence):\n' >&2
      set +e
      DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}" dotnet "${PREFIX}/bin.new/ExpertiseApi.dll" migrate \
        > "${PREFIX}/.diag-dll.stdout" 2> "${PREFIX}/.diag-dll.stderr"
      dll_rc=$?
      set -e
      printf -- '[diag] dll exited rc=%d\n' "${dll_rc}" >&2
      printf -- '[diag] dll stdout (%d bytes):\n' "$(wc -c < "${PREFIX}/.diag-dll.stdout")" >&2
      cat "${PREFIX}/.diag-dll.stdout" >&2 || true
      printf -- '\n[diag] dll stderr (%d bytes):\n' "$(wc -c < "${PREFIX}/.diag-dll.stderr")" >&2
      cat "${PREFIX}/.diag-dll.stderr" >&2 || true
    ) >&2 2>&1
    printf -- '----- end direct binary migrate retry -----\n\n' >&2
  fi
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
# 11. macOS-only: install.sh --system must hard-error exit 2 (preflight
#     guard for #285) and print the expected ERROR line naming #145.
#     This assertion is macOS-specific; on Linux --system errors at a
#     different stage (install_service, exit 1) — that behavior is not
#     the subject of this fix and is tested separately.
# ---------------------------------------------------------------------------
case "$(uname -s)" in
  Darwin)
    # Do NOT pass --skip-preflight: the guard lives in preflight() and must
    # fire before --skip-preflight would bypass it. The guard is the FIRST
    # check in preflight(), so no real host probes run on this code path.
    # Use a sub-directory of ${PREFIX} (which contains "expertise-api" as a
    # path component) so prefix-validation passes without --allow-system-prefix.
    #
    # Capture stdout+stderr WITHOUT || true so that $? reflects install.sh's
    # exit code, not `true`. set +e / set -e wraps the subshell to prevent
    # the outer set -u pipeline from aborting on the expected non-zero exit.
    set +e
    _system_err_output=$(bash "${ROOT}/scripts/install.sh" \
      --prefix "${PREFIX}/_system_guard_probe" \
      --system \
      2>&1)
    _system_rc=$?
    set -e
    if [ "${_system_rc}" = "2" ]; then
      PASS=$((PASS + 1))
      printf 'PASS: install.sh --system exits 2 on macOS\n'
    else
      FAIL=$((FAIL + 1))
      printf 'FAIL: install.sh --system exited %d on macOS (expected 2)\n' "${_system_rc}" >&2
    fi
    if printf '%s\n' "${_system_err_output}" | grep -qF '#145'; then
      PASS=$((PASS + 1))
      printf 'PASS: install.sh --system error output names #145\n'
    else
      FAIL=$((FAIL + 1))
      printf 'FAIL: install.sh --system error output did not mention #145 (got: %s)\n' "${_system_err_output}" >&2
    fi
    ;;
  *)
    log "SKIP: --system macOS preflight guard test (not running on Darwin; uname=$(uname -s))"
    ;;
esac

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo
echo "${TEST_NAME}: ${PASS} passed, ${FAIL} failed"
[ "${FAIL}" = 0 ]
