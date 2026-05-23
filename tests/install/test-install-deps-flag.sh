#!/usr/bin/env bash
# tests/install/test-install-deps-flag.sh
# Unit tests for the --install-deps / --upgrade-deps flag plumbing in
# scripts/install.sh. Does not invoke any per-OS bootstrap module; covers:
#  - --upgrade-deps without --install-deps hard-errors
#  - help text mentions both flags
#  - usage()'s sed window covers the dep-bootstrap doc block

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
INSTALL="${SCRIPT_DIR}/scripts/install.sh"

PASS=0
FAIL=0
assert() {
  local name="$1"; shift
  if "$@"; then PASS=$((PASS+1)); else printf 'FAIL: %s\n' "${name}" >&2; FAIL=$((FAIL+1)); fi
}

# 1. --upgrade-deps without --install-deps must hard-error before any work.
out=$("${INSTALL}" --upgrade-deps --prefix /tmp/expertise-api-test --skip-preflight 2>&1)
rc=$?
assert "upgrade-deps alone exits non-zero" [ "${rc}" -ne 0 ]
assert "upgrade-deps alone names the requirement" bash -c "[[ '${out}' == *'requires --install-deps'* ]]"

# 2. --help must mention both flags.
out=$("${INSTALL}" --help 2>&1)
rc=$?
assert "--help exits 0" [ "${rc}" -eq 0 ]
assert "--help mentions --install-deps" bash -c "[[ '${out}' == *'--install-deps'* ]]"
assert "--help mentions --upgrade-deps" bash -c "[[ '${out}' == *'--upgrade-deps'* ]]"

# 3. --install-deps alone is accepted by the parser (the bootstrap itself
#    requires a real OS environment, so we don't actually run it here; we
#    only verify the parser accepts the flag combination). We invoke with
#    --help so the script exits cleanly after arg parsing.
out=$("${INSTALL}" --install-deps --upgrade-deps --help 2>&1)
rc=$?
assert "--install-deps --upgrade-deps --help exits 0" [ "${rc}" -eq 0 ]

# 4. Unknown flag still rejected (no regression).
out=$("${INSTALL}" --install-deps-please 2>&1)
rc=$?
assert "unknown flag rejected" [ "${rc}" -ne 0 ]

printf '\n[test-install-deps-flag] %d passed, %d failed\n' "${PASS}" "${FAIL}"
if (( FAIL == 0 )); then
  echo "PASS — 0 errors, 0 warnings"
  exit 0
else
  echo "FAIL — ${FAIL} errors, 0 warnings"
  exit 1
fi
