#!/usr/bin/env bash
#
# scripts/build/generate-manifest.sh — produces the release manifest JSON
# that anchors cosign-signed verification for Archetype A2 installs.
# Companion to ADR-011 (cosign-signed published tarball over SDK-on-host).
#
# Invoked from .github/workflows/release.yml after the portable
# `dotnet publish` step and after the deterministic tarball is built.
# Reads the published runtimeconfig.json for the runtime floor (NEVER
# hand-set — ADR-011 sub-decision "Signing scope"), computes the tarball
# SHA-256, and emits a flat JSON document signed by `cosign sign-blob`.
#
# Schema fields (schemaVersion=1):
#   schemaVersion        manifest schema version; bump on breaking change
#   appVersion           semantic-release version, no leading "v"
#   gitSha               source commit the artifacts were built from
#   publishMode          "portable" (RID-agnostic) or "self-contained"
#   targetFramework      net10.0 / etc., from runtimeconfig
#   requiredRuntime      { name, minVersion, rollForward } sourced from
#                        publish/<App>.runtimeconfig.json — installer
#                        compares with `sort -V` against the host's
#                        installed runtime floor
#   sdkUsedToPublish     informational; aids reproducibility forensics
#   buildTimestamp       commit time (SOURCE_DATE_EPOCH derived), not
#                        wall-clock — keeps the manifest deterministic
#                        across reruns of the same source rev
#   artifacts.tarball    { name, sha256, sizeBytes } — primary asset
#   artifacts.openapiSha256  sidecar sha for openapi.json if attached
#
# Exit codes:
#   0  manifest written
#   1  argument / environment error
#   2  runtimeconfig missing or malformed (HIGH — manifest with hand-set
#      requiredRuntime is exactly the failure mode ADR-011 calls out)
#   3  jq / sha256sum / required tool missing
#
# Usage:
#   generate-manifest.sh \
#       --publish-dir dist/publish \
#       --app-version 1.2.3 \
#       --git-sha "$GITHUB_SHA" \
#       --tarball dist/expertise-api-1.2.3-portable.tar.gz \
#       --source-date-epoch "$(git log -1 --format=%ct HEAD)" \
#       [--openapi-sha-file dist/openapi/openapi.json.sha256] \
#       --output dist/expertise-api-1.2.3.manifest.json
#

set -euo pipefail

# bash >= 4 required: mapfile + indirect-expansion semantics. The shebang
# is `env bash` so this only matters for contributors whose `env bash`
# resolves to /bin/bash on stock macOS (3.2). Ubuntu / Homebrew bash are 4+.
if [ "${BASH_VERSINFO[0]:-0}" -lt 4 ]; then
  printf 'generate-manifest: requires bash >= 4 (got %s)\n' "${BASH_VERSION:-unknown}" >&2
  exit 3
fi

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
PUBLISH_DIR=""
APP_VERSION=""
GIT_SHA=""
TARBALL=""
SOURCE_DATE_EPOCH=""
OPENAPI_SHA_FILE=""
OUTPUT=""
SDK_VERSION_OVERRIDE=""
PUBLISH_MODE="portable"

usage() {
  sed -n '4,49p' "$0"
}

while [ $# -gt 0 ]; do
  case "$1" in
    --publish-dir)        PUBLISH_DIR="${2:?--publish-dir requires a value}"; shift 2 ;;
    --app-version)        APP_VERSION="${2:?--app-version requires a value}"; shift 2 ;;
    --git-sha)            GIT_SHA="${2:?--git-sha requires a value}"; shift 2 ;;
    --tarball)            TARBALL="${2:?--tarball requires a value}"; shift 2 ;;
    --source-date-epoch)  SOURCE_DATE_EPOCH="${2:?--source-date-epoch requires a value}"; shift 2 ;;
    --openapi-sha-file)   OPENAPI_SHA_FILE="${2:?--openapi-sha-file requires a value}"; shift 2 ;;
    --output)             OUTPUT="${2:?--output requires a value}"; shift 2 ;;
    --sdk-version)        SDK_VERSION_OVERRIDE="${2:?--sdk-version requires a value}"; shift 2 ;;
    --publish-mode)       PUBLISH_MODE="${2:?--publish-mode requires a value}"; shift 2 ;;
    -h|--help)            usage; exit 0 ;;
    *)                    printf 'generate-manifest: unknown argument: %s\n' "$1" >&2; usage >&2; exit 1 ;;
  esac
