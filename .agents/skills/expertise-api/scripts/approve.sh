#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/approve.sh
#
# Approve a draft entry: POST /expertise/{id}/approve.
# Requires the caller's token to carry `expertise.write.approve`.
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
    echo "usage: approve.sh <id>" >&2
    exit 2
}

[ $# -eq 1 ] || usage
id="$1"
case "$id" in -h|--help) usage ;; esac

load_secrets
require_env
api_curl "/expertise/$(urlencode "$id")/approve" \
    -X POST
