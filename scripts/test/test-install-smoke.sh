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
#   4a. (release mode only) ${PREFIX}/.install-mode contains "release".
#   4b. (release mode only) ${PREFIX}/.install-version-semver is non-empty
#       and carries appVersion matching SMOKE_RELEASE_VERSION.
#   4c. (release mode only) ${PREFIX}/.install-history is non-empty.
#   5. `systemctl --user is-active expertise-api.service` reports active.
#   6. /health/live returns 200 within READY_TIMEOUT seconds.
#   7. /health/ready returns 200 (proves DB + ONNX + migrations are all
#      green, per #143's readiness aggregation).
#   8. `expertise-apictl restart` completes and /health/ready returns
#      200 again post-restart (regression guard for #141 / PR #164).
#   9. `expertise-apictl stop` completes cleanly.
#  10. No orphan dotnet ExpertiseApi process remains after stop.
#  11. uninstall.sh --yes --purge exits 0.
#  11a. (macOS only) `launchctl print-disabled gui/UID` contains no entry
#       for the service label after uninstall — regression guard for #286.
#       SKIP (not FAIL) if print-disabled itself errors (sandbox constraint).
#  12. (macOS only) install.sh --system is accepted (#145). When
#       SMOKE_SYSTEM_SCOPE=1: full LaunchDaemon end-to-end smoke including
#       plist render + uninstall assertions. Otherwise: lightweight guard that
#       --system no longer exits with "not supported on macOS".
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
# Release-mode env (E3 / #260 — only set these for --from-release runs):
#   SMOKE_INSTALL_MODE      — set to "release" to activate release-path
#                             assertions (4a/4b/4c) and pass --from-release
#                             to install.sh (default: empty = source mode)
#   SMOKE_RELEASE_VERSION   — vX.Y.Z tag to install, required when
#                             SMOKE_INSTALL_MODE=release (e.g. "v1.0.0")
#
# System-scope env (#145 — macOS LaunchDaemon only):
#   SMOKE_SYSTEM_SCOPE      — set to "1" to run a full LaunchDaemon install
#                             smoke on macOS (requires passwordless sudo).
#                             Default is unset (user-scope smoke only).
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

# E3 / #260 — release-mode smoke parameters.
SMOKE_INSTALL_MODE="${SMOKE_INSTALL_MODE:-}"
SMOKE_RELEASE_VERSION="${SMOKE_RELEASE_VERSION:-}"

if [ "${SMOKE_INSTALL_MODE}" = "release" ] && [ -z "${SMOKE_RELEASE_VERSION}" ]; then
  err_exit "SMOKE_INSTALL_MODE=release requires SMOKE_RELEASE_VERSION to be set (e.g. v1.0.0)"
fi

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
#    Release mode (E3 / #260): --from-release --version adds cosign-verified
#    tarball download + extract in place of dotnet publish.
# ---------------------------------------------------------------------------
log "running install.sh with prefix=${PREFIX} bind=${BIND_ADDR} mode=${SMOKE_INSTALL_MODE:-source}"

# Build install args. Release-mode appends --from-release + --version so
# the harness can be driven by either path without duplicating install logic.
install_args=(--skip-preflight --prefix "${PREFIX}" --bind "${BIND_ADDR}")
if [ "${SMOKE_INSTALL_MODE}" = "release" ]; then
  install_args+=(--from-release --version "${SMOKE_RELEASE_VERSION}")
fi

# Preserve the staged tree on failure so the failure-handler below can
# re-run the migrate binary directly and capture its real stderr. The
# escape hatch is a CI / debug-only env that production installs never
# set. Same for EXPERTISE_API_RELAXED_HARDENING (#166 / E1) which strips
# the systemd hardening directives that require CAP_SYS_ADMIN to apply
# (incompatible with privileged-container user-systemd).
if EXPERTISE_API_PRESERVE_STAGE_ON_FAILURE=1 \
    EXPERTISE_API_RELAXED_HARDENING=1 \
    bash "${ROOT}/scripts/install.sh" \
     "${install_args[@]}" \
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

