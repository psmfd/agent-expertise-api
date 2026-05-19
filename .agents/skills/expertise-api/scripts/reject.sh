#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/reject.sh
#
# Reject a draft entry: POST /expertise/{id}/reject.
# Requires the caller's token to carry `expertise.write.approve`.
# Reason is mandatory (server requires 1-2000 chars).
#
# Idempotency: api_curl auto-injects an Idempotency-Key header on POST.
# Wrap-and-retry callers should pre-generate a key (`uuidgen`) and
# export IDEMPOTENCY_KEY so every retry of *this* invocation hits the
# server-side replay cache (ADR-010) and returns the original response.

set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/common.sh
. "${here}/lib/common.sh"

usage() {
    echo "usage: reject.sh <id> <reason>" >&2
    exit 2
}

[ $# -eq 2 ] || usage
id="$1"
reason="$2"
case "$id" in -h|--help) usage ;; esac

if [ -z "$reason" ]; then
    echo "error: reason cannot be empty" >&2
    exit 2
fi

load_secrets
require_env

# Build the JSON body via printf + a tiny escape pass so we don't need jq at runtime.
# We escape backslash, double-quote, CR, LF, tab; control bytes < 0x20 are stripped.
escape_json() {
    local s="$1" out="" c
    local i
    for ((i = 0; i < ${#s}; i++)); do
        c="${s:i:1}"
        # shellcheck disable=SC1003  # the '\' and '"' patterns are literal single chars, not quote-escape attempts
        case "$c" in
            '\') out+='\\' ;;
            '"') out+='\"' ;;
            $'\n') out+='\n' ;;
            $'\r') out+='\r' ;;
            $'\t') out+='\t' ;;
            *)
                # strip any other C0 control character
                if [ "$(printf '%d' "'$c")" -lt 32 ]; then
                    continue
                fi
                out+="$c"
                ;;
        esac
    done
    printf '%s' "$out"
}

body="$(printf '{"rejectionReason":"%s"}' "$(escape_json "$reason")")"

api_curl "/expertise/$(urlencode "$id")/reject" \
    -X POST \
    -H 'Content-Type: application/json' \
    --data-binary "$body"
