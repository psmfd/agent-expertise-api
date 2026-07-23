#!/usr/bin/env bash
# release-consumer.sh — D3 of ADR-011: helpers that turn a published
# GitHub Release (tarball + manifest + cosign signature) into a staged
# bin-tree ready for install.sh's atomic_swap.
#
# Sourced by install.sh; not directly executable. Functions are prefixed
# rc_ to avoid name collisions in the parent shell.
#
# Contract with install.sh:
#   * Caller has resolved OS, RID, REPO, STAGE_DIR, PREFIX.
#   * Caller has defined log()/warn()/err() (install.sh helpers).
#   * Caller will run the existing atomic_swap + migrate flow on STAGE_DIR
#     after rc_publish_from_release() returns successfully.
#   * Mode + semver + history markers are written by install.sh after
#     atomic_swap so they reflect committed state (not interim state).
#
# Constants for cosign live in scripts/verify-release.sh — sourced via
# SOURCE_ONLY=1 so there is exactly ONE definition of the trust root.

# ---------------------------------------------------------------------------
# RELEASE_REPO — the {owner}/{repo} slug that owns the release artifacts.
# Hard-coded; forks must edit. Mirrors the COSIGN_IDENTITIES constant in
# scripts/verify-release.sh — if you change one without the other the
# verification step will refuse the install with a clear error.
# (Owner renamed TheSemicolon -> psmfd, 2026-06, #294 — the old slug only
# worked via GitHub's 301 redirect, which survives just until the old
# name is reclaimed.)
# ---------------------------------------------------------------------------
readonly RELEASE_REPO="psmfd/agent-expertise-api"

# ---------------------------------------------------------------------------
# rc_source_verify_release — load constants + helpers from verify-release.sh
# into the calling shell. Idempotent.
# ---------------------------------------------------------------------------
rc_source_verify_release() {
  if [ "${RC_VERIFY_RELEASE_SOURCED:-0}" = "1" ]; then return 0; fi
  # shellcheck source=verify-release.sh disable=SC1091
  SOURCE_ONLY=1 . "${SCRIPT_DIR}/verify-release.sh"
  RC_VERIFY_RELEASE_SOURCED=1
}

# ---------------------------------------------------------------------------
# rc_curl_https — hardened curl invocation for fetching release artifacts.
#
# Asserts https-only redirect chain, TLS >= 1.2, fail-fast on 4xx/5xx,
# bounded retry, bounded total time. Writes to ${dest}.part and atomically
# renames on success so a half-written file never reaches the verifier.
#
# Args: dest_path url
# ---------------------------------------------------------------------------
rc_curl_https() {
  local dest=$1 url=$2
  command -v curl >/dev/null 2>&1 || err "curl required for --from-release (not found in PATH)"

  # Detect curl flag availability once. --fail-with-body and
  # --retry-all-errors are 7.71+ / 7.76+; Ubuntu 20.04 ships 7.68 which
  # lacks both. Use `curl --help` (universal across 7.x) and grep for the
  # flag name. D3 pre-PR (shell-expert LOW): `curl --help all` is 7.73+
  # and itself errors on older binaries — the feature-detect worked by
  # accident before this fix.
  local fail_flag="--fail"
  local retry_all_flag=""
  local curl_help; curl_help=$(curl --help 2>&1 || true)
  if printf '%s' "$curl_help" | grep -q -- '--fail-with-body'; then
    fail_flag="--fail-with-body"
  fi
  if printf '%s' "$curl_help" | grep -q -- '--retry-all-errors'; then
    retry_all_flag="--retry-all-errors"
  fi

  log "fetching $(basename -- "$dest") from ${url}"
  # shellcheck disable=SC2086  # retry_all_flag is intentionally word-split
  curl --proto '=https' --tlsv1.2 \
       $fail_flag \
       --location --max-redirs 5 \
       --retry 3 --retry-delay 2 --retry-connrefused $retry_all_flag \
       --connect-timeout 10 --max-time 300 \
       --silent --show-error \
       --output "${dest}.part" \
       "$url" \
    || { rm -f -- "${dest}.part"; err "curl failed: ${url}"; }
  mv -f -- "${dest}.part" "$dest"
}

# ---------------------------------------------------------------------------
# rc_resolve_version — translate operator-supplied --version into a concrete
# tag string. If "latest", call the GitHub Releases API. If explicit, return
# verbatim. Caller is responsible for the first-install policy (refuse
# "latest" without a prior semver marker) — see install.sh main flow.
#
# Echos the resolved tag (without leading 'v') on stdout.
# Args: requested_version (literal or "latest")
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# rc_assert_semver — fail closed if a resolved version string is not plain
# semver (X.Y.Z, optional prerelease/build suffix). Guards the command-
# substitution seam between rc_resolve_version and its callers: any stray
# stdout (a mis-redirected log line, curl noise) becomes a clear one-line
# diagnosis instead of a corrupted release-tag URL downstream (#440).
# Args: candidate version string (no leading v)
# ---------------------------------------------------------------------------
rc_assert_semver() {
  # Reject embedded newlines first: grep matches per-line, so a log-polluted
  # multi-line value with a valid semver on its last line would slip through.
  local nl; nl=$(printf '\nx'); nl=${nl%x}
  case "$1" in
    *"$nl"*) err "resolved version contains a newline — resolver stdout is polluted (#440)" ;;
  esac
  printf '%s\n' "$1" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.+-]+)?$' \
    || err "resolved version '$1' is not a semver string — resolver stdout may be polluted (#440)"
}

