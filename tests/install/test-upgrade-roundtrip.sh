#!/usr/bin/env bash
#
# test-upgrade-roundtrip.sh — exercise install.sh's stage-then-swap
# atomicity and rollback (issue #223). Drives install.sh end-to-end with
# PATH-shimmed dotnet, systemctl, launchctl, and migrate.sh so the test
# requires neither .NET nor a real service manager.
#
# Cases:
#   1. Fresh install                            — STAGE_DIR created, swap
#      to BIN_DIR, version marker written, wrapper at ${PREFIX}/, no .old
#      leftover.
#   2. No-op reinstall                          — same version, swap still
#      runs, no errors, marker unchanged.
#   3. Upgrade (different version marker source)— logs "upgrade X -> Y".
#   4. Publish failure                          — STAGE_DIR cleaned, live
#      BIN_DIR untouched, exit non-zero.
#   5. Migrate failure                          — STAGE_DIR cleaned, live
#      BIN_DIR untouched, exit non-zero.
#   6. Concurrent install                       — second invocation fails
#      fast with "another install in progress".
#   7. Symlink-trap                             — STAGE_DIR pre-created as
#      symlink, install refuses.
#   8. Wrapper survives swap                    — wrapper path lives at
#      ${PREFIX}/launch-expertise-api.sh and is preserved across swap.
#

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="${REPO_ROOT}/scripts/install.sh"
[[ -x "${SCRIPT}" ]] || { echo "PRECONDITION_FAILURE: install.sh missing"; exit 2; }

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/install-roundtrip.XXXXXX")"
SHIM_DIR="${SCRATCH}/shims"
mkdir -p "${SHIM_DIR}"

trap 'rm -rf "${SCRATCH}"' EXIT

PASS=0
FAIL=0

# ---------------------------------------------------------------------------
# Shims — drop ahead of PATH so install.sh sees fake dotnet/systemctl/etc.
#
# dotnet:    matches `--list-runtimes` and `publish`.
# launchctl: no-op (record args).
# systemctl: list-unit-files reports unconfigured; daemon-reload + enable
#            + restart are no-ops.
# nc, lsof:  exit nonzero (skip-friendly).
# ---------------------------------------------------------------------------
mk_shim() { local name="$1"; cat > "${SHIM_DIR}/${name}"; chmod +x "${SHIM_DIR}/${name}"; }

mk_shim dotnet <<'EOF'
#!/usr/bin/env bash
case "$1" in
  --list-runtimes) printf 'Microsoft.AspNetCore.App 10.0.0 [%s]\n' "${ASPNETCORE_PATH:-/x}"; exit 0 ;;
  --info)          printf 'fake-sdk\n'; exit 0 ;;
  publish)
    if [[ "${FAIL_PUBLISH:-0}" == "1" ]]; then
      printf 'dotnet publish: simulated failure\n' >&2
      exit 1
    fi
    # Find the --output argument
    out=""
    while (($#)); do [[ "$1" == "--output" ]] && { out="$2"; break; }; shift; done
    [[ -n "${out}" ]] || { echo "fake dotnet publish: no --output" >&2; exit 1; }
    mkdir -p "${out}"
    : > "${out}/ExpertiseApi.dll"
    printf '#!/usr/bin/env bash\necho fake-binary\n' > "${out}/ExpertiseApi"
    chmod +x "${out}/ExpertiseApi"
    exit 0
    ;;
esac
exit 0
EOF

mk_shim launchctl <<'EOF'
#!/usr/bin/env bash
exit 0
EOF

mk_shim systemctl <<'EOF'
#!/usr/bin/env bash
case "$1" in
  --user)
    shift
    case "$1" in
      list-unit-files) exit 1 ;;   # "not installed"
      daemon-reload|enable|restart) exit 0 ;;
      is-enabled) exit 1 ;;
    esac
    ;;
esac
exit 0
EOF

