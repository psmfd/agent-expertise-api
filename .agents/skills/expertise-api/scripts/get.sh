#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/get.sh
#
# Fetch a single expertise entry: GET /expertise/{id}.

set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/common.sh
. "${here}/lib/common.sh"

usage() {
    echo "usage: get.sh <id>" >&2
    exit 2
}

[ $# -eq 1 ] || usage
id="$1"
case "$id" in -h|--help) usage ;; esac

load_secrets
require_env
api_curl "/expertise/$(urlencode "$id")"