rc_resolve_version() {
  local requested=$1
  if [ "$requested" != "latest" ]; then
    # Strip leading v for the manifest comparison (manifest's appVersion
    # has no leading v per generate-manifest.sh contract).
    printf '%s\n' "${requested#v}"
    return 0
  fi
  command -v jq >/dev/null 2>&1 || err "jq required to resolve --version latest"
  # stdout of this function IS its return value (command substitution at the
  # call site) — log lines must go to stderr or they corrupt the resolved
  # version string (#440).
  log "resolving --version latest via GitHub Releases API" >&2
  local api_url="https://api.github.com/repos/${RELEASE_REPO}/releases/latest"
  # D3 pre-PR (shell-expert LOW): use the unambiguous `mktemp TEMPLATE`
  # form instead of `mktemp -t prefix` (BSD treats -t as prefix-only,
  # GNU treats it as template-requires-XXX — different filename shapes).
  local tmp; tmp=$(mktemp "${TMPDIR:-/tmp}/expertise-api-release.XXXXXX")
  # Pass GH_TOKEN/GITHUB_TOKEN through when set (60/h → 5000/h limit lift)
  # but never require it; the repo is public.
  local auth_header=()
  if [ -n "${GH_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GH_TOKEN}")
  elif [ -n "${GITHUB_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
  fi
  # D3 pre-PR (security-review LOW): pin --max-redirs for parity with
  # rc_curl_https — defense-in-depth against a misbehaving proxy or a
  # future GitHub redirect-chain change.
  if ! curl --proto '=https' --tlsv1.2 --fail --location --max-redirs 5 \
       --connect-timeout 10 --max-time 30 --silent --show-error \
       -H 'Accept: application/vnd.github+json' \
       ${auth_header[@]+"${auth_header[@]}"} \
       -o "$tmp" \
       "$api_url"; then
    rm -f -- "$tmp"
    err "GitHub Releases API request failed: ${api_url}"
  fi
  local tag
  tag=$(jq -r '.tag_name // empty' "$tmp")
  rm -f -- "$tmp"
  [ -n "$tag" ] || err "GitHub Releases API returned no tag_name"
  printf '%s\n' "${tag#v}"
}

# ---------------------------------------------------------------------------
# rc_crosscheck_release_api — independent second-trust-path check. Confirm
# the GH Releases API for /releases/tags/v${version} returns a tag_name
# matching ${version}. Closes the swap-asset-name attack vector (an
# adversary with contents:write can rename an old legitimately-signed
# tarball over a new version, but they cannot change the API's
# tag_name → asset list binding without also forging api.github.com).
#
# Skipped when SKIP_RELEASE_API_CROSSCHECK=1 (operator opt-out for air-gap).
#
# Args: version (no leading v)
# ---------------------------------------------------------------------------
rc_crosscheck_release_api() {
  local version=$1
  if [ "${SKIP_RELEASE_API_CROSSCHECK:-0}" = "1" ]; then
    warn "GitHub Releases API cross-check disabled by --skip-release-api-crosscheck — manifest signature is the sole trust anchor"
    return 0
  fi
  command -v jq >/dev/null 2>&1 || err "jq required for release-api cross-check"
  local api_url="https://api.github.com/repos/${RELEASE_REPO}/releases/tags/v${version}"
  log "release-api cross-check: ${api_url}"
  local tmp; tmp=$(mktemp "${TMPDIR:-/tmp}/expertise-api-crosscheck.XXXXXX")
  local auth_header=()
  if [ -n "${GH_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GH_TOKEN}")
  elif [ -n "${GITHUB_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
  fi
  if ! curl --proto '=https' --tlsv1.2 --fail --location --max-redirs 5 \
       --connect-timeout 10 --max-time 30 --silent --show-error \
       -H 'Accept: application/vnd.github+json' \
       ${auth_header[@]+"${auth_header[@]}"} \
       -o "$tmp" \
       "$api_url"; then
    rm -f -- "$tmp"
    err "release-api cross-check FAILED: cannot fetch ${api_url}"
  fi
  local api_tag
  api_tag=$(jq -r '.tag_name // empty' "$tmp")
  # Also assert the four expected asset filenames are present in the API's
  # asset listing — defends against a partial-asset swap (e.g. attacker
  # uploads a tarball but not the manifest, hoping the operator silently
  # falls back to source mode).
  # Asset names carry the BARE version (no leading v) — release.yml names
  # them from NEW_VERSION ("expertise-api-1.0.0-portable.tar.gz") per the
  # generate-manifest.sh contract. Only the git tag itself is v-prefixed.
  # Caught by the first E3 CI run against v1.0.0 (#260).
  local expected_tarball="expertise-api-${version}-portable.tar.gz"
  local expected_manifest="expertise-api-${version}.manifest.json"
  local missing=""
  local name
  for name in "$expected_tarball" \
              "${expected_tarball}.sha256" \
              "$expected_manifest" \
              "${expected_manifest}.sigstore.json"; do
    if ! jq -e --arg n "$name" '.assets[] | select(.name == $n)' "$tmp" >/dev/null 2>&1; then
      missing="${missing} ${name}"
    fi
  done
  rm -f -- "$tmp"

  if [ "$api_tag" != "v${version}" ]; then
    err "release-api cross-check FAILED: requested v${version} but API tag_name='${api_tag}'"
  fi
  if [ -n "$missing" ]; then
    err "release-api cross-check FAILED: assets missing from release v${version}:${missing}"
  fi
  log "release-api cross-check: tag_name=v${version}, all 4 expected assets present"
}

# ---------------------------------------------------------------------------
# rc_download_release_artifacts — fetch tarball + sha + manifest + cosign
# bundle into ${dest_dir}. Caller is responsible for prior validation (version
# resolved, release-api cross-checked).
#
# Args: dest_dir version
# Globals (export): TARBALL_PATH MANIFEST_PATH BUNDLE_PATH
# ---------------------------------------------------------------------------
rc_download_release_artifacts() {
  local dest_dir=$1 version=$2
  mkdir -p "$dest_dir"
  local base="https://github.com/${RELEASE_REPO}/releases/download/v${version}"
  # Bare version in asset names; v-prefix only on the tag path segment above.
  local tarball="expertise-api-${version}-portable.tar.gz"
  local manifest="expertise-api-${version}.manifest.json"

  rc_curl_https "${dest_dir}/${tarball}"                 "${base}/${tarball}"
  rc_curl_https "${dest_dir}/${tarball}.sha256"          "${base}/${tarball}.sha256"
  rc_curl_https "${dest_dir}/${manifest}"                "${base}/${manifest}"
  # Single self-contained Sigstore bundle (sig + cert + Rekor proof) replaces
  # the legacy detached .sig/.pem pair (ADR-011 Amendment 1, #399).
  rc_curl_https "${dest_dir}/${manifest}.sigstore.json"  "${base}/${manifest}.sigstore.json"

  TARBALL_PATH="${dest_dir}/${tarball}"
  MANIFEST_PATH="${dest_dir}/${manifest}"
  BUNDLE_PATH="${dest_dir}/${manifest}.sigstore.json"
}

# ---------------------------------------------------------------------------
# _rc_semver_lt — SemVer 2.0 §11-aware less-than comparator.
#
# Returns 0 (true) if version $1 < version $2, 1 (false) otherwise.
# Ignores build metadata (strips +... before comparing).
# Handles prerelease ordering per §11: a version with prerelease IS lower
# than the same core version without prerelease (e.g. 1.0.0-rc.1 < 1.0.0).
# Prerelease dot-tokens are compared: numerically when both are digits,
# ASCII-lexically otherwise. Numeric tokens sort lower than alphanumeric.
# Fewer tokens < more tokens when all shared tokens are equal.
#
# Bash 3.2 safe: uses set --/positional-param splitting; no arrays at file
# scope; no ${var,,}; no mapfile/readarray/declare -A.
#
# Args: ver_a ver_b
# Returns: 0 if ver_a < ver_b, 1 if ver_a >= ver_b
# ---------------------------------------------------------------------------
_rc_semver_lt() {
  # Strip build metadata (everything from first '+' onward) before parsing.
  local a b
  # ${var%%+*} strips from the first '+' to end-of-string.
  a="${1%%+*}"
  b="${2%%+*}"

  # Split each version into core (X.Y.Z) and prerelease parts.
  local a_core a_pre b_core b_pre
  case "$a" in
    *-*) a_core="${a%%-*}"; a_pre="${a#*-}" ;;
    *)   a_core="$a";        a_pre="" ;;
  esac
  case "$b" in
    *-*) b_core="${b%%-*}"; b_pre="${b#*-}" ;;
    *)   b_core="$b";        b_pre="" ;;
  esac

  # Compare X.Y.Z numerically field-by-field using IFS-split into positionals.
  # We compare three fields; if any comparison is definitive we return early.
  local IFS=.
  # shellcheck disable=SC2086  # intentional word-split on IFS
  set -- $a_core
  local a_maj=$1 a_min=$2 a_pat=$3
  # shellcheck disable=SC2086
  set -- $b_core
  local b_maj=$1 b_min=$2 b_pat=$3

  # Compare major
  if [ "$a_maj" -lt "$b_maj" ] 2>/dev/null; then return 0; fi
  if [ "$a_maj" -gt "$b_maj" ] 2>/dev/null; then return 1; fi
  # Compare minor
  if [ "$a_min" -lt "$b_min" ] 2>/dev/null; then return 0; fi
  if [ "$a_min" -gt "$b_min" ] 2>/dev/null; then return 1; fi
  # Compare patch
  if [ "$a_pat" -lt "$b_pat" ] 2>/dev/null; then return 0; fi
  if [ "$a_pat" -gt "$b_pat" ] 2>/dev/null; then return 1; fi

  # Core X.Y.Z is equal. Apply §11 prerelease rules.
  # A version with no prerelease is GREATER than any same-core prerelease.
  if [ -z "$a_pre" ] && [ -z "$b_pre" ]; then return 1; fi  # equal → not lt
  if [ -z "$a_pre" ] && [ -n "$b_pre" ]; then return 1; fi  # 1.0.0 > 1.0.0-rc1
  if [ -n "$a_pre" ] && [ -z "$b_pre" ]; then return 0; fi  # 1.0.0-rc1 < 1.0.0

  # Both have prerelease; compare dot-token by dot-token.
  # Traverse tokens in lockstep using IFS split onto positionals.
  local a_tok b_tok a_rest b_rest
  a_rest="$a_pre"
  b_rest="$b_pre"
  while true; do
    # Extract next dot-delimited token from each side.
    case "$a_rest" in
      *.*) a_tok="${a_rest%%.*}"; a_rest="${a_rest#*.}" ;;
      *)   a_tok="$a_rest"; a_rest="" ;;
    esac
    case "$b_rest" in
      *.*) b_tok="${b_rest%%.*}"; b_rest="${b_rest#*.}" ;;
      *)   b_tok="$b_rest"; b_rest="" ;;
    esac

    # Detect if both tokens are purely numeric (no leading zeros check per
    # spec, but §11.4 says "identifiers MUST comprise only ASCII alphanumerics
    # and hyphens"; we classify digit-only strings as numeric).
    local a_is_num b_is_num
    case "$a_tok" in
      *[!0-9]*) a_is_num=0 ;;
      '') a_is_num=0 ;;
      *) a_is_num=1 ;;
    esac
    case "$b_tok" in
      *[!0-9]*) b_is_num=0 ;;
      '') b_is_num=0 ;;
      *) b_is_num=1 ;;
    esac

    if [ "$a_is_num" = "1" ] && [ "$b_is_num" = "1" ]; then
      # Both numeric: compare as integers.
      if [ "$a_tok" -lt "$b_tok" ]; then return 0; fi
      if [ "$a_tok" -gt "$b_tok" ]; then return 1; fi
    elif [ "$a_is_num" = "1" ] && [ "$b_is_num" = "0" ]; then
      # Numeric < alphanumeric per §11.4.1.
      return 0
    elif [ "$a_is_num" = "0" ] && [ "$b_is_num" = "1" ]; then
      # Alphanumeric > numeric per §11.4.1.
      return 1
    else
      # Both alphanumeric: ASCII lexical order.
      if [ "$a_tok" \< "$b_tok" ]; then return 0; fi
      if [ "$a_tok" \> "$b_tok" ]; then return 1; fi
    fi

    # Tokens were equal; check if either side is exhausted.
    if [ -z "$a_rest" ] && [ -z "$b_rest" ]; then return 1; fi  # equal → not lt
    if [ -z "$a_rest" ] && [ -n "$b_rest" ]; then return 0; fi  # fewer tokens < more
    if [ -n "$a_rest" ] && [ -z "$b_rest" ]; then return 1; fi  # more tokens > fewer
  done
}

