#!/usr/bin/env bash
#
# tests/install/test-generate-manifest.sh — exercise the release-manifest
# generator (scripts/build/generate-manifest.sh) without invoking dotnet,
# git, or cosign. Companion to ADR-011 and the D2 release-pipeline
# changes (issue #249).
#
# Cases:
#   1. Happy path with a realistic runtimeconfig — manifest is valid JSON
#      with all required fields populated from the runtimeconfig.
#   2. Missing runtimeconfig — exits 2, refuses to synthesize values.
#   3. Malformed runtimeconfig (missing framework.version) — exits 2.
#   4. Multiple runtimeconfigs in publish dir — exits 2 (ambiguous shape).
#   5. Missing required CLI argument — exits 1.
#   6. Non-integer SOURCE_DATE_EPOCH — exits 1.
#   7. Determinism: two runs against the same input produce byte-identical
#      manifests (the load-bearing property of SOURCE_DATE_EPOCH-derived
#      timestamps).
#   8. Tarball SHA is computed correctly (cross-check against sha256sum).
#   9. Tarball size is reported correctly.
#  10. Optional openapi sha sidecar — present when file exists, omitted
#      when --openapi-sha-file absent or path does not exist.
#  11. Schema invariants — jq queries for each field assert presence
#      and type (string vs number vs object). Lock the manifest shape
#      so install.sh (D3) can rely on it.
#  12. requiredRuntime.minVersion is sourced verbatim from runtimeconfig
#      (hand-set value would be exactly the ADR-011 footgun this script
#      exists to prevent).
#

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
GEN="${SCRIPT_DIR}/scripts/build/generate-manifest.sh"
[ -x "$GEN" ] || { echo "FAIL: generate-manifest.sh missing or not executable" >&2; exit 2; }

# Preconditions — same tools the script itself requires
for tool in jq sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || { echo "SKIP: $tool not available" >&2; exit 0; }
done
# Fixture builder uses GNU-tar long opts (--sort, --owner=, --group=,
# --numeric-owner, --format=ustar). On macOS the default `tar` is bsdtar
# and silently rejects these. Prefer `gtar` if available; SKIP cleanly
# otherwise so the suite stays informative on developer macOS without
# masking failures inside the fixture.
if command -v gtar >/dev/null 2>&1; then
  TAR_BIN=gtar
elif tar --version 2>/dev/null | grep -qi 'gnu tar'; then
  TAR_BIN=tar
else
  echo "SKIP: GNU tar (gtar or system tar) not available — install via brew install gnu-tar" >&2
  exit 0
fi

SCRATCH="$(mktemp -d -t generate-manifest.XXXXXX)"
trap 'rm -rf "${SCRATCH}"' EXIT

PASS=0
FAIL=0
assert() {
  local name="$1"; shift
  if "$@"; then
    PASS=$((PASS+1))
  else
    printf 'FAIL: %s\n' "$name" >&2
    FAIL=$((FAIL+1))
  fi
}

# ---------------------------------------------------------------------------
# Fixture builder — minimal publish layout
# ---------------------------------------------------------------------------
build_fixture() {
  local case_dir=$1
  local runtimeconfig_content=${2:-DEFAULT}
  local publish=${case_dir}/publish
  mkdir -p "$publish"

  if [ "$runtimeconfig_content" = "DEFAULT" ]; then
    cat > "${publish}/ExpertiseApi.runtimeconfig.json" <<'JSON'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "rollForward": "LatestMinor",
    "framework": {
      "name": "Microsoft.AspNetCore.App",
      "version": "10.0.0"
    }
  }
}
JSON
  elif [ "$runtimeconfig_content" = "NONE" ]; then
    : # caller wants no runtimeconfig at all
  else
    printf '%s' "$runtimeconfig_content" > "${publish}/ExpertiseApi.runtimeconfig.json"
  fi

  # Tiny payload so the tarball isn't empty
  printf 'placeholder dll\n' > "${publish}/ExpertiseApi.dll"

  # Deterministic tarball — matches what release.yml does for real.
  # stderr deliberately NOT redirected: a tar failure here would otherwise
  # produce an empty tarball that the SHA cross-check below would still
  # "pass" against (both sides hashing the same garbage bytes).
  "$TAR_BIN" --sort=name --owner=0 --group=0 --numeric-owner \
      --mtime='2020-01-01 00:00:00 UTC' \
      --format=ustar \
      -C "$publish" -cf - . \
    | gzip -n -9 > "${case_dir}/artifact.tar.gz"

  printf '%s\n' "$publish"
}