# 4a–4c: release-mode marker assertions (E3 / #260).
# Only run when SMOKE_INSTALL_MODE=release — safe no-ops for source-mode
# invocations from the E1/E2 jobs.
if [ "${SMOKE_INSTALL_MODE}" = "release" ]; then
  assert "post-install .install-mode contains 'release'" \
    bash -c "[ \"\$(cat '${PREFIX}/.install-mode' 2>/dev/null)\" = 'release' ]"

  # .install-version-semver must carry appVersion= matching the pinned version.
  # SMOKE_RELEASE_VERSION carries the v-prefixed tag (e.g. v1.0.0).
  # release-consumer.sh writes appVersion without the v prefix (matching the
  # manifest's appVersion field), so strip it before the grep.
  expected_appver="${SMOKE_RELEASE_VERSION#v}"
  assert "post-install .install-version-semver carries expected appVersion (${expected_appver})" \
    bash -c "grep -qF 'appVersion=${expected_appver}' '${PREFIX}/.install-version-semver' 2>/dev/null"

  assert "post-install .install-history is non-empty" test -s "${PREFIX}/.install-history"
fi

# ---------------------------------------------------------------------------
# 4d. install.env written by install.sh records EXPERTISE_API_LOG_DIR (#287).
#     Stored at ${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api/install.env
#     so expertise-apictl can locate it without knowing which --prefix was
#     used at install time. The smoke always uses --prefix so
#     LOG_DIR == ${PREFIX}/logs; verify that the file records exactly that.
# ---------------------------------------------------------------------------
INSTALL_ENV_FILE="${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api/install.env"
assert "post-install install.env exists at XDG config location" \
  test -f "${INSTALL_ENV_FILE}"
assert "post-install install.env records EXPERTISE_API_LOG_DIR" \
  bash -c "grep -q '^EXPERTISE_API_LOG_DIR=' '${INSTALL_ENV_FILE}' 2>/dev/null"
assert "post-install install.env EXPERTISE_API_LOG_DIR points to prefix logs dir" \
  bash -c "grep -qF 'EXPERTISE_API_LOG_DIR=${PREFIX}/logs' '${INSTALL_ENV_FILE}' 2>/dev/null"

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
# 7b. apictl logs -n 1 succeeds with the prefix install layout (#287).
#     This is the core regression guard: before the fix, macOS logs would
#     hardcode ${HOME}/Library/Logs/expertise-api and miss ${PREFIX}/logs.
#     On macOS the fix causes apictl to read LOG_DIR from install.env (written
#     in step 4d). On Linux journalctl is path-agnostic. Both platforms must
#     pass. The service must be running (checked in 7) for logs to exist.
# ---------------------------------------------------------------------------
assert "apictl logs -n 1 exits 0 (prefix-aware log path, #287 regression guard)" \
  bash -c "'${APICTL}' logs -n 1 >/dev/null 2>&1"

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
# 11. Uninstall — smoke uninstall.sh and assert no stale launchd override
#     state remains (macOS) and no plist remains (macOS). Runs on all OS.
#
#     macOS: after uninstall.sh --yes --purge, `launchctl print-disabled
#     gui/UID` must NOT contain the service label. This is the acceptance
#     criterion for #286: launchd override entries (written by BOTH
#     `launchctl enable` and `launchctl disable`) are persistent and cannot
#     be removed without root, so the fix is to never write one — install.sh
#     runs `enable` only when the label is actually disabled, and uninstall
#     runs no enable/disable at all. A fresh install→uninstall cycle must
#     therefore leave no entry for the label in print-disabled.
#
#     If `launchctl print-disabled` itself errors (e.g. sandbox restrictions
#     on a CI runner), we treat it as SKIP — not a FAIL — and log the reason.
# ---------------------------------------------------------------------------
_uninstall_label="com.thesemicolon.expertise-api"

_uninstall_log="${TMPDIR:-/tmp}/.smoke-uninstall-$$.log"
log "running uninstall.sh --yes --purge (prefix=${PREFIX})"
if bash "${ROOT}/scripts/uninstall.sh" --yes --purge --prefix "${PREFIX}" \
     > "${_uninstall_log}" 2>&1; then
  PASS=$((PASS + 1))
  printf 'PASS: uninstall.sh --yes --purge exit 0\n'