# ---------------------------------------------------------------------------
# rc_enforce_downgrade_defense — refuse if manifest.appVersion is older than
# the recorded ${PREFIX}/.install-version-semver (release-mode only). Also
# refuse same-version reinstalls with a DIFFERENT manifest sha (republish
# vector) unless --accept-republished-version is passed.
#
# Args: incoming_version incoming_manifest_sha
# Globals: PREFIX, ALLOW_DOWNGRADE, ACCEPT_REPUBLISHED_VERSION
# ---------------------------------------------------------------------------
rc_enforce_downgrade_defense() {
  local incoming_ver=$1 incoming_sha=$2
  local marker="${PREFIX}/.install-version-semver"

  # Refuse to read through a symlink (matches existing install.sh markers).
  if [ -L "$marker" ]; then
    err "${marker} exists as a symlink — refusing to read (potential information disclosure)"
  fi
  if [ ! -r "$marker" ]; then
    log "downgrade defense: no prior release-mode marker (${marker}); permitting first --from-release install"
    return 0
  fi

  local prior_ver prior_sha
  prior_ver=$(LC_ALL=C grep -m1 -E '^appVersion=' "$marker" | sed 's/^appVersion=//' | LC_ALL=C tr -cd '[:alnum:].+-' | cut -c1-64 || true)
  prior_sha=$(LC_ALL=C grep -m1 -E '^manifestSha256=' "$marker" | sed 's/^manifestSha256=//' | LC_ALL=C tr -cd '[:alnum:]' | cut -c1-64 || true)

  if [ -z "$prior_ver" ]; then
    warn "marker ${marker} is malformed (no appVersion=); treating as absent"
    return 0
  fi

  # Validate both shapes: must be at least X.Y.Z (with optional prerelease/
  # build metadata). Garbage input is rejected before reaching the comparator.
  local semver_re='^[0-9]+\.[0-9]+\.[0-9]+([+.a-zA-Z0-9-]*)$'
  if ! printf '%s' "$prior_ver" | grep -qE "$semver_re"; then
    warn "prior version '${prior_ver}' is not valid semver (X.Y.Z[-pre][+build]); skipping downgrade compare"
    return 0
  fi
  if ! printf '%s' "$incoming_ver" | grep -qE "$semver_re"; then
    err "appVersion '${incoming_ver}' is not valid semver (X.Y.Z[-pre][+build]); cannot proceed"
  fi

  if _rc_semver_lt "$incoming_ver" "$prior_ver"; then
    if [ "${ALLOW_DOWNGRADE:-0}" = "1" ]; then
      warn "downgrade ${prior_ver} -> ${incoming_ver} permitted by --allow-downgrade"
    else
      err "downgrade refused: ${incoming_ver} < ${prior_ver}. Pass --allow-downgrade to override (cosign verification still applies)."
    fi
  elif [ "$prior_ver" = "$incoming_ver" ] && [ -n "$prior_sha" ] && [ "$prior_sha" != "$incoming_sha" ]; then
    if [ "${ACCEPT_REPUBLISHED_VERSION:-0}" = "1" ]; then
      warn "republished version ${incoming_ver} (manifest sha changed: ${prior_sha:0:16}... -> ${incoming_sha:0:16}...) permitted by --accept-republished-version"
    else
      err "republished version refused: ${incoming_ver} already installed but manifest sha differs (prior=${prior_sha:0:16}... new=${incoming_sha:0:16}...). Pass --accept-republished-version to override; investigate whether the release was legitimately re-signed."
    fi
  else
    log "downgrade defense: OK (prior=${prior_ver}, incoming=${incoming_ver})"
  fi
}