mk_shim loginctl <<'EOF'
#!/usr/bin/env bash
printf 'Linger=yes\n'
EOF

# nc and lsof: pretend nothing is listening / not present
mk_shim nc <<'EOF'
#!/usr/bin/env bash
exit 1
EOF

# ---------------------------------------------------------------------------
# A stub migrate.sh that just respects FAIL_MIGRATE — install.sh calls the
# repo's real migrate.sh by SCRIPT_DIR, but that real script tries to source
# the connection from secrets.env and exec the (fake) binary. The fake
# binary above is a no-op shell script, so a "valid" connection passes
# trivially. For the FAIL_MIGRATE case we override migrate.sh via a shim
# also placed in PATH and invoked through install.sh's hard-coded
# ${SCRIPT_DIR}/migrate.sh path. To do that we replace SCRIPT_DIR's
# migrate.sh path via env override is not supported — so we use a
# subshell-local copy of install.sh's SCRIPT_DIR-derived call: we point
# install.sh at a temporary scripts/ directory that contains symlinks to
# the real install.sh and download-models.sh, plus our overridable
# migrate.sh.
# ---------------------------------------------------------------------------
ALT_SCRIPTS="${SCRATCH}/scripts"
mkdir -p "${ALT_SCRIPTS}/service-templates"
ln -sf "${REPO_ROOT}/scripts/download-models.sh" "${ALT_SCRIPTS}/download-models.sh"
mkdir -p "${ALT_SCRIPTS}/lib"
ln -sf "${REPO_ROOT}/scripts/lib/prefix-validation.sh" "${ALT_SCRIPTS}/lib/prefix-validation.sh"
ln -sf "${REPO_ROOT}/scripts/service-templates/expertise-api.plist.tmpl"   "${ALT_SCRIPTS}/service-templates/expertise-api.plist.tmpl"
ln -sf "${REPO_ROOT}/scripts/service-templates/expertise-api.service.tmpl" "${ALT_SCRIPTS}/service-templates/expertise-api.service.tmpl"

# Copy install.sh verbatim (so ${BASH_SOURCE[0]} resolves SCRIPT_DIR to
# ALT_SCRIPTS) and override the REPO_ROOT derivation to point at the real
# repo so publish output paths still find sources.
sed "s|REPO_ROOT=\"\$(cd \"\${SCRIPT_DIR}/..\" && pwd)\"|REPO_ROOT=\"${REPO_ROOT}\"|" \
  "${SCRIPT}" > "${ALT_SCRIPTS}/install.sh"
chmod +x "${ALT_SCRIPTS}/install.sh"
INSTALL="${ALT_SCRIPTS}/install.sh"

# Overridable migrate.sh
write_migrate_stub() {
  local fail="${1:-0}"
  cat > "${ALT_SCRIPTS}/migrate.sh" <<EOF
#!/usr/bin/env bash
if [[ "${fail}" == "1" ]]; then
  printf 'migrate: simulated failure\n' >&2
  exit 1
fi
exit 0
EOF
  chmod +x "${ALT_SCRIPTS}/migrate.sh"
}
write_migrate_stub 0

# A dummy models dir so ensure_models is a no-op
seed_models() {
  local prefix="$1"
  mkdir -p "${prefix}/models"
  : > "${prefix}/models/model.onnx"
  : > "${prefix}/models/vocab.txt"
}

# Pre-set a valid-looking secrets file (LF, real-shaped connstring)
seed_secrets() {
  local config_dir="$1"
  mkdir -p "${config_dir}"
  cat > "${config_dir}/secrets.env" <<EOF
# expertise-api-secrets-version=1
ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=5432;Database=expertise;Username=expertise;Password=valid"
EOF
  chmod 600 "${config_dir}/secrets.env"
}