run_gen() {
  # Wrapper that always provides --sdk-version (tests run without dotnet)
  "$GEN" --sdk-version test-9.9.9 "$@"
}

# ---------------------------------------------------------------------------
# Case 1: happy path
# ---------------------------------------------------------------------------
case1=${SCRATCH}/case1
publish=$(build_fixture "$case1")
out=${case1}/manifest.json
rc=0
run_gen \
  --publish-dir "$publish" \
  --app-version "1.2.3" \
  --git-sha "abc123def456" \
  --tarball "${case1}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "$out" >/dev/null 2>&1 || rc=$?
assert "case 1: exit 0 on happy path" [ "$rc" -eq 0 ]
assert "case 1: manifest file created" [ -f "$out" ]
assert "case 1: manifest is valid JSON" bash -c "jq -e . '$out' >/dev/null"

# ---------------------------------------------------------------------------
# Case 11: schema invariants — fields, types, values (do early so the
# manifest from case 1 is freshly minted)
# ---------------------------------------------------------------------------
assert "case 11: schemaVersion == '1'"     bash -c "[ \"\$(jq -r .schemaVersion       '$out')\" = '1' ]"
assert "case 11: appVersion == '1.2.3'"    bash -c "[ \"\$(jq -r .appVersion          '$out')\" = '1.2.3' ]"
assert "case 11: gitSha == 'abc123def456'" bash -c "[ \"\$(jq -r .gitSha              '$out')\" = 'abc123def456' ]"
assert "case 11: publishMode == 'portable'" bash -c "[ \"\$(jq -r .publishMode        '$out')\" = 'portable' ]"
assert "case 11: targetFramework == 'net10.0'" bash -c "[ \"\$(jq -r .targetFramework '$out')\" = 'net10.0' ]"
assert "case 11: requiredRuntime is an object" bash -c "[ \"\$(jq -r '.requiredRuntime | type' '$out')\" = 'object' ]"
assert "case 11: artifacts.tarball is an object" bash -c "[ \"\$(jq -r '.artifacts.tarball | type' '$out')\" = 'object' ]"
assert "case 11: artifacts.tarball.sizeBytes is a number" bash -c "[ \"\$(jq -r '.artifacts.tarball.sizeBytes | type' '$out')\" = 'number' ]"
assert "case 11: openapiSha256 absent when --openapi-sha-file omitted (case 1 happy path)" \
  bash -c "[ \"\$(jq -r '.artifacts | has(\"openapiSha256\")' '$out')\" = 'false' ]"

# ---------------------------------------------------------------------------
# Case 12: requiredRuntime sourced from runtimeconfig verbatim (ADR-011)
# ---------------------------------------------------------------------------
assert "case 12: requiredRuntime.name == fixture value" \
  bash -c "[ \"\$(jq -r .requiredRuntime.name '$out')\" = 'Microsoft.AspNetCore.App' ]"
assert "case 12: requiredRuntime.minVersion == fixture value" \
  bash -c "[ \"\$(jq -r .requiredRuntime.minVersion '$out')\" = '10.0.0' ]"
assert "case 12: requiredRuntime.rollForward == fixture value" \
  bash -c "[ \"\$(jq -r .requiredRuntime.rollForward '$out')\" = 'LatestMinor' ]"

