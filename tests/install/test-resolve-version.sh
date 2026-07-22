#!/usr/bin/env bash
#
# tests/install/test-resolve-version.sh — regression tests for #440:
# rc_resolve_version's stdout IS its return value (command substitution at
# the call site), so an stdout-printing log() — exactly what install.sh
# defines — must never leak into the resolved version string.
#
# Covers:
#   1. explicit-version passthrough (v-prefix stripped, stdout pure)
#   2. --version latest path with a stubbed curl: stdout carries ONLY the
#      resolved version even when log() writes to stdout (the #440 bug shape)
#   3. rc_assert_semver accepts valid semver (incl. prerelease/build)
#   4. rc_assert_semver rejects a log-polluted string and plain garbage
#
# Runs under bash 3.2 (macOS /bin/bash) and newer bash.
# Follows script-output conventions (OK/ERROR labels, PASS/FAIL summary).
#

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
LIB="${SCRIPT_DIR}/scripts/lib/release-consumer.sh"

# Reproduce install.sh's REAL log() (stdout, not a silent stub) — the whole
# point of this suite is proving resolver stdout stays pure despite it.
log()  { printf '[install] %s\n' "$1"; }
warn() { printf 'WARN  [test-stub] %s\n' "$*" >&2; }
# err in the library aborts; for assert_semver rejection tests we need it
# non-fatal, so record and return via a flag file (subshell-safe).
ERR_FLAG=""
err()  { ERR_FLAG="yes"; printf 'ERROR [test-stub] %s\n' "$*" >&2; return 1; }

# shellcheck source=../../scripts/lib/release-consumer.sh
. "$LIB" 2>/dev/null || { echo "ERROR [test-resolve-version] cannot source ${LIB}" >&2; exit 2; }

for fn in rc_resolve_version rc_assert_semver; do
  if ! type "$fn" >/dev/null 2>&1; then
    echo "ERROR [test-resolve-version] ${fn} not defined after sourcing ${LIB}" >&2
    exit 2
  fi
done

PASS=0
FAIL=0
ok()   { PASS=$((PASS+1)); printf 'OK    %s\n' "$1"; }
fail() { FAIL=$((FAIL+1)); printf 'ERROR %s\n' "$1" >&2; }

# --- 1. explicit version passthrough ---------------------------------------
v=$(rc_resolve_version "v1.2.3")
[ "$v" = "1.2.3" ] && ok "explicit v-prefixed version resolves to bare semver" \
                   || fail "explicit version: expected '1.2.3', got '$v'"

v=$(rc_resolve_version "1.2.3")
[ "$v" = "1.2.3" ] && ok "explicit bare version passes through" \
                   || fail "explicit bare version: expected '1.2.3', got '$v'"

# --- 2. latest path: stdout purity under an stdout log() --------------------
# Stub curl to write a canned Releases API response to the -o target; jq is
# real (required by the code path and present in CI/dev environments).
curl() {
  local out=""
  while [ $# -gt 0 ]; do
    if [ "$1" = "-o" ]; then out="$2"; shift; fi
    shift
  done
  [ -n "$out" ] || return 1
  printf '{"tag_name":"v9.9.9"}' > "$out"
}

v=$(rc_resolve_version "latest")
if [ "$v" = "9.9.9" ]; then
  ok "latest path returns pure version on stdout (log line did not leak)"
else
  fail "latest path stdout polluted or wrong: expected '9.9.9', got '$v'"
fi
unset -f curl

# --- 3. rc_assert_semver accepts -------------------------------------------
for good in "1.5.0" "0.1.0" "10.20.30" "1.0.0-rc.1" "1.0.0+build.7"; do
  ERR_FLAG=""
  if rc_assert_semver "$good" 2>/dev/null && [ -z "$ERR_FLAG" ]; then
    ok "assert_semver accepts '$good'"
  else
    fail "assert_semver rejected valid '$good'"
  fi
done

# --- 4. rc_assert_semver rejects --------------------------------------------
polluted="$(printf '[install] resolving --version latest via GitHub Releases API\n1.5.0')"
for bad in "$polluted" "latest" "" "1.5" "v1.5.0" "1.5.0; rm -rf /"; do
  ERR_FLAG=""
  if rc_assert_semver "$bad" 2>/dev/null && [ -z "$ERR_FLAG" ]; then
    fail "assert_semver accepted invalid '$(printf '%s' "$bad" | head -1)'"
  else
    ok "assert_semver rejects '$(printf '%s' "$bad" | head -1 | cut -c1-50)'"
  fi
done

printf 'PASS %d FAIL %d\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
