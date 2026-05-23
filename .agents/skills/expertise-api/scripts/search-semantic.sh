#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/search-semantic.sh
#
# Semantic vector search: GET /expertise/search/semantic?q=...&limit=...
# The server embeds the query in-process and ranks by cosine similarity.

set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/common.sh
. "${here}/lib/common.sh"

usage() {
    cat <<'EOF' >&2
usage: search-semantic.sh --q TEXT [--limit N] [--include-deprecated]

  --q TEXT              query text (required)
  --limit N             max results (1-100, server default 10)
  --include-deprecated  include soft-deleted entries
EOF
    exit 2
}

q=""
limit=""
include_deprecated=""

while [ $# -gt 0 ]; do
    case "$1" in
        --q)                  q="$2"; shift 2 ;;
        --limit)              limit="$2"; shift 2 ;;
        --include-deprecated) include_deprecated="true"; shift ;;
        -h|--help)            usage ;;
        *)                    echo "unknown arg: $1" >&2; usage ;;
    esac
done

if [ -z "$q" ]; then
    echo "error: --q is required" >&2
    usage
fi

load_secrets
require_env

qs="?q=$(urlencode "$q")"
[ -n "$limit" ] && qs+="&limit=$(urlencode "$limit")"
[ -n "$include_deprecated" ] && qs+="&includeDeprecated=$(urlencode "$include_deprecated")"

api_curl "/expertise/search/semantic${qs}"
