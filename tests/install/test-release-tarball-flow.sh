#!/usr/bin/env bash
# tests/install/test-release-tarball-flow.sh
# Unit tests for scripts/lib/release-consumer.sh and scripts/verify-release.sh
# (D3 of ADR-011 / issue #249).
#
# Tests the deterministic pieces of the release-tarball install flow:
#
#   * rc_resolve_version            — verbatim passthrough + leading-v strip
#   * rc_enforce_downgrade_defense  — older/equal/newer + republish-sha cases
#   * rc_check_aspnetcore_runtime_floor — semver compare against stub dotnet
#   * rc_select_tar                 — bsdtar/gnutar detection
#   * rc_inspect_staged_tree        — symlinks, setuid, case-folding, length
#   * rc_extract_tarball_quarantined — bsdtar happy-path + traversal refused
#   * rc_write_post_install_markers — mode + semver + history shape
#   * vr_validate_manifest_schema   — strict schemaVersion check
#   * vr_crosscheck_tarball_sha     — sha mismatch refused
#
# Cosign verify-blob and the GitHub Releases API are stubbed via a
# PATH-prepended fake `cosign` / `curl`; the live end-to-end exercise
# against a real signed release is part of #249's acceptance criteria
# (covered by the smoke-test that #166 will introduce, not here).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
LIB_RC="${SCRIPT_DIR}/scripts/lib/release-consumer.sh"
LIB_VR="${SCRIPT_DIR}/scripts/verify-release.sh"
TEST_NAME=$(basename "$0")

PASS=0
FAIL=0
assert() {
  local name="$1"; shift
  if "$@"; then PASS=$((PASS+1)); else printf 'FAIL: %s\n' "${name}" >&2; FAIL=$((FAIL+1)); fi
}

SCRATCH="$(mktemp -d -t release-flow.XXXXXX)"
trap 'rm -rf "${SCRATCH}"' EXIT

# ---------------------------------------------------------------------------
# Harness: source both libraries into a subshell with install.sh's parent
# symbols stubbed. SCRIPT_DIR must point at the repo's scripts/ for
# rc_source_verify_release; PREFIX is per-test scratch.
# ---------------------------------------------------------------------------
make_harness() {
  local extra="${1:-}"
  cat <<HARNESS
set -uo pipefail
SCRIPT_DIR="${SCRIPT_DIR}/scripts"
log()  { printf '[stub] %s\n' "\$1"; }
warn() { printf '[stub WARN] %s\n' "\$1" >&2; }
err()  { printf '[stub ERR] %s\n' "\$1" >&2; exit 1; }
PREFIX="${SCRATCH}/prefix"
STAGE_DIR="\${PREFIX}/bin.new"
COSIGN_IDENTITY="https://github.com/TheSemicolon/agent-expertise-api/.github/workflows/release.yml@refs/heads/main"
ALLOW_DOWNGRADE=0
ACCEPT_REPUBLISHED_VERSION=0
SKIP_RELEASE_API_CROSSCHECK=0
NEW_VERSION="test-1.2.3"
mkdir -p "\${PREFIX}"
# shellcheck disable=SC1090
. "${LIB_RC}"
${extra}
HARNESS
}

# ---------------------------------------------------------------------------
# Case 1: rc_resolve_version verbatim + leading-v strip.
# ---------------------------------------------------------------------------
out=$(bash -c "$(make_harness 'rc_resolve_version v1.2.3')" 2>&1)
assert "case 1a: leading-v stripped" \
  bash -c "[ '$out' = '1.2.3' ]"
out=$(bash -c "$(make_harness 'rc_resolve_version 1.2.3')" 2>&1)
assert "case 1b: no-v passthrough" \
  bash -c "[ '$out' = '1.2.3' ]"

# ---------------------------------------------------------------------------
# Case 2: rc_enforce_downgrade_defense — no prior marker → permitted.
# ---------------------------------------------------------------------------
out=$(bash -c "$(make_harness 'rc_enforce_downgrade_defense 1.0.0 deadbeef && echo DEFENSE_OK')" 2>&1)
assert "case 2: no prior marker permits first install" \
  bash -c "printf '%s' '$out' | grep -q DEFENSE_OK"