# ---------------------------------------------------------------------------
# Case 8 + 9: tarball sha and size are correct
# ---------------------------------------------------------------------------
expected_sha=$(sha256sum "${case1}/artifact.tar.gz" | awk '{print $1}')
expected_size=$(wc -c < "${case1}/artifact.tar.gz" | tr -d ' ')
got_sha=$(jq -r .artifacts.tarball.sha256 "$out")
got_size=$(jq -r .artifacts.tarball.sizeBytes "$out")
assert "case 8: tarball sha matches sha256sum" [ "$expected_sha" = "$got_sha" ]
assert "case 9: tarball size matches wc -c"     [ "$expected_size" = "$got_size" ]

# ---------------------------------------------------------------------------
# Case 7: determinism — re-run produces identical manifest
# ---------------------------------------------------------------------------
out2=${case1}/manifest2.json
run_gen \
  --publish-dir "$publish" \
  --app-version "1.2.3" \
  --git-sha "abc123def456" \
  --tarball "${case1}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "$out2" >/dev/null 2>&1
assert "case 7: two runs produce byte-identical manifest" cmp -s "$out" "$out2"

# ---------------------------------------------------------------------------
# Case 10a: openapi sha sidecar present
# ---------------------------------------------------------------------------
sha_file=${case1}/openapi.json.sha256
printf 'deadbeefcafebabe1234567890abcdef\n' > "$sha_file"
out3=${case1}/manifest_with_openapi.json
run_gen \
  --publish-dir "$publish" \
  --app-version "1.2.3" \
  --git-sha "abc123def456" \
  --tarball "${case1}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --openapi-sha-file "$sha_file" \
  --output "$out3" >/dev/null 2>&1
assert "case 10a: openapiSha256 present when sidecar exists" \
  bash -c "[ \"\$(jq -r '.artifacts.openapiSha256 // empty' '$out3')\" = 'deadbeefcafebabe1234567890abcdef' ]"

# ---------------------------------------------------------------------------
# Case 10b: openapi sha sidecar absent path silently omitted
# ---------------------------------------------------------------------------
out4=${case1}/manifest_no_sidecar.json
run_gen \
  --publish-dir "$publish" \
  --app-version "1.2.3" \
  --git-sha "abc123def456" \
  --tarball "${case1}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --openapi-sha-file "${case1}/does-not-exist.sha256" \
  --output "$out4" >/dev/null 2>&1
assert "case 10b: openapiSha256 omitted when sidecar missing" \
  bash -c "[ \"\$(jq -r '.artifacts | has(\"openapiSha256\")' '$out4')\" = 'false' ]"

# ---------------------------------------------------------------------------
# Case 2: missing runtimeconfig — exit 2
# ---------------------------------------------------------------------------
case2=${SCRATCH}/case2
mkdir -p "${case2}/publish"
printf 'placeholder\n' > "${case2}/publish/ExpertiseApi.dll"
"$TAR_BIN" --sort=name --owner=0 --group=0 --numeric-owner --mtime='2020-01-01 00:00:00 UTC' --format=ustar \
  -C "${case2}/publish" -cf - . | gzip -n > "${case2}/artifact.tar.gz"
rc=0
run_gen \
  --publish-dir "${case2}/publish" \
  --app-version "1.2.3" \
  --git-sha "abc" \
  --tarball "${case2}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "${case2}/manifest.json" >/dev/null 2>&1 || rc=$?
assert "case 2: missing runtimeconfig exits 2" [ "$rc" -eq 2 ]

# ---------------------------------------------------------------------------
# Case 3: malformed runtimeconfig (no framework.version)
# ---------------------------------------------------------------------------
case3=${SCRATCH}/case3
publish3=$(build_fixture "$case3" '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.AspNetCore.App"}}}')
rc=0
run_gen \
  --publish-dir "$publish3" \
  --app-version "1.2.3" \
  --git-sha "abc" \
  --tarball "${case3}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "${case3}/manifest.json" >/dev/null 2>&1 || rc=$?
assert "case 3: malformed runtimeconfig exits 2" [ "$rc" -eq 2 ]

