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

# _validate_idempotency_key KEY
# Mirror of the server-side IdempotencyKeyValidator (IETF draft-ietf-
# httpapi-idempotency-key-header-06 §2.2 + ADR-010): 1–255 characters,
# printable ASCII only (0x21–0x7E), no whitespace, no control chars.
# This client-side guard exists primarily to defuse the
# argv/header-injection vector when a caller pre-sets IDEMPOTENCY_KEY:
# a newline in the value would split into extra '-H' header lines (and
# under the previous process-substitution build, into stray curl flags
# entirely — e.g. '-o /tmp/pwn'). Validating before splicing keeps the
# client and server contracts identical and fails loudly rather than
# silently constructing a malformed curl invocation.
_validate_idempotency_key() {
    local key="$1"
    local len=${#key}
    if [ "$len" -lt 1 ] || [ "$len" -gt 255 ]; then
        echo "error: IDEMPOTENCY_KEY length must be 1-255 characters (got $len)" >&2
        exit 2
    fi
    # Reject any char outside printable ASCII (0x21-0x7E): rules out
    # whitespace (incl. \t \r \n), control chars, DEL, and non-ASCII.
    case "$key" in
        *[!\!-\~]*)
            echo "error: IDEMPOTENCY_KEY must contain only printable ASCII (0x21-0x7E); no whitespace or control characters" >&2
            exit 2
            ;;
    esac
}

# _resolve_idempotency_key
# Echo the Idempotency-Key value to use for a POST call. Honours a
# pre-set IDEMPOTENCY_KEY env var (validated for shape) so callers that
# own a retry loop can pin one key across attempts; otherwise mints a
# fresh one via uuidgen. Designed to be called from a normal
# command-substitution context (NOT process substitution) so that an
# `exit 2` from require_cmd / _validate_idempotency_key propagates to
# the caller process and the request fails loudly rather than silently
# emitting an unkeyed POST.
_resolve_idempotency_key() {
    if [ -n "${IDEMPOTENCY_KEY:-}" ]; then
        _validate_idempotency_key "$IDEMPOTENCY_KEY"
        printf '%s' "$IDEMPOTENCY_KEY"
        return 0
    fi
    require_cmd uuidgen
    uuidgen
}

# api_curl PATH [curl-args...]
# - PATH starts with '/' (e.g. /expertise/search?q=foo)
# - Bearer token + Accept: application/json injected.
# - On POST (detected via '-X POST' / '--request POST'), an
#   Idempotency-Key header is injected automatically. Default value is
#   `uuidgen`; pre-set IDEMPOTENCY_KEY in the environment to pin a key
#   across an outer retry loop (server-side replay per ADR-010).
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
    if _args_have_post_method "$@"; then
        local _idem_key
        _idem_key="$(_resolve_idempotency_key)"
        idem_args=(-H "Idempotency-Key: ${_idem_key}")
    fi

    # ${idem_args[@]+"${idem_args[@]}"} expands to nothing when the
    # array is empty, which is safe under `set -u` on bash 3.2 (macOS
    # system bash) as well as bash 4.4+. The plain "${idem_args[@]}"
    # form raises 'unbound variable' on bash < 4.4 when the array is
    # empty, which would break every non-POST request on a developer
    # workstation running /bin/bash.
    status="$(curl -sS \
        -o "$body_file" \
        -w '%{http_code}' \
        -H "Authorization: Bearer ${EXPERTISE_API_TOKEN}" \
        -H 'Accept: application/json' \
        ${idem_args[@]+"${idem_args[@]}"} \
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
    if _args_have_post_method "$@"; then
        local _idem_key
        _idem_key="$(_resolve_idempotency_key)"
        idem_args=(-H "Idempotency-Key: ${_idem_key}")
    fi

    status="$(curl -sS \
        -o "$body_file" \
        -w '%{http_code}' \
        -H "Authorization: Bearer ${EXPERTISE_API_TOKEN}" \
        -H 'Accept: application/json' \
        ${idem_args[@]+"${idem_args[@]}"} \
        "$@" \
        "$url")"

    printf '%s' "$status"
    cat "$body_file" >&2
}
