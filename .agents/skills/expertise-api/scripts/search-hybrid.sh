#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/search-hybrid.sh
#
# Hybrid search: GET /expertise/search/hybrid?q=...&limit=...
# Keyword and semantic arms fused server-side with Reciprocal Rank Fusion
# (ADR-016) — covers exact identifiers AND paraphrases. Recommended default.

set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/common.sh
. "${here}/lib/common.sh"

usage() {
    cat <<'EOF' >&2
usage: search-hybrid.sh --q TEXT [--limit N] [--domain D] [--tags a,b]
                        [--entry-type T] [--severity S] [--include-deprecated]

  --q TEXT              query text (required)
  --limit N             max results (1-100, server default 10)
  --domain D            filter to one domain
  --tags a,b            comma-separated tags (all must match)
  --entry-type T        IssueFix | Caveat | Requirement | Pattern
  --severity S          Info | Warning | Critical
  --include-deprecated  include soft-deleted entries
EOF
    exit 2
}

q=""
limit=""
domain=""
tags=""
entry_type=""
severity=""
include_deprecated=""

while [ $# -gt 0 ]; do
    case "$1" in
        --q)                  q="$2"; shift 2 ;;
        --limit)              limit="$2"; shift 2 ;;
        --domain)             domain="$2"; shift 2 ;;
        --tags)               tags="$2"; shift 2 ;;
        --entry-type)         entry_type="$2"; shift 2 ;;
        --severity)           severity="$2"; shift 2 ;;
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
[ -n "$domain" ] && qs+="&domain=$(urlencode "$domain")"
[ -n "$tags" ] && qs+="&tags=$(urlencode "$tags")"
[ -n "$entry_type" ] && qs+="&entryType=$(urlencode "$entry_type")"
[ -n "$severity" ] && qs+="&severity=$(urlencode "$severity")"
[ -n "$include_deprecated" ] && qs+="&includeDeprecated=$(urlencode "$include_deprecated")"

api_curl "/expertise/search/hybrid${qs}"
