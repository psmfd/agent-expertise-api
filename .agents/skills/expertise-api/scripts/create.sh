#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/create.sh
#
# Create a draft expertise entry: POST /expertise.
# Reads JSON body from --file or stdin.
#
# Idempotency: api_curl auto-injects an Idempotency-Key header on POST.
# Wrap-and-retry callers should pre-generate a key (`uuidgen`) and
# export IDEMPOTENCY_KEY so every retry of *this* invocation hits the
# server-side replay cache (ADR-010) instead of double-creating.

set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/common.sh
. "${here}/lib/common.sh"

usage() {
    cat <<'EOF' >&2
usage: create.sh [--file PATH]

  --file PATH   read JSON body from PATH; if omitted, read from stdin

JSON shape (server-rejected if invalid):

  {
    "domain":        "shared",
    "tags":          ["pgbouncer","postgres"],
    "title":         "...",
    "body":          "## markdown ...",
    "entryType":     "Caveat",
    "severity":      "Warning",
    "source":        "agent-session-id-or-name",
    "sourceVersion": "PgBouncer 1.21.0"
  }
EOF
    exit 2
}

file=""
while [ $# -gt 0 ]; do
    case "$1" in
        --file)    file="$2"; shift 2 ;;
        -h|--help) usage ;;
        *)         echo "unknown arg: $1" >&2; usage ;;
    esac
done

load_secrets
require_env

body=""
if [ -n "$file" ]; then
    [ -r "$file" ] || { echo "error: cannot read $file" >&2; exit 2; }
    body="$(cat -- "$file")"
else
    if [ -t 0 ]; then
        echo "error: no --file and stdin is a tty" >&2
        usage
    fi
    body="$(cat)"
fi

if [ -z "$body" ]; then
    echo "error: empty request body" >&2
    exit 2
fi

api_curl "/expertise" \
    -X POST \
    -H 'Content-Type: application/json' \
    --data-binary "$body"
