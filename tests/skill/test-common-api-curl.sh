#!/usr/bin/env bash
# tests/skill/test-common-api-curl.sh
#
# Argv-hygiene tests for api_curl / api_curl_status in
# .agents/skills/expertise-api/scripts/lib/common.sh (issue #486).
#
# A curl stub on PATH records its argv and snapshots the --config file it
# was handed, then emits a canned HTTP status + body. Asserts that the
# bearer token never appears in curl argv, that the injected headers
# (Authorization, Accept, Idempotency-Key on POST) travel via the config
# file, that the config file is removed after each call, and that the
# unsafe-token charset guard fails closed before curl is ever invoked.
# No network, no API.
#
# Usage: bash tests/skill/test-common-api-curl.sh
# Exit codes: 0 all cases pass, 1 one or more cases fail.

# shellcheck disable=SC2016  # deliberate: $1/$@ inside bash -c '…' must expand in the INNER shell
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
COMMON_SH="${REPO_ROOT}/.agents/skills/expertise-api/scripts/lib/common.sh"

WORK_DIR="$(mktemp -d -t skill-argv-test.XXXXXX)"
trap 'rm -rf "$WORK_DIR"' EXIT

errors=0
ok()   { printf 'OK    [%s] %s\n' "$1" "$2"; }
err()  { printf 'ERROR [%s] %s\n' "$1" "$2" >&2; errors=$((errors + 1)); }

TOKEN="secret-bearer-token-486"

# --- curl stub ---------------------------------------------------------------
# Records argv (one line per element) to argv.<n>, snapshots the file named
# by --config to cfg.<n>, honours -o FILE and -w '%{http_code}' the way the
# real curl would for these invocations. STUB_STATUS/STUB_BODY control the
# canned response; STUB_FAIL=1 exits 7 (transport failure) without output.
STUB_DIR="$WORK_DIR/bin"
mkdir -p "$STUB_DIR"
cat > "$STUB_DIR/curl" <<'EOF'
#!/usr/bin/env bash
set -u
n=0
while [ -e "${STUB_STATE}/argv.$n" ]; do n=$((n + 1)); done
printf '%s\n' "$@" > "${STUB_STATE}/argv.$n"
out=""
prev=""
for arg in "$@"; do
    case "$prev" in
        --config) cp "$arg" "${STUB_STATE}/cfg.$n" ;;
        -o)       out="$arg" ;;
    esac
    prev="$arg"
done
if [ "${STUB_FAIL:-0}" = "1" ]; then
    exit 7
fi
[ -n "$out" ] && printf '%s' "${STUB_BODY:-{}}" > "$out"
printf '%s' "${STUB_STATUS:-200}"
EOF
chmod +x "$STUB_DIR/curl"

# run_api NAME FUNC PATH [curl-args...]
# Runs FUNC from common.sh in a subshell with the stub curl first on PATH.
# Streams: stdout -> $WORK_DIR/out, stderr -> $WORK_DIR/stderr; exit code
# echoed. STUB_STATE is reset per call so argv/cfg indices start at 0.
run_api() {
    local name="$1" func="$2"; shift 2
    local state="$WORK_DIR/state-$name"
    mkdir -p "$state"
    rm -f "$WORK_DIR/out" "$WORK_DIR/stderr"
    set +e
    env PATH="$STUB_DIR:$PATH" STUB_STATE="$state" \
        STUB_STATUS="${STUB_STATUS:-200}" STUB_BODY="${STUB_BODY:-{}}" \
        STUB_FAIL="${STUB_FAIL:-0}" \
        EXPERTISE_API_BASE_URL="https://api.example.test" \
        EXPERTISE_API_TOKEN="${TEST_TOKEN:-$TOKEN}" \
        bash -c '. "$1"; shift; f="$1"; shift; "$f" "$@"' \
        _ "$COMMON_SH" "$func" "$@" \
        > "$WORK_DIR/out" 2> "$WORK_DIR/stderr"
    local rc=$?
    set -e
    LAST_STATE="$state"
    return "$rc"
}

# --- case 1: bearer absent from argv; --config present (GET) ------------------
run_api get-hygiene api_curl "/expertise?limit=1" || true
if grep -qx -- '--config' "$LAST_STATE/argv.0" \
   && ! grep -q "$TOKEN" "$LAST_STATE/argv.0"; then
    ok argv-no-bearer "GET: --config in argv, bearer literal absent"
else
    err argv-no-bearer "argv leaked the bearer or lacked --config: $(tr '\n' ' ' < "$LAST_STATE/argv.0")"
fi

# --- case 2: config file carries Authorization + Accept, no idem key on GET ---
if grep -q "header = \"Authorization: Bearer ${TOKEN}\"" "$LAST_STATE/cfg.0" \
   && grep -q 'header = "Accept: application/json"' "$LAST_STATE/cfg.0" \
   && ! grep -q 'Idempotency-Key' "$LAST_STATE/cfg.0"; then
    ok cfg-headers-get "config file has Authorization+Accept, no Idempotency-Key on GET"
else
    err cfg-headers-get "unexpected config content: $(tr '\n' ' ' < "$LAST_STATE/cfg.0")"
fi

# --- case 3: POST gets an Idempotency-Key via the config file -----------------
run_api post-idem api_curl "/expertise" -X POST --data '{"x":1}' || true
if grep -q 'header = "Idempotency-Key: ' "$LAST_STATE/cfg.0" \
   && ! grep -q "$TOKEN" "$LAST_STATE/argv.0"; then
    ok cfg-idem-post "POST: Idempotency-Key in config file, bearer still off argv"
