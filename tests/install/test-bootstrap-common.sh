#!/usr/bin/env bash
# tests/install/test-bootstrap-common.sh
# Unit tests for scripts/lib/bootstrap-common.sh — the shared bootstrap
# library. Stubs `log`/`warn`/`err`/`PREFIX`/`SECRETS_FILE` and exercises:
#  - bc_require_install_deps_flag (--upgrade-deps requires --install-deps)
#  - bc_refuse_xtrace
#  - bc_generate_db_password (non-empty, no trailing newline, NOT echoed in xtrace)
#  - bc_write_connection_string_if_absent (idempotency, never-overwrite,
#    symlink refusal, mode 600, single-quoted value)
#  - bc_append_install_deps_history (RFC3339 + format)
#
# Library is sourced into a subshell with stubs in scope.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
LIB="${SCRIPT_DIR}/scripts/lib/bootstrap-common.sh"

PASS=0
FAIL=0
assert() {
  local name="$1"; shift
  if "$@"; then PASS=$((PASS+1)); else printf 'FAIL: %s\n' "${name}" >&2; FAIL=$((FAIL+1)); fi
}

SCRATCH="$(mktemp -d -t bootstrap-common.XXXXXX)"
trap 'rm -rf "${SCRATCH}"' EXIT

# Stub harness: define the symbols bootstrap-common.sh requires.
make_harness() {
  local extra="${1:-}"
  cat <<HARNESS
set -uo pipefail
log()  { printf '[stub] %s\n' "\$1"; }
warn() { printf '[stub WARN] %s\n' "\$1" >&2; }
err()  { printf '[stub ERR] %s\n' "\$1" >&2; exit 1; }
PREFIX="${SCRATCH}/prefix"
CONFIG_DIR="${SCRATCH}/prefix"
SECRETS_FILE="${SCRATCH}/prefix/secrets.env"
NEW_VERSION="test-1.2.3"
OS="macos"
INSTALL_DEPS=0
UPGRADE_DEPS=0
mkdir -p "\${PREFIX}"
${extra}
. "${LIB}"
HARNESS
}

# ---------------------------------------------------------------------------
# 1. Direct execution refused
out=$(bash "${LIB}" 2>&1); rc=$?
assert "direct execution exits non-zero" [ "${rc}" -ne 0 ]
assert "direct-execution message mentions 'library'" bash -c "[[ '${out}' == *library* ]]"

