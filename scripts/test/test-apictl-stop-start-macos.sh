#!/usr/bin/env bash
#
# test-apictl-stop-start-macos.sh — exercises `expertise-apictl stop` then
# `expertise-apictl start` against a stub launchd service to validate the
# stop/start lifecycle fix for #284.
#
# Before the fix, `cmd_stop` ran `launchctl bootout`, which UNREGISTERED the
# service. The subsequent `cmd_start` called `launchctl kickstart` against an
# unregistered label, which failed with "No such process". The fix changes
# `cmd_stop` to use `launchctl kill SIGTERM`, leaving the service registered
# so `kickstart` in `cmd_start` works.
#
# The stub plist uses KeepAlive=false so that a SIGTERM'd process (which exits
# 0 via Python's default SIGTERM handler) is NOT respawned — mirroring the real
# plist's `KeepAlive { SuccessfulExit = false }` semantics. This lets us verify
# that stop leaves the process absent and that start brings it back exactly once.
#
# Assertions (one pass over a stop→start→stop→start cycle):
#
#   After stop:
#     - expertise-apictl stop exits 0
#     - launchctl state eventually transitions to NOT "running" (no respawn)
#     - /health/ready is no longer reachable after process exits
#     - process stays stopped for a hold period (KeepAlive respawn guard)
#
#   After start:
#     - expertise-apictl start exits 0
#     - launchctl state = running
#     - /health/ready returns 200 (proves wait_for_ready in cmd_start worked)
#     - PID is different from the pre-stop PID
#
# Test artifacts are cleaned up by trap regardless of pass/fail.

set -euo pipefail

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APICTL="${ROOT}/scripts/expertise-apictl"
STUB="${ROOT}/scripts/test/stub-server.py"

TEST_LABEL="com.thesemicolon.expertise-api-test-stop-start"
TEST_SERVICE="expertise-api-test-stop-start"
TEST_PORT="18081"
TEST_URL="http://127.0.0.1:${TEST_PORT}"
PLIST_PATH="${HOME}/Library/LaunchAgents/${TEST_LABEL}.plist"
DOMAIN="gui/$(id -u)"
# How many complete stop→start cycles to exercise.
CYCLES=3
# Seconds to hold after stop before asserting the service stays gone.
KEEPALIVE_GUARD_SECS=3
PYTHON_BIN="$(command -v python3)"

export EXPERTISE_API_LABEL="${TEST_LABEL}"
export EXPERTISE_API_SERVICE="${TEST_SERVICE}"
export EXPERTISE_API_URL="${TEST_URL}"
export EXPERTISE_API_READY_TIMEOUT=15
# Suppress wait_for_ready so cmd_stop returns promptly (no HTTP listener).
# We probe readiness ourselves below to validate actual lifecycle state.
export EXPERTISE_API_SKIP_READY=1

log()  { printf '[test-apictl-stop-start-macos] %s\n' "$1"; }
fail() { printf '[test-apictl-stop-start-macos] FAIL: %s\n' "$1" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Cleanup
# ---------------------------------------------------------------------------

cleanup() {
  local rc=$?
  log "cleanup (rc=${rc})"
  launchctl bootout "${DOMAIN}/${TEST_LABEL}" 2>/dev/null || true
  rm -f "${PLIST_PATH}"
  exit "${rc}"
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

[[ -x "${STUB}" || -r "${STUB}" ]] || fail "stub server not found at ${STUB}"
[[ -n "${PYTHON_BIN}" ]]           || fail "python3 not found on PATH"
command -v curl >/dev/null         || fail "curl not found"

if lsof -nP -iTCP:"${TEST_PORT}" -sTCP:LISTEN >/dev/null 2>&1; then
  fail "port ${TEST_PORT} already in use; aborting"
fi

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Emit the launchctl state field for the test service.
launchd_state() {
  launchctl print "${DOMAIN}/${TEST_LABEL}" 2>/dev/null \
    | awk '/^[[:space:]]*state = / {print $3; exit}'
}

# Emit the launchctl pid field for the test service (empty if not running).
launchd_pid() {
  launchctl print "${DOMAIN}/${TEST_LABEL}" 2>/dev/null \
    | awk '/^[[:space:]]*pid = / {print $3; exit}'
}

# Wait up to $1 seconds for launchd state to reach "running".
wait_running() {
  local timeout_secs="$1"
  local state=""
  local i=0
  while (( i < timeout_secs * 4 )); do
    state="$(launchd_state)"
    case "${state}" in
      running) return 0 ;;
      spawn|"") ;;
      *) fail "unexpected launchd state '${state}' while waiting for running" ;;
    esac
    sleep 0.25
    i=$(( i + 1 ))
  done
  fail "service did not reach state=running within ${timeout_secs}s (state='${state}')"
}