# ---------------------------------------------------------------------------
# Case 4: multiple runtimeconfigs — ambiguous
# ---------------------------------------------------------------------------
case4=${SCRATCH}/case4
publish4=$(build_fixture "$case4")
cp "${publish4}/ExpertiseApi.runtimeconfig.json" "${publish4}/Other.runtimeconfig.json"
rc=0
run_gen \
  --publish-dir "$publish4" \
  --app-version "1.2.3" \
  --git-sha "abc" \
  --tarball "${case4}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "${case4}/manifest.json" >/dev/null 2>&1 || rc=$?
assert "case 4: multiple runtimeconfigs exits 2 (ambiguous)" [ "$rc" -eq 2 ]

# ---------------------------------------------------------------------------
# Case 5: missing required CLI argument
# ---------------------------------------------------------------------------
rc=0
run_gen --publish-dir /tmp >/dev/null 2>&1 || rc=$?
assert "case 5: missing required args exits 1" [ "$rc" -eq 1 ]

# ---------------------------------------------------------------------------
# Case 6: non-integer SOURCE_DATE_EPOCH
# ---------------------------------------------------------------------------
case6=${SCRATCH}/case6
publish6=$(build_fixture "$case6")
rc=0
run_gen \
  --publish-dir "$publish6" \
  --app-version "1.2.3" \
  --git-sha "abc" \
  --tarball "${case6}/artifact.tar.gz" \
  --source-date-epoch "not-a-number" \
  --output "${case6}/manifest.json" >/dev/null 2>&1 || rc=$?
assert "case 6: non-integer SOURCE_DATE_EPOCH exits 1" [ "$rc" -eq 1 ]

# ---------------------------------------------------------------------------
# Case 12b: mutation-style proof — changing fixture's framework.version
# produces a different manifest. Locks the verbatim-sourcing invariant
# beyond the static greps below (which only fire on shell-assignment
# literals, not on `--arg required_min "<const>"` regressions).
# ---------------------------------------------------------------------------
case12b=${SCRATCH}/case12b
publish12b=$(build_fixture "$case12b" '{"runtimeOptions":{"tfm":"net10.0","rollForward":"LatestMinor","framework":{"name":"Microsoft.AspNetCore.App","version":"11.0.42"}}}')
out_mut=${case12b}/manifest.json
run_gen \
  --publish-dir "$publish12b" \
  --app-version "1.2.3" \
  --git-sha "abc123def456" \
  --tarball "${case12b}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "$out_mut" >/dev/null 2>&1
assert "case 12b: mutated fixture.framework.version flows into manifest" \
  bash -c "[ \"\$(jq -r .requiredRuntime.minVersion '$out_mut')\" = '11.0.42' ]"
assert "case 12b: manifest from mutated fixture differs from case 1" \
  bash -c "! cmp -s '$out' '$out_mut'"

# ---------------------------------------------------------------------------
# Case 13: --publish-mode self-contained plumbs through correctly
# ---------------------------------------------------------------------------
case13=${SCRATCH}/case13
publish13=$(build_fixture "$case13")
out13=${case13}/manifest.json
run_gen \
  --publish-dir "$publish13" \
  --app-version "1.2.3" \
  --git-sha "abc" \
  --tarball "${case13}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --publish-mode "self-contained" \
  --output "$out13" >/dev/null 2>&1
assert "case 13: publishMode override flows into manifest" \
  bash -c "[ \"\$(jq -r .publishMode '$out13')\" = 'self-contained' ]"

# ---------------------------------------------------------------------------
# Case 14: runtimeconfig.json with `frameworks` ARRAY form (single entry).
# .NET SDK is permitted to emit either singular `.framework` (the typical
# Microsoft.NET.Sdk.Web shape) OR an array `.frameworks` shape. The
# defensive jq path must accept both and produce the same manifest. This
# is the principal regression test for the runtimeconfig-shape-tolerance
# refactor (D2.1 / generate-manifest defensive jq).
# ---------------------------------------------------------------------------
case14=${SCRATCH}/case14
publish14=$(build_fixture "$case14" '{"runtimeOptions":{"tfm":"net10.0","rollForward":"LatestMinor","frameworks":[{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}')
out14=${case14}/manifest.json
run_gen \
  --publish-dir "$publish14" \
  --app-version "1.2.3" \
  --git-sha "abc" \
  --tarball "${case14}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "$out14" >/dev/null 2>&1