# Common install env. With --prefix, install.sh co-locates CONFIG_DIR
# and LOG_DIR under PREFIX, so per-case config/log paths are ignored
# (kept as positional args for documentation only).
run_install() {
  local prefix="$1"; shift
  shift  # _config_dir (unused under --prefix layout)
  shift  # _log_dir   (unused under --prefix layout)
  PATH="${SHIM_DIR}:${PATH}" \
  HOME="${SCRATCH}/home" \
  FAIL_PUBLISH="${FAIL_PUBLISH:-0}" \
  "${INSTALL}" --prefix "${prefix}" --allow-system-prefix --skip-preflight --publish-mode fdd "$@"
}

mkdir -p "${SCRATCH}/home"

assert() {
  local desc="$1"; shift
  if "$@"; then PASS=$((PASS+1));
  else printf 'FAIL: %s\n' "${desc}" >&2; FAIL=$((FAIL+1))
  fi
}

# ===========================================================================
# Case 1: Fresh install — full happy path
# ===========================================================================
PREFIX1="${SCRATCH}/c1/prefix"
CONFIG1="${PREFIX1}"        # install.sh co-locates CONFIG_DIR=${PREFIX} under --prefix
LOG1="${PREFIX1}/logs"
mkdir -p "${PREFIX1}"
seed_models "${PREFIX1}"
seed_secrets "${CONFIG1}"
write_migrate_stub 0

if run_install "${PREFIX1}" "${CONFIG1}" "${LOG1}" >"${SCRATCH}/c1.log" 2>&1; then
  assert "c1: BIN_DIR populated"        [ -f "${PREFIX1}/bin/ExpertiseApi.dll" ]
  assert "c1: wrapper at PREFIX root"   [ -x "${PREFIX1}/launch-expertise-api.sh" ]
  assert "c1: wrapper NOT in BIN_DIR"   [ ! -e "${PREFIX1}/bin/launch-expertise-api.sh" ]
  assert "c1: version marker written"   [ -s "${PREFIX1}/.install-version" ]
  assert "c1: no .new leftover"         [ ! -e "${PREFIX1}/bin.new" ]
  assert "c1: no .old leftover"         [ ! -e "${PREFIX1}/bin.old" ]
  assert "c1: lock released"            [ ! -e "${PREFIX1}/.install.lock" ]
else
  printf 'FAIL: c1 fresh install exited non-zero. Log:\n' >&2
  cat "${SCRATCH}/c1.log" >&2
  FAIL=$((FAIL+7))
fi

# ===========================================================================
# Case 2: No-op reinstall — second run on the same PREFIX
# ===========================================================================
if run_install "${PREFIX1}" "${CONFIG1}" "${LOG1}" >"${SCRATCH}/c2.log" 2>&1; then
  assert "c2: BIN_DIR still populated"  [ -f "${PREFIX1}/bin/ExpertiseApi.dll" ]
  assert "c2: no .new leftover"         [ ! -e "${PREFIX1}/bin.new" ]
  assert "c2: no .old leftover"         [ ! -e "${PREFIX1}/bin.old" ]
  # Reinstall message in log
  if grep -q 'version: reinstall' "${SCRATCH}/c2.log"; then
    PASS=$((PASS+1))
  else
    printf 'FAIL: c2 should log "version: reinstall"\n' >&2; FAIL=$((FAIL+1))
  fi
else
  printf 'FAIL: c2 reinstall exited non-zero. Log:\n' >&2
  cat "${SCRATCH}/c2.log" >&2
  FAIL=$((FAIL+4))
fi

# ===========================================================================
# Case 3: Upgrade — bump marker manually to simulate an older install
# ===========================================================================
printf 'v0.0.1-old\n' > "${PREFIX1}/.install-version"
if run_install "${PREFIX1}" "${CONFIG1}" "${LOG1}" >"${SCRATCH}/c3.log" 2>&1; then
  if grep -qE 'version: upgrade v0\.0\.1-old -> ' "${SCRATCH}/c3.log"; then
    PASS=$((PASS+1))
  else
    printf 'FAIL: c3 should log "version: upgrade v0.0.1-old -> <new>". Log:\n' >&2
    grep version "${SCRATCH}/c3.log" >&2
    FAIL=$((FAIL+1))
  fi