# Wait up to $1 seconds for launchd state to leave "running"
# (i.e., the process has exited and launchd has acknowledged it).
wait_stopped() {
  local timeout_secs="$1"
  local state=""
  local i=0
  while (( i < timeout_secs * 4 )); do
    state="$(launchd_state)"
    case "${state}" in
      running) ;;
      *) return 0 ;;  # any non-running state (including empty/wait) is "stopped"
    esac
    sleep 0.25
    i=$(( i + 1 ))
  done
  fail "service did not leave state=running within ${timeout_secs}s — may not have stopped"
}

# ---------------------------------------------------------------------------
# Setup — install stub plist with KeepAlive=false to mirror real plist
# semantics (SuccessfulExit=false means a clean exit does not trigger respawn).
# ---------------------------------------------------------------------------

log "writing stub plist to ${PLIST_PATH}"
mkdir -p "$(dirname "${PLIST_PATH}")"
cat > "${PLIST_PATH}" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>${TEST_LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${PYTHON_BIN}</string>
    <string>${STUB}</string>
    <string>${TEST_PORT}</string>
  </array>
  <key>RunAtLoad</key><true/>
  <!-- KeepAlive=false mirrors the real plist's KeepAlive{SuccessfulExit=false}:
       a clean exit (SIGTERM → exit 0) does NOT trigger an automatic respawn. -->
  <key>KeepAlive</key><false/>
</dict>
</plist>
EOF

launchctl bootout "${DOMAIN}/${TEST_LABEL}" 2>/dev/null || true
launchctl bootstrap "${DOMAIN}" "${PLIST_PATH}"
launchctl enable "${DOMAIN}/${TEST_LABEL}"
launchctl kickstart "${DOMAIN}/${TEST_LABEL}"

log "waiting for stub HTTP server to come up on ${TEST_URL}"
for _ in $(seq 1 40); do
  if curl -fsS --max-time 1 "${TEST_URL}/health/ready" >/dev/null 2>&1; then
    log "stub service up; HTTP stub serving on ${TEST_URL}"
    break
  fi
  sleep 0.25
done
curl -fsS --max-time 2 "${TEST_URL}/health/ready" >/dev/null \
  || fail "stub HTTP server did not respond after setup"

initial_state="$(launchd_state)"
[[ "${initial_state}" == "running" ]] \
  || fail "service not in running state after setup (state='${initial_state}')"

# ---------------------------------------------------------------------------
# Cycle loop: stop → verify stopped → start → verify running
# ---------------------------------------------------------------------------

prev_start_pid="$(launchd_pid)"

for cycle in $(seq 1 "${CYCLES}"); do
  log "--- cycle ${cycle}/${CYCLES} ---"

  # --- STOP ---
  log "  stopping service"
  if ! bash "${APICTL}" stop; then
    fail "cycle ${cycle}: expertise-apictl stop exited non-zero"
  fi

  # Wait for launchd to acknowledge the process has exited.
  wait_stopped 10
  log "  service left running state"

  # Verify no KeepAlive respawn: hold for KEEPALIVE_GUARD_SECS seconds and
  # confirm the service does NOT come back to running on its own.
  log "  holding ${KEEPALIVE_GUARD_SECS}s to verify no KeepAlive respawn"
  sleep "${KEEPALIVE_GUARD_SECS}"
  post_hold_state="$(launchd_state)"
  if [[ "${post_hold_state}" == "running" ]]; then
    fail "cycle ${cycle}: service respawned to 'running' after stop (KeepAlive guard failed)"
  fi
  log "  no respawn confirmed (state='${post_hold_state}')"

  # Verify HTTP is not reachable (the process is actually gone).
  if curl -fsS --max-time 1 "${TEST_URL}/health/ready" >/dev/null 2>&1; then
    fail "cycle ${cycle}: /health/ready still reachable after stop — process may still be running"
  fi
  log "  /health/ready unreachable: process confirmed gone"

  # --- START ---
  # Unset EXPERTISE_API_SKIP_READY for the start call so wait_for_ready runs
  # and genuinely validates the listener is up before cmd_start returns.
  log "  starting service"
  if ! EXPERTISE_API_SKIP_READY=0 bash "${APICTL}" start; then
    fail "cycle ${cycle}: expertise-apictl start exited non-zero"
  fi

  # Poll for state=running (wait_for_ready in cmd_start already confirmed HTTP
  # readiness, but we also verify launchd agrees so the assertion is complete).
  wait_running 15
  log "  service reached state=running"

  new_pid="$(launchd_pid)"
  [[ -n "${new_pid}" ]] || fail "cycle ${cycle}: could not extract pid after start"

  if [[ -n "${prev_start_pid}" && "${new_pid}" == "${prev_start_pid}" ]]; then
    fail "cycle ${cycle}: PID did not change after stop→start (${new_pid}); process may not have actually stopped"
  fi
  prev_start_pid="${new_pid}"

  # Verify HTTP readiness independently (belt-and-braces on top of wait_for_ready).
  curl -fsS --max-time 2 "${TEST_URL}/health/ready" >/dev/null \
    || fail "cycle ${cycle}: /health/ready not responding after start"

  log "  cycle ${cycle} ok: state=running pid=${new_pid} ready=200"
done

log "PASS — ${CYCLES}/${CYCLES} stop→start cycles succeeded"
