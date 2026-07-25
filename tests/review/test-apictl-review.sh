#!/usr/bin/env bash
# tests/review/test-apictl-review.sh
# Unit tests for the drafts / review subcommands added to scripts/expertise-apictl
# (issue #485, ADR-019). No database and no real API — a Python http.server mock
# (mock-api-server.py) serves a canned /expertise/drafts queue and records every
# request. The interactive loop is driven through a real pty (python3 pty.spawn)
# so /dev/tty behaves as in production.
#
#  1. drafts --json emits the server payload verbatim.
#  2. drafts table strips terminal control bytes (ESC/BEL/DEL) and the bidi
#     override from agent-authored content (CWE-150).
#  3. bearer token never appears in curl argv (curl wrapper logs "$@");
#     --config carries it instead.
#  4. EXPERTISE_API_TOKEN_FILE indirection works (trailing newline stripped).
#  5. token with a curl-config-unsafe character fails loudly, before any request.
#  6. review without a controlling terminal exits 2.
#  7. review approve (Private default) + reject flow: correct POST paths and
#     bodies, DISTINCT Idempotency-Keys per action, Rejected queue entries are
#     not iterated, rendered output is control-byte-free, no service-shaped
#     warning for a LocalDev token.
#  8. approving as Shared demands the consequence-naming confirmation; a wrong
#     confirmation cancels (no POST).
#  9. TOCTOU guard: content hash changed between render and approve cancels the
#     action (no POST).
# 10. JWT preflight warns on a service-shaped token (sub == azp), fails soft.
# 11. --help mentions drafts and review.

# shellcheck disable=SC2016  # single-quoted `bash -c` bodies use their OWN $1/$2 — expansion is deliberate
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
APICTL="${SCRIPT_DIR}/scripts/expertise-apictl"
FIXTURE="${SCRIPT_DIR}/tests/review/drafts-fixture.json"
MOCK="${SCRIPT_DIR}/tests/review/mock-api-server.py"

PASS=0
FAIL=0

assert() {
  local name="$1"; shift
  if "$@"; then
    PASS=$((PASS+1))
  else
    printf 'FAIL: %s\n' "${name}" >&2
    FAIL=$((FAIL+1))
  fi
}

assert_contains() {
  local name="$1" haystack="$2" needle="$3"
  case "${haystack}" in
    *"${needle}"*) PASS=$((PASS+1)) ;;
    *) printf 'FAIL: %s (missing: %s)\n' "${name}" "${needle}" >&2; FAIL=$((FAIL+1)) ;;
  esac
}

assert_not_contains() {
  local name="$1" haystack="$2" needle="$3"
  case "${haystack}" in
    *"${needle}"*) printf 'FAIL: %s (unexpectedly found: %s)\n' "${name}" "${needle}" >&2; FAIL=$((FAIL+1)) ;;
    *) PASS=$((PASS+1)) ;;
  esac
}

for tool in python3 jq curl; do
  if ! command -v "${tool}" >/dev/null 2>&1; then
    printf 'SKIP: all review CLI tests — %s not on PATH\n' "${tool}"
    exit 0
  fi
done

# ---------------------------------------------------------------------------
# Sandbox + curl argv recorder. The wrapper logs "$@" then execs the real
# curl, so test 3 can assert the bearer travels via --config, never argv.
# ---------------------------------------------------------------------------
SANDBOX="$(mktemp -d)"
SERVER_PID=""
cleanup() {
  if [[ -n "${SERVER_PID}" ]]; then
    { kill "${SERVER_PID}" && wait "${SERVER_PID}"; } 2>/dev/null
  fi
  rm -rf "${SANDBOX}"
}
trap cleanup EXIT

REAL_CURL="$(command -v curl)"
STUB_DIR="${SANDBOX}/stubs"
ARGV_LOG="${SANDBOX}/curl-argv.log"
mkdir -p "${STUB_DIR}"
cat > "${STUB_DIR}/curl" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$*" >> "${ARGV_LOG}"
exec "${REAL_CURL}" "\$@"
EOF
chmod +x "${STUB_DIR}/curl"
export PATH="${STUB_DIR}:${PATH}"

STATE="${SANDBOX}/state"

