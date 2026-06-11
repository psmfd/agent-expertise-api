#!/usr/bin/env bash
#
# tests/uninstall/test-prefix-ancestors.sh
#
# Unit + black-box tests for validate_prefix_ancestors in
# scripts/lib/prefix-validation.sh (issue #242 — prefix-parent TOCTOU
# guard for --system uninstalls).
#
# Layer 1 (unit): sources the lib in a subshell where `uname` and `stat`
# are shadowed by shell functions, so root-owned / group-writable /
# sticky ancestor permutations are simulated deterministically on any
# host without sudo. Both the Linux (stat -c '%u %a') and macOS
# (stat -f '%u %Mp%Lp') branches are exercised this way. The symlink
# check ([ -L ]) runs against the real filesystem, so the symlink case
# builds a real symlinked ancestor under a scratch dir.
#
# Layer 2 (black-box): runs scripts/uninstall.sh --system --dry-run
# against a HOME-local scratch prefix whose ancestor chain is not
# root-owned, and asserts the ancestor guard fires before any
# "DRY-RUN: rm" plan line is emitted.
#
# Run from repo root:
#   bash tests/uninstall/test-prefix-ancestors.sh
#
# Exit 0 = PASS, 1 = at least one assertion failed.
#

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LIB="${ROOT}/scripts/lib/prefix-validation.sh"
SCRIPT="${ROOT}/scripts/uninstall.sh"
[[ -f "${LIB}" ]] || { echo "FAIL: ${LIB} not found" >&2; exit 1; }
[[ -x "${SCRIPT}" ]] || { echo "FAIL: ${SCRIPT} not executable" >&2; exit 1; }

PASS=0
FAIL=0

# ---------------------------------------------------------------------------
# Layer 1 — unit cases with shadowed uname/stat
#
# run_case NAME FAKE_OS WANT_RC WANT_SNIPPET PREFIX TABLE
#
# TABLE is newline-separated "path uid perms" rows consumed by the fake
# stat; perms uses the OS-native shape ("755"/"1777" for Linux %a,
# "0755"/"1777" for macOS %Mp%Lp). WANT_RC 0 expects success; non-zero
# expects the guard to refuse with WANT_SNIPPET somewhere in the output.
# ---------------------------------------------------------------------------
run_case() {
  local name="$1" fake_os="$2" want_rc="$3" want_snip="$4" prefix="$5" table="$6"
  local out rc
  out=$(
    FAKE_OS="${fake_os}" FAKE_STAT_TABLE="${table}" bash -c '
      set -u
      err() { printf "ERROR [ancestor-guard] %s\n" "$1" >&2; exit 1; }
      uname() { echo "${FAKE_OS}"; }
      stat() {
        # Lib calls: stat -c FMT PATH (Linux) | stat -f FMT PATH (macOS).
        # PATH is always $3.
        local _path="$3" _line _p _u _m
        while IFS= read -r _line; do
          [ -n "${_line}" ] || continue
          _p="${_line%% *}"; _line="${_line#* }"
          _u="${_line%% *}"; _m="${_line#* }"
          if [ "${_p}" = "${_path}" ]; then
            printf "%s %s\n" "${_u}" "${_m}"
            return 0
          fi
        done <<TBL
${FAKE_STAT_TABLE}
TBL
        return 1
      }
      . "$1"
      validate_prefix_ancestors "$2"
      echo UNIT-OK
    ' _ "${LIB}" "${prefix}" 2>&1
  )
  rc=$?
  if [ "${want_rc}" = "0" ]; then
    if [ "${rc}" -eq 0 ] && printf '%s' "${out}" | grep -q "UNIT-OK"; then
      PASS=$((PASS + 1)); printf 'PASS: %s\n' "${name}"
    else
      FAIL=$((FAIL + 1)); printf 'FAIL: %s (rc=%d)\n%s\n' "${name}" "${rc}" "${out}" >&2
    fi
  else
    if [ "${rc}" -ne 0 ] && printf '%s' "${out}" | grep -qF "${want_snip}"; then
      PASS=$((PASS + 1)); printf 'PASS: %s\n' "${name}"
    else
      FAIL=$((FAIL + 1)); printf 'FAIL: %s (rc=%d, wanted snippet "%s")\n%s\n' "${name}" "${rc}" "${want_snip}" "${out}" >&2
    fi
  fi
}

# Ancestors of /xtest/parent/expertise-api are: / , /xtest , /xtest/parent.
# /xtest does not exist on any host, so the real-fs [ -L ] check is inert
# for every unit case and the faked stat fully controls the outcome.

# --- Linux branch -----------------------------------------------------------
run_case "linux: all ancestors root 755 pass" Linux 0 "" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 755
/xtest/parent 0 755"

run_case "linux: non-root ancestor refused" Linux 1 "not owned by root" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 755
/xtest/parent 1000 755"

run_case "linux: group-writable ancestor without sticky refused" Linux 1 "group-writable without sticky" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 775
/xtest/parent 0 755"

run_case "linux: group-writable ancestor WITH sticky passes" Linux 0 "" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 1775
/xtest/parent 0 755"

# 757 not 777: a 777 dir trips the group-write check first (also correct);
# 757 isolates the world-write branch.
run_case "linux: world-writable ancestor without sticky refused" Linux 1 "world-writable without sticky" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 757
/xtest/parent 0 755"

