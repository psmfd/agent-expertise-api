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
#                        Resolves EXPERTISE_API_TOKEN_FILE indirection first
#                        (issue #464): when EXPERTISE_API_TOKEN is empty and
#                        EXPERTISE_API_TOKEN_FILE names a readable non-empty
#                        file, the token is read from that file. Token-by-path
#                        is the recommended contract for agent hosts — no
#                        bearer literal sits in an env file for scanners or
#                        session hooks to trip on.
#   - api_curl ARGS...   Wrap curl with -sS, Bearer auth, and HTTP-status check.
#                        Writes response body to stdout. On non-2xx, writes the
#                        body to stderr along with the status line and exits 1.
#                        The bearer travels via `curl --config` (a 600-perm
#                        temp file), NEVER argv — `ps` on a shared host must
#                        not see it (issue #486, pattern per ADR-019).
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

# _resolve_token
# Resolution ladder (issue #464), mirrored by the pi extension's
# resolveToken() in .pi/extensions/expertise-api/index.ts:
#   1. EXPERTISE_API_TOKEN set and non-empty       -> wins (explicit beats
#      indirection, the standard *_FILE convention).
#   2. EXPERTISE_API_TOKEN_FILE set                -> read the file; trailing
#      newline/whitespace stripped. A missing, unreadable, or empty file is a
#      hard exit 2 naming the path — never a silent fall-through to "not set",
#      which would misdiagnose a bad path as absent configuration.
#   3. Neither                                     -> leave unset; require_env
#      reports both variables.
_resolve_token() {
    if [ -n "${EXPERTISE_API_TOKEN:-}" ]; then
        return 0
    fi
    if [ -z "${EXPERTISE_API_TOKEN_FILE:-}" ]; then
        return 0
    fi
    if [ ! -f "$EXPERTISE_API_TOKEN_FILE" ] || [ ! -r "$EXPERTISE_API_TOKEN_FILE" ]; then
        echo "error: EXPERTISE_API_TOKEN_FILE points to a missing or unreadable file: ${EXPERTISE_API_TOKEN_FILE}" >&2
        exit 2
    fi
    # $(cat ...) strips trailing newlines; the extra trim handles a file whose
    # last line carries trailing spaces/tabs (e.g. hand-edited token files).
    EXPERTISE_API_TOKEN="$(cat "$EXPERTISE_API_TOKEN_FILE")"
    while [ "${EXPERTISE_API_TOKEN%[[:space:]]}" != "$EXPERTISE_API_TOKEN" ]; do
        EXPERTISE_API_TOKEN="${EXPERTISE_API_TOKEN%[[:space:]]}"
    done
    if [ -z "$EXPERTISE_API_TOKEN" ]; then
        echo "error: EXPERTISE_API_TOKEN_FILE is empty: ${EXPERTISE_API_TOKEN_FILE}" >&2
        exit 2
    fi
}

require_env() {
    _resolve_token
    local missing=0
    if [ -z "${EXPERTISE_API_BASE_URL:-}" ]; then
        echo "error: EXPERTISE_API_BASE_URL is not set" >&2
        missing=1
    fi
    if [ -z "${EXPERTISE_API_TOKEN:-}" ]; then
        echo "error: EXPERTISE_API_TOKEN is not set (set it, or point EXPERTISE_API_TOKEN_FILE at a token file)" >&2
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

# _validate_token_charset TOKEN
# Charset guard before splicing the bearer into a curl config file value:
# inside curl's double-quoted config values only \\ \" \t \n \r \v are
# escapes, '#' starts a comment, and control bytes/whitespace would
# truncate the header. Legitimate bearer tokens (JWT base64url,
# LocalDev dev:{tenant}:{scopes}) contain none of these — fail loudly
# rather than send a corrupted Authorization header. Mirrors apictl's
# _resolve_api_token guard (ADR-019).
_validate_token_charset() {
    # shellcheck disable=SC1003  # '\' below is a one-character backslash pattern, not a quote escape
    case "$1" in
        *[![:graph:]]*|*'"'*|*'\'*|*'#'*)
            echo "error: token contains characters never valid in a bearer token (whitespace, control byte, quote, backslash, or '#') — check your token source" >&2
            exit 2
            ;;
    esac
}

# _make_auth_config [curl-args...]
# Write the injected headers (Authorization, Accept, and — when the
# caller's curl args specify a POST — Idempotency-Key) to a 600-perm
# temp curl config file and echo its path. The bearer never touches
# curl argv this way (issue #486): `ps` shows only `--config <path>`.
# The file is registered in _API_CURL_TMP_FILES so the EXIT trap cleans
# it up on abnormal exit; callers still `rm -f` it immediately after
# the request. Called via normal command substitution (NOT process
# substitution) so an `exit 2` from the token guard or
# _resolve_idempotency_key propagates to the caller process.
_make_auth_config() {
    _validate_token_charset "$EXPERTISE_API_TOKEN"
    local cfg
    cfg="$(mktemp -t expertise-api-cfg.XXXXXX)"
    _API_CURL_TMP_FILES+=("$cfg")
    local idem_key=""
    if _args_have_post_method "$@"; then
        idem_key="$(_resolve_idempotency_key)"
    fi
    # mktemp creates the file 600; the umask subshell is belt-and-braces
    # in case a hardened mktemp replacement honours the caller's umask.
    ( umask 077
      {
          printf 'header = "Authorization: Bearer %s"\n' "$EXPERTISE_API_TOKEN"
          printf 'header = "Accept: application/json"\n'
          if [ -n "$idem_key" ]; then
              printf 'header = "Idempotency-Key: %s"\n' "$idem_key"
          fi
      } > "$cfg"
    )
    printf '%s' "$cfg"
}

# api_curl PATH [curl-args...]
# - PATH starts with '/' (e.g. /expertise/search?q=foo)
# - Bearer token + Accept: application/json injected via a 600-perm
#   `curl --config` temp file, never argv (issue #486, ADR-019).
#   NEVER add -v/--trace* to these curl invocations (or pass them from
#   a caller): curl verbose modes print the Authorization header in
#   cleartext — the standing ADR-019 constraint.
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
    local body_file status cfg
    body_file="$(mktemp -t expertise-api.XXXXXX)"
    _API_CURL_TMP_FILES+=("$body_file")
    cfg="$(_make_auth_config "$@")"

    status="$(curl -sS \
        -o "$body_file" \
        -w '%{http_code}' \
        --config "$cfg" \
        "$@" \
        "$url")" || { rm -f "$cfg"; return 1; }
    rm -f "$cfg"

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
    local body_file status cfg
    body_file="$(mktemp -t expertise-api.XXXXXX)"
    _API_CURL_TMP_FILES+=("$body_file")
    cfg="$(_make_auth_config "$@")"

    status="$(curl -sS \
        -o "$body_file" \
        -w '%{http_code}' \
        --config "$cfg" \
        "$@" \
        "$url")" || { rm -f "$cfg"; return 1; }
    rm -f "$cfg"

    printf '%s' "$status"
    cat "$body_file" >&2
}
