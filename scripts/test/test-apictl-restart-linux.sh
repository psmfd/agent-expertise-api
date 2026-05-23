#!/usr/bin/env bash
#
# test-apictl-restart-linux.sh — exercises `expertise-apictl restart` against
# a stub systemd-user service to validate atomic-restart-primitive parity
# with the macOS fix for #141.
#
# The supervised process IS the HTTP stub (scripts/test/stub-server.py), so
# /health/ready returns 200 only when the post-restart process is actually
# running — this means `wait_for_ready` is genuinely exercised end-to-end.
#
# Asserts per iteration:
#   - exit 0 from `expertise-apictl restart`
#   - systemctl --user is-active = active
#   - MainPID changes from previous iteration (proves the restart killed)
#   - /health/ready returns 200 post-restart (proves wait_for_ready worked
#     against the freshly spawned listener, not a stale one)
#
# Requirements (caller must arrange):
#   - systemd as PID 1
#   - linger enabled for the invoking user (loginctl enable-linger),
#     or a logged-in user session
#   - XDG_RUNTIME_DIR set or createable by pam_systemd
#
# Test artifacts are cleaned up by trap regardless of pass/fail.

set -euo pipefail

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APICTL="${ROOT}/scripts/expertise-apictl"
STUB="${ROOT}/scripts/test/stub-server.py"

TEST_SERVICE="expertise-api-test"
TEST_PORT="18080"
TEST_URL="http://127.0.0.1:${TEST_PORT}"
UNIT_DIR="${HOME}/.config/systemd/user"
UNIT_PATH="${UNIT_DIR}/${TEST_SERVICE}.service"
ITERATIONS=10
PYTHON_BIN="$(command -v python3 || true)"

export EXPERTISE_API_SERVICE="${TEST_SERVICE}"
export EXPERTISE_API_URL="${TEST_URL}"
export EXPERTISE_API_READY_TIMEOUT=10

log() { printf '[test-apictl-restart-linux] %s\n' "$1"; }
fail() { printf '[test-apictl-restart-linux] FAIL: %s\n' "$1" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Cleanup
# ---------------------------------------------------------------------------

cleanup() {
  local rc=$?
  log "cleanup (rc=${rc})"
  systemctl --user stop "${TEST_SERVICE}.service" 2>/dev/null || true
  rm -f "${UNIT_PATH}"
  systemctl --user daemon-reload 2>/dev/null || true
  exit "${rc}"
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

command -v systemctl >/dev/null || fail "systemctl not found"
command -v curl      >/dev/null || fail "curl not found"
[[ -n "${PYTHON_BIN}" ]]         || fail "python3 not found on PATH"
[[ -r "${STUB}" ]]               || fail "stub server not found at ${STUB}"

: "${XDG_RUNTIME_DIR:=/run/user/$(id -u)}"
export XDG_RUNTIME_DIR

systemctl --user is-system-running 2>/dev/null \
  || log "warning: systemctl --user reports degraded state (proceeding)"

# Verify nothing is already bound to TEST_PORT.
if command -v ss >/dev/null && ss -ltn "sport = :${TEST_PORT}" | grep -q LISTEN; then
  fail "port ${TEST_PORT} already in use; aborting"
fi

# ---------------------------------------------------------------------------
# Setup — install the stub unit whose ExecStart IS the HTTP stub.
# ---------------------------------------------------------------------------

mkdir -p "${UNIT_DIR}"
cat > "${UNIT_PATH}" <<EOF
[Unit]
Description=expertise-api stub for apictl restart race regression test
# Disable systemd's default StartLimitBurst (5 starts / 10s) so the test
# can drive 10 restarts in rapid succession without tripping rate-limit.
StartLimitIntervalSec=0

[Service]
ExecStart=${PYTHON_BIN} ${STUB} ${TEST_PORT}
Restart=no
EOF

systemctl --user daemon-reload
systemctl --user start "${TEST_SERVICE}.service"

for _ in $(seq 1 20); do
  if curl -fsS --max-time 1 "${TEST_URL}/health/ready" >/dev/null 2>&1; then
    log "stub service up; HTTP stub serving on ${TEST_URL}"
    break
  fi
  sleep 0.25
done
curl -fsS --max-time 2 "${TEST_URL}/health/ready" >/dev/null \
  || fail "stub HTTP server did not respond after setup"

state=$(systemctl --user is-active "${TEST_SERVICE}.service" || true)
[[ "${state}" == "active" ]] || fail "stub service not active (state=${state})"

# ---------------------------------------------------------------------------
# Test loop
# ---------------------------------------------------------------------------

prev_pid=""
for i in $(seq 1 "${ITERATIONS}"); do
  log "iteration ${i}/${ITERATIONS}"

  if ! bash "${APICTL}" restart >/dev/null; then
    fail "restart failed on iteration ${i}"
  fi

  state=$(systemctl --user is-active "${TEST_SERVICE}.service" || true)
  [[ "${state}" == "active" ]] || fail "iter ${i}: is-active='${state}', expected 'active'"

  pid=$(systemctl --user show -p MainPID --value "${TEST_SERVICE}.service")
  [[ -n "${pid}" && "${pid}" != "0" ]] || fail "iter ${i}: invalid MainPID='${pid}'"

  if [[ -n "${prev_pid}" && "${pid}" == "${prev_pid}" ]]; then
    fail "iter ${i}: pid did not change (${pid}); restart may not have killed"
  fi
  prev_pid="${pid}"

  curl -fsS --max-time 2 "${TEST_URL}/health/ready" >/dev/null \
    || fail "iter ${i}: /health/ready not responding post-restart"

  log "  ok: state=active pid=${pid} ready=200"
done

log "PASS — ${ITERATIONS}/${ITERATIONS} iterations succeeded"
