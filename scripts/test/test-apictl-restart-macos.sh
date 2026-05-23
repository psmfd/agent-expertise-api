#!/usr/bin/env bash
#
# test-apictl-restart-macos.sh — exercises `expertise-apictl restart` against
# a stub launchd service to validate the race fix for #141.
#
# The supervised process IS the HTTP stub (scripts/test/stub-server.py), so
# /health/ready returns 200 only when the post-restart process is actually
# running. This means `wait_for_ready` (the readiness gate added by this PR)
# is genuinely exercised end-to-end on each iteration — not just the kill+
# respawn primitive.
#
# Asserts per iteration:
#   - exit 0 from `expertise-apictl restart`
#   - launchctl state = running
#   - launchctl pid changes from previous iteration (proves the kill happened)
#   - /health/ready returns 200 after restart (proves wait_for_ready worked
#     against the freshly spawned listener, not a stale one)
#
# Test artifacts are cleaned up by trap regardless of pass/fail.

set -euo pipefail

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APICTL="${ROOT}/scripts/expertise-apictl"
STUB="${ROOT}/scripts/test/stub-server.py"

TEST_LABEL="com.thesemicolon.expertise-api-test"
TEST_SERVICE="expertise-api-test"
TEST_PORT="18080"
TEST_URL="http://127.0.0.1:${TEST_PORT}"
PLIST_PATH="${HOME}/Library/LaunchAgents/${TEST_LABEL}.plist"
DOMAIN="gui/$(id -u)"
ITERATIONS=10
PYTHON_BIN="$(command -v python3)"

export EXPERTISE_API_LABEL="${TEST_LABEL}"
export EXPERTISE_API_SERVICE="${TEST_SERVICE}"
export EXPERTISE_API_URL="${TEST_URL}"
export EXPERTISE_API_READY_TIMEOUT=10

log() { printf '[test-apictl-restart-macos] %s\n' "$1"; }
fail() { printf '[test-apictl-restart-macos] FAIL: %s\n' "$1" >&2; exit 1; }

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
[[ -n "${PYTHON_BIN}" ]]            || fail "python3 not found on PATH"
command -v curl >/dev/null          || fail "curl not found"

# Verify nothing is already bound to TEST_PORT.
if lsof -nP -iTCP:"${TEST_PORT}" -sTCP:LISTEN >/dev/null 2>&1; then
  fail "port ${TEST_PORT} already in use; aborting"
fi

# ---------------------------------------------------------------------------
# Setup — install the stub plist whose supervised program IS the HTTP stub.
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
  <key>KeepAlive</key><true/>
</dict>
</plist>
EOF

launchctl bootout "${DOMAIN}/${TEST_LABEL}" 2>/dev/null || true
launchctl bootstrap "${DOMAIN}" "${PLIST_PATH}"
launchctl enable "${DOMAIN}/${TEST_LABEL}"
launchctl kickstart "${DOMAIN}/${TEST_LABEL}"

# Wait for the stub HTTP server to respond before starting the loop.
for _ in $(seq 1 20); do
  if curl -fsS --max-time 1 "${TEST_URL}/health/ready" >/dev/null 2>&1; then
    log "stub service up; HTTP stub serving on ${TEST_URL}"
    break
  fi
  sleep 0.25
done
curl -fsS --max-time 2 "${TEST_URL}/health/ready" >/dev/null \
  || fail "stub HTTP server did not respond after setup"

# ---------------------------------------------------------------------------
# Test loop
# ---------------------------------------------------------------------------

prev_pid=""
for i in $(seq 1 "${ITERATIONS}"); do
  log "iteration ${i}/${ITERATIONS}"

  if ! bash "${APICTL}" restart >/dev/null; then
    fail "restart failed on iteration ${i}"
  fi

  # launchd briefly reports state=spawn between kickstart and the supervised
  # process being fully running. Poll for the steady-state transition.
  state=""
  for _ in $(seq 1 30); do
    state=$(launchctl print "${DOMAIN}/${TEST_LABEL}" 2>/dev/null \
      | awk '/^[[:space:]]*state = / {print $3; exit}')
    case "${state}" in
      running) break ;;
      spawn|"") sleep 0.1 ;;
      *) fail "iter ${i}: unexpected state='${state}'" ;;
    esac
  done
  [[ "${state}" == "running" ]] || fail "iter ${i}: state='${state}' after settle, expected 'running'"

  pid=$(launchctl print "${DOMAIN}/${TEST_LABEL}" \
    | awk '/^[[:space:]]*pid = / {print $3; exit}')
  [[ -n "${pid}" ]] || fail "iter ${i}: could not extract pid"

  if [[ -n "${prev_pid}" && "${pid}" == "${prev_pid}" ]]; then
    fail "iter ${i}: pid did not change (${pid}); kill may not have happened"
  fi
  prev_pid="${pid}"

  # Verify wait_for_ready actually worked: the freshly spawned listener
  # must respond to /health/ready right now (without us re-polling).
  curl -fsS --max-time 2 "${TEST_URL}/health/ready" >/dev/null \
    || fail "iter ${i}: /health/ready not responding post-restart"

  log "  ok: state=running pid=${pid} ready=200"
done

log "PASS — ${ITERATIONS}/${ITERATIONS} iterations succeeded"