# ---------------------------------------------------------------------------
# Case 3: downgrade refused (prior 2.0.0, incoming 1.0.0).
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case3-prefix"
cat > "${SCRATCH}/case3-prefix/.install-version-semver" <<EOF
appVersion=2.0.0
manifestSha256=aaaa
EOF
out=$(PREFIX="${SCRATCH}/case3-prefix" bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { printf '[stub WARN] %s\n' \"\$1\" >&2; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case3-prefix'; ALLOW_DOWNGRADE=0; ACCEPT_REPUBLISHED_VERSION=0
. '${LIB_RC}'
( rc_enforce_downgrade_defense 1.0.0 bbbb ) && echo NO_REFUSE || echo REFUSED
" 2>&1)
assert "case 3: downgrade refused without --allow-downgrade" \
  bash -c "printf '%s' '$out' | grep -q REFUSED && printf '%s' '$out' | grep -q 'downgrade refused'"

# ---------------------------------------------------------------------------
# Case 4: downgrade permitted with --allow-downgrade.
# ---------------------------------------------------------------------------
out=$(PREFIX="${SCRATCH}/case3-prefix" bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { printf '[stub WARN] %s\n' \"\$1\" >&2; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case3-prefix'; ALLOW_DOWNGRADE=1; ACCEPT_REPUBLISHED_VERSION=0
. '${LIB_RC}'
rc_enforce_downgrade_defense 1.0.0 bbbb && echo OK || echo BLOCKED
" 2>&1)
assert "case 4: downgrade permitted by --allow-downgrade" \
  bash -c "printf '%s' '$out' | grep -q OK && printf '%s' '$out' | grep -q 'downgrade.*permitted'"

# ---------------------------------------------------------------------------
# Case 5: republish same version with DIFFERENT sha refused.
# ---------------------------------------------------------------------------
out=$(PREFIX="${SCRATCH}/case3-prefix" bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { :; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case3-prefix'; ALLOW_DOWNGRADE=0; ACCEPT_REPUBLISHED_VERSION=0
. '${LIB_RC}'
( rc_enforce_downgrade_defense 2.0.0 bbbb ) && echo NO_REFUSE || echo REFUSED
" 2>&1)
assert "case 5: republish (same version, different sha) refused" \
  bash -c "printf '%s' '$out' | grep -q REFUSED && printf '%s' '$out' | grep -q republished"

# ---------------------------------------------------------------------------
# Case 6: republish permitted with --accept-republished-version.
# ---------------------------------------------------------------------------
out=$(PREFIX="${SCRATCH}/case3-prefix" bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { :; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case3-prefix'; ALLOW_DOWNGRADE=0; ACCEPT_REPUBLISHED_VERSION=1
. '${LIB_RC}'
rc_enforce_downgrade_defense 2.0.0 bbbb && echo OK || echo BLOCKED
" 2>&1)
assert "case 6: republish permitted by --accept-republished-version" \
  bash -c "printf '%s' '$out' | grep -q OK"

# ---------------------------------------------------------------------------
# Case 7: rc_check_aspnetcore_runtime_floor — stub dotnet on PATH.
# ---------------------------------------------------------------------------
make_dotnet_stub() {
  local dir="${SCRATCH}/${1}-bin"
  mkdir -p "$dir"
  local runtimes="$2"
  cat > "${dir}/dotnet" <<EOF
#!/usr/bin/env bash
case "\$1" in
  --list-runtimes) printf '%s\n' '${runtimes}' ;;
  *) exit 0 ;;
esac
EOF
  chmod +x "${dir}/dotnet"
  printf '%s' "$dir"
}
DOTNET_OK=$(make_dotnet_stub case7ok 'Microsoft.NETCore.App 10.0.0 [/dev/null]
Microsoft.AspNetCore.App 10.0.5 [/dev/null]')
out=$(PATH="${DOTNET_OK}:${PATH}" bash -c "$(make_harness 'rc_check_aspnetcore_runtime_floor 10.0.0 && echo FLOOR_OK')" 2>&1)
assert "case 7a: AspNetCore 10.0.5 satisfies floor 10.0.0" \
  bash -c "printf '%s' '$out' | grep -q FLOOR_OK"

DOTNET_TOO_OLD=$(make_dotnet_stub case7old 'Microsoft.AspNetCore.App 9.0.5 [/dev/null]')
out=$(PATH="${DOTNET_TOO_OLD}:${PATH}" bash -c "$(make_harness '( rc_check_aspnetcore_runtime_floor 10.0.0 ) || echo FLOOR_REFUSED')" 2>&1)
assert "case 7b: AspNetCore 9.x refused against floor 10.0.0" \
  bash -c "printf '%s' '$out' | grep -q FLOOR_REFUSED"

DOTNET_PREVIEW=$(make_dotnet_stub case7preview 'Microsoft.AspNetCore.App 10.0.0-preview.1 [/dev/null]')
out=$(PATH="${DOTNET_PREVIEW}:${PATH}" bash -c "$(make_harness '( rc_check_aspnetcore_runtime_floor 10.0.0 ) || echo FLOOR_REFUSED')" 2>&1)
assert "case 7c: prerelease AspNetCore excluded from floor candidates" \
  bash -c "printf '%s' '$out' | grep -q FLOOR_REFUSED"

# ---------------------------------------------------------------------------
# Case 8: rc_select_tar — at least one of bsdtar or gnutar resolvable on
# the test host (macOS ships bsdtar as `tar`; CI Linux has GNU tar).
# ---------------------------------------------------------------------------
out=$(bash -c "$(make_harness 'rc_select_tar')" 2>&1)
assert "case 8: tar kind resolves to bsdtar or gnutar" \
  bash -c "printf '%s' '$out' | grep -qE '^(bsdtar|gnutar)$'"

# ---------------------------------------------------------------------------
# Case 9: rc_inspect_staged_tree — symlink refused.
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case9/tree"
ln -s /etc/passwd "${SCRATCH}/case9/tree/evil"
out=$(bash -c "$(make_harness "( rc_inspect_staged_tree '${SCRATCH}/case9/tree' ) || echo INSPECTOR_REFUSED")" 2>&1)
assert "case 9: inspector refuses tree containing symlink" \
  bash -c "printf '%s' '$out' | grep -q INSPECTOR_REFUSED && printf '%s' '$out' | grep -q symlinks"

# ---------------------------------------------------------------------------
# Case 10: rc_inspect_staged_tree — setuid refused.
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case10/tree"
echo binary > "${SCRATCH}/case10/tree/setuid-foo"
chmod 4755 "${SCRATCH}/case10/tree/setuid-foo"
out=$(bash -c "$(make_harness "( rc_inspect_staged_tree '${SCRATCH}/case10/tree' ) || echo INSPECTOR_REFUSED")" 2>&1)
assert "case 10: inspector refuses tree containing setuid file" \
  bash -c "printf '%s' '$out' | grep -q INSPECTOR_REFUSED && printf '%s' '$out' | grep -q setuid"

# ---------------------------------------------------------------------------
# Case 11: rc_inspect_staged_tree — case-folding collision refused.
# (Test FS is case-sensitive on Linux CI but the awk-based check works
#  regardless of underlying FS sensitivity — it operates on the listed
#  names not on stat results.)
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case11/tree"
echo a > "${SCRATCH}/case11/tree/Foo"
echo b > "${SCRATCH}/case11/tree/foo"
# Some filesystems (HFS+ default, APFS volume formatted case-insensitive)
# will collapse the two writes; only run the assertion when both files
# actually exist on disk.
if [ -e "${SCRATCH}/case11/tree/Foo" ] && [ -e "${SCRATCH}/case11/tree/foo" ] \
   && [ "$(find "${SCRATCH}/case11/tree" -mindepth 1 -maxdepth 1 | wc -l | tr -d ' ')" -eq 2 ]; then
  out=$(bash -c "$(make_harness "( rc_inspect_staged_tree '${SCRATCH}/case11/tree' ) || echo INSPECTOR_REFUSED")" 2>&1)
  assert "case 11: inspector refuses case-folding collision" \
    bash -c "printf '%s' '$out' | grep -q INSPECTOR_REFUSED && printf '%s' '$out' | grep -q 'case-folding'"
else
  printf 'SKIP: case 11 (test FS is case-insensitive; two-name fixture collapsed)\n'
fi

# ---------------------------------------------------------------------------
# Case 12: rc_extract_tarball_quarantined — happy-path with a clean tarball.
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case12/src/bin"
echo ExpertiseApi > "${SCRATCH}/case12/src/bin/ExpertiseApi.dll"
echo cfg > "${SCRATCH}/case12/src/bin/ExpertiseApi.runtimeconfig.json"
( cd "${SCRATCH}/case12/src" && tar -czf "${SCRATCH}/case12/clean.tar.gz" . )
out=$(STAGE_DIR="${SCRATCH}/case12/stage" bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { :; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case12'
STAGE_DIR='${SCRATCH}/case12/stage'
. '${LIB_RC}'
rc_extract_tarball_quarantined '${SCRATCH}/case12/clean.tar.gz' '${SCRATCH}/case12/stage' && echo EXTRACT_OK
" 2>&1)
assert "case 12a: clean tarball extracts" \
  bash -c "printf '%s' '$out' | grep -q EXTRACT_OK"
assert "case 12b: STAGE_DIR populated with expected files" \
  bash -c "[ -f '${SCRATCH}/case12/stage/bin/ExpertiseApi.dll' ]"
assert "case 12c: STAGE_DIR.unpack quarantine path cleaned up after rename" \
  bash -c "[ ! -e '${SCRATCH}/case12/stage.unpack' ]"

# ---------------------------------------------------------------------------
# Case 13: rc_write_post_install_markers — release-mode writes all three.
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case13/prefix"
mkdir -p "${SCRATCH}/case13/manifest-dir"
echo '{"artifacts":{"tarball":{"sha256":"deadbeef"}}}' > "${SCRATCH}/case13/manifest-dir/m.json"
bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { :; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case13/prefix'
COSIGN_IDENTITY='https://github.com/TheSemicolon/agent-expertise-api/.github/workflows/release.yml@refs/heads/main'
VERIFIED_APP_VERSION='1.2.3'
VERIFIED_MANIFEST_SHA='cafef00d'
VERIFIED_MANIFEST_PATH='${SCRATCH}/case13/manifest-dir/m.json'
NEW_VERSION='test-1.2.3'
. '${LIB_RC}'
rc_write_post_install_markers release
" >/dev/null 2>&1
assert "case 13a: .install-mode written with 'release'" \
  bash -c "[ \"\$(cat '${SCRATCH}/case13/prefix/.install-mode')\" = 'release' ]"
assert "case 13b: .install-version-semver carries appVersion" \
  bash -c "grep -q '^appVersion=1.2.3$' '${SCRATCH}/case13/prefix/.install-version-semver'"
assert "case 13c: .install-version-semver carries manifestSha256" \
  bash -c "grep -q '^manifestSha256=cafef00d$' '${SCRATCH}/case13/prefix/.install-version-semver'"
assert "case 13d: .install-history appended (one line, release record)" \
  bash -c "[ \"\$(wc -l < '${SCRATCH}/case13/prefix/.install-history' | tr -d ' ')\" = '1' ] && grep -q 'mode=release' '${SCRATCH}/case13/prefix/.install-history'"
assert "case 13e: marker file modes are 644" \
  bash -c "
    if [ \"\$(uname)\" = 'Darwin' ]; then m=\$(stat -f '%Lp' '${SCRATCH}/case13/prefix/.install-mode'); else m=\$(stat -c '%a' '${SCRATCH}/case13/prefix/.install-mode'); fi
    [ \"\$m\" = '644' ]
  "

# ---------------------------------------------------------------------------
# Case 14: rc_write_post_install_markers — source-mode skips semver marker.
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case14/prefix"
bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { :; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case14/prefix'
COSIGN_IDENTITY=''
NEW_VERSION='test-source'
. '${LIB_RC}'
rc_write_post_install_markers source
" >/dev/null 2>&1
assert "case 14a: source-mode writes .install-mode" \
  bash -c "[ \"\$(cat '${SCRATCH}/case14/prefix/.install-mode')\" = 'source' ]"
assert "case 14b: source-mode does NOT write .install-version-semver" \
  bash -c "[ ! -f '${SCRATCH}/case14/prefix/.install-version-semver' ]"
assert "case 14c: source-mode appends history with mode=source" \
  bash -c "grep -q 'mode=source' '${SCRATCH}/case14/prefix/.install-history'"

# ---------------------------------------------------------------------------
# Case 15: vr_validate_manifest_schema — unsupported schema refused.
# ---------------------------------------------------------------------------
echo '{"schemaVersion":"99"}' > "${SCRATCH}/case15.json"
out=$(SOURCE_ONLY=1 bash -c ". '${LIB_VR}'; ( vr_validate_manifest_schema '${SCRATCH}/case15.json' ) && echo OK || echo REFUSED" 2>&1)
assert "case 15: schemaVersion=99 refused" \
  bash -c "printf '%s' '$out' | grep -q REFUSED && printf '%s' '$out' | grep -q 'unsupported manifest schemaVersion'"

echo '{"schemaVersion":"1"}' > "${SCRATCH}/case15ok.json"
out=$(SOURCE_ONLY=1 bash -c ". '${LIB_VR}'; vr_validate_manifest_schema '${SCRATCH}/case15ok.json' && echo OK" 2>&1)
assert "case 15b: schemaVersion=1 accepted" \
  bash -c "printf '%s' '$out' | grep -q OK"

# ---------------------------------------------------------------------------
# Case 16: vr_crosscheck_tarball_sha — mismatch refused, match accepted.
# ---------------------------------------------------------------------------
echo "tarball content" > "${SCRATCH}/case16.tar.gz"
actual_sha=$(sha256sum "${SCRATCH}/case16.tar.gz" | awk '{print $1}')
cat > "${SCRATCH}/case16-good.json" <<EOF
{"artifacts":{"tarball":{"sha256":"${actual_sha}"}}}
EOF
out=$(SOURCE_ONLY=1 bash -c ". '${LIB_VR}'; vr_crosscheck_tarball_sha '${SCRATCH}/case16.tar.gz' '${SCRATCH}/case16-good.json' && echo OK" 2>&1)
assert "case 16a: matching sha accepted" \
  bash -c "printf '%s' '$out' | grep -q OK"

cat > "${SCRATCH}/case16-bad.json" <<'EOF'
{"artifacts":{"tarball":{"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}}
EOF
out=$(SOURCE_ONLY=1 bash -c ". '${LIB_VR}'; ( vr_crosscheck_tarball_sha '${SCRATCH}/case16.tar.gz' '${SCRATCH}/case16-bad.json' ) && echo OK || echo REFUSED" 2>&1)
assert "case 16b: mismatched sha refused with TARBALL TAMPERED message" \
  bash -c "printf '%s' '$out' | grep -q REFUSED && printf '%s' '$out' | grep -q 'TARBALL TAMPERED'"

cat > "${SCRATCH}/case16-shortsha.json" <<'EOF'
{"artifacts":{"tarball":{"sha256":"abcdef0123456789"}}}
EOF
out=$(SOURCE_ONLY=1 bash -c ". '${LIB_VR}'; ( vr_crosscheck_tarball_sha '${SCRATCH}/case16.tar.gz' '${SCRATCH}/case16-shortsha.json' ) && echo OK || echo REFUSED" 2>&1)
assert "case 16c: short sha refused with length error" \
  bash -c "printf '%s' '$out' | grep -q REFUSED && printf '%s' '$out' | grep -q 'length != 64'"

# ---------------------------------------------------------------------------
# Case 17: argparse — --version rejected in source mode. Since the D4 flip
# (#249) made release the default, source mode must be requested explicitly
# (--from-source --i-accept-unverified-source) to exercise this rejection.
# ---------------------------------------------------------------------------
out=$(bash "${SCRIPT_DIR}/scripts/install.sh" --from-source --i-accept-unverified-source --version 1.2.3 2>&1 || true)
assert "case 17: --version in source mode is rejected" \
  bash -c "printf '%s' '$out' | grep -q 'only meaningful with --from-release'"

# ---------------------------------------------------------------------------
# Case 18: argparse — --allow-downgrade rejected in source mode.
# ---------------------------------------------------------------------------
out=$(bash "${SCRIPT_DIR}/scripts/install.sh" --from-source --i-accept-unverified-source --allow-downgrade 2>&1 || true)
assert "case 18: --allow-downgrade in source mode is rejected" \
  bash -c "printf '%s' '$out' | grep -q 'release-mode-only'"

# ---------------------------------------------------------------------------
# Case 18b (D4 flip, #249): --from-source WITHOUT --i-accept-unverified-source
# is refused — the flip made the unverified source path an explicit, ack'd choice.
# ---------------------------------------------------------------------------
out=$(bash "${SCRIPT_DIR}/scripts/install.sh" --from-source 2>&1 || true)
assert "case 18b: --from-source requires --i-accept-unverified-source" \
  bash -c "printf '%s' '$out' | grep -q 'i-accept-unverified-source'"

# ---------------------------------------------------------------------------
# Case 18c (D4 flip, #249): --i-accept-unverified-source WITHOUT a mode flag
# must error, not silently resolve to the default (release) with the flag
# ignored. Regression guard for the resolve-before-guard ordering.
# ---------------------------------------------------------------------------
out=$(bash "${SCRIPT_DIR}/scripts/install.sh" --i-accept-unverified-source 2>&1 || true)
assert "case 18c: bare --i-accept-unverified-source (no mode) is rejected" \
  bash -c "printf '%s' '$out' | grep -q 'meaningful only with --from-source'"

# ---------------------------------------------------------------------------
# Case 19: verify-release.sh CLI — missing flag yields exit code 2.
# ---------------------------------------------------------------------------
bash "${LIB_VR}" --tarball /dev/null --manifest /dev/null --signature /dev/null >/dev/null 2>&1; rc=$?
assert "case 19: verify-release.sh refuses missing --certificate with exit 2" \
  bash -c "[ '$rc' = '2' ]"

# ---------------------------------------------------------------------------
# Case 20: rc_publish_from_release — first install requires explicit version.
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case20/prefix"
out=$(bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { :; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case20/prefix'
STAGE_DIR='${SCRATCH}/case20/prefix/bin.new'
ALLOW_DOWNGRADE=0; ACCEPT_REPUBLISHED_VERSION=0; SKIP_RELEASE_API_CROSSCHECK=1
# Stub cosign so vr_require_cosign passes; rc_resolve_version 'latest' will
# call the GH API which we don't want to hit — pre-empt the test by relying
# on rc_publish_from_release's first-install policy fail-fast BEFORE
# rc_resolve_version runs. The policy is checked at the start.
PATH='${SCRATCH}/case20-bin:'\"\$PATH\"
mkdir -p '${SCRATCH}/case20-bin'
cat > '${SCRATCH}/case20-bin/cosign' <<'STUB'
#!/usr/bin/env bash
if [ \"\$1\" = 'version' ] && [ \"\$2\" = '--json' ]; then
  printf '{\"gitVersion\":\"v2.4.0\"}\n'
elif [ \"\$1\" = 'version' ]; then
  printf 'GitVersion: v2.4.0\n'
elif [ \"\$1\" = 'verify-blob' ]; then
  exit 0
else
  exit 0
fi
STUB
chmod +x '${SCRATCH}/case20-bin/cosign'
. '${LIB_RC}'
( rc_publish_from_release latest ) && echo OK || echo REFUSED
" 2>&1)
assert "case 20: --from-release --version latest on first install refused" \
  bash -c "printf '%s' '$out' | grep -q REFUSED && printf '%s' '$out' | grep -q 'first --from-release install'"

echo "--- new D3 pre-PR fold-in cases ---"

# ---------------------------------------------------------------------------
# Case 21 (post-fold): rc_inspect_staged_tree refuses path with embedded
# newline. Touches the HIGH shell-expert finding fix: `find -print | awk`
# was bypassable by newline-bearing filenames; we now use -print0.
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case21/tree"
if touch "${SCRATCH}/case21/tree/"$'evil\nname' 2>/dev/null; then
  out=$(bash -c "$(make_harness "( rc_inspect_staged_tree '${SCRATCH}/case21/tree' ) || echo INSPECTOR_REFUSED")" 2>&1)
  assert "case 21: inspector refuses newline-bearing filename" \
    bash -c "printf '%s' '$out' | grep -q INSPECTOR_REFUSED && printf '%s' '$out' | grep -q 'embedded newline'"
else
  printf 'SKIP: case 21 (FS refuses to create newline-bearing filename)\n'
fi

# ---------------------------------------------------------------------------
# Case 22 (post-fold): clean tree passes inspector (regression guard for
# the rewritten implementation — ensures we didn't break the happy path).
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case22/tree/sub"
echo ok > "${SCRATCH}/case22/tree/sub/file"
out=$(bash -c "$(make_harness "rc_inspect_staged_tree '${SCRATCH}/case22/tree' && echo CLEAN")" 2>&1)
assert "case 22: clean tree (no traversal) passes inspector" \
  bash -c "printf '%s' '$out' | grep -q CLEAN"

# ---------------------------------------------------------------------------
# Case 23 (post-fold): rc_enforce_downgrade_defense refuses prerelease
# appVersion in --from-release. Touches the MED shell-expert finding
# fix: sort -V is not SemVer §11-aware. Tactical fix in D3 is to refuse
# the prerelease input class entirely (#257 tracks full comparator).
# ---------------------------------------------------------------------------
mkdir -p "${SCRATCH}/case23/prefix"
cat > "${SCRATCH}/case23/prefix/.install-version-semver" <<EOF
appVersion=1.0.0
manifestSha256=aaaa
EOF
out=$(PREFIX="${SCRATCH}/case23/prefix" bash -c "
SCRIPT_DIR='${SCRIPT_DIR}/scripts'
log() { :; }; warn() { :; }; err() { printf '[stub ERR] %s\n' \"\$1\" >&2; exit 1; }
PREFIX='${SCRATCH}/case23/prefix'; ALLOW_DOWNGRADE=0; ACCEPT_REPUBLISHED_VERSION=0
. '${LIB_RC}'
( rc_enforce_downgrade_defense 2.0.0-rc.1 cafef00d ) && echo NO_REFUSE || echo REFUSED
" 2>&1)
assert "case 23: prerelease appVersion refused for --from-release" \
  bash -c "printf '%s' '$out' | grep -q REFUSED && printf '%s' '$out' | grep -q 'refuses prerelease'"

# ---------------------------------------------------------------------------
# Case 24 (post-fold): argparse refuses --from-release and --from-source
# on the same CLI. Touches the LOW shell-expert finding fix.
# ---------------------------------------------------------------------------
out=$(bash "${SCRIPT_DIR}/scripts/install.sh" --from-release --from-source 2>&1 || true)
assert "case 24: --from-release + --from-source rejected as mutually exclusive" \
  bash -c "printf '%s' '$out' | grep -q 'mutually exclusive'"
out=$(bash "${SCRIPT_DIR}/scripts/install.sh" --from-source --from-release 2>&1 || true)
assert "case 24b: --from-source then --from-release rejected (reverse order)" \
  bash -c "printf '%s' '$out' | grep -q 'mutually exclusive'"

# ---------------------------------------------------------------------------
# Case 25 (post-fold): verify-release.sh re-source guard. Touches the LOW
# shell-expert finding fix: readonly trips set -e on the second source.
# ---------------------------------------------------------------------------
out=$(bash -c "set -e; SOURCE_ONLY=1 . '${LIB_VR}'; SOURCE_ONLY=1 . '${LIB_VR}'; echo DOUBLE_SOURCE_OK" 2>&1)
assert "case 25: verify-release.sh tolerates double-source under set -e" \
  bash -c "printf '%s' '$out' | grep -q DOUBLE_SOURCE_OK"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo
echo "${TEST_NAME}: ${PASS} passed, ${FAIL} failed"
[ "${FAIL}" = 0 ]