else
    err cfg-idem-post "POST config/argv wrong: cfg=$(tr '\n' ' ' < "$LAST_STATE/cfg.0")"
fi
key1="$(sed -n 's/^header = "Idempotency-Key: \(.*\)"$/\1/p' "$LAST_STATE/cfg.0")"

# --- case 4: a second POST mints a distinct Idempotency-Key -------------------
run_api post-idem2 api_curl "/expertise" -X POST --data '{"x":2}' || true
key2="$(sed -n 's/^header = "Idempotency-Key: \(.*\)"$/\1/p' "$LAST_STATE/cfg.0")"
if [ -n "$key1" ] && [ -n "$key2" ] && [ "$key1" != "$key2" ]; then
    ok idem-distinct "consecutive POSTs mint distinct Idempotency-Keys"
else
    err idem-distinct "keys not distinct: '$key1' vs '$key2'"
fi

# --- case 5: pinned IDEMPOTENCY_KEY is honoured --------------------------------
state="$WORK_DIR/state-pinned"; mkdir -p "$state"
set +e
env PATH="$STUB_DIR:$PATH" STUB_STATE="$state" STUB_STATUS=200 STUB_BODY='{}' STUB_FAIL=0 \
    EXPERTISE_API_BASE_URL="https://api.example.test" \
    EXPERTISE_API_TOKEN="$TOKEN" IDEMPOTENCY_KEY="pinned-key-123" \
    bash -c '. "$1"; api_curl /expertise -X POST --data "{}"' _ "$COMMON_SH" \
    >/dev/null 2>&1
set -e
if grep -q 'header = "Idempotency-Key: pinned-key-123"' "$state/cfg.0"; then
    ok idem-pinned "pre-set IDEMPOTENCY_KEY carried through the config file"
else
    err idem-pinned "pinned key missing from config: $(tr '\n' ' ' < "$state/cfg.0")"
fi

# --- case 6: unsafe token fails exit 2 before curl runs ------------------------
TEST_TOKEN='bad#token'
run_api unsafe-hash api_curl "/expertise" && rc=0 || rc=$?
if [ "$rc" = "2" ] && grep -q "never valid in a bearer token" "$WORK_DIR/stderr" \
   && [ ! -e "$LAST_STATE/argv.0" ]; then
    ok unsafe-hash "token with '#' exits 2 and curl is never invoked"
else
    err unsafe-hash "expected exit 2 + no curl call, got rc=$rc, argv.0 $( [ -e "$LAST_STATE/argv.0" ] && echo present || echo absent )"
fi

TEST_TOKEN='bad token'
run_api unsafe-space api_curl "/expertise" && rc=0 || rc=$?
if [ "$rc" = "2" ] && [ ! -e "$LAST_STATE/argv.0" ]; then
    ok unsafe-space "token with a space exits 2 and curl is never invoked"
else
    err unsafe-space "expected exit 2 + no curl call, got rc=$rc"
fi
unset TEST_TOKEN

# --- case 7: config file removed after a successful call ----------------------
run_api cleanup api_curl "/expertise?limit=1" || true
cfg_path="$(grep -A1 -x -- '--config' "$LAST_STATE/argv.0" | tail -1)"
if [ -n "$cfg_path" ] && [ ! -e "$cfg_path" ]; then
    ok cfg-removed "config file deleted immediately after the request"
else
    err cfg-removed "config file still present (or path not captured): '$cfg_path'"
fi

# --- case 8: non-2xx still surfaces status + body on stderr, exit 1 -----------
STUB_STATUS=404 STUB_BODY='{"title":"not found"}' run_api non2xx api_curl "/expertise/nope" && rc=0 || rc=$?
if [ "$rc" = "1" ] && grep -q "HTTP 404" "$WORK_DIR/stderr" \
   && grep -q "not found" "$WORK_DIR/stderr"; then
    ok non2xx "404 path unchanged: exit 1, status + body on stderr"
else
    err non2xx "expected exit 1 + 404 body on stderr, got rc=$rc / $(tr '\n' ' ' < "$WORK_DIR/stderr")"
fi

# --- case 9: api_curl_status also keeps the bearer off argv --------------------
STUB_STATUS=409 STUB_BODY='{"title":"conflict"}' run_api status-fn api_curl_status "/expertise" -X POST --data '{}' || true
if ! grep -q "$TOKEN" "$LAST_STATE/argv.0" \
   && grep -q "header = \"Authorization: Bearer ${TOKEN}\"" "$LAST_STATE/cfg.0" \
   && grep -qx '409' "$WORK_DIR/out"; then
    ok status-fn "api_curl_status: bearer off argv, status on stdout"
else
    err status-fn "api_curl_status leaked bearer or wrong status: out=$(cat "$WORK_DIR/out")"
fi

# --- case 10: transport failure removes the config file ------------------------
STUB_FAIL=1 run_api transport-fail api_curl "/expertise" && rc=0 || rc=$?
cfg_path="$(grep -A1 -x -- '--config' "$LAST_STATE/argv.0" | tail -1)"
if [ "$rc" != "0" ] && [ -n "$cfg_path" ] && [ ! -e "$cfg_path" ]; then
    ok transport-fail "curl transport failure: nonzero exit, config file removed"
else
    err transport-fail "expected nonzero + removed config, got rc=$rc, cfg '$cfg_path'"
fi

echo "=================================="
if [ "$errors" -eq 0 ]; then
    echo "PASS — 0 errors"
    exit 0
fi
echo "FAIL — ${errors} errors"
exit 1
