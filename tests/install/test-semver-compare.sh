#!/usr/bin/env bash
#
# tests/install/test-semver-compare.sh — unit tests for _rc_semver_lt in
# scripts/lib/release-consumer.sh.
#
# Covers the SemVer 2.0 §11 canonical ordering chain:
#   1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta
#     < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0
# Plus core-version numeric ordering, build-metadata stripping, and
# equality (not-less-than) assertions.
#
# Runs under bash 3.2 (macOS /bin/bash) and newer bash.
# Follows script-output conventions (OK/ERROR labels, PASS/FAIL summary).
# Exits 1 on any failure so CI catches regressions.
#

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
LIB="${SCRIPT_DIR}/scripts/lib/release-consumer.sh"

# release-consumer.sh is sourced inside install.sh and relies on log()/
# warn()/err() being present in the calling shell. Define stubs so sourcing
# does not hard-fail on the missing helpers.
log()  { :; }
warn() { printf 'WARN  [test-stub] %s\n' "$*" >&2; }
err()  { printf 'ERROR [test-stub] %s\n' "$*" >&2; exit 1; }

# release-consumer.sh reads RELEASE_REPO as a readonly at file scope; we
# must not re-define it. Source the lib into a subshell to pick up
# _rc_semver_lt without polluting the test shell with rc_* globals that
# require a full install.sh environment.
# We source it here directly — the rc_* functions that need SCRIPT_DIR,
# PREFIX etc. are never called by this test.
# shellcheck source=../../scripts/lib/release-consumer.sh
. "$LIB" 2>/dev/null || { echo "ERROR [test-semver] cannot source ${LIB}" >&2; exit 2; }

# Verify _rc_semver_lt was picked up.
if ! command -v _rc_semver_lt >/dev/null 2>&1 && ! type _rc_semver_lt >/dev/null 2>&1; then
  echo "ERROR [test-semver] _rc_semver_lt not defined after sourcing ${LIB}" >&2
  exit 2
fi

# ---------------------------------------------------------------------------
# Counters and assertion helpers
# ---------------------------------------------------------------------------
PASS=0
FAIL=0
ERR_COUNT=0

assert_lt() {
  local name="$1" a="$2" b="$3"
  if _rc_semver_lt "$a" "$b"; then
    PASS=$((PASS + 1))
    printf 'OK    [semver-lt] %s  (%s < %s)\n' "$name" "$a" "$b"
  else
    FAIL=$((FAIL + 1))
    ERR_COUNT=$((ERR_COUNT + 1))
    printf 'ERROR [semver-lt] %s  expected %s < %s, got NOT less-than\n' "$name" "$a" "$b" >&2
  fi
}

assert_not_lt() {
  local name="$1" a="$2" b="$3"
  if ! _rc_semver_lt "$a" "$b"; then
    PASS=$((PASS + 1))
    printf 'OK    [semver-not-lt] %s  (%s not < %s)\n' "$name" "$a" "$b"
  else
    FAIL=$((FAIL + 1))
    ERR_COUNT=$((ERR_COUNT + 1))
    printf 'ERROR [semver-not-lt] %s  expected %s NOT < %s, got less-than\n' "$name" "$a" "$b" >&2
  fi
}

# ---------------------------------------------------------------------------
# §11 canonical ordering chain
# ---------------------------------------------------------------------------
printf 'INFO  SemVer §11 canonical chain\n'
assert_lt     "alpha < alpha.1"       "1.0.0-alpha"    "1.0.0-alpha.1"
assert_lt     "alpha.1 < alpha.beta"  "1.0.0-alpha.1"  "1.0.0-alpha.beta"
assert_lt     "alpha.beta < beta"     "1.0.0-alpha.beta" "1.0.0-beta"
assert_lt     "beta < beta.2"         "1.0.0-beta"     "1.0.0-beta.2"
assert_lt     "beta.2 < beta.11"      "1.0.0-beta.2"   "1.0.0-beta.11"
assert_lt     "beta.11 < rc.1"        "1.0.0-beta.11"  "1.0.0-rc.1"
assert_lt     "rc.1 < release"        "1.0.0-rc.1"     "1.0.0"