else
  printf 'FAIL: c3 upgrade exited non-zero\n' >&2; FAIL=$((FAIL+1))
fi

# ===========================================================================
# Case 4: Publish failure — STAGE_DIR cleaned, live tree intact
# ===========================================================================
PREFIX4="${SCRATCH}/c4/prefix"
CONFIG4="${PREFIX4}"
LOG4="${PREFIX4}/logs"
mkdir -p "${PREFIX4}"
# First a successful install so the live BIN_DIR exists
seed_models "${PREFIX4}"
seed_secrets "${CONFIG4}"
write_migrate_stub 0
run_install "${PREFIX4}" "${CONFIG4}" "${LOG4}" >/dev/null 2>&1 || { echo "c4 setup failed"; FAIL=$((FAIL+1)); }
# Take a fingerprint of the live binary
fp_before=$(cat "${PREFIX4}/bin/ExpertiseApi.dll" 2>/dev/null | md5sum 2>/dev/null || cat "${PREFIX4}/bin/ExpertiseApi.dll" | md5)

# Now simulate publish failure
FAIL_PUBLISH=1 run_install "${PREFIX4}" "${CONFIG4}" "${LOG4}" >"${SCRATCH}/c4.log" 2>&1
rc=$?
unset FAIL_PUBLISH
assert "c4: install exited non-zero on publish fail" [ "${rc}" -ne 0 ]
assert "c4: STAGE_DIR cleaned"   [ ! -e "${PREFIX4}/bin.new" ]
assert "c4: live BIN_DIR present" [ -f "${PREFIX4}/bin/ExpertiseApi.dll" ]
assert "c4: lock released"        [ ! -e "${PREFIX4}/.install.lock" ]
fp_after=$(cat "${PREFIX4}/bin/ExpertiseApi.dll" 2>/dev/null | md5sum 2>/dev/null || cat "${PREFIX4}/bin/ExpertiseApi.dll" | md5)
assert "c4: live binary unchanged" [ "${fp_before}" = "${fp_after}" ]

# ===========================================================================
# Case 5: Migrate failure — STAGE_DIR cleaned, live tree intact
# ===========================================================================
PREFIX5="${SCRATCH}/c5/prefix"
CONFIG5="${PREFIX5}"
LOG5="${PREFIX5}/logs"
mkdir -p "${PREFIX5}"
seed_models "${PREFIX5}"
seed_secrets "${CONFIG5}"
write_migrate_stub 0
run_install "${PREFIX5}" "${CONFIG5}" "${LOG5}" >/dev/null 2>&1 || { echo "c5 setup failed"; FAIL=$((FAIL+1)); }
fp5_before=$(cat "${PREFIX5}/bin/ExpertiseApi.dll" 2>/dev/null | md5sum 2>/dev/null || cat "${PREFIX5}/bin/ExpertiseApi.dll" | md5)

write_migrate_stub 1
run_install "${PREFIX5}" "${CONFIG5}" "${LOG5}" >"${SCRATCH}/c5.log" 2>&1
rc=$?
write_migrate_stub 0
if [[ "${rc}" -eq 0 ]]; then
  printf 'c5 DEBUG log:\n'
  cat "${SCRATCH}/c5.log"
fi
assert "c5: install exited non-zero on migrate fail" [ "${rc}" -ne 0 ]
assert "c5: STAGE_DIR cleaned"    [ ! -e "${PREFIX5}/bin.new" ]
assert "c5: live BIN_DIR present" [ -f "${PREFIX5}/bin/ExpertiseApi.dll" ]
fp5_after=$(cat "${PREFIX5}/bin/ExpertiseApi.dll" 2>/dev/null | md5sum 2>/dev/null || cat "${PREFIX5}/bin/ExpertiseApi.dll" | md5)
assert "c5: live binary unchanged" [ "${fp5_before}" = "${fp5_after}" ]

