#!/usr/bin/env bash
#
# verify-release.sh — sanctioned cosign-verify entrypoint for Archetype A2
# release tarballs published by .github/workflows/release.yml under ADR-011.
#
# Why this script exists:
#
#   * Operators verifying a downloaded release manually should call THIS
#     script, not hand-craft a `cosign verify-blob` invocation. The OIDC
#     identity + issuer + Rekor URL are hard-coded constants here so that
#     copy-paste-from-stale-README does not silently weaken the trust root.
#   * scripts/install.sh sources this file (sourceable via SOURCE_ONLY=1)
#     to share the constants + helper functions. There must be exactly ONE
#     definition of the trust-root constants in the repo.
#
# Usage (as a CLI):
#
#   scripts/verify-release.sh \
#       --tarball   expertise-api-vX.Y.Z-portable.tar.gz \
#       --manifest  expertise-api-vX.Y.Z.manifest.json \
#       --signature expertise-api-vX.Y.Z.manifest.json.sig \
#       --certificate expertise-api-vX.Y.Z.manifest.json.pem
#
# Exit codes:
#
#   0  signature valid AND manifest.artifacts.tarball.sha256 matches the
#      computed sha256 of the tarball.
#   1  verification failure (signature invalid, identity mismatch, sha
#      mismatch, schemaVersion unsupported, etc.)
#   2  precondition failure (missing tool, missing flag, file unreadable).
#
# Refs: ADR-011 (Verification UX); README §Supply-chain verification.
#

set -euo pipefail

# ---------------------------------------------------------------------------
# Trust-root constants — EXACT match, never regexp.
#
# Forks must edit BOTH constants and the README recipe. The exact-match
# semantics are deliberate: --certificate-identity-regexp is the foot-gun
# this script's existence is designed to prevent.
# ---------------------------------------------------------------------------
readonly COSIGN_IDENTITY='https://github.com/TheSemicolon/agent-expertise-api/.github/workflows/release.yml@refs/heads/main'
readonly COSIGN_OIDC_ISSUER='https://token.actions.githubusercontent.com'
readonly REKOR_URL='https://rekor.sigstore.dev'

# cosign 2.x stabilized the keyless verify-blob bundle format and the
# --certificate-identity / --certificate-oidc-issuer flag pair; 1.x
# rejects them. Pin a minimum.
readonly COSIGN_MIN_VERSION='2.2.0'

# Manifest schema versions this script knows how to interpret. Strict —
# refuses unknown values rather than silently forward-compatting (a
# compromised future release could ship schemaVersion=2 with fields a
# stale parser would miss).
readonly SUPPORTED_MANIFEST_SCHEMA_VERSIONS=('1')

# ---------------------------------------------------------------------------
# Logging helpers (style matches scripts/install.sh)
# ---------------------------------------------------------------------------
vr_log()  { printf '[verify-release] %s\n' "$1"; }
vr_warn() { printf '[verify-release] WARN: %s\n' "$1" >&2; }
# vr_err is print-only (no implicit exit). vr_die owns the exit code so
# `vr_die "msg" 2` actually exits 2 rather than being short-circuited by
# set -e on vr_err's return-1.
vr_err()  { printf '[verify-release] ERROR: %s\n' "$1" >&2; }
vr_die()  { vr_err "$1"; exit "${2:-1}"; }