assert "case 14: frameworks-array form yields requiredRuntime.name = AspNetCore.App" \
  bash -c "[ \"\$(jq -r .requiredRuntime.name '$out14')\" = 'Microsoft.AspNetCore.App' ]"
assert "case 14: frameworks-array form yields requiredRuntime.minVersion = 10.0.0" \
  bash -c "[ \"\$(jq -r .requiredRuntime.minVersion '$out14')\" = '10.0.0' ]"

# ---------------------------------------------------------------------------
# Case 15: runtimeconfig.json with `frameworks` array containing BOTH
# Microsoft.NETCore.App and Microsoft.AspNetCore.App. The filter MUST pick
# AspNetCore.App (NETCore.App is a lower-level floor that would give
# install.sh's preflight the wrong answer).
# ---------------------------------------------------------------------------
case15=${SCRATCH}/case15
publish15=$(build_fixture "$case15" '{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.5"}]}}')
out15=${case15}/manifest.json
run_gen \
  --publish-dir "$publish15" \
  --app-version "1.2.3" \
  --git-sha "abc" \
  --tarball "${case15}/artifact.tar.gz" \
  --source-date-epoch "1577836800" \
  --output "$out15" >/dev/null 2>&1
assert "case 15: multi-framework array picks AspNetCore.App, not NETCore.App" \
  bash -c "[ \"\$(jq -r .requiredRuntime.name '$out15')\" = 'Microsoft.AspNetCore.App' ]"
assert "case 15: multi-framework array picks AspNetCore.App version (10.0.5), not NETCore (10.0.0)" \
  bash -c "[ \"\$(jq -r .requiredRuntime.minVersion '$out15')\" = '10.0.5' ]"

# ---------------------------------------------------------------------------
# Case 16: runtimeconfig.json with frameworks-array containing ONLY
# Microsoft.NETCore.App (no AspNetCore.App). install.sh preflight checks
# the AspNetCore floor; emitting a manifest with no AspNetCore floor
# would either misdirect preflight or silently pass garbage. Refuse to
# emit (exit 2 + actionable error message).
# ---------------------------------------------------------------------------
case16=${SCRATCH}/case16
publish16=$(build_fixture "$case16" '{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}')
out16=${case16}/manifest.json
run_gen --publish-dir "$publish16" --app-version "1.2.3" --git-sha "abc" \
  --tarball "${case16}/artifact.tar.gz" --source-date-epoch "1577836800" \
  --output "$out16" >/dev/null 2>&1 && rc=0 || rc=$?
assert "case 16: refuses when frameworks array lacks AspNetCore.App" \
  bash -c "[ '$rc' -ne 0 ]"
assert "case 16: did not produce a manifest file" \
  bash -c "[ ! -s '$out16' ]"

# ---------------------------------------------------------------------------
# Static lint-time guard: refuse to emit a manifest that hand-sets
# requiredRuntime — the script must always read it from runtimeconfig.
# Grep-based defense (mirrors the argv-leak guard in test-bootstrap-common).
# ---------------------------------------------------------------------------
assert "static guard: no hardcoded requiredRuntime.minVersion in generator" \
  bash -c "! grep -E 'requiredRuntime.minVersion[[:space:]]*=[[:space:]]*\"?[0-9]' '$GEN'"
assert "static guard: requiredRuntime sourced via jq from runtimeconfig (D2.1 framework_jq form)" \
  bash -c "grep -qF 'framework_jq' '$GEN'"
assert "static guard: defensive filter pins Microsoft.AspNetCore.App" \
  bash -c "grep -qF 'Microsoft.AspNetCore.App' '$GEN'"
assert "static guard: accepts both singular .framework AND array .frameworks shapes" \
  bash -c "grep -qF '.runtimeOptions.frameworks' '$GEN' && grep -qF '.runtimeOptions.framework' '$GEN'"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
printf '\n'
printf 'test-generate-manifest.sh: %d passed, %d failed\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