start_server() {
  # start_server [MUTATE_ON_REFETCH]
  [[ -n "${SERVER_PID}" ]] && { kill "${SERVER_PID}" 2>/dev/null; wait "${SERVER_PID}" 2>/dev/null; SERVER_PID=""; }
  rm -rf "${STATE}"
  mkdir -p "${STATE}"
  cp "${FIXTURE}" "${STATE}/drafts.json"
  MOCK_STATE_DIR="${STATE}" MUTATE_ON_REFETCH="${1:-0}" python3 "${MOCK}" &
  SERVER_PID=$!
  local i=0
  while [[ ! -f "${STATE}/port" ]]; do
    i=$((i+1))
    if [[ "${i}" -gt 50 ]]; then
      printf 'FATAL: mock server did not start\n' >&2
      exit 1
    fi
    sleep 0.1
  done
  PORT="$(cat "${STATE}/port")"
  export EXPERTISE_API_URL="http://127.0.0.1:${PORT}"
}

reset_requests() { : > "${STATE}/requests.log"; rm -f "${STATE}/get_count"; }

export EXPERTISE_API_TOKEN="dev:legacy:approve"

# run_review_pty INPUT — drive `review` through a real pty so /dev/tty works.
run_review_pty() {
  printf '%b' "$1" | python3 -c '
import os, pty, sys
status = pty.spawn(["bash", sys.argv[1], "review"])
if hasattr(os, "waitstatus_to_exitcode"):
    sys.exit(os.waitstatus_to_exitcode(status))
sys.exit(status >> 8)
' "${APICTL}"
}

start_server

# ---------------------------------------------------------------------------
# 1. drafts --json emits the payload verbatim.
# ---------------------------------------------------------------------------
out="$("${APICTL}" drafts --json 2>&1)"
rc=$?
assert "drafts --json exits zero" test "${rc}" -eq 0
assert "drafts --json payload identical" \
  bash -c 'diff <(printf "%s" "$1" | jq -S .) <(jq -S . "$2") >/dev/null' _ "${out}" "${FIXTURE}"

# ---------------------------------------------------------------------------
# 2. drafts table sanitizes control bytes and bidi overrides.
# ---------------------------------------------------------------------------
out="$("${APICTL}" drafts 2>&1)"
rc=$?
assert "drafts table exits zero" test "${rc}" -eq 0
assert_contains "table shows draft 1 id" "${out}" "11111111-1111-1111-1111-111111111111"
assert_contains "table keeps title text" "${out}" "Evil"
assert_contains "table shows author+agent" "${out}" "agent-principal-1 (claude-code)"
assert_not_contains "table has no ESC byte" "${out}" "$(printf '\033')"
assert_not_contains "table has no BEL byte" "${out}" "$(printf '\007')"
assert_not_contains "table has no DEL byte" "${out}" "$(printf '\177')"
assert_not_contains "table has no bidi override" "${out}" "$(printf '\342\200\256')"

# ---------------------------------------------------------------------------
# 3. bearer never in curl argv; --config used instead.
# ---------------------------------------------------------------------------
assert "curl argv log exists" test -s "${ARGV_LOG}"
assert "curl called with --config" grep -q -- '--config' "${ARGV_LOG}"
assert_not_contains "token absent from curl argv" "$(cat "${ARGV_LOG}")" "${EXPERTISE_API_TOKEN}"
# ...and the token did reach the server as a Bearer header.
assert "server saw Authorization header" grep -q "auth" "${STATE}/requests.log"

# ---------------------------------------------------------------------------
# 4. EXPERTISE_API_TOKEN_FILE indirection (trailing newline stripped).
# ---------------------------------------------------------------------------
reset_requests
printf 'dev:legacy:approve\n' > "${SANDBOX}/token.txt"
out="$(env -u EXPERTISE_API_TOKEN EXPERTISE_API_TOKEN_FILE="${SANDBOX}/token.txt" "${APICTL}" drafts --json 2>&1)"
rc=$?
assert "token-file drafts exits zero" test "${rc}" -eq 0
assert "token-file request authenticated" grep -q "auth" "${STATE}/requests.log"

# ---------------------------------------------------------------------------
# 5. curl-config-unsafe token fails loudly before any request.
# ---------------------------------------------------------------------------
reset_requests
out="$(EXPERTISE_API_TOKEN='bad#token' "${APICTL}" drafts --json 2>&1)"
rc=$?
assert "unsafe token exits nonzero" test "${rc}" -ne 0
assert_contains "unsafe token names the problem" "${out}" "never valid in a bearer token"
assert "unsafe token sent no request" test ! -s "${STATE}/requests.log"

# ---------------------------------------------------------------------------
# 6. review without a controlling terminal exits 2.
# ---------------------------------------------------------------------------
out="$(python3 -c '
import subprocess, sys
r = subprocess.run(["bash", sys.argv[1], "review"], start_new_session=True,
                   stdin=subprocess.DEVNULL, capture_output=True, text=True)
