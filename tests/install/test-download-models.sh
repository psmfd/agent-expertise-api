#!/usr/bin/env bash
#
# tests/install/test-download-models.sh — regression tests for #456:
# download-models.sh must RE-DOWNLOAD an existing file whose checksum does not
# match the pinned SHA-256 (stale model after a version bump, or corruption)
# instead of hard-erroring, so install.sh's ensure_models can unconditionally
# delegate to it on upgrade installs. The post-download checksum verification
# must remain a hard gate.
#
# Covers:
#   1. stale oversized file → "re-downloading" path taken (not the old
#      "Delete the file and re-run" abort), and a bad downloaded payload still
#      aborts via the post-download checksum gate
#   2. missing file + failing network → clean download-failure abort
#   3. undersized file → existing "suspiciously small" re-download path intact
#
# Runs under bash 3.2 (macOS /bin/bash) and newer bash.
# Follows script-output conventions (OK/ERROR labels, PASS/FAIL summary).
#

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
TARGET="${SCRIPT_DIR}/scripts/download-models.sh"

[ -f "$TARGET" ] || { echo "ERROR [test-download-models] ${TARGET} not found" >&2; exit 2; }

PASS=0
FAIL=0
ok()   { PASS=$((PASS+1)); printf 'OK    %s\n' "$1"; }
fail() { FAIL=$((FAIL+1)); printf 'ERROR %s\n' "$1" >&2; }

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

# Stub curl on PATH. Behavior is selected via CURL_STUB_MODE:
#   fail    → exit 1 (network down)
#   garbage → write >=1MB of junk to the -o target, exit 0
STUB_BIN="${TMP_ROOT}/bin"
mkdir -p "$STUB_BIN"
cat > "${STUB_BIN}/curl" <<'EOF'
#!/usr/bin/env bash
out=""
prev=""
for a in "$@"; do
  [ "$prev" = "-o" ] && out="$a"
  prev="$a"
done
case "${CURL_STUB_MODE:-fail}" in
  garbage)
    [ -n "$out" ] || exit 1
    dd if=/dev/zero bs=1024 count=2048 2>/dev/null | tr '\0' 'x' > "$out"
    exit 0
    ;;
  *)
    exit 1
    ;;
esac
EOF
chmod +x "${STUB_BIN}/curl"

run_script() {
  # Args: <dest_dir> <curl_mode>. Prints combined output; returns script's exit.
  DEST_DIR="$1" CURL_STUB_MODE="$2" PATH="${STUB_BIN}:${PATH}" \
    bash "$TARGET" 2>&1
}

# --- 1. stale oversized file: re-download path + post-download hard gate ----
DEST1="${TMP_ROOT}/case1"
mkdir -p "$DEST1"
# >= 1 MiB so it passes the size check and reaches the checksum comparison,
# but with content that cannot match the pinned SHA-256.
dd if=/dev/zero of="${DEST1}/model.onnx" bs=1024 count=2048 2>/dev/null
printf 'stale' >> "${DEST1}/model.onnx"

out1="$(run_script "$DEST1" garbage)"
rc1=$?

if printf '%s' "$out1" | grep -q "stale or corrupt; re-downloading"; then
  ok "stale file takes the re-download path"
else
  fail "stale file: expected 're-downloading' in output, got: $out1"
fi

if printf '%s' "$out1" | grep -q "checksum mismatch" && [ "$rc1" -ne 0 ]; then
  ok "bad downloaded payload still aborts via post-download checksum gate (rc=$rc1)"
else
  fail "post-download gate: expected checksum-mismatch abort, rc=$rc1, output: $out1"
fi

# The stale file must have been REPLACED by the downloaded payload (all-x),
# proving the download actually ran rather than the old abort-in-place.
if grep -q 'xxxx' "${DEST1}/model.onnx" 2>/dev/null; then
  ok "stale file was replaced by the re-downloaded payload"
else
  fail "stale file content unchanged — re-download never wrote the destination"
fi

# --- 2. missing file + network failure --------------------------------------
DEST2="${TMP_ROOT}/case2"
mkdir -p "$DEST2"
out2="$(run_script "$DEST2" fail)"
rc2=$?
if [ "$rc2" -ne 0 ] && printf '%s' "$out2" | grep -q "Failed to download"; then
  ok "missing file with failing network aborts cleanly (rc=$rc2)"
else
  fail "missing-file case: expected download-failure abort, rc=$rc2, output: $out2"
fi

# --- 3. undersized existing file keeps the re-download path -----------------
DEST3="${TMP_ROOT}/case3"
mkdir -p "$DEST3"
printf 'tiny' > "${DEST3}/model.onnx"
out3="$(run_script "$DEST3" fail)"
rc3=$?
if printf '%s' "$out3" | grep -q "suspiciously small" && [ "$rc3" -ne 0 ]; then
  ok "undersized file still routes to re-download ('suspiciously small')"
else
  fail "undersized case: expected 'suspiciously small' path, rc=$rc3, output: $out3"
fi

# --- summary ----------------------------------------------------------------
echo "=================================="
if [ "$FAIL" -eq 0 ]; then
  echo "PASS — 0 errors ($PASS checks)"
  exit 0
else
  echo "FAIL — $FAIL errors, $PASS passed"
  exit 1
fi