# ---------------------------------------------------------------------------
# vr_require_cosign — assert cosign present + meets COSIGN_MIN_VERSION.
# ---------------------------------------------------------------------------
vr_require_cosign() {
  if ! command -v cosign >/dev/null 2>&1; then
    vr_die "cosign required for release verification (ADR-011). Install:
    macOS:  brew install cosign
    Linux:  https://docs.sigstore.dev/cosign/system_config/installation/
  scripts/install.sh --install-deps will fold cosign in via D4 (#249)." 2
  fi
  # cosign version --json is reliable on 2.x; fall back to plain text.
  local v
  v=$(cosign version --json 2>/dev/null \
        | grep -o '"gitVersion":"[^"]*"' \
        | head -1 \
        | sed 's/.*"v\{0,1\}\([0-9.]\{1,\}\).*/\1/' || true)
  if [ -z "$v" ]; then
    v=$(cosign version 2>&1 \
          | grep -oE 'GitVersion:[[:space:]]*v?[0-9.]+' \
          | head -1 \
          | sed 's/.*v\{0,1\}\([0-9.]\{1,\}\).*/\1/' || true)
  fi
  if [ -z "$v" ]; then
    vr_warn "cosign version could not be determined — proceeding (cosign verify-blob will fail clearly on a 1.x mismatch)"
    return 0
  fi
  # sort -V semver compare. Validate inputs against a basic semver regex
  # before relying on sort -V, which does not error on garbage.
  if ! printf '%s\n' "$v" "$COSIGN_MIN_VERSION" | grep -qE '^[0-9]+\.[0-9]+(\.[0-9]+)?'; then
    vr_warn "cosign version '$v' does not match a parseable semver; proceeding"
    return 0
  fi
  local lowest
  lowest=$(printf '%s\n%s\n' "$COSIGN_MIN_VERSION" "$v" | sort -V | head -1)
  if [ "$lowest" != "$COSIGN_MIN_VERSION" ]; then
    vr_die "cosign >= ${COSIGN_MIN_VERSION} required for keyless verify-blob bundle format (got ${v}). Upgrade via brew/your-package-manager." 2
  fi
  vr_log "cosign: ${v} (>= ${COSIGN_MIN_VERSION})"
}

# ---------------------------------------------------------------------------
# vr_cosign_verify_manifest — invoke cosign verify-blob with the pinned
# trust-root constants. Returns 0 on success.
#
# Args: signature_path cert_path manifest_path
# ---------------------------------------------------------------------------
vr_cosign_verify_manifest() {
  local sig=$1 cert=$2 manifest=$3
  [ -r "$sig" ]      || vr_die "signature unreadable: $sig" 2
  [ -r "$cert" ]     || vr_die "certificate unreadable: $cert" 2
  [ -r "$manifest" ] || vr_die "manifest unreadable: $manifest" 2

  vr_log "cosign verify-blob: identity=${COSIGN_IDENTITY}"
  vr_log "                    issuer=${COSIGN_OIDC_ISSUER}"
  vr_log "                    rekor=${REKOR_URL}"
  if ! cosign verify-blob \
      --certificate-identity "$COSIGN_IDENTITY" \
      --certificate-oidc-issuer "$COSIGN_OIDC_ISSUER" \
      --rekor-url "$REKOR_URL" \
      --signature "$sig" \
      --certificate "$cert" \
      "$manifest" >/dev/null 2>&1; then
    vr_die "cosign verify-blob FAILED. Possible causes:
    1. Manifest tampered or signature does not match
    2. Signer identity mismatch (expected: ${COSIGN_IDENTITY})
    3. Rekor unreachable (try --from-source --i-accept-unverified-source
       fallback; structured offline-verify support tracked by #256)" 1
  fi
  vr_log "cosign verify-blob: OK"
}

# ---------------------------------------------------------------------------
# vr_validate_manifest_schema — strict schemaVersion check.
# ---------------------------------------------------------------------------
vr_validate_manifest_schema() {
  local manifest=$1
  command -v jq >/dev/null 2>&1 || vr_die "jq required to parse manifest" 2

  local schema
  schema=$(jq -r '.schemaVersion // empty' "$manifest" 2>/dev/null) \
    || vr_die "manifest is not valid JSON" 1
  [ -n "$schema" ] || vr_die "manifest missing required field: schemaVersion" 1

  local ok=0
  local v
  for v in "${SUPPORTED_MANIFEST_SCHEMA_VERSIONS[@]}"; do
    if [ "$schema" = "$v" ]; then ok=1; break; fi
  done
  if [ "$ok" = "0" ]; then
    vr_die "unsupported manifest schemaVersion='${schema}' (supported: ${SUPPORTED_MANIFEST_SCHEMA_VERSIONS[*]}).
  This usually means install.sh is older than the release. Upgrade install.sh
  to a version that supports schemaVersion=${schema}, or fetch an older
  release whose manifest schemaVersion is supported." 1
  fi
  vr_log "manifest schemaVersion: ${schema} (supported)"
}

# ---------------------------------------------------------------------------
# vr_crosscheck_tarball_sha — confirm tarball SHA matches the (now-trusted)
# manifest's artifacts.tarball.sha256.
# ---------------------------------------------------------------------------
vr_crosscheck_tarball_sha() {
  local tarball=$1 manifest=$2
  command -v sha256sum >/dev/null 2>&1 || vr_die "sha256sum required" 2
  command -v jq        >/dev/null 2>&1 || vr_die "jq required" 2

  local expected actual
  expected=$(jq -r '.artifacts.tarball.sha256 // empty' "$manifest")
  [ -n "$expected" ] || vr_die "manifest missing artifacts.tarball.sha256" 1
  # Sanity: expected must look like a sha256.
  case "$expected" in
    [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]*) ;;
    *) vr_die "manifest artifacts.tarball.sha256 is not a hex sha256: ${expected}" 1 ;;
  esac
  if [ "${#expected}" -ne 64 ]; then
    vr_die "manifest artifacts.tarball.sha256 length != 64: ${expected}" 1
  fi
  actual=$(sha256sum "$tarball" | awk '{print $1}')
  if [ "$expected" != "$actual" ]; then
    vr_die "TARBALL TAMPERED — manifest declares sha256=${expected} but tarball is sha256=${actual}" 1
  fi
  vr_log "tarball sha256 cross-check: OK (${actual:0:16}...)"
}