done

for var in PUBLISH_DIR APP_VERSION GIT_SHA TARBALL SOURCE_DATE_EPOCH OUTPUT; do
  if [ -z "${!var}" ]; then
    printf 'generate-manifest: missing required --%s (or its env equivalent)\n' \
      "$(printf '%s' "$var" | tr '[:upper:]_' '[:lower:]-')" >&2
    exit 1
  fi
done

# ---------------------------------------------------------------------------
# Tool preconditions
# ---------------------------------------------------------------------------
for tool in jq sha256sum stat; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    printf 'generate-manifest: required tool missing: %s\n' "$tool" >&2
    exit 3
  fi
done

# stat on BSD (macOS) uses -f, on GNU (Linux) uses -c. Detect and pick.
file_size() {
  local f=$1
  if stat -c%s "$f" >/dev/null 2>&1; then
    stat -c%s "$f"
  else
    stat -f%z "$f"
  fi
}

[ -d "$PUBLISH_DIR" ] || { printf 'generate-manifest: publish dir not found: %s\n' "$PUBLISH_DIR" >&2; exit 1; }
[ -f "$TARBALL" ]     || { printf 'generate-manifest: tarball not found: %s\n' "$TARBALL" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Locate runtimeconfig.json — there must be exactly one in PUBLISH_DIR
# matching *.runtimeconfig.json. Refuse if zero or many (ambiguous shape
# indicates the publish layout has drifted and this script's invariant
# is broken). Capture-first avoids the well-known process-substitution
# exit-code-not-propagated trap under `set -e`.
# ---------------------------------------------------------------------------
if ! runtimeconfigs_raw=$(find "$PUBLISH_DIR" -maxdepth 1 -type f -name '*.runtimeconfig.json' | sort); then
  printf 'generate-manifest: find failed under %s\n' "$PUBLISH_DIR" >&2
  exit 2
fi
mapfile -t runtimeconfigs <<< "$runtimeconfigs_raw"
# `<<<` always feeds at least one (possibly empty) line; collapse the empty case
if [ "${#runtimeconfigs[@]}" -eq 1 ] && [ -z "${runtimeconfigs[0]}" ]; then
  runtimeconfigs=()
fi
case ${#runtimeconfigs[@]} in
  0)
    printf 'generate-manifest: no *.runtimeconfig.json in %s — was the publish step run?\n' "$PUBLISH_DIR" >&2
    exit 2
    ;;
  1)
    runtime_config=${runtimeconfigs[0]}
    ;;
  *)
    printf 'generate-manifest: multiple *.runtimeconfig.json in %s (ambiguous):\n' "$PUBLISH_DIR" >&2
    printf '  %s\n' "${runtimeconfigs[@]}" >&2
    exit 2
    ;;
esac

# ---------------------------------------------------------------------------
# requiredRuntime sourced from runtimeconfig (NEVER hand-set — ADR-011)
# ---------------------------------------------------------------------------
required_runtime_name=$(jq -r '.runtimeOptions.framework.name // empty' "$runtime_config")
required_runtime_version=$(jq -r '.runtimeOptions.framework.version // empty' "$runtime_config")
required_runtime_rollforward=$(jq -r '.runtimeOptions.rollForward // "Minor"' "$runtime_config")
target_framework=$(jq -r '.runtimeOptions.tfm // empty' "$runtime_config")

if [ -z "$required_runtime_name" ] || [ -z "$required_runtime_version" ] || [ -z "$target_framework" ]; then
  printf 'generate-manifest: runtimeconfig %s missing framework.name/version or tfm — refusing to emit manifest with synthesized values\n' \
    "$runtime_config" >&2
  exit 2
fi

