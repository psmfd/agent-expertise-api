#!/usr/bin/env bash
# .agents/skills/expertise-api/scripts/lib/common.sh
#
# Shared helpers for the expertise-api skill scripts. Source from each script:
#
#   # shellcheck source=lib/common.sh
#   . "$(dirname "$0")/lib/common.sh"
#
# Provides:
#   - load_secrets       Source ~/.config/expertise-api/secrets.env if present.
#   - require_env        Fail loudly if EXPERTISE_API_BASE_URL/_TOKEN unset.
#   - api_curl ARGS...   Wrap curl with -sS, Bearer auth, and HTTP-status check.
#                        Writes response body to stdout. On non-2xx, writes the
#                        body to stderr along with the status line and exits 1.
#   - urlencode STR      RFC 3986 percent-encoding for query-string values.
#   - require_cmd CMD    Fail loudly if a required CLI is missing.
#
# Idempotency contract (ADR-010, issue #205):
#   api_curl / api_curl_status inject an Idempotency-Key header on any
#   request whose curl args specify '-X POST' (or '--request POST').
#   Default key is `uuidgen`; callers can pre-set IDEMPOTENCY_KEY in the
#   environment to pin a key across a retry loop or to drive an
#   intentional server-side replay. The header is scoped to POST to
#   match the server-side filter (server records only writes).

set -euo pipefail

# Track every temp file created by api_curl across the lifetime of the calling
# process so the EXIT trap cleans them all up. Bash replaces (not appends) the
# EXIT trap on each `trap ... EXIT` call, so installing the trap per-invocation
# would clobber prior entries and leak temp files when a script calls api_curl
# more than once (e.g. skill-smoke-test.sh, which calls it ~6 times).
_API_CURL_TMP_FILES=()
_api_curl_cleanup() {
    if [ "${#_API_CURL_TMP_FILES[@]}" -gt 0 ]; then
        rm -f "${_API_CURL_TMP_FILES[@]}" 2>/dev/null || true
    fi
}
trap _api_curl_cleanup EXIT

load_secrets() {
    local secrets_file="${EXPERTISE_API_SECRETS_FILE:-${HOME}/.config/expertise-api/secrets.env}"
    if [ -f "$secrets_file" ]; then
        # shellcheck disable=SC1090
        . "$secrets_file"
    fi
}

require_env() {
    local missing=0
    if [ -z "${EXPERTISE_API_BASE_URL:-}" ]; then
        echo "error: EXPERTISE_API_BASE_URL is not set" >&2
        missing=1
    fi
    if [ -z "${EXPERTISE_API_TOKEN:-}" ]; then
        echo "error: EXPERTISE_API_TOKEN is not set" >&2
        missing=1
    fi
    if [ "$missing" -ne 0 ]; then
        echo "hint: export the variables or write them to ~/.config/expertise-api/secrets.env" >&2
        exit 2
    fi
    # Strip any trailing slash from the base URL so callers can append paths cleanly.
    EXPERTISE_API_BASE_URL="${EXPERTISE_API_BASE_URL%/}"
    export EXPERTISE_API_BASE_URL
}

require_cmd() {
    local cmd="$1"
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo "error: required command '$cmd' not found on PATH" >&2
        exit 2
    fi
}

urlencode() {
    # Pure-bash RFC 3986 percent-encoding. Reserves [A-Za-z0-9._~-].
    local s="${1-}" out="" c
    local i
    for ((i = 0; i < ${#s}; i++)); do
        c="${s:i:1}"
        case "$c" in
            [a-zA-Z0-9._~-]) out+="$c" ;;
            *) printf -v c '%%%02X' "'$c"; out+="$c" ;;
        esac
    done
    printf '%s' "$out"
}

# _args_have_post_method ARGS...
# Returns 0 (true) if the curl arg list specifies an HTTP POST via either
# '-X POST' or '--request POST' (separate-arg form). Returns 1 otherwise.
#
# The skill's three POST scripts (create.sh, approve.sh, reject.sh) and
# the smoke-test reject-after-approve negative path all use the literal
# '-X POST' separate-arg form, which this helper matches. The joined
# forms '-XPOST' / '--request=POST' are not produced by any current
# caller; if a future caller uses them, extend the detector below.
_args_have_post_method() {
    local prev="" arg
    for arg in "$@"; do
        case "$prev" in
            -X|--request)
                if [ "$arg" = "POST" ]; then
                    return 0
                fi
                ;;
        esac
        prev="$arg"
    done
    return 1
}