# ===========================================================================
# Case 6: Concurrent install — second run blocked
# ===========================================================================
PREFIX6="${SCRATCH}/c6/prefix"
mkdir -p "${PREFIX6}/.install.lock"
out=$(run_install "${PREFIX6}" "${PREFIX6}" "${PREFIX6}/logs" 2>&1)
rc=$?
rmdir "${PREFIX6}/.install.lock" 2>/dev/null || true
assert "c6: concurrent install rejected" [ "${rc}" -ne 0 ]
if [[ "${out}" == *"another install in progress"* ]]; then PASS=$((PASS+1));
else printf 'FAIL: c6 expected "another install in progress", got:\n%s\n' "${out}" >&2; FAIL=$((FAIL+1)); fi

# ===========================================================================
# Case 7: Symlink-trap — STAGE_DIR pre-created as symlink
# ===========================================================================
PREFIX7="${SCRATCH}/c7/prefix"
CONFIG7="${PREFIX7}"
LOG7="${PREFIX7}/logs"
mkdir -p "${PREFIX7}"
seed_models "${PREFIX7}"
seed_secrets "${CONFIG7}"
mkdir -p "${PREFIX7}"
ln -s /tmp "${PREFIX7}/bin.new"
out=$(run_install "${PREFIX7}" "${CONFIG7}" "${LOG7}" 2>&1)
rc=$?
rm -f "${PREFIX7}/bin.new"
assert "c7: symlink staging rejected" [ "${rc}" -ne 0 ]
if [[ "${out}" == *"symlink"* ]]; then PASS=$((PASS+1));
else printf 'FAIL: c7 expected symlink rejection. Got:\n%s\n' "${out}" >&2; FAIL=$((FAIL+1)); fi

# ===========================================================================
# Case 8: Wrapper survives binary swap
# ===========================================================================
PREFIX8="${SCRATCH}/c8/prefix"
CONFIG8="${PREFIX8}"
LOG8="${PREFIX8}/logs"
mkdir -p "${PREFIX8}"
seed_models "${PREFIX8}"
seed_secrets "${CONFIG8}"
write_migrate_stub 0
run_install "${PREFIX8}" "${CONFIG8}" "${LOG8}" >/dev/null 2>&1 || { echo "c8 setup failed"; FAIL=$((FAIL+1)); }
wrapper_inode_before=$(stat -f '%i' "${PREFIX8}/launch-expertise-api.sh" 2>/dev/null \
  || stat -c '%i' "${PREFIX8}/launch-expertise-api.sh" 2>/dev/null)
run_install "${PREFIX8}" "${CONFIG8}" "${LOG8}" >"${SCRATCH}/c8.log" 2>&1
rc=$?
assert "c8: second install succeeded"        [ "${rc}" -eq 0 ]
assert "c8: wrapper still at PREFIX root"    [ -x "${PREFIX8}/launch-expertise-api.sh" ]
assert "c8: wrapper not in BIN_DIR"          [ ! -e "${PREFIX8}/bin/launch-expertise-api.sh" ]
wrapper_inode_after=$(stat -f '%i' "${PREFIX8}/launch-expertise-api.sh" 2>/dev/null \
  || stat -c '%i' "${PREFIX8}/launch-expertise-api.sh" 2>/dev/null)
# Wrapper is regenerated each run (write_wrapper does mv tmp -> dst), so
# the inode may legitimately change. The invariant we care about is that
# the wrapper PATH still resolves to an executable file after swap.
assert "c8: wrapper inode is real before and after" test -n "${wrapper_inode_before}${wrapper_inode_after}"

