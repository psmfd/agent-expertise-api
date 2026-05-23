#!/usr/bin/env bash
#
# test-crlf-detector.sh — exercise scripts/install.sh's secrets.env CRLF
# detector (issue #223). The detector lives in preflight() so we drive the
# script with --skip-preflight off and stub everything that would otherwise
# require a working environment (dotnet, lsof/nc, mkdir into ${PREFIX}).
#
# We do NOT execute install.sh end-to-end here; we extract and exercise the
# detector function in isolation by sourcing it under controlled conditions.
# The full install path is covered by test-upgrade-roundtrip.sh.
#

set -uo pipefail

SCRIPT="$(cd "$(dirname "$0")/../.." && pwd)/scripts/install.sh"
[[ -x "${SCRIPT}" ]] || { echo "FAIL: install.sh missing"; exit 2; }

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/install-crlf.XXXXXX")"
trap 'rm -rf "${SCRATCH}"' EXIT

PASS=0
FAIL=0

# ---------------------------------------------------------------------------
# Helpers — source install.sh's detector under a controlled SECRETS_FILE.
# install.sh has a `main "$@"` trailer that would fire on sourcing, so we
# extract only the two functions we need.
# ---------------------------------------------------------------------------
detector_lib="${SCRATCH}/detector.sh"
awk '
  /^check_secrets_line_endings\(\)/,/^}$/  { print; next }
  /^fix_secrets_line_endings\(\)/,/^}$/    { print; next }
' "${SCRIPT}" > "${detector_lib}"

# Stub log/warn/err for sourcing context.
cat > "${SCRATCH}/stubs.sh" <<'EOF'
log()  { printf '[detector] %s\n' "$1"; }
warn() { printf '[detector] WARN: %s\n' "$1" >&2; }
err()  { printf '[detector] ERROR: %s\n' "$1" >&2; return 1; }
FIX_LINE_ENDINGS=0
EOF

run_detector() {
  ( set +e
    # shellcheck disable=SC1090,SC1091
    . "${SCRATCH}/stubs.sh"
    # shellcheck disable=SC1090,SC1091
    . "${detector_lib}"
    SECRETS_FILE="$1" FIX_LINE_ENDINGS="${2:-0}" check_secrets_line_endings
    echo "rc=$?"
  )
}

# ---------------------------------------------------------------------------
# Case 1: no secrets file → no-op success
# ---------------------------------------------------------------------------
out=$(run_detector "${SCRATCH}/nonexistent")
if [[ "${out}" == *"rc=0"* ]]; then
  PASS=$((PASS+1))
else
  printf 'FAIL: missing secrets file should be no-op (got: %s)\n' "${out}" >&2
  FAIL=$((FAIL+1))
fi

# ---------------------------------------------------------------------------
# Case 2: LF-only secrets file → success
# ---------------------------------------------------------------------------
lf_file="${SCRATCH}/secrets-lf.env"
printf 'ConnectionStrings__DefaultConnection="Host=localhost"\n# comment\n' > "${lf_file}"
chmod 600 "${lf_file}"
out=$(run_detector "${lf_file}")
if [[ "${out}" == *"rc=0"* ]]; then
  PASS=$((PASS+1))
else
  printf 'FAIL: LF-only file should pass (got: %s)\n' "${out}" >&2
  FAIL=$((FAIL+1))
fi

# ---------------------------------------------------------------------------
# Case 3: CRLF without --fix-line-endings → fail with line number, no leak
# ---------------------------------------------------------------------------
crlf_file="${SCRATCH}/secrets-crlf.env"
printf '# header\r\nConnectionStrings__DefaultConnection="Host=localhost;Password=SECRET_TOKEN_DO_NOT_LEAK"\r\n' > "${crlf_file}"
chmod 600 "${crlf_file}"

out=$(run_detector "${crlf_file}" 0 2>&1)
if [[ "${out}" == *"CRLF line endings detected"* ]] \
   && [[ "${out}" == *":1"* || "${out}" == *":2"* ]] \
   && [[ "${out}" != *"SECRET_TOKEN_DO_NOT_LEAK"* ]] \
   && [[ "${out}" == *"rc=1"* ]]; then
  PASS=$((PASS+1))
else
  printf 'FAIL: CRLF detector should fail with line number and no value leak. Got:\n%s\n' "${out}" >&2
  FAIL=$((FAIL+1))
fi

# ---------------------------------------------------------------------------
# Case 4: CRLF with --fix-line-endings → fixed in place, mode 600 preserved
# ---------------------------------------------------------------------------
fix_file="${SCRATCH}/secrets-fix.env"
printf 'A=1\r\nB=2\r\n' > "${fix_file}"
chmod 600 "${fix_file}"
out=$(run_detector "${fix_file}" 1 2>&1)
if [[ "${out}" == *"rc=0"* ]] && ! LC_ALL=C grep -q $'\r' "${fix_file}"; then
  PASS=$((PASS+1))
else
  printf 'FAIL: --fix-line-endings should strip CR and exit 0. Got:\n%s\nFile bytes:\n' "${out}" >&2
  od -c "${fix_file}" | head -3 >&2
  FAIL=$((FAIL+1))
fi

# Mode 600 preserved?
if [[ "$(stat -f '%Lp' "${fix_file}" 2>/dev/null || stat -c '%a' "${fix_file}")" == "600" ]]; then
  PASS=$((PASS+1))
else
  printf 'FAIL: --fix-line-endings did not preserve mode 600 (got: %s)\n' \
    "$(stat -f '%Lp' "${fix_file}" 2>/dev/null || stat -c '%a' "${fix_file}")" >&2
  FAIL=$((FAIL+1))
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
TOTAL=$((PASS+FAIL))
: "${TOTAL:?}"  # silence SC2034
printf '\n[test-crlf-detector] %d passed, %d failed\n' "${PASS}" "${FAIL}"
if (( FAIL > 0 )); then
  printf 'FAIL — %d errors, 0 warnings\n' "${FAIL}"
  exit 1
fi
printf 'PASS — 0 errors, 0 warnings\n'
exit 0