sys.stderr.write(r.stderr)
sys.exit(r.returncode)
' "${APICTL}" 2>&1)"
rc=$?
assert "no-tty review exits 2" test "${rc}" -eq 2
assert_contains "no-tty review says why" "${out}" "controlling terminal"

# ---------------------------------------------------------------------------
# 7. review approve (default Private) + reject flow through a pty.
#    Draft 1: a, <enter> (Private). Draft 2: r, reason. Rejected entry 3
#    must not be presented.
# ---------------------------------------------------------------------------
reset_requests
out="$(run_review_pty 'a\n\nr\nnot good enough\n' 2>&1)"
rc=$?
assert "review flow exits zero" test "${rc}" -eq 0
assert_contains "review announces 2 drafts" "${out}" "2 Draft entries to review"
approve_line="$(grep -F '/expertise/11111111-1111-1111-1111-111111111111/approve' "${STATE}/requests.log" || true)"
reject_line="$(grep -F '/expertise/22222222-2222-2222-2222-222222222222/reject' "${STATE}/requests.log" || true)"
assert "approve POST recorded" test -n "${approve_line}"
assert "reject POST recorded" test -n "${reject_line}"
assert_contains "approve body defaults Private" "${approve_line}" '{"visibility":"Private"}'
assert_contains "reject body carries reason" "${reject_line}" '"rejectionReason":"not good enough"'
assert_not_contains "rejected entry not actioned" "$(cat "${STATE}/requests.log")" "33333333-3333-3333-3333-333333333333"
idem_keys="$(awk -F'\t' '$1 == "POST" { print $3 }' "${STATE}/requests.log" | sort)"
distinct_keys="$(printf '%s\n' "${idem_keys}" | sort -u)"
assert "both POSTs carried an Idempotency-Key" bash -c '! printf "%s\n" "$1" | grep -qx -- "-"' _ "${idem_keys}"
assert "Idempotency-Keys are distinct per action" test "${idem_keys}" = "${distinct_keys}"
assert_not_contains "rendered output has no ESC byte" "${out}" "$(printf '\033')"
assert_not_contains "rendered output has no BEL byte" "${out}" "$(printf '\007')"
assert_not_contains "LocalDev token: no service-shaped warning" "${out}" "service-shaped"
assert_contains "provenance rendered (origin instance)" "${out}" "spoke-a"

# ---------------------------------------------------------------------------
# 8. Shared visibility demands its own confirmation; wrong input cancels.
#    Draft 1: a, S, wrong-confirm -> cancelled -> s (skip). Draft 2: q.
# ---------------------------------------------------------------------------
reset_requests
out="$(run_review_pty 'a\nS\nnope\ns\nq\n' 2>&1)"
assert_contains "shared confirm prompt shown" "${out}" 'EVERY tenant'
assert_contains "wrong confirm cancels approve" "${out}" "not confirmed"
assert "no POST after cancelled shared approve" bash -c '! grep -q "^POST" "$1"' _ "${STATE}/requests.log"

# ---------------------------------------------------------------------------
# 9. TOCTOU guard: hash changes between render and approve -> cancelled.
# ---------------------------------------------------------------------------
start_server 1
out="$(run_review_pty 'a\n\ns\nq\n' 2>&1)"
assert_contains "toctou mismatch reported" "${out}" "integrity hash mismatch"
assert "toctou blocked the POST" bash -c '! grep -q "^POST" "$1"' _ "${STATE}/requests.log"
start_server

# ---------------------------------------------------------------------------
# 10. JWT preflight warns on a service-shaped token, fails soft.
# ---------------------------------------------------------------------------
svc_jwt="$(python3 -c '
import base64, json
enc = lambda d: base64.urlsafe_b64encode(json.dumps(d).encode()).rstrip(b"=").decode()
print(enc({"alg": "RS256"}) + "." + enc({"sub": "svc-1", "azp": "svc-1"}) + ".sig")
')"
out="$(EXPERTISE_API_TOKEN="${svc_jwt}" run_review_pty 'q\n' 2>&1)"
rc=$?
assert "service-shaped review still runs" test "${rc}" -eq 0
assert_contains "service-shaped warning emitted" "${out}" "service-shaped"

# ---------------------------------------------------------------------------
# 11. --help mentions the new subcommands.
# ---------------------------------------------------------------------------
out="$("${APICTL}" --help 2>&1)"
assert_contains "help mentions drafts" "${out}" "drafts [--json]"
assert_contains "help mentions review" "${out}" "expertise-apictl review"

# ---------------------------------------------------------------------------
printf '\n%d passed, %d failed\n' "${PASS}" "${FAIL}"
[[ "${FAIL}" -eq 0 ]] || exit 1
