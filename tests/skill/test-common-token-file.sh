#!/usr/bin/env bash
# tests/skill/test-common-token-file.sh
#
# Unit tests for the EXPERTISE_API_TOKEN_FILE resolution ladder in
# .agents/skills/expertise-api/scripts/lib/common.sh (issue #464).
#
# Each case runs require_env in a subshell with a controlled environment and
# asserts on the resolved token / exit code / stderr. No network, no API.
#
# Usage: bash tests/skill/test-common-token-file.sh
# Exit codes: 0 all cases pass, 1 one or more cases fail.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
COMMON_SH="${REPO_ROOT}/.agents/skills/expertise-api/scripts/lib/common.sh"

WORK_DIR="$(mktemp -d -t skill-token-test.XXXXXX)"
trap 'rm -rf "$WORK_DIR"' EXIT

errors=0
ok()   { printf 'OK    [%s] %s\n' "$1" "$2"; }
err()  { printf 'ERROR [%s] %s\n' "$1" "$2" >&2; errors=$((errors + 1)); }

# run_require_env NAME [VAR=VALUE ...]
# Sources common.sh with a scrubbed env plus the given assignments, runs
# require_env, and prints "<exit>|<resolved-token>" for the caller to assert.
run_require_env() {
    shift # NAME is for the caller's bookkeeping only
    # shellcheck disable=SC2016  # deliberate: $? / $EXPERTISE_API_TOKEN must expand in the INNER shell
    env -i HOME="$WORK_DIR" PATH="$PATH" "$@" bash -c '
        . "'"$COMMON_SH"'"
        require_env >/dev/null 2>"'"$WORK_DIR"'/stderr"
        printf "%s|%s" "$?" "$EXPERTISE_API_TOKEN"
    ' 2>>"$WORK_DIR/stderr" || printf '%s|' "$?"
}

BASE="EXPERTISE_API_BASE_URL=https://api.example.test"

# --- case 1: file-only resolves the token -----------------------------------
printf 'file-token-value\n' > "$WORK_DIR/token1"
result="$(run_require_env case1 "$BASE" EXPERTISE_API_TOKEN_FILE="$WORK_DIR/token1")"
if [ "$result" = "0|file-token-value" ]; then
    ok token-file-resolves "token read from EXPERTISE_API_TOKEN_FILE"
else
    err token-file-resolves "expected '0|file-token-value', got '$result'"
fi

# --- case 2: explicit token beats the file ----------------------------------
result="$(run_require_env case2 "$BASE" EXPERTISE_API_TOKEN=explicit-wins EXPERTISE_API_TOKEN_FILE="$WORK_DIR/token1")"
if [ "$result" = "0|explicit-wins" ]; then
    ok explicit-wins "EXPERTISE_API_TOKEN takes precedence over the file"
else
    err explicit-wins "expected '0|explicit-wins', got '$result'"
fi

# --- case 3: missing file is a hard exit 2 naming the path ------------------
result="$(run_require_env case3 "$BASE" EXPERTISE_API_TOKEN_FILE="$WORK_DIR/does-not-exist")"
if [ "$result" = "2|" ] && grep -q "missing or unreadable file: ${WORK_DIR}/does-not-exist" "$WORK_DIR/stderr"; then
    ok missing-file "missing token file exits 2 and names the path"
else
    err missing-file "expected exit 2 + path in stderr, got '$result' / $(tr '\n' ' ' < "$WORK_DIR/stderr")"
fi

# --- case 4: empty file is a hard exit 2 -------------------------------------
: > "$WORK_DIR/empty"
result="$(run_require_env case4 "$BASE" EXPERTISE_API_TOKEN_FILE="$WORK_DIR/empty")"
if [ "$result" = "2|" ] && grep -q "EXPERTISE_API_TOKEN_FILE is empty" "$WORK_DIR/stderr"; then
    ok empty-file "empty token file exits 2"
else
    err empty-file "expected exit 2 + empty-file message, got '$result' / $(tr '\n' ' ' < "$WORK_DIR/stderr")"
fi

# --- case 5: neither variable set names both in the error --------------------
result="$(run_require_env case5 "$BASE")"
if [ "$result" = "2|" ] && grep -q "EXPERTISE_API_TOKEN is not set (set it, or point EXPERTISE_API_TOKEN_FILE" "$WORK_DIR/stderr"; then
    ok neither-set "unset token errors naming both variables"
else
    err neither-set "expected exit 2 + both-variable message, got '$result' / $(tr '\n' ' ' < "$WORK_DIR/stderr")"
fi

# --- case 6: trailing newline and whitespace stripped -------------------------
printf 'padded-token \t\n\n' > "$WORK_DIR/token-padded"
result="$(run_require_env case6 "$BASE" EXPERTISE_API_TOKEN_FILE="$WORK_DIR/token-padded")"
if [ "$result" = "0|padded-token" ]; then
    ok trailing-trim "trailing whitespace/newlines stripped from file token"
else
    err trailing-trim "expected '0|padded-token', got '$result'"
fi

echo "=================================="
if [ "$errors" -eq 0 ]; then
    echo "PASS — 0 errors"
    exit 0
fi
echo "FAIL — ${errors} errors"
exit 1