# ---------------------------------------------------------------------------
# rc_check_aspnetcore_runtime_floor — semver compare against an arg-supplied
# floor. Uses _rc_semver_lt for §11-aware comparison so prerelease runtimes
# (e.g. -preview, -rc) are ordered correctly; they remain excluded from
# candidates because the .NET host refuses to roll-forward from a release
# floor onto a prerelease without explicit rollForwardToPreRelease.
#
# Args: required_min (e.g. "10.0.0")
# ---------------------------------------------------------------------------
rc_check_aspnetcore_runtime_floor() {
  local floor=$1
  command -v dotnet >/dev/null 2>&1 \
    || err "dotnet CLI not found in PATH (required to verify the ASP.NET Core runtime floor; install ASP.NET Core ${floor} runtime via https://dot.net)"

  local semver_re='^[0-9]+\.[0-9]+\.[0-9]+$'
  if ! printf '%s' "$floor" | grep -qE "$semver_re"; then
    err "invalid required runtime floor (not X.Y.Z semver): ${floor}"
  fi

  # Filter to the same major; exclude prereleases (hyphenated versions) because
  # the .NET host's rollForward policy does not roll onto them automatically.
  local need_major
  need_major=$(printf '%s' "$floor" | cut -d. -f1)
  local installed
  installed=$(dotnet --list-runtimes 2>/dev/null \
    | awk '$1=="Microsoft.AspNetCore.App" {print $2}' \
    | grep -v -- '-' \
    | awk -F. -v M="$need_major" '$1==M' || true)

  if [ -z "$installed" ]; then
    err "no ASP.NET Core ${need_major}.x runtime installed (need >= ${floor}); install via https://dot.net"
  fi

  # Walk installed versions; find the highest one that satisfies >= floor.
  # Uses _rc_semver_lt so the comparison is §11-aware (no sort -V gap).
  local satisfied="" best_found="" ver
  while IFS= read -r ver; do
    [ -n "$ver" ] || continue
    # ver >= floor ↔ NOT (ver < floor)
    if ! _rc_semver_lt "$ver" "$floor"; then
      satisfied="yes"
      # Track the highest: best_found < ver → replace best_found.
      if [ -z "$best_found" ] || _rc_semver_lt "$best_found" "$ver"; then
        best_found="$ver"
      fi
    fi
  done <<EOF
$installed
EOF

  if [ -z "$satisfied" ]; then
    err "ASP.NET Core runtime ${need_major}.x installed but none >= ${floor}; installed: $(printf '%s' "$installed" | tr '\n' ',')"
  fi
  log "ASP.NET Core runtime floor: ${floor} satisfied (highest installed: ${best_found})"
}