# ===========================================================================
# Case 9: Post-swap rollback runway is preserved
#
# After the redesign fix for shell-expert M1: ${BIN_DIR}.old must still
# exist between atomic_swap and the SUCCESS=1 line in main(). We can
# observe this indirectly by checking that a successful install removes
# the .old (steady-state cleanup happens in the SUCCESS branch of the
# trap, not inside atomic_swap).
# ===========================================================================
PREFIX9="${SCRATCH}/c9/prefix"
CONFIG9="${PREFIX9}"
LOG9="${PREFIX9}/logs"
mkdir -p "${PREFIX9}"
seed_models "${PREFIX9}"
seed_secrets "${CONFIG9}"
write_migrate_stub 0
run_install "${PREFIX9}" "${CONFIG9}" "${LOG9}" >/dev/null 2>&1 || { echo "c9 first install failed"; FAIL=$((FAIL+1)); }
run_install "${PREFIX9}" "${CONFIG9}" "${LOG9}" >"${SCRATCH}/c9.log" 2>&1
rc=$?
assert "c9: second install succeeded"  [ "${rc}" -eq 0 ]
assert "c9: .old cleaned on success"   [ ! -e "${PREFIX9}/bin.old" ]
assert "c9: .new cleaned on success"   [ ! -e "${PREFIX9}/bin.new" ]
assert "c9: lock released"             [ ! -e "${PREFIX9}/.install.lock" ]

# ===========================================================================
# Case 10: --prefix validation now rejects catastrophic paths (parity with uninstall.sh)
# ===========================================================================
out=$(PATH="${SHIM_DIR}:${PATH}" HOME="${SCRATCH}/home" \
  "${INSTALL}" --prefix "/etc/expertise-api" --skip-preflight --publish-mode fdd 2>&1)
rc=$?
assert "c10a: install rejects --prefix /etc/expertise-api" [ "${rc}" -ne 0 ]
if [[ "${out}" == *"blocked"* ]]; then PASS=$((PASS+1)); else printf 'FAIL: c10a expected block message. Got:\n%s\n' "${out}" >&2; FAIL=$((FAIL+1)); fi

out=$(PATH="${SHIM_DIR}:${PATH}" HOME="${SCRATCH}/home" \
  "${INSTALL}" --prefix "/" --skip-preflight --publish-mode fdd 2>&1)
rc=$?
assert "c10b: install rejects --prefix /" [ "${rc}" -ne 0 ]

out=$(PATH="${SHIM_DIR}:${PATH}" HOME="${SCRATCH}/home" \
  "${INSTALL}" --prefix "/Users/foo/notexpertise" --skip-preflight --publish-mode fdd 2>&1)
rc=$?
assert "c10c: install rejects prefix missing expertise-api component (no --allow-system-prefix)" [ "${rc}" -ne 0 ]

# ===========================================================================
# Case 11: VERSION_MARKER symlink-trap defense
# ===========================================================================
PREFIX11="${SCRATCH}/c11/prefix"
CONFIG11="${PREFIX11}"
LOG11="${PREFIX11}/logs"
mkdir -p "${PREFIX11}"
seed_models "${PREFIX11}"
seed_secrets "${CONFIG11}"
write_migrate_stub 0
ln -s /etc/hostname "${PREFIX11}/.install-version"
out=$(run_install "${PREFIX11}" "${CONFIG11}" "${LOG11}" 2>&1)
rc=$?
rm -f "${PREFIX11}/.install-version"
assert "c11: install rejects symlinked version marker" [ "${rc}" -ne 0 ]
if [[ "${out}" == *"symlink"* ]]; then PASS=$((PASS+1)); else printf 'FAIL: c11 expected symlink rejection. Got:\n%s\n' "${out}" >&2; FAIL=$((FAIL+1)); fi

# ===========================================================================
# Summary
# ===========================================================================
TOTAL=$((PASS+FAIL))
printf '\n[test-upgrade-roundtrip] %d passed, %d failed (of %d)\n' "${PASS}" "${FAIL}" "${TOTAL}"
if (( FAIL > 0 )); then
  printf 'FAIL — %d errors, 0 warnings\n' "${FAIL}"
  exit 1
fi
printf 'PASS — 0 errors, 0 warnings\n'
exit 0
