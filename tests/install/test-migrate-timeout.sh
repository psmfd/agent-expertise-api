#!/usr/bin/env bash
# tests/install/test-migrate-timeout.sh
# Unit tests for the --migrate-timeout flag in scripts/migrate.sh.
#
# Tests use a stub "dotnet" binary placed earlier in PATH to control timing:
#
#  1. Slow stub (sleeps longer than the timeout) → non-zero exit + timeout message.
#  2. --migrate-timeout 0 → disabled bound; slow stub still completes (fast stub used).
#  3. Fast stub → zero exit (happy path unaffected).
#  4. --help mentions --migrate-timeout.
#  5. install.sh --help mentions --migrate-timeout.
#
# Requirement: 'timeout' or 'gtimeout' must be available on the host; if
# neither is present the timeout tests are skipped (matching migrate.sh's own
# behaviour — it warns and proceeds unbounded rather than failing).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
MIGRATE="${SCRIPT_DIR}/scripts/migrate.sh"
INSTALL="${SCRIPT_DIR}/scripts/install.sh"

PASS=0
FAIL=0
SKIP=0

assert() {
  local name="$1"; shift
  if "$@"; then
    PASS=$((PASS+1))
  else
    printf 'FAIL: %s\n' "${name}" >&2
    FAIL=$((FAIL+1))
  fi
}

# assert_contains NAME HAYSTACK NEEDLE
# Uses case/in for substring matching — avoids apostrophe/quoting hazards
# (same pattern as test-install-deps-flag.sh, fixes issue #266).
assert_contains() {
  local name="$1" haystack="$2" needle="$3"
  case "${haystack}" in
    *"${needle}"*) PASS=$((PASS+1)) ;;
    *) printf 'FAIL: %s\n' "${name}" >&2; FAIL=$((FAIL+1)) ;;
  esac
}

skip_test() {
  local name="$1" reason="$2"
  printf 'SKIP: %s — %s\n' "${name}" "${reason}"
  SKIP=$((SKIP+1))
}

# ---------------------------------------------------------------------------
# Detect whether a timeout binary is available (mirrors migrate.sh logic).
# ---------------------------------------------------------------------------
TIMEOUT_BIN=""
if command -v timeout >/dev/null 2>&1; then
  TIMEOUT_BIN="timeout"
elif command -v gtimeout >/dev/null 2>&1; then
  TIMEOUT_BIN="gtimeout"
fi

# ---------------------------------------------------------------------------
# Create a temporary directory for stub binaries and scratch files.
# ---------------------------------------------------------------------------
TMPDIR_WORK="$(mktemp -d)"
trap 'rm -rf "${TMPDIR_WORK}"' EXIT

# Stub bin dir — we create fake "ExpertiseApi" and "dotnet" here.
STUB_BIN="${TMPDIR_WORK}/bin"
mkdir -p "${STUB_BIN}"

# Fake secrets.env with a valid (non-CHANGE_ME) connection string so
# migrate.sh passes the fail-fast guard.
SECRETS="${TMPDIR_WORK}/secrets.env"
printf 'ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=5432;Database=test;Username=test;Password=testpassword"\n' > "${SECRETS}"

# BIN_DIR for migrate.sh — we place stubs here so it picks up ExpertiseApi.
BIN_DIR="${TMPDIR_WORK}/expertise-api-bin"
mkdir -p "${BIN_DIR}"

# ---------------------------------------------------------------------------
# Helper: create a stub ExpertiseApi binary that sleeps for N seconds.
# ---------------------------------------------------------------------------
make_slow_stub() {
  local sleep_secs="$1"
  cat > "${BIN_DIR}/ExpertiseApi" <<EOF
#!/bin/sh
sleep ${sleep_secs}
exit 0
EOF
  chmod 755 "${BIN_DIR}/ExpertiseApi"
}

# Helper: create a fast stub ExpertiseApi binary that exits immediately.
make_fast_stub() {
  cat > "${BIN_DIR}/ExpertiseApi" <<'EOF'
#!/bin/sh
exit 0
EOF
  chmod 755 "${BIN_DIR}/ExpertiseApi"
}

# ---------------------------------------------------------------------------
# Test 1: slow stub + short timeout → non-zero exit + timeout message.
# ---------------------------------------------------------------------------
if [[ -z "${TIMEOUT_BIN}" ]]; then
  skip_test "slow-stub-times-out" "no timeout/gtimeout binary on this host"
else
  make_slow_stub 20

  out=$("${MIGRATE}" \
    --bin-dir "${BIN_DIR}" \
    --secrets-file "${SECRETS}" \
    --migrate-timeout 2 \
    2>&1) || true
  rc=$?

  assert "slow-stub-times-out: exits non-zero" [ "${rc}" -ne 0 ]
  assert_contains "slow-stub-times-out: timeout message present" "${out}" "migration exceeded"
  assert_contains "slow-stub-times-out: mentions NOT swapped"    "${out}" "NOT swapped"
fi

# ---------------------------------------------------------------------------
# Test 2: --migrate-timeout 0 disables the bound; fast stub exits 0.
# ---------------------------------------------------------------------------
make_fast_stub

out=$("${MIGRATE}" \
  --bin-dir "${BIN_DIR}" \
  --secrets-file "${SECRETS}" \
  --migrate-timeout 0 \
  2>&1)
rc=$?

assert "timeout-zero-fast-stub: exits 0" [ "${rc}" -eq 0 ]

# ---------------------------------------------------------------------------
# Test 3: default timeout + fast stub → exits 0 (happy path unaffected).
# ---------------------------------------------------------------------------
make_fast_stub

out=$("${MIGRATE}" \
  --bin-dir "${BIN_DIR}" \
  --secrets-file "${SECRETS}" \
  2>&1)
rc=$?

assert "default-timeout-fast-stub: exits 0" [ "${rc}" -eq 0 ]

# ---------------------------------------------------------------------------
# Test 4: --help mentions --migrate-timeout.
# ---------------------------------------------------------------------------
out=$("${MIGRATE}" --help 2>&1)
rc=$?
assert "help exits 0" [ "${rc}" -eq 0 ]
assert_contains "help mentions --migrate-timeout" "${out}" "--migrate-timeout"

# ---------------------------------------------------------------------------
# Test 5: install.sh --help mentions --migrate-timeout.
# ---------------------------------------------------------------------------
out=$("${INSTALL}" --help 2>&1)
rc=$?
assert "install --help exits 0" [ "${rc}" -eq 0 ]
assert_contains "install --help mentions --migrate-timeout" "${out}" "--migrate-timeout"

# ---------------------------------------------------------------------------
# Test 6: unknown arg still rejected (regression guard).
# ---------------------------------------------------------------------------
"${MIGRATE}" --migrate-timeout-please >/dev/null 2>&1
rc=$?
assert "unknown arg rejected" [ "${rc}" -ne 0 ]

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
printf '\n[test-migrate-timeout] %d passed, %d failed, %d skipped\n' \
  "${PASS}" "${FAIL}" "${SKIP}"

if (( FAIL == 0 )); then
  echo "PASS — 0 errors, 0 warnings"
  exit 0
else
  echo "FAIL — ${FAIL} errors, 0 warnings"
  exit 1
fi