else
  rc=$?
  FAIL=$((FAIL + 1))
  printf 'FAIL: uninstall.sh --yes --purge exit %d\n' "$rc" >&2
  tail -n 40 "${_uninstall_log}" >&2 || true
fi

# macOS-only: assert no stale launchd override entry remains after uninstall.
case "$(uname -s)" in
  Darwin)
    # Capture print-disabled output; treat a command failure as a SKIP.
    set +e
    _pd_output=$(launchctl print-disabled "gui/$(id -u)" 2>&1)
    _pd_rc=$?
    set -e
    if [ "${_pd_rc}" != "0" ]; then
      log "SKIP: launchctl print-disabled exited ${_pd_rc} — sandbox restriction; cannot assert launchd override state"
      log "SKIP: print-disabled output: ${_pd_output}"
    elif printf '%s\n' "${_pd_output}" | grep -qF "${_uninstall_label}"; then
      FAIL=$((FAIL + 1))
      printf 'FAIL: launchd override entry for %s still present after uninstall\n' "${_uninstall_label}" >&2
      printf 'FAIL: launchctl print-disabled gui/%s output:\n' "$(id -u)" >&2
      printf '%s\n' "${_pd_output}" >&2
    else
      PASS=$((PASS + 1))
      printf 'PASS: no launchd override entry for %s after uninstall\n' "${_uninstall_label}"
    fi
    ;;
  *)
    log "SKIP: launchd override assertion (not running on Darwin; uname=$(uname -s))"
    ;;
esac