# ---------------------------------------------------------------------------
# SDK version — informational. Operators do not gate on this; it's
# present for reproducibility forensics ("which SDK built this artifact?").
# Override-able for tests that don't have dotnet on PATH.
# ---------------------------------------------------------------------------
if [ -n "$SDK_VERSION_OVERRIDE" ]; then
  sdk_version=$SDK_VERSION_OVERRIDE
elif command -v dotnet >/dev/null 2>&1; then
  sdk_version=$(dotnet --version 2>/dev/null || printf 'unknown')
else
  sdk_version=unknown
fi

# ---------------------------------------------------------------------------
# Tarball measurements
# ---------------------------------------------------------------------------
tarball_name=$(basename "$TARBALL")
tarball_sha=$(sha256sum "$TARBALL" | awk '{print $1}')
tarball_size=$(file_size "$TARBALL")

# Optional sidecar — openapi.json sha. Skipped if file absent (don't fail
# the manifest; openapi attachment is a separate concern).
openapi_sha=""
if [ -n "$OPENAPI_SHA_FILE" ] && [ -f "$OPENAPI_SHA_FILE" ]; then
  openapi_sha=$(awk '{print $1}' "$OPENAPI_SHA_FILE")
fi

# ---------------------------------------------------------------------------
# Build timestamp — derived from SOURCE_DATE_EPOCH, never wall clock.
# Two reruns of this script against the same source rev produce byte-
# identical manifests, which lets operators sanity-check by hashing.
# ---------------------------------------------------------------------------
# Validate SOURCE_DATE_EPOCH is a positive integer before feeding to date(1)
case "$SOURCE_DATE_EPOCH" in
  ''|*[!0-9]*) printf 'generate-manifest: --source-date-epoch must be a positive integer, got: %s\n' "$SOURCE_DATE_EPOCH" >&2; exit 1 ;;
esac

if date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%dT%H:%M:%SZ >/dev/null 2>&1; then
  build_timestamp=$(date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%dT%H:%M:%SZ)
else
  # BSD date (macOS) — different invocation
  build_timestamp=$(date -u -r "$SOURCE_DATE_EPOCH" +%Y-%m-%dT%H:%M:%SZ)
fi

# ---------------------------------------------------------------------------
# Emit manifest. jq -n with --arg keeps the JSON literal-safe (no string
# interpolation; embedded quotes/backslashes in any value are properly
# escaped by jq).
# ---------------------------------------------------------------------------
manifest_json=$(jq -n \
  --arg schemaVersion "1" \
  --arg appVersion "$APP_VERSION" \
  --arg gitSha "$GIT_SHA" \
  --arg publishMode "$PUBLISH_MODE" \
  --arg targetFramework "$target_framework" \
  --arg required_name "$required_runtime_name" \
  --arg required_min "$required_runtime_version" \
  --arg required_rf "$required_runtime_rollforward" \
  --arg sdk "$sdk_version" \
  --arg buildTimestamp "$build_timestamp" \
  --arg tarballName "$tarball_name" \
  --arg tarballSha "$tarball_sha" \
  --argjson tarballSize "$tarball_size" \
  --arg openapiSha "$openapi_sha" \
  '{
    schemaVersion: $schemaVersion,
    appVersion: $appVersion,
    gitSha: $gitSha,
    publishMode: $publishMode,
    targetFramework: $targetFramework,
    requiredRuntime: {
      name: $required_name,
      minVersion: $required_min,
      rollForward: $required_rf
    },
    sdkUsedToPublish: $sdk,
    buildTimestamp: $buildTimestamp,
    artifacts: ({
      tarball: {
        name: $tarballName,
        sha256: $tarballSha,
        sizeBytes: $tarballSize
      }
    } + (if $openapiSha == "" then {} else { openapiSha256: $openapiSha } end))
  }')

# Atomic write — never half-flush a manifest cosign is about to sign.
mkdir -p "$(dirname "$OUTPUT")"
tmp=$(mktemp "${OUTPUT}.XXXXXX")
trap 'rm -f "$tmp"' EXIT
printf '%s\n' "$manifest_json" > "$tmp"
mv "$tmp" "$OUTPUT"
trap - EXIT

printf 'generate-manifest: wrote %s (%s bytes)\n' "$OUTPUT" "$(file_size "$OUTPUT")"
