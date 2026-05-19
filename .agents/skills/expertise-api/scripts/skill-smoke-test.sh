#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/skill-smoke-test.sh
#
# Round-trip smoke test:
#   1. baseline search
#   2. create entry A  (Draft)
#   3. get entry A
#   4. semantic search (best-effort)
#   5. approve entry A         -> Approved
#   6. reject entry A          -> expected HTTP 409 (state machine)
#   7. create entry B  (Draft)
#   8. reject entry B with reason  -> Rejected
#   9. idempotency replay      -> two POST /expertise calls under one
#                                 pinned IDEMPOTENCY_KEY return the same
#                                 entry id (server-side replay, ADR-010)
#
# Step 8 is critical: it exercises reject.sh against a real Draft so a
# wrong-field-name or other reject-body regression is caught (rather than
# being masked by the negative-path 409 in step 6).
#
# Requires the API running locally with Auth:Mode=LocalDev (or Hybrid).
#
# Usage:
#   EXPERTISE_API_BASE_URL=http://localhost:8080 \
#   EXPERTISE_API_TOKEN='dev:smoke:expertise.read+expertise.write.draft+expertise.write.approve' \
#   ./skill-smoke-test.sh

set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/common.sh
. "${here}/lib/common.sh"

require_cmd jq
require_cmd curl
load_secrets
require_env

base_url="$EXPERTISE_API_BASE_URL"
echo "==> base URL: $base_url"

step() { printf '\n==> %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

mk_body() {
    local title="$1" suffix="$2"
    cat <<EOF
{
  "domain":        "shared",
  "tags":          ["smoke","test"],
  "title":         "${title}",
  "body":          "Smoke test entry created at ${suffix}.",
  "entryType":     "Pattern",
  "severity":      "Info",
  "source":        "skill-smoke-test",
  "sourceVersion": "${suffix}"
}
EOF
}

# ---- 1. Baseline search ----
step "search.sh --q 'smoke-test-baseline' (expect 200, possibly empty)"
"${here}/search.sh" --q "smoke-test-baseline" >/dev/null \
    || fail "search.sh baseline call failed"

# ---- 2. Create entry A ----
suffix_a="$(date +%s)-$$-a"
title_a="smoke-test approve-path ${suffix_a}"
step "create.sh A (title=${title_a})"
created_a="$(mk_body "$title_a" "$suffix_a" | "${here}/create.sh")" \
    || fail "create.sh A did not return 2xx"
id_a="$(printf '%s' "$created_a" | jq -er '.id')" \
    || fail "create.sh A response missing .id"
echo "    created id_a=$id_a"

# ---- 3. Get entry A ----
step "get.sh ${id_a}"
got_a="$("${here}/get.sh" "$id_a")" \
    || fail "get.sh returned non-2xx"
got_title="$(printf '%s' "$got_a" | jq -er '.title')"
[ "$got_title" = "$title_a" ] || fail "title round-trip: expected '$title_a', got '$got_title'"

# ---- 4. Semantic search (best-effort) ----
step "search-semantic.sh --q 'smoke-test' --limit 5"
"${here}/search-semantic.sh" --q "smoke-test" --limit 5 >/dev/null \
    || fail "search-semantic.sh failed"

# ---- 5. Approve entry A ----
step "approve.sh ${id_a}"
approved="$("${here}/approve.sh" "$id_a")" \
    || fail "approve.sh failed (token may be missing expertise.write.approve)"
state="$(printf '%s' "$approved" | jq -er '.reviewState')"
[ "$state" = "Approved" ] || fail "approve: expected reviewState=Approved, got '$state'"

# ---- 6. Negative path: reject-after-approve must be HTTP 409 ----
step "reject.sh ${id_a} (expect HTTP 409 because already Approved)"
status="$(api_curl_status "/expertise/$(urlencode "$id_a")/reject" \
    -X POST \
    -H 'Content-Type: application/json' \
    --data-binary '{"rejectionReason":"post-approve reject (smoke negative path)"}' \
    2>/dev/null)"
case "$status" in
    409) echo "    confirmed HTTP 409 for reject-after-approve" ;;
    *)   fail "reject after approve: expected 409, got ${status}" ;;
esac

# ---- 7. Create entry B (fresh Draft for the happy-path reject) ----
suffix_b="$(date +%s)-$$-b"
title_b="smoke-test reject-path ${suffix_b}"
step "create.sh B (title=${title_b})"
created_b="$(mk_body "$title_b" "$suffix_b" | "${here}/create.sh")" \
    || fail "create.sh B did not return 2xx"
id_b="$(printf '%s' "$created_b" | jq -er '.id')" \
    || fail "create.sh B response missing .id"
echo "    created id_b=$id_b"

# ---- 8. Reject entry B (happy path; catches reject-body regressions) ----
step "reject.sh ${id_b} 'smoke-test rejection reason'"
rejected="$("${here}/reject.sh" "$id_b" "smoke-test rejection reason (id=${id_b})")" \
    || fail "reject.sh B failed (Draft entry should be rejectable; check reject-body field names)"
rstate="$(printf '%s' "$rejected" | jq -er '.reviewState')"
[ "$rstate" = "Rejected" ] || fail "reject B: expected reviewState=Rejected, got '$rstate'"
rreason="$(printf '%s' "$rejected" | jq -er '.rejectionReason')"
case "$rreason" in
    *"smoke-test rejection reason"*) : ;;
    *) fail "reject B: rejectionReason did not round-trip; got '$rreason'" ;;
esac

# ---- 9. Idempotency replay (ADR-010) ----
# Two POST /expertise calls under the same pinned IDEMPOTENCY_KEY must
# return the same entry id and (on the second call) the
# 'Idempotency-Replay: true' marker. This catches regressions in either
# the api_curl injection path or the server-side replay cache.
require_cmd uuidgen
suffix_c="$(date +%s)-$$-c"
title_c="smoke-test idempotency ${suffix_c}"
step "idempotency replay: two POSTs under one pinned key (title=${title_c})"
body_c="$(mk_body "$title_c" "$suffix_c")"
IDEMPOTENCY_KEY="$(uuidgen)"
export IDEMPOTENCY_KEY
echo "    pinned IDEMPOTENCY_KEY=${IDEMPOTENCY_KEY}"
first_c="$(printf '%s' "$body_c" | "${here}/create.sh")" \
    || fail "idempotency: first create did not return 2xx"
first_id="$(printf '%s' "$first_c" | jq -er '.id')" \
    || fail "idempotency: first response missing .id"
second_c="$(printf '%s' "$body_c" | "${here}/create.sh")" \
    || fail "idempotency: second create did not return 2xx (replay should succeed)"
second_id="$(printf '%s' "$second_c" | jq -er '.id')" \
    || fail "idempotency: second response missing .id"
[ "$first_id" = "$second_id" ] \
    || fail "idempotency: replay returned a different id ($first_id vs $second_id)"
echo "    confirmed both POSTs returned id=$first_id"
unset IDEMPOTENCY_KEY

echo
echo "OK: skill-smoke-test passed (approve id_a=$id_a, reject id_b=$id_b, idempotent id=$first_id)"
