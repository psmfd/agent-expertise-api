#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/search.sh
#
# Search expertise entries. Two modes:
#   - keyword/filter listing: GET /expertise?domain=...&tags=...&entryType=...&severity=...
#   - full-text search:       GET /expertise/search?q=...
# If --q is supplied, full-text search is used (filters not applicable).
# Without --q, filter listing is used; all filters are optional.

set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/common.sh
. "${here}/lib/common.sh"

usage() {
    cat <<'EOF' >&2
usage: search.sh [--q TEXT] [--domain D] [--tags a,b,c] [--entry-type T]
                 [--severity S] [--include-deprecated]

  --q TEXT              full-text query; if present, uses /expertise/search
  --domain D            filter by domain (e.g. "shared", "azure-devops")
  --tags a,b,c          comma-separated tag list (any-match)
  --entry-type T        IssueFix | Caveat | Requirement | Pattern
  --severity S          Info | Warning | Critical
  --include-deprecated  include soft-deleted entries
EOF
    exit 2
}

q=""
domain=""
tags=""
entry_type=""
severity=""
include_deprecated=""

while [ $# -gt 0 ]; do
    case "$1" in
        --q)                  q="$2"; shift 2 ;;
        --domain)             domain="$2"; shift 2 ;;
        --tags)               tags="$2"; shift 2 ;;
        --entry-type)         entry_type="$2"; shift 2 ;;
        --severity)           severity="$2"; shift 2 ;;
        --include-deprecated) include_deprecated="true"; shift ;;
        -h|--help)            usage ;;
        *)                    echo "unknown arg: $1" >&2; usage ;;
    esac
done

load_secrets
require_env

build_qs() {
    local qs="" sep="?" k v
    for kv in "$@"; do
        k="${kv%%=*}"
        v="${kv#*=}"
        [ -z "$v" ] && continue
        qs+="${sep}${k}=$(urlencode "$v")"
        sep="&"
    done
    printf '%s' "$qs"
}

if [ -n "$q" ]; then
    qs="$(build_qs "q=$q" "includeDeprecated=$include_deprecated")"
    api_curl "/expertise/search${qs}"
else
    qs="$(build_qs \
        "domain=$domain" \
        "tags=$tags" \
        "entryType=$entry_type" \
        "severity=$severity" \
        "includeDeprecated=$include_deprecated")"
    api_curl "/expertise${qs}"
fi