# ---------------------------------------------------------------------------
# vr_verify_all — the canonical sequence: cosign → schema → sha cross-check.
# Returns 0 on full success. Callers (install.sh + CLI) use this entry point.
# ---------------------------------------------------------------------------
vr_verify_all() {
  local tarball=$1 manifest=$2 sig=$3 cert=$4
  [ -r "$tarball" ] || vr_die "tarball unreadable: $tarball" 2
  vr_require_cosign
  vr_cosign_verify_manifest "$sig" "$cert" "$manifest"
  vr_validate_manifest_schema "$manifest"
  vr_crosscheck_tarball_sha "$tarball" "$manifest"
  vr_log "verification: PASS"
}

# ---------------------------------------------------------------------------
# CLI entrypoint (skipped when sourced via SOURCE_ONLY=1).
# ---------------------------------------------------------------------------
if [ "${SOURCE_ONLY:-0}" != "1" ]; then
  TARBALL=""; MANIFEST=""; SIGNATURE=""; CERTIFICATE=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --tarball)     TARBALL="${2:?--tarball needs a path}"; shift 2 ;;
      --manifest)    MANIFEST="${2:?--manifest needs a path}"; shift 2 ;;
      --signature)   SIGNATURE="${2:?--signature needs a path}"; shift 2 ;;
      --certificate) CERTIFICATE="${2:?--certificate needs a path}"; shift 2 ;;
      --help|-h)
        sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
        exit 0 ;;
      *) vr_die "unknown flag: $1 (try --help)" 2 ;;
    esac
  done
  [ -n "$TARBALL" ]     || vr_die "--tarball required" 2
  [ -n "$MANIFEST" ]    || vr_die "--manifest required" 2
  [ -n "$SIGNATURE" ]   || vr_die "--signature required" 2
  [ -n "$CERTIFICATE" ] || vr_die "--certificate required" 2
  vr_verify_all "$TARBALL" "$MANIFEST" "$SIGNATURE" "$CERTIFICATE"
fi