# ---------------------------------------------------------------------------
# rc_select_tar — pick bsdtar if present, else GNU tar with hardening flags.
# Echoes a sentinel: "bsdtar" or "gnutar". Errors if neither qualifies.
# ---------------------------------------------------------------------------
rc_select_tar() {
  if command -v bsdtar >/dev/null 2>&1; then
    printf 'bsdtar\n'; return 0
  fi
  if command -v tar >/dev/null 2>&1; then
    local ver
    ver=$(tar --version 2>&1 | head -1 || true)
    case "$ver" in
      *bsdtar*) printf 'bsdtar\n'; return 0 ;;
      *"GNU tar"*|*"gnu tar"*) printf 'gnutar\n'; return 0 ;;
    esac
    # macOS ships bsdtar as `tar`; if --version contains neither marker,
    # probe for libarchive-only flags.
    if tar --version 2>&1 | grep -qi libarchive; then
      printf 'bsdtar\n'; return 0
    fi
  fi
  err "no suitable tar found (need bsdtar or GNU tar); install bsdtar (Linux: apt install libarchive-tools / yum install bsdtar; macOS: default tar is bsdtar)"
}

# ---------------------------------------------------------------------------
# rc_inspect_staged_tree — refuse symlinks, special files, setuid/setgid,
# case-folding-collision pairs, over-long paths, traversal-shaped names,
# and newline-bearing names. Walks the tree once with `find -print0` so
# filenames containing whitespace, newlines, or non-ASCII bytes are not
# silently mis-attributed; reports every violation class before erroring.
#
# D3 pre-PR (shell-expert HIGH): the previous implementation used `find
# -print | awk` and `LC_ALL=C tolower()`, both of which were bypassable
# against a hostile tarball — the threat model this function defends.
# `find -print` splits on `\n` which legal filenames may contain; and
# `LC_ALL=C tolower()` only lowercases A–Z, missing Unicode case folds
# (Café/café, Ω/ω) on APFS/HFS+/NTFS — exactly the case-insensitive
# filesystems this check targets.
#
# Args: root_path
# ---------------------------------------------------------------------------
rc_inspect_staged_tree() {
  local root=$1
  local violations=0
  local tmp_paths; tmp_paths=$(mktemp "${TMPDIR:-/tmp}/rc-inspect-paths.XXXXXX")
  local tmp_bad; tmp_bad=$(mktemp "${TMPDIR:-/tmp}/rc-inspect-bad.XXXXXX")

  # 0. Pre-pass: enumerate ALL paths NUL-delimited. macOS BSD awk does not
  # honor RS="\0" (gawk does, but we cannot assume gawk), so all NUL-record
  # scans below use bash `read -d ''` loops which are portable across
  # BSD/GNU.
  find "$root" -print0 > "$tmp_paths" 2>/dev/null || true

  # 0a. Reject newline-bearing names up front. Count raw \n bytes in the
  # NUL-delimited stream: each one is a filename character (find's record
  # separator is NUL, not \n).
  local newline_count
  newline_count=$(LC_ALL=C tr -cd '\n' < "$tmp_paths" | wc -c | tr -d ' ')
  if [ "$newline_count" -gt 0 ]; then
    warn "tarball contains ${newline_count} path(s) with embedded newline characters (refused; would break safety checks)"
    violations=$((violations + 1))
  fi

  # 1. Symlinks / block / char / fifo / socket — refused.
  find "$root" \( -type l -o -type b -o -type c -o -type p -o -type s \) -print0 \
    >"$tmp_bad" 2>/dev/null || true
  if [ -s "$tmp_bad" ]; then
    warn "tarball contains symlinks or special files (refused):"
    local _p
    while IFS= read -r -d '' _p; do printf '  %s\n' "$_p" >&2; done < "$tmp_bad"
    violations=$((violations + 1))
  fi

  # 2. Setuid / setgid bits — refused (no service binary needs these).
  find "$root" -type f \( -perm -4000 -o -perm -2000 \) -print0 \
    >"$tmp_bad" 2>/dev/null || true
  if [ -s "$tmp_bad" ]; then
    warn "tarball contains setuid/setgid files (refused):"
    while IFS= read -r -d '' _p; do printf '  %s\n' "$_p" >&2; done < "$tmp_bad"
    violations=$((violations + 1))
  fi

  # 3. Traversal-shaped components anywhere in the path. The previous
  # `-name '..'` was dead code (find matches basename and neither bsdtar
  # nor GNU tar materialize a literal `..` basename). Walk the NUL list
  # with bash glob match for `..` components.
  : > "$tmp_bad"
  while IFS= read -r -d '' _p; do
    case "$_p" in
      */../*|*/..|../*|..) printf '%s\n' "$_p" >> "$tmp_bad" ;;
    esac
  done < "$tmp_paths"
  if [ -s "$tmp_bad" ]; then
    warn "tarball contains paths with traversal components (refused):"
    sed 's|^|  |' "$tmp_bad" >&2
    violations=$((violations + 1))
  fi

  # 4. Over-long paths (>1024 bytes). Some hosts have PATH_MAX=1024.
  : > "$tmp_bad"
  while IFS= read -r -d '' _p; do
    if [ "${#_p}" -gt 1024 ]; then printf '%s\n' "$_p" >> "$tmp_bad"; fi
  done < "$tmp_paths"
  if [ -s "$tmp_bad" ]; then
    warn "tarball contains paths longer than 1024 bytes (refused):"
    sed 's|^|  |' "$tmp_bad" >&2
    violations=$((violations + 1))
  fi

  # 5. Case-folding collisions. HFS+/APFS default + WSL on NTFS are
  # case-insensitive; a tarball containing bin/Foo + bin/foo lets a
  # malicious entry shadow a legitimate one after extraction.
  #
  # Use `tr [:upper:] [:lower:]` under the operator's locale so Unicode
  # case-folding actually works (LC_ALL=C would limit to ASCII A-Z and
  # miss Café/café, Ω/ω on APFS). `tr`'s POSIX [:upper:]/[:lower:]
  # classes honor the current locale on macOS and glibc both.
  : > "$tmp_bad"
  local tmp_lc; tmp_lc=$(mktemp "${TMPDIR:-/tmp}/rc-inspect-lc.XXXXXX")
  : > "$tmp_lc"
  while IFS= read -r -d '' _p; do
    [ -n "$_p" ] || continue
    case "$_p" in *$'\n'*) continue ;; esac
    printf '%s\t%s\n' "$(printf '%s' "$_p" | tr '[:upper:]' '[:lower:]')" "$_p" >> "$tmp_lc"
  done < "$tmp_paths"
  # awk emits both colliding paths for each lowercase key seen ≥ 2 times.
  LC_ALL=C sort -t $'\t' -k1,1 "$tmp_lc" \
    | awk -F '\t' '{ if ($1==prev) print prev_path"\n"$2; prev=$1; prev_path=$2 }' \
    | LC_ALL=C sort -u > "$tmp_bad" 2>/dev/null || true
  rm -f -- "$tmp_lc"
  if [ -s "$tmp_bad" ]; then
    warn "tarball contains case-folding-collision paths (refused on case-insensitive FS):"
    sed 's|^|  |' "$tmp_bad" >&2
    violations=$((violations + 1))
  fi

  rm -f -- "$tmp_paths" "$tmp_bad"

  if [ "$violations" -gt 0 ]; then
    err "tarball failed post-extract safety inspection (${violations} class(es)). Refusing to install."
  fi
  log "post-extract inspector: OK"
}

# ---------------------------------------------------------------------------
# rc_extract_tarball_quarantined — two-phase extract: unpack to a sibling
# of STAGE_DIR, inspect, then rename to STAGE_DIR. Defense-in-depth against
# future libarchive CVEs that bypass `..` rejection.
#
# Args: tarball_path stage_dir
# ---------------------------------------------------------------------------
rc_extract_tarball_quarantined() {
  local tarball=$1 stage_dir=$2
  local unpack="${stage_dir}.unpack"

  # Symlink-guard the unpack path (parity with publish_app_staged).
  if [ -L "$unpack" ]; then
    err "${unpack} exists as a symlink — refusing to overwrite (potential TOCTOU)"
  fi
  if [ -d "$unpack" ]; then
    log "removing leftover unpack tree at ${unpack}"
    rm -rf -- "$unpack"
  fi
  mkdir -p "$unpack"

  local tar_kind
  tar_kind=$(rc_select_tar)
  log "extracting (kind=${tar_kind}) ${tarball} -> ${unpack}"
  case "$tar_kind" in
    bsdtar)
      # bsdtar (libarchive): rejects `..` by default; safe defaults.
      # --no-same-owner is a GNU-compat alias accepted by bsdtar.
      # `-p` (preserve perms) intentionally OMITTED; we want mode bits
      # clamped by current umask.
      if command -v bsdtar >/dev/null 2>&1; then
        bsdtar -xz --no-same-owner -C "$unpack" -f "$tarball" \
          || err "bsdtar extraction failed"
      else
        tar -xz --no-same-owner -C "$unpack" -f "$tarball" \
          || err "tar (bsdtar) extraction failed"
      fi
      ;;
    gnutar)
      tar -xz \
          --no-same-owner --no-same-permissions --no-overwrite-dir \
          --no-xattrs --no-acls --no-selinux \
          --delay-directory-restore \
          -C "$unpack" -f "$tarball" \
        || err "GNU tar extraction failed"
      ;;
    *) err "internal: unknown tar kind '${tar_kind}'" ;;
  esac

  # Inspect regardless of tar kind — bsdtar is good but not perfect, and
  # the case-folding / length checks are not tar-flag covered.
  rc_inspect_staged_tree "$unpack"

  # Rename into STAGE_DIR atomically. Sibling-dir rename → rename(2)-atomic.
  if [ -L "$stage_dir" ]; then
    err "${stage_dir} exists as a symlink — refusing to overwrite"
  fi
  if [ -d "$stage_dir" ]; then
    log "removing leftover staged tree at ${stage_dir}"
    rm -rf -- "$stage_dir"
  fi
  mv -- "$unpack" "$stage_dir"
  log "extracted + inspected -> ${stage_dir}"
}

# ---------------------------------------------------------------------------
# rc_publish_from_release — top-level orchestrator for --from-release.
# Replaces publish_app_staged when MODE=release. After this returns the
# rest of install.sh's flow (write_wrapper / run_migrate_staged /
# atomic_swap / install_service / markers) runs unchanged.
#
# Args: requested_version (literal vX.Y.Z, X.Y.Z, or "latest")
# Globals consumed: PREFIX, STAGE_DIR, ALLOW_DOWNGRADE, ACCEPT_REPUBLISHED_VERSION,
#                   SKIP_RELEASE_API_CROSSCHECK
# Globals produced: VERIFIED_APP_VERSION, VERIFIED_MANIFEST_SHA,
#                   VERIFIED_MANIFEST_PATH
# ---------------------------------------------------------------------------
rc_publish_from_release() {
  # STAGE is consumed by install.sh's cleanup trap; shellcheck cannot see
  # the cross-file reference.
  # shellcheck disable=SC2034
  STAGE="staged"
  rc_source_verify_release
  vr_require_cosign

  local version
  version=$(rc_resolve_version "$1")
  rc_assert_semver "$version"
  log "release: requested version=${version}"

  # First-install policy: latest is only acceptable when a prior semver
  # marker exists. Otherwise the operator must pin via --version vX.Y.Z so
  # the trust commitment lands on the CLI rather than being silently
  # resolved during a swap-asset-name attack window.
  if [ "$1" = "latest" ] && [ ! -r "${PREFIX}/.install-version-semver" ]; then
    err "--version latest is not permitted on first --from-release install. Pin an exact version, e.g. --version vX.Y.Z (find one at https://github.com/${RELEASE_REPO}/releases)."
  fi

  rc_crosscheck_release_api "$version"

  local dl_dir="${PREFIX}/.release-download"
  if [ -L "$dl_dir" ]; then err "${dl_dir} exists as a symlink"; fi
  if [ -d "$dl_dir" ]; then rm -rf -- "$dl_dir"; fi
  mkdir -p "$dl_dir"

  rc_download_release_artifacts "$dl_dir" "$version"

  # Verify in the canonical order: cosign → schema → tarball sha cross-check.
  vr_verify_all "$TARBALL_PATH" "$MANIFEST_PATH" "$BUNDLE_PATH"

  # Trusted-content reads from the verified manifest.
  local app_version manifest_sha required_min
  app_version=$(jq -r '.appVersion // empty' "$MANIFEST_PATH")
  manifest_sha=$(sha256sum "$MANIFEST_PATH" | awk '{print $1}')
  required_min=$(jq -r '.requiredRuntime.minVersion // empty' "$MANIFEST_PATH")
  [ -n "$app_version" ]  || err "manifest missing appVersion"
  [ -n "$required_min" ] || err "manifest missing requiredRuntime.minVersion"

  # Belt-and-suspenders: incoming appVersion must match the URL we fetched
  # against. A mismatch would mean either a corrupt release or a packed
  # tarball uploaded under a different version (asset rename attack).
  if [ "$app_version" != "$version" ]; then
    err "manifest appVersion='${app_version}' does not match requested version='${version}' (possible asset-rename attack)"
  fi

  rc_enforce_downgrade_defense "$app_version" "$manifest_sha"
  rc_check_aspnetcore_runtime_floor "$required_min"

  # STAGE_DIR is set by install.sh path-layout block; shellcheck cannot see
  # the cross-file reference.
  # shellcheck disable=SC2153
  rc_extract_tarball_quarantined "$TARBALL_PATH" "$STAGE_DIR"

  # Export for install.sh post-swap marker write + history audit.
  VERIFIED_APP_VERSION="$app_version"
  VERIFIED_MANIFEST_SHA="$manifest_sha"
  VERIFIED_MANIFEST_PATH="$MANIFEST_PATH"
}

# ---------------------------------------------------------------------------
# rc_write_post_install_markers — write .install-mode, optionally
# .install-version-semver (release mode only), append .install-history.
# Called by install.sh AFTER atomic_swap succeeds so markers reflect
# committed state.
#
# Args: mode ("release" or "source")
# Globals: PREFIX, VERIFIED_APP_VERSION (release only),
#          VERIFIED_MANIFEST_SHA (release only)
# ---------------------------------------------------------------------------
rc_write_post_install_markers() {
  local mode=$1
  local mode_marker="${PREFIX}/.install-mode"
  local semver_marker="${PREFIX}/.install-version-semver"
  local history="${PREFIX}/.install-history"

  # Symlink-guard all three before writing.
  local p
  for p in "$mode_marker" "$semver_marker" "$history"; do
    if [ -L "$p" ]; then err "${p} exists as a symlink — refusing to write"; fi
  done

  printf '%s\n' "$mode" > "${mode_marker}.tmp"
  mv -f -- "${mode_marker}.tmp" "$mode_marker"
  chmod 644 "$mode_marker"

  if [ "$mode" = "release" ] && [ -n "${VERIFIED_APP_VERSION:-}" ]; then
    {
      printf 'appVersion=%s\n' "$VERIFIED_APP_VERSION"
      printf 'manifestSha256=%s\n' "${VERIFIED_MANIFEST_SHA:-unknown}"
    } > "${semver_marker}.tmp"
    mv -f -- "${semver_marker}.tmp" "$semver_marker"
    chmod 644 "$semver_marker"
  fi

  # Append-only history (forensic audit trail). No secrets land here.
  local ts; ts=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  local cosign_ver=""
  if command -v cosign >/dev/null 2>&1; then
    cosign_ver=$(cosign version 2>&1 \
                   | grep -oE 'GitVersion:[[:space:]]*v?[0-9.]+' \
                   | head -1 \
                   | sed 's/.*v\{0,1\}\([0-9.]\{1,\}\).*/\1/' || true)
  fi
  if [ "$mode" = "release" ]; then
    printf '%s\tmode=release\tversion=%s\tmanifest_sha=%s\ttarball_sha=%s\tcosign_identity=%s\tcosign_version=%s\n' \
      "$ts" "${VERIFIED_APP_VERSION:-unknown}" \
      "${VERIFIED_MANIFEST_SHA:-unknown}" \
      "$(jq -r '.artifacts.tarball.sha256 // "unknown"' "${VERIFIED_MANIFEST_PATH:-/dev/null}" 2>/dev/null || echo unknown)" \
      "${VERIFIED_COSIGN_IDENTITY:-unknown}" \
      "${cosign_ver:-unknown}" \
      >> "$history"
  else
    printf '%s\tmode=source\tversion=%s\n' "$ts" "${NEW_VERSION:-unknown}" >> "$history"
  fi
  chmod 644 "$history"
}