# ---------------------------------------------------------------------------
# Equality: same version is NOT less-than
# ---------------------------------------------------------------------------
printf 'INFO  Equality (not-less-than)\n'
assert_not_lt "1.0.0 = 1.0.0"         "1.0.0"        "1.0.0"
assert_not_lt "1.0.0-rc.1 = 1.0.0-rc.1" "1.0.0-rc.1" "1.0.0-rc.1"
assert_not_lt "1.2.3 = 1.2.3"         "1.2.3"        "1.2.3"

# ---------------------------------------------------------------------------
# Core X.Y.Z numeric ordering (not lexical: 1.10.0 > 1.9.0)
# ---------------------------------------------------------------------------
printf 'INFO  Core numeric ordering\n'
assert_lt     "1.9.0 < 1.10.0"        "1.9.0"        "1.10.0"
assert_lt     "1.0.9 < 1.0.10"        "1.0.9"        "1.0.10"
assert_lt     "0.9.0 < 1.0.0"         "0.9.0"        "1.0.0"
assert_lt     "1.0.0 < 2.0.0"         "1.0.0"        "2.0.0"
assert_lt     "1.0.0 < 1.1.0"         "1.0.0"        "1.1.0"
assert_lt     "1.0.0 < 1.0.1"         "1.0.0"        "1.0.1"
assert_not_lt "1.10.0 not < 1.9.0"    "1.10.0"       "1.9.0"
assert_not_lt "2.0.0 not < 1.99.99"   "2.0.0"        "1.99.99"

# ---------------------------------------------------------------------------
# Build metadata is ignored (stripped before compare)
# ---------------------------------------------------------------------------
printf 'INFO  Build metadata ignored\n'
assert_not_lt "1.0.0+build1 = 1.0.0"  "1.0.0+build1"  "1.0.0"
assert_not_lt "1.0.0 = 1.0.0+build2"  "1.0.0"         "1.0.0+build2"
assert_lt     "1.0.0-rc.1+b1 < 1.0.0" "1.0.0-rc.1+b1" "1.0.0"
assert_lt     "1.0.0-rc.1+b1 < 1.0.0+b2" "1.0.0-rc.1+b1" "1.0.0+b2"

# ---------------------------------------------------------------------------
# Prerelease: release > any same-core prerelease
# ---------------------------------------------------------------------------
printf 'INFO  Release > prerelease (§11)\n'
assert_lt     "1.0.0-alpha < 1.0.0"   "1.0.0-alpha"  "1.0.0"
assert_not_lt "1.0.0 not < 1.0.0-rc1" "1.0.0"        "1.0.0-rc1"
assert_lt     "1.0.0-1 < 1.0.0"       "1.0.0-1"      "1.0.0"
assert_not_lt "1.0.0 not < 1.0.0-1"   "1.0.0"        "1.0.0-1"

# ---------------------------------------------------------------------------
# Numeric token < alphanumeric token in prerelease (§11.4.1)
# ---------------------------------------------------------------------------
printf 'INFO  Numeric < alphanumeric in prerelease\n'
assert_lt     "1.0.0-1 < 1.0.0-alpha" "1.0.0-1"     "1.0.0-alpha"
assert_not_lt "1.0.0-alpha not < 1.0.0-1" "1.0.0-alpha" "1.0.0-1"

# ---------------------------------------------------------------------------
# Fewer prerelease tokens < more tokens when shared tokens equal
# ---------------------------------------------------------------------------
printf 'INFO  Fewer tokens < more tokens\n'
assert_lt     "1.0.0-alpha < 1.0.0-alpha.1" "1.0.0-alpha" "1.0.0-alpha.1"
assert_not_lt "1.0.0-alpha.1 not < 1.0.0-alpha" "1.0.0-alpha.1" "1.0.0-alpha"

# ---------------------------------------------------------------------------
# Downgrade scenarios (pairs from rc_enforce_downgrade_defense call sites)
# ---------------------------------------------------------------------------
printf 'INFO  Downgrade scenarios\n'
assert_lt     "old < new release"      "1.0.0"   "1.1.0"
assert_not_lt "same version not lt"    "1.1.0"   "1.1.0"
assert_lt     "prerelease < release"   "1.1.0-rc.1" "1.1.0"
assert_not_lt "release not < prerelease" "1.1.0"  "1.1.0-rc.1"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
printf '\n==================================\n'
if [ "$ERR_COUNT" -eq 0 ]; then
  printf 'PASS — 0 errors, 0 warnings\n'
  exit 0
else
  printf 'FAIL — %d errors, 0 warnings\n' "$ERR_COUNT"
  exit 1
fi