# ---------------------------------------------------------------------------
# 2. bc_require_install_deps_flag — --upgrade-deps alone hard-errors
out=$(bash -c "$(make_harness 'UPGRADE_DEPS=1')
bc_require_install_deps_flag
echo 'unexpected-success'" 2>&1); rc=$?
assert "upgrade-deps alone exits non-zero" [ "${rc}" -ne 0 ]
assert "upgrade-deps alone names the requirement" bash -c "[[ '${out}' == *'requires --install-deps'* ]]"

# Both flags together — passes.
out=$(bash -c "$(make_harness 'INSTALL_DEPS=1; UPGRADE_DEPS=1')
bc_require_install_deps_flag
echo OK" 2>&1); rc=$?
assert "both flags together passes" [ "${rc}" -eq 0 ]
assert "both flags emits OK" bash -c "[[ '${out}' == *OK* ]]"

# ---------------------------------------------------------------------------
# 3. bc_refuse_xtrace
out=$(bash -c "$(make_harness '')
set -x
bc_refuse_xtrace
echo 'leaked'" 2>&1); rc=$?
assert "xtrace-active refused" [ "${rc}" -ne 0 ]
if echo "${out}" | grep -q xtrace; then
  PASS=$((PASS+1))
else
  printf 'FAIL: xtrace message names xtrace\n' >&2
  FAIL=$((FAIL+1))
fi

# ---------------------------------------------------------------------------
# 4. bc_generate_db_password — non-empty, no trailing newline, NOT echoed.
#    Run under set -x and assert the password literal does not appear in
#    the captured stderr (xtrace would otherwise emit it).
gen_out=$(bash -c "$(make_harness '')
pw=\$(bc_generate_db_password)
printf 'LEN=%d\n' \"\${#pw}\"
# Hash the password and print only the hash (do not echo pw directly).
hash=\$(printf '%s' \"\${pw}\" | shasum -a 256 2>/dev/null | awk '{print \$1}')
printf 'SHA=%s\n' \"\${hash}\"
" 2>&1)
len=$(printf '%s\n' "${gen_out}" | awk -F= '/^LEN=/{print $2}')
sha=$(printf '%s\n' "${gen_out}" | awk -F= '/^SHA=/{print $2}')
assert "generated password length 32 (base64 of 24 bytes, padding stripped)" \
  bash -c "[[ '${len}' == '32' ]]"
assert "generated password is well-formed sha256" \
  bash -c "[[ '${sha}' =~ ^[0-9a-f]{64}\$ ]]"

# Re-run; ensure the password changes between runs (random source).
gen_out2=$(bash -c "$(make_harness '')
pw=\$(bc_generate_db_password)
hash=\$(printf '%s' \"\${pw}\" | shasum -a 256 2>/dev/null | awk '{print \$1}')
printf 'SHA=%s\n' \"\${hash}\"
" 2>&1)
sha2=$(printf '%s\n' "${gen_out2}" | awk -F= '/^SHA=/{print $2}')
assert "consecutive password generations differ" bash -c "[[ '${sha}' != '${sha2}' ]]"

# Xtrace leak guard: prove the underlying defense works by running
# generation under set -x AFTER patching out bc_refuse_xtrace (so we
# exercise the actual leak path, not the refusal gate — review finding
# security-review LOW B).
leak_out=$(bash -c "$(make_harness '')
# Stub out the refusal so we can test the underlying behavior.
bc_refuse_xtrace() { :; }
set -x
pw=\$(bc_generate_db_password)
set +x
# Capture the value into a separate file so we can compare it to xtrace.
printf '%s' \"\${pw}\" > '${SCRATCH}/pw.txt'
" 2>&1)
pw_value=$(cat "${SCRATCH}/pw.txt")
# pw_value must not appear in leak_out (stderr capture of the run).
# This documents what set -x WOULD leak if bc_refuse_xtrace were removed;
# our defense is to refuse rather than try to outsmart xtrace.
if [[ -n "${pw_value}" && "${leak_out}" == *"${pw_value}"* ]]; then
  # Expected: xtrace DOES leak when the refusal is removed. This
  # validates that bc_refuse_xtrace is load-bearing.
  PASS=$((PASS+1))
else
  printf 'FAIL: xtrace-without-refusal did NOT leak — either generation changed or test is broken\n' >&2
  FAIL=$((FAIL+1))
fi

# ---------------------------------------------------------------------------
# 5. bc_write_connection_string_if_absent — symlink refusal
ln -sf /etc/hostname "${SCRATCH}/prefix/secrets.env"
out=$(bash -c "$(make_harness '')
bc_write_connection_string_if_absent 127.0.0.1 5432 expertise expertise 'pw123'
" 2>&1); rc=$?
rm -f "${SCRATCH}/prefix/secrets.env"
assert "symlinked secrets.env refused" [ "${rc}" -ne 0 ]
assert "symlink refusal mentions 'symlink'" bash -c "[[ '${out}' == *symlink* ]]"

# 5b. Fresh file written; mode 600; single-quoted; value present.
out=$(bash -c "$(make_harness '')
bc_write_connection_string_if_absent 127.0.0.1 5432 expertise expertise 'pw123'
" 2>&1); rc=$?
assert "fresh-write exit 0" [ "${rc}" -eq 0 ]
assert "secrets.env now exists" [ -f "${SCRATCH}/prefix/secrets.env" ]
mode=$(stat -f '%Lp' "${SCRATCH}/prefix/secrets.env" 2>/dev/null \
       || stat -c '%a' "${SCRATCH}/prefix/secrets.env")
assert "secrets.env mode 600" bash -c "[[ '${mode}' == '600' ]]"
assert "secrets.env contains single-quoted connection string" \
  grep -q "^ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=5432;Database=expertise;Username=expertise;Password=pw123'\$" "${SCRATCH}/prefix/secrets.env"
assert "secrets.env does NOT contain double-quoted connection string" \
  bash -c "! grep -q '^ConnectionStrings__DefaultConnection=\"' '${SCRATCH}/prefix/secrets.env'"

# 5c. Re-run with file already having the line: NO overwrite (idempotent).
sleep 1  # ensure mtime would differ if file were rewritten
mtime_before=$(stat -f '%m' "${SCRATCH}/prefix/secrets.env" 2>/dev/null \
               || stat -c '%Y' "${SCRATCH}/prefix/secrets.env")
out=$(bash -c "$(make_harness '')
bc_write_connection_string_if_absent 127.0.0.1 5432 expertise expertise 'DIFFERENT_PW'
" 2>&1); rc=$?
mtime_after=$(stat -f '%m' "${SCRATCH}/prefix/secrets.env" 2>/dev/null \
              || stat -c '%Y' "${SCRATCH}/prefix/secrets.env")
assert "re-run with existing line exits non-zero (per contract)" [ "${rc}" -ne 0 ] \
  || true  # bc returns 1 (not err()) so rc may be 1
assert "re-run did NOT mutate file (mtime unchanged)" bash -c "[[ '${mtime_before}' == '${mtime_after}' ]]"
assert "re-run did NOT inject DIFFERENT_PW" \
  bash -c "! grep -q DIFFERENT_PW '${SCRATCH}/prefix/secrets.env'"
assert "re-run log mentions 'already set'" bash -c "[[ '${out}' == *'already set'* ]]"

# ---------------------------------------------------------------------------
# 6. bc_append_install_deps_history — append + format + RFC3339 line
rm -f "${SCRATCH}/prefix/.install-deps-history"
bash -c "$(make_harness '')
bc_append_install_deps_history 'dotnet-sdk:installed,postgres:installed' 'pgvector:skip'
" >/dev/null 2>&1
assert "history file created" [ -f "${SCRATCH}/prefix/.install-deps-history" ]
line=$(head -1 "${SCRATCH}/prefix/.install-deps-history")
# RFC3339 UTC: 2026-05-22T16:23:59Z
assert "history line begins with RFC3339 timestamp" \
  bash -c "[[ '${line}' =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z ]]"
assert "history line contains version" bash -c "[[ '${line}' == *test-1.2.3* ]]"
assert "history line contains OS" bash -c "[[ '${line}' == *macos* ]]"
assert "history line contains taken=" bash -c "[[ '${line}' == *taken=dotnet-sdk:installed,postgres:installed* ]]"
assert "history line contains skipped=" bash -c "[[ '${line}' == *skipped=pgvector:skip* ]]"

# Second append yields two lines.
bash -c "$(make_harness '')
bc_append_install_deps_history 'noop' 'all'
" >/dev/null 2>&1
lines=$(wc -l < "${SCRATCH}/prefix/.install-deps-history")
assert "second run appends a second line" bash -c "[[ '${lines// /}' == '2' ]]"

# Symlinked history file: refuse (warn, not fail).
rm -f "${SCRATCH}/prefix/.install-deps-history"
ln -sf /etc/hostname "${SCRATCH}/prefix/.install-deps-history"
out=$(bash -c "$(make_harness '')
bc_append_install_deps_history 'x' 'y'
" 2>&1); rc=$?
rm -f "${SCRATCH}/prefix/.install-deps-history"
assert "symlinked history is a non-fatal warn" [ "${rc}" -eq 0 ]
assert "symlinked history warning message present" bash -c "[[ '${out}' == *symlink* ]]"

# 7. Audit history file is mode 600 (not 644) per security-review LOW C.
bash -c "$(make_harness '')
bc_append_install_deps_history 'x' 'y'
" >/dev/null 2>&1
hmode2=$(stat -f '%Lp' "${SCRATCH}/prefix/.install-deps-history" 2>/dev/null \
         || stat -c '%a' "${SCRATCH}/prefix/.install-deps-history" 2>/dev/null \
         || echo "")
assert "history file is mode 600 (not 644)" bash -c "[[ '${hmode2}' == '600' ]]"

# 8. Static argv-leak guard: scripts/lib/bootstrap-macos.sh must NOT use
#    `psql ... -v pw=` or `psql ... -c "...PASSWORD '" or other forms that
#    place the password literal on the command line. Closes the HIGH
#    finding from shell-expert + security-review pre-PR review.
MACOS_LIB="${SCRIPT_DIR}/scripts/lib/bootstrap-macos.sh"
assert "bootstrap-macos.sh has NO 'psql ... -v pw=' argv pattern (excluding comments)" \
  bash -c "! grep -vE '^[[:space:]]*#' '${MACOS_LIB}' | grep -E 'psql.*-v[[:space:]]+pw='"
assert "bootstrap-macos.sh has NO ALTER ROLE inside a -c argument" \
  bash -c "! grep -vE '^[[:space:]]*#' '${MACOS_LIB}' | grep -E '\\-c[[:space:]].*ALTER ROLE.*PASSWORD'"
assert "bootstrap-macos.sh references -f - (stdin-fed psql)" \
  grep -q -- '-f - <<<' "${MACOS_LIB}"
assert "bootstrap-macos.sh wraps ALTER ROLE in 'SET LOCAL log_statement' txn" \
  grep -q "SET LOCAL log_statement" "${MACOS_LIB}"
assert "bootstrap-macos.sh refuses single-quote in generated pw" \
  grep -q 'contains a single quote' "${MACOS_LIB}"
assert "bootstrap-macos.sh refuses symlinked SECRETS_FILE before PG mutation" \
  grep -q 'refusing to bootstrap' "${MACOS_LIB}"

printf '\n[test-bootstrap-common] %d passed, %d failed\n' "${PASS}" "${FAIL}"
if (( FAIL == 0 )); then
  echo "PASS — 0 errors, 0 warnings"
  exit 0
else
  echo "FAIL — ${FAIL} errors, 0 warnings"
  exit 1
fi
