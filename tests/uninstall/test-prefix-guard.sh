#!/usr/bin/env bash
#
# tests/uninstall/test-prefix-guard.sh
#
# Black-box guard test for scripts/uninstall.sh. Two layers:
#
#   1. Primary: --dry-run output never contains "DRY-RUN: rm" for any
#      prefix that should be rejected. Asserts the validation gate fires
#      before the action dispatcher.
#
#   2. Secondary: PATH-shimmed `rm`, `launchctl`, `systemctl` capture any
#      command the script tried to run against a blocked prefix when
#      --dry-run is not in play. Second line of defense in case a future
#      refactor moves validation after the action loop.
#
# Run from repo root:
#   bash tests/uninstall/test-prefix-guard.sh
#
# Exit 0 = PASS, 1 = at least one assertion failed.
#

set -uo pipefail

SCRIPT="$(cd "$(dirname "$0")/../.." && pwd)/scripts/uninstall.sh"
[[ -x "$SCRIPT" ]] || { echo "FAIL: $SCRIPT not executable" >&2; exit 1; }

PASS=0
FAIL=0

# Use a HOME-local scratch dir; /var/folders (macOS mktemp default) is
# blocked by the prefix-match guard which would invalidate the layout step.
SCRATCH="${TMPDIR:-/tmp}/expertise-api-test-prefix-guard.$$"
case "${SCRATCH}" in
  /var/*|/private/*)
    SCRATCH="/tmp/expertise-api-test-prefix-guard.$$" ;;
esac
mkdir -p "${SCRATCH}/expertise-api/bin"
touch "${SCRATCH}/expertise-api/bin/dummy"
trap 'rm -rf "${SCRATCH}"' EXIT

SHIM_DIR="${SCRATCH}/shims"
SHIM_LOG="${SCRATCH}/shim.log"
mkdir -p "${SHIM_DIR}"
for cmd in rm launchctl systemctl; do
  cat > "${SHIM_DIR}/${cmd}" <<EOF
#!/usr/bin/env bash
printf '%s %s\n' "${cmd}" "\$*" >> "${SHIM_LOG}"
exit 0
EOF
  chmod +x "${SHIM_DIR}/${cmd}"
done

# ---------------------------------------------------------------------------
# Assertion helpers
# ---------------------------------------------------------------------------

# assert_dry_blocked PREFIX [EXTRA_ARGS...]
#
# Runs the script with --dry-run and asserts:
#   (a) exit code is non-zero, AND
#   (b) stdout/stderr contains no "DRY-RUN: rm" line (validation rejected
#       before the action loop emitted any planned rm).
assert_dry_blocked() {
  local prefix="$1"; shift
  local label="prefix='${prefix}'"
  local out
  if out=$("${SCRIPT}" --prefix "${prefix}" --yes --purge --dry-run "$@" 2>&1); then
    printf 'FAIL: %s — expected non-zero exit but got 0\n' "${label}" >&2
    printf '%s\n' "${out}" | sed 's/^/  | /'
    FAIL=$((FAIL+1)); return
  fi
  if printf '%s\n' "${out}" | grep -q 'DRY-RUN: rm'; then
    printf 'FAIL: %s — guard fired but DRY-RUN: rm appeared in output\n' "${label}" >&2
    printf '%s\n' "${out}" | sed 's/^/  | /'
    FAIL=$((FAIL+1)); return
  fi
  PASS=$((PASS+1))
}

# assert_dry_allowed PREFIX [EXTRA_ARGS...]
#
# Runs the script with --dry-run on a path the guard should accept and
# asserts (a) exit 0 and (b) at least one DRY-RUN: line was emitted
# (proves the validation gate passed and the planner ran).
assert_dry_allowed() {
  local prefix="$1"; shift
  local label="prefix='${prefix}'"
  local out
  if ! out=$("${SCRIPT}" --prefix "${prefix}" --yes --purge --dry-run "$@" 2>&1); then
    printf 'FAIL: %s — expected exit 0 but guard rejected\n' "${label}" >&2
    printf '%s\n' "${out}" | sed 's/^/  | /'
    FAIL=$((FAIL+1)); return
  fi
  # Validation passed iff the planner reached the "plan:" line. We do NOT
  # require DRY-RUN: rm because the planner correctly emits "not present —
  # skipping" for non-existent dirs (these prefixes are not real installs).
  if ! printf '%s\n' "${out}" | grep -q '^\[uninstall\] plan:$'; then
    printf 'FAIL: %s — accepted but planner never ran\n' "${label}" >&2
    printf '%s\n' "${out}" | sed 's/^/  | /'
    FAIL=$((FAIL+1)); return
  fi
  PASS=$((PASS+1))
}

# assert_shim_silent PREFIX [EXTRA_ARGS...]
#
# Secondary defense layer: runs with --yes (no --dry-run) under
# PATH-shimmed rm/launchctl/systemctl. Asserts (a) script exited
# non-zero and (b) shim log is empty (no action was attempted before
# validation refused).
assert_shim_silent() {
  local prefix="$1"; shift
  local label="prefix='${prefix}' (shim layer)"
  : > "${SHIM_LOG}"
  local out
  if out=$(PATH="${SHIM_DIR}:${PATH}" "${SCRIPT}" --prefix "${prefix}" --yes --purge "$@" 2>&1); then
    printf 'FAIL: %s — expected non-zero exit but got 0\n' "${label}" >&2
    printf '%s\n' "${out}" | sed 's/^/  | /'
    FAIL=$((FAIL+1)); return
  fi
  if [[ -s "${SHIM_LOG}" ]]; then
    printf 'FAIL: %s — shim log non-empty (guard let actions through)\n' "${label}" >&2
    sed 's/^/  | /' < "${SHIM_LOG}" >&2
    FAIL=$((FAIL+1)); return
  fi
  PASS=$((PASS+1))
}

# assert_normalizes_to PREFIX EXPECTED
#
# Runs --dry-run and asserts the planner echoed "PREFIX     = EXPECTED",
# proving normalization stripped trailing slashes / collapsed //.
assert_normalizes_to() {
  local prefix="$1" expected="$2"
  local out
  out=$("${SCRIPT}" --prefix "${prefix}" --dry-run 2>&1) || true
  if printf '%s\n' "${out}" | grep -qE "^\[uninstall\][[:space:]]+PREFIX[[:space:]]+= ${expected}$"; then
    PASS=$((PASS+1))
  else
    printf 'FAIL: normalization — input %q expected PREFIX=%q\n' "${prefix}" "${expected}" >&2
    printf '%s\n' "${out}" | grep PREFIX | sed 's/^/  | /' >&2
    FAIL=$((FAIL+1))
  fi
}

# ---------------------------------------------------------------------------
# Test cases — blocked
# ---------------------------------------------------------------------------

# Exact roots
assert_dry_blocked /
assert_dry_blocked /home
assert_dry_blocked /Users
assert_dry_blocked /opt
assert_dry_blocked /usr/local
assert_dry_blocked /mnt
assert_dry_blocked /tmp

# Prefix-match catastrophic roots (any descendant blocked even with
# --allow-system-prefix)
assert_dry_blocked /bin/expertise-api
assert_dry_blocked /etc/expertise-api
assert_dry_blocked /sbin/expertise-api
assert_dry_blocked /lib/expertise-api
assert_dry_blocked /System/foo --allow-system-prefix
assert_dry_blocked /Library/foo --allow-system-prefix
assert_dry_blocked /private/var/expertise-api

# Traversal attack
assert_dry_blocked /Users/x/.local/../../etc

# Component check
assert_dry_blocked /custom/svc-data

# --allow-system-prefix relaxes only the component check, not the blocklist
assert_dry_blocked /etc/something --allow-system-prefix
assert_dry_blocked / --allow-system-prefix
assert_dry_blocked /home --allow-system-prefix

# ---------------------------------------------------------------------------
# Test cases — allowed
# ---------------------------------------------------------------------------

# Default-style HOME-adjacent install
assert_dry_allowed "${SCRATCH}/expertise-api"

# Descendants of exact-only parents (these were the regression that nearly
# shipped: /Users prefix-match would block every real macOS install)
assert_dry_allowed /home/foo/expertise-api
assert_dry_allowed /Users/me/svc/expertise-api
assert_dry_allowed /opt/expertise-api
assert_dry_allowed /usr/local/expertise-api

# --allow-system-prefix unlocks the component check
assert_dry_allowed /custom/svc-data --allow-system-prefix

# ---------------------------------------------------------------------------
# Normalization
# ---------------------------------------------------------------------------

assert_normalizes_to "${SCRATCH}/expertise-api/"     "${SCRATCH}/expertise-api"
assert_normalizes_to "${SCRATCH}//expertise-api///"  "${SCRATCH}/expertise-api"
assert_normalizes_to "${SCRATCH}/expertise-api///"   "${SCRATCH}/expertise-api"

# ---------------------------------------------------------------------------
# Secondary defense — PATH shims should never log a command for blocked
# prefixes, even if --dry-run is omitted.
# ---------------------------------------------------------------------------

assert_shim_silent /
assert_shim_silent /etc/expertise-api
assert_shim_silent /Users/x/.local/../../etc
assert_shim_silent /custom/svc-data

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

printf '\n[test-prefix-guard] %d passed, %d failed\n' "${PASS}" "${FAIL}"
if (( FAIL == 0 )); then
  echo 'PASS — 0 errors, 0 warnings'
  exit 0
else
  echo "FAIL — ${FAIL} errors, 0 warnings"
  exit 1
fi