# _idempotency_header_args ARGS...
# If ARGS specify an HTTP POST, prints two lines suitable for splicing
# into curl args (one element per line, read into an array via a
# `while IFS= read` loop): a literal '-H' followed by the
# 'Idempotency-Key: <key>' value. Honours a pre-set IDEMPOTENCY_KEY env
# var so callers that own a retry loop (or that need to drive the
# server-side replay path explicitly) can pin the key for all attempts.
# Emits nothing when ARGS are not a POST.
#
# Server contract (ADR-010): /expertise, /expertise/{id}/approve and
# /expertise/{id}/reject record (tenant, key) for the configured TTL.
# Sending the header on non-write paths is harmless but wasted, so we
# scope injection to POST to match the server-side filter exactly.
_idempotency_header_args() {
    if ! _args_have_post_method "$@"; then
        return 0
    fi
    require_cmd uuidgen
    local key="${IDEMPOTENCY_KEY:-$(uuidgen)}"
    printf -- '-H\n'
    printf 'Idempotency-Key: %s\n' "$key"
}

# api_curl PATH [curl-args...]
# - PATH starts with '/' (e.g. /expertise/search?q=foo)
# - Bearer token + Accept: application/json injected.
# - On POST (detected via '-X POST' / '--request POST'), an
#   Idempotency-Key header is injected automatically (uuidgen) unless
#   the caller has pre-set IDEMPOTENCY_KEY in the environment. See
#   _idempotency_header_args above for the retry-pinning contract.
# - Captures body to a temp file and status code separately so we can
#   surface non-2xx responses with the body verbatim.
api_curl() {
    require_cmd curl
    local path="$1"; shift
    local url="${EXPERTISE_API_BASE_URL}${path}"
    local body_file status
    body_file="$(mktemp -t expertise-api.XXXXXX)"
    _API_CURL_TMP_FILES+=("$body_file")

    local idem_args=()
    while IFS= read -r line; do
        idem_args+=("$line")
    done < <(_idempotency_header_args "$@")

    status="$(curl -sS \
        -o "$body_file" \
        -w '%{http_code}' \
        -H "Authorization: Bearer ${EXPERTISE_API_TOKEN}" \
        -H 'Accept: application/json' \
        "${idem_args[@]}" \
        "$@" \
        "$url")"

    case "$status" in
        2??)
            cat "$body_file"
            return 0
            ;;
        *)
            echo "error: HTTP ${status} from ${url}" >&2
            cat "$body_file" >&2
            echo >&2
            return 1
            ;;
    esac
}

# api_curl_status PATH [curl-args...]
# Same as api_curl but writes the HTTP status code to stdout and the response
# body to stderr (used by smoke tests that need to assert on specific status
# codes without treating non-2xx as a hard failure). Returns 0 regardless of
# the HTTP status, so callers must inspect the captured status themselves.
api_curl_status() {
    require_cmd curl
    local path="$1"; shift
    local url="${EXPERTISE_API_BASE_URL}${path}"
    local body_file status
    body_file="$(mktemp -t expertise-api.XXXXXX)"
    _API_CURL_TMP_FILES+=("$body_file")

    local idem_args=()
    while IFS= read -r line; do
        idem_args+=("$line")
    done < <(_idempotency_header_args "$@")

    status="$(curl -sS \
        -o "$body_file" \
        -w '%{http_code}' \
        -H "Authorization: Bearer ${EXPERTISE_API_TOKEN}" \
        -H 'Accept: application/json' \
        "${idem_args[@]}" \
        "$@" \
        "$url")"

    printf '%s' "$status"
    cat "$body_file" >&2
}