run_case "linux: 777 ancestor refused (group-write check fires first)" Linux 1 "group-writable without sticky" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 777
/xtest/parent 0 755"

run_case "linux: world-writable ancestor WITH sticky (tmp-like 1777) passes" Linux 0 "" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 1777
/xtest/parent 0 755"

run_case "linux: setgid-only digit (2) is not sticky — group-write 2775 refused" Linux 1 "group-writable without sticky" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 2775
/xtest/parent 0 755"

run_case "linux: sticky+setgid digit (3) counts as sticky — 3775 passes" Linux 0 "" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest 0 3775
/xtest/parent 0 755"

run_case "linux: unstatable ancestor refused" Linux 1 "cannot stat ancestor" \
  "/xtest/parent/expertise-api" \
"/ 0 755
/xtest/parent 0 755"

# --- macOS branch (stat -f '%u %Mp%Lp' always yields a 4-digit octal) -------
run_case "macos: all ancestors root 0755 pass" Darwin 0 "" \
  "/xtest/parent/expertise-api" \
"/ 0 0755
/xtest 0 0755
/xtest/parent 0 0755"

run_case "macos: non-root ancestor refused" Darwin 1 "not owned by root" \
  "/xtest/parent/expertise-api" \
"/ 0 0755
/xtest 501 0755
/xtest/parent 0 0755"

run_case "macos: group-writable without sticky refused" Darwin 1 "group-writable without sticky" \
  "/xtest/parent/expertise-api" \
"/ 0 0755
/xtest 0 0775
/xtest/parent 0 0755"

run_case "macos: world-writable with sticky (1777) passes" Darwin 0 "" \
  "/xtest/parent/expertise-api" \
"/ 0 0755
/xtest 0 1777
/xtest/parent 0 0755"

# --- Symlinked ancestor (real filesystem [ -L ]) -----------------------------
# Build SCRATCH/real (dir) and SCRATCH/link -> real, then validate a prefix
# under .../link/. Every real ancestor on the way down gets a permissive
# fake-stat row; the [ -L ] check must fire on a symlinked component before
# stat is even consulted for it. On macOS the scratch path may itself sit
# below a system symlink (/tmp -> private/tmp), in which case the guard
# correctly fires there first — either way the refusal names a symlink.
SCRATCH="${TMPDIR:-/tmp}/expertise-api-test-ancestors.$$"
mkdir -p "${SCRATCH}/real"
ln -s "${SCRATCH}/real" "${SCRATCH}/link"
trap 'rm -rf "${SCRATCH}"' EXIT

_sym_prefix="${SCRATCH}/link/expertise-api"
_sym_table="/ 0 755"
_p="${_sym_prefix%/*}"
while [ -n "${_p}" ] && [ "${_p}" != "/" ]; do
  _sym_table="${_sym_table}
${_p} 0 755"
  _p="${_p%/*}"
  [ -z "${_p}" ] && _p="/"
done

run_case "symlinked ancestor refused (real lstat)" "$(uname -s)" 1 "is a symlink" \
  "${_sym_prefix}" "${_sym_table}"

# ---------------------------------------------------------------------------
# Layer 2 — black-box: uninstall.sh --system --dry-run refuses a prefix
# whose ancestor chain is not root-owned, before emitting any plan line.
#
# A HOME-local scratch prefix guarantees at least one non-root-owned
# ancestor (the user's own directories) on every host, including CI
# runners. On macOS the chain may instead trip the symlink check at a
# system symlink — both are ancestor-guard refusals, so the assertion
# accepts any "--system ancestor" error.
# ---------------------------------------------------------------------------
if [ "$(uname -s)" = "Darwin" ]; then
  # uninstall.sh hard-errors on --system on macOS (parity with install.sh,
  # #291/#145), so the ancestor guard is unreachable black-box here. The
  # Linux CI job exercises this layer; the unit layer above already covers
  # both OS stat branches.
  printf 'SKIP: black-box --system layer (macOS rejects --system before the ancestor guard; covered on Linux CI)\n'
else
  BB_SCRATCH="${HOME}/.expertise-api-test-ancestors.$$"
  mkdir -p "${BB_SCRATCH}/expertise-api/bin"
  trap 'rm -rf "${SCRATCH}" "${BB_SCRATCH}"' EXIT

  bb_out=$(bash "${SCRIPT}" --system --allow-system-prefix --dry-run --yes \
    --prefix "${BB_SCRATCH}/expertise-api" 2>&1)
  bb_rc=$?
  if [ "${bb_rc}" -ne 0 ] \
     && printf '%s' "${bb_out}" | grep -q -- "--system ancestor" \
     && ! printf '%s' "${bb_out}" | grep -q "DRY-RUN: rm"; then
    PASS=$((PASS + 1))
    printf 'PASS: black-box --system --dry-run refuses non-root ancestor chain\n'
  else
    FAIL=$((FAIL + 1))
    printf 'FAIL: black-box --system --dry-run (rc=%d)\n%s\n' "${bb_rc}" "${bb_out}" >&2
  fi
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo "=================================="
if [ "${FAIL}" -eq 0 ]; then
  printf 'PASS — %d passed, 0 failed\n' "${PASS}"
  exit 0
else
  printf 'FAIL — %d passed, %d failed\n' "${PASS}" "${FAIL}" >&2
  exit 1
fi