# ---------------------------------------------------------------------------
# 12. macOS-only: install.sh --system is now supported (#145).
#     When SMOKE_SYSTEM_SCOPE=1 (opt-in): attempt a real LaunchDaemon
#     install and verify that --system is accepted (exit 0) and that the
#     daemon plist renders with UserName + the expected paths. This requires
#     passwordless sudo (available on GHA macOS runners) and is gated
#     behind SMOKE_SYSTEM_SCOPE=1 so the default user-scope smoke is
#     unaffected.
#
#     When SMOKE_SYSTEM_SCOPE is unset: assert that --system no longer exits 2
#     with a "not supported" error (regression guard for the #291 guards that
#     were lifted by #145), but do NOT attempt the full install (no sudo here).
# ---------------------------------------------------------------------------
SMOKE_SYSTEM_SCOPE="${SMOKE_SYSTEM_SCOPE:-}"
case "$(uname -s)" in
  Darwin)
    if [ "${SMOKE_SYSTEM_SCOPE}" = "1" ]; then
      # Full system-scope install smoke (requires passwordless sudo — GHA macOS).
      #
      # Uses the DEFAULT system prefix (/opt/expertise-api), not a TMPDIR
      # prefix: the #242 ancestor walk requires every ancestor of the prefix
      # to be root-owned and non-writable, which a path under the runner's
      # TMPDIR (/var symlink + runner-owned dirs) can never satisfy. The
      # default prefix also exercises the documented operator path verbatim.
      _sys_prefix="/opt/expertise-api"
      _sys_config_dir="/etc/expertise-api"
      _sys_bind="127.0.0.1:18090"

      # Seed the system-scope secrets file (mirrors the user-scope seeding
      # above; install.sh reads CONFIG_DIR=/etc/expertise-api in system scope).
      sudo mkdir -p "${_sys_config_dir}"
      printf '%s\n' \
        "ConnectionStrings__DefaultConnection=\"Host=${POSTGRES_HOST};Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}\"" \
        "ASPNETCORE_ENVIRONMENT=Development" \
        "Auth__Mode=ApiKey" \
        "Auth__ApiKey=smoke-api-key-not-secret" \
        "Onnx__ModelPath=${_sys_prefix}/models/model.onnx" \
        "Onnx__VocabPath=${_sys_prefix}/models/vocab.txt" \
        "DOTNET_ROOT=${effective_dotnet_root}" \
        | sudo tee "${_sys_config_dir}/secrets.env" >/dev/null
      sudo chmod 600 "${_sys_config_dir}/secrets.env"

      log "SMOKE_SYSTEM_SCOPE=1: attempting LaunchDaemon install at default prefix=${_sys_prefix}"
      set +e
      _sys_install_log="${TMPDIR:-/tmp}/.smoke-system-install-$$.log"
      # SC2024: log path is under TMPDIR (runner-writable); the shell opens
      # the fd before exec'ing sudo, which is correct here.
      # shellcheck disable=SC2024
      sudo bash "${ROOT}/scripts/install.sh" \
        --system \
        --bind "${_sys_bind}" \
        > "${_sys_install_log}" 2>&1
      _sys_rc=$?
      set -e
      if [ "${_sys_rc}" = "0" ]; then
        PASS=$((PASS + 1))
        printf 'PASS: install.sh --system exits 0 on macOS (LaunchDaemon)\n'
      else
        FAIL=$((FAIL + 1))
        printf 'FAIL: install.sh --system exited %d on macOS\n' "${_sys_rc}" >&2
        tail -n 40 "${_sys_install_log}" >&2 || true
      fi

      # Daemon answers over HTTP — the strongest boot-equivalent signal we
      # can get without an actual reboot.
      _sys_ready=0
      for _ in $(seq 1 60); do
        if curl -fsS "http://${_sys_bind}/health/live" >/dev/null 2>&1; then
          _sys_ready=1
          break
        fi
        sleep 1
      done
      if [ "${_sys_ready}" = "1" ]; then
        PASS=$((PASS + 1))
        printf 'PASS: LaunchDaemon /health/live returns 200 within 60s\n'
      else
        FAIL=$((FAIL + 1))
        printf 'FAIL: LaunchDaemon /health/live did not return 200 within 60s\n' >&2
        printf '[diag] launchctl print:\n' >&2
        sudo launchctl print "system/com.thesemicolon.expertise-api" 2>&1 | head -60 >&2 || true
        printf '[diag] path layout:\n' >&2
        sudo ls -la "${_sys_prefix}" "${_sys_prefix}/bin" /var/log/expertise-api "${_sys_config_dir}" >&2 2>&1 || true
        printf '[diag] stdout.log:\n' >&2
        sudo cat /var/log/expertise-api/stdout.log >&2 2>&1 || printf '[diag] (stdout.log missing/unreadable)\n' >&2
        printf '[diag] stderr.log:\n' >&2
        sudo cat /var/log/expertise-api/stderr.log >&2 2>&1 || printf '[diag] (stderr.log missing/unreadable)\n' >&2
        printf '[diag] direct wrapper run as %s (8s window):\n' "$(id -un)" >&2
        sudo -u "$(id -un)" /bin/bash -c \
          "'${_sys_prefix}/launch-expertise-api.sh' > '${TMPDIR:-/tmp}/.wrap-direct-$$.log' 2>&1 & _wp=\$!; sleep 8; kill \$_wp 2>/dev/null; true" || true
        head -n 40 "${TMPDIR:-/tmp}/.wrap-direct-$$.log" >&2 2>/dev/null || printf '[diag] (no direct-run output captured)\n' >&2
        printf '[diag] launchd unified log (last 4m, expertise lines):\n' >&2
        sudo log show --last 4m --style compact 2>/dev/null | grep -i expertise | tail -n 25 >&2 || true
      fi

      # Assert the daemon plist was written with UserName and correct paths.
      _daemon_plist="/Library/LaunchDaemons/com.thesemicolon.expertise-api.plist"
      if [ -f "${_daemon_plist}" ]; then
        PASS=$((PASS + 1))
        printf 'PASS: daemon plist written to /Library/LaunchDaemons/\n'
        if grep -q '<key>UserName</key>' "${_daemon_plist}"; then
          PASS=$((PASS + 1))
          printf 'PASS: daemon plist contains UserName key\n'
        else
          FAIL=$((FAIL + 1))
          printf 'FAIL: daemon plist missing UserName key\n' >&2
        fi
        if grep -q "${_sys_prefix}" "${_daemon_plist}"; then
          PASS=$((PASS + 1))
          printf 'PASS: daemon plist references install prefix\n'
        else
          FAIL=$((FAIL + 1))
          printf 'FAIL: daemon plist does not reference install prefix\n' >&2
        fi
      else
        FAIL=$((FAIL + 1))
        printf 'FAIL: daemon plist not written to /Library/LaunchDaemons/\n' >&2
      fi

      # Assert install.env records scope=system.
      _sys_install_env="${HOME}/.config/expertise-api/install.env"
      if grep -q '^EXPERTISE_API_SCOPE=system' "${_sys_install_env}" 2>/dev/null; then
        PASS=$((PASS + 1))
        printf 'PASS: install.env records EXPERTISE_API_SCOPE=system\n'
      else
        FAIL=$((FAIL + 1))
        printf 'FAIL: install.env does not record EXPERTISE_API_SCOPE=system\n' >&2
      fi

      # Teardown: uninstall.sh --system --yes --purge (default prefix, same
      # as the install above — exercises the documented operator path).
      set +e
      # SC2024: same TMPDIR-writable pattern as the install step above.
      # shellcheck disable=SC2024
      sudo bash "${ROOT}/scripts/uninstall.sh" \
        --system \
        --yes --purge \
        > "${TMPDIR:-/tmp}/.smoke-system-uninstall-$$.log" 2>&1
      _sys_uninst_rc=$?
      set -e
      if [ "${_sys_uninst_rc}" = "0" ]; then
        PASS=$((PASS + 1))
        printf 'PASS: uninstall.sh --system --yes --purge exits 0\n'
      else
        FAIL=$((FAIL + 1))
        printf 'FAIL: uninstall.sh --system --yes --purge exited %d\n' "${_sys_uninst_rc}" >&2
      fi

      # Assert daemon plist removed; assert no stale entry in print-disabled system.
      if [ ! -f "${_daemon_plist}" ]; then
        PASS=$((PASS + 1))
        printf 'PASS: daemon plist removed after uninstall\n'
      else
        FAIL=$((FAIL + 1))
        printf 'FAIL: daemon plist still present after uninstall\n' >&2
      fi
      set +e
      _pd_system=$(launchctl print-disabled system 2>&1)
      _pd_sys_rc=$?
      set -e
      if [ "${_pd_sys_rc}" != "0" ]; then
        log "SKIP: launchctl print-disabled system exited ${_pd_sys_rc} — cannot assert system override state"
      elif printf '%s\n' "${_pd_system}" | grep -qF 'com.thesemicolon.expertise-api'; then
        FAIL=$((FAIL + 1))
        printf 'FAIL: stale launchd override entry in system domain after --system uninstall\n' >&2
      else
        PASS=$((PASS + 1))
        printf 'PASS: no stale launchd override entry in system domain after uninstall\n'
      fi
    else
      # Default: assert --system is now accepted (does not exit 2 with "not supported").
      # We only verify that the preflight root-check fires (exit 2 = not running as root),
      # not the old "not supported" guard that was removed by #145. This is a lightweight
      # regression guard without needing sudo.
      log "SMOKE_SYSTEM_SCOPE not set: verifying --system is accepted (not rejected as unsupported)"
      set +e
      _system_err_output=$(bash "${ROOT}/scripts/install.sh" \
        --prefix "${PREFIX}/_system_guard_probe" \
        --system \
        2>&1)
      _system_rc=$?
      set -e
      # Exit 2 is expected (not running as root), but the message must NOT say
      # "not supported on macOS" (the old guard that was lifted by #145).
      if printf '%s\n' "${_system_err_output}" | grep -q 'not supported on macOS'; then
        FAIL=$((FAIL + 1))
        printf 'FAIL: install.sh --system still prints "not supported on macOS" (guard from #291 not lifted)\n' >&2
      else
        PASS=$((PASS + 1))
        printf 'PASS: install.sh --system does not reject with "not supported on macOS"\n'
      fi
    fi
    ;;
  *)
    log "SKIP: --system macOS LaunchDaemon test (not running on Darwin; uname=$(uname -s))"
    ;;
esac

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo
echo "${TEST_NAME}: ${PASS} passed, ${FAIL} failed"
[ "${FAIL}" = 0 ]
