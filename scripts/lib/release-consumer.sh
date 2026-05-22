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
# Hard-coded; forks must edit. Mirrors the COSIGN_IDENTITY constant in
# scripts/verify-release.sh — if you change one without the other the
# verification step will refuse the install with a clear error.
# ---------------------------------------------------------------------------
readonly RELEASE_REPO="TheSemicolon/agent-expertise-api"

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
  # lacks both. Fall back cleanly.
  local fail_flag="--fail"
  local retry_all_flag=""
  if curl --help all 2>/dev/null | grep -q -- '--fail-with-body'; then
    fail_flag="--fail-with-body"
  fi
  if curl --help all 2>/dev/null | grep -q -- '--retry-all-errors'; then
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
rc_resolve_version() {
  local requested=$1
  if [ "$requested" != "latest" ]; then
    # Strip leading v for the manifest comparison (manifest's appVersion
    # has no leading v per generate-manifest.sh contract).
    printf '%s\n' "${requested#v}"
    return 0
  fi
  command -v jq >/dev/null 2>&1 || err "jq required to resolve --version latest"
  log "resolving --version latest via GitHub Releases API"
  local api_url="https://api.github.com/repos/${RELEASE_REPO}/releases/latest"
  local tmp; tmp=$(mktemp -t expertise-api-release.XXXXXX)
  # Pass GH_TOKEN/GITHUB_TOKEN through when set (60/h → 5000/h limit lift)
  # but never require it; the repo is public.
  local auth_header=()
  if [ -n "${GH_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GH_TOKEN}")
  elif [ -n "${GITHUB_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
  fi
  if ! curl --proto '=https' --tlsv1.2 --fail --location \
       --connect-timeout 10 --max-time 30 --silent --show-error \
       -H 'Accept: application/vnd.github+json' \
       "${auth_header[@]}" \
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
  local tmp; tmp=$(mktemp -t expertise-api-crosscheck.XXXXXX)
  local auth_header=()
  if [ -n "${GH_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GH_TOKEN}")
  elif [ -n "${GITHUB_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
  fi
  if ! curl --proto '=https' --tlsv1.2 --fail --location \
       --connect-timeout 10 --max-time 30 --silent --show-error \
       -H 'Accept: application/vnd.github+json' \
       "${auth_header[@]}" \
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
  local expected_tarball="expertise-api-v${version}-portable.tar.gz"
  local expected_manifest="expertise-api-v${version}.manifest.json"
  local missing=""
  local name
  for name in "$expected_tarball" \
              "${expected_tarball}.sha256" \
              "$expected_manifest" \
              "${expected_manifest}.sig" \
              "${expected_manifest}.pem"; do
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
  log "release-api cross-check: tag_name=v${version}, all 5 expected assets present"
}

# ---------------------------------------------------------------------------
# rc_download_release_artifacts — fetch tarball + sha + manifest + sig + cert
# into ${dest_dir}. Caller is responsible for prior validation (version
# resolved, release-api cross-checked).
#
# Args: dest_dir version
# Globals (export): TARBALL_PATH MANIFEST_PATH SIGNATURE_PATH CERTIFICATE_PATH
# ---------------------------------------------------------------------------
rc_download_release_artifacts() {
  local dest_dir=$1 version=$2
  mkdir -p "$dest_dir"
  local base="https://github.com/${RELEASE_REPO}/releases/download/v${version}"
  local tarball="expertise-api-v${version}-portable.tar.gz"
  local manifest="expertise-api-v${version}.manifest.json"

  rc_curl_https "${dest_dir}/${tarball}"        "${base}/${tarball}"
  rc_curl_https "${dest_dir}/${tarball}.sha256" "${base}/${tarball}.sha256"
  rc_curl_https "${dest_dir}/${manifest}"        "${base}/${manifest}"
  rc_curl_https "${dest_dir}/${manifest}.sig"    "${base}/${manifest}.sig"
  rc_curl_https "${dest_dir}/${manifest}.pem"    "${base}/${manifest}.pem"

  TARBALL_PATH="${dest_dir}/${tarball}"
  MANIFEST_PATH="${dest_dir}/${manifest}"
  SIGNATURE_PATH="${dest_dir}/${manifest}.sig"
  CERTIFICATE_PATH="${dest_dir}/${manifest}.pem"
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

  # Validate both shapes before sort -V (sort -V silently misorders garbage).
  local semver_re='^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.+-]+)?$'
  if ! printf '%s' "$prior_ver" | grep -qE "$semver_re"; then
    warn "prior version '${prior_ver}' is not parseable semver; skipping downgrade compare"
    return 0
  fi
  if ! printf '%s' "$incoming_ver" | grep -qE "$semver_re"; then
    err "incoming manifest appVersion '${incoming_ver}' is not parseable semver"
  fi

  local lowest
  lowest=$(printf '%s\n%s\n' "$prior_ver" "$incoming_ver" | sort -V | head -1)
  if [ "$prior_ver" != "$incoming_ver" ] && [ "$lowest" = "$incoming_ver" ]; then
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
# floor. Excludes prerelease runtimes (-preview/-rc) from candidates because
# the .NET host refuses to roll-forward from a release floor onto a
# prerelease without explicit rollForwardToPreRelease.
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

  # Filter to the same major; exclude prereleases (hyphenated versions).
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

  local best
  best=$(printf '%s\n%s\n' "$floor" "$installed" | sort -V | tail -1)
  if [ "$best" = "$floor" ] && ! printf '%s\n' "$installed" | grep -qFx "$floor"; then
    err "ASP.NET Core runtime ${need_major}.x installed but none >= ${floor}; installed: $(printf '%s' "$installed" | paste -sd, -)"
  fi
  log "ASP.NET Core runtime floor: ${floor} satisfied (highest installed: $(printf '%s\n' "$installed" | sort -V | tail -1))"
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
# case-folding-collision pairs, over-long paths, traversal-shaped names.
# Walks once; reports every violation before erroring (operator-friendly).
#
# Args: root_path
# ---------------------------------------------------------------------------
rc_inspect_staged_tree() {
  local root=$1
  local violations=0
  local out

  # 1. Symlinks / block / char / fifo / socket — refused.
  out=$(find "$root" \( -type l -o -type b -o -type c -o -type p -o -type s \) -print 2>/dev/null || true)
  if [ -n "$out" ]; then
    warn "tarball contains symlinks or special files (refused):"
    printf '%s\n' "$out" | sed 's|^|  |' >&2
    violations=$((violations + 1))
  fi

  # 2. Setuid / setgid bits — refused (no service binary needs these).
  out=$(find "$root" -type f \( -perm -4000 -o -perm -2000 \) -print 2>/dev/null || true)
  if [ -n "$out" ]; then
    warn "tarball contains setuid/setgid files (refused):"
    printf '%s\n' "$out" | sed 's|^|  |' >&2
    violations=$((violations + 1))
  fi

  # 3. Traversal-shaped names (belt-and-suspenders; bsdtar should already
  # have rejected during extraction).
  out=$(find "$root" \( -name '..' -o -name '.*..' \) -print 2>/dev/null || true)
  if [ -n "$out" ]; then
    warn "tarball contains traversal-shaped paths (refused):"
    printf '%s\n' "$out" | sed 's|^|  |' >&2
    violations=$((violations + 1))
  fi

  # 4. Over-long paths (>1024 bytes). Some hosts have PATH_MAX=1024.
  out=$(find "$root" -print 2>/dev/null | awk 'length > 1024' || true)
  if [ -n "$out" ]; then
    warn "tarball contains paths longer than 1024 bytes (refused):"
    printf '%s\n' "$out" | sed 's|^|  |' >&2
    violations=$((violations + 1))
  fi

  # 5. Case-folding collisions. HFS+/APFS default + WSL on NTFS are
  # case-insensitive; a tarball containing bin/Foo + bin/foo lets a
  # malicious entry shadow a legitimate one after extraction.
  out=$(find "$root" -print 2>/dev/null | LC_ALL=C awk '{l=tolower($0); if (seen[l]++ && seen[l]==2) print $0; orig[l]=$0}' || true)
  if [ -n "$out" ]; then
    warn "tarball contains case-folding-collision paths (refused on case-insensitive FS):"
    printf '%s\n' "$out" | sed 's|^|  |' >&2
    violations=$((violations + 1))
  fi

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
  vr_verify_all "$TARBALL_PATH" "$MANIFEST_PATH" "$SIGNATURE_PATH" "$CERTIFICATE_PATH"

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
      "${COSIGN_IDENTITY:-unknown}" \
      "${cosign_ver:-unknown}" \
      >> "$history"
  else
    printf '%s\tmode=source\tversion=%s\n' "$ts" "${NEW_VERSION:-unknown}" >> "$history"
  fi
  chmod 644 "$history"
}
