#!/usr/bin/env bash
# tests/backup/test-apictl-backup.sh
# Unit tests for the backup-init / backup / restore subcommands added to
# scripts/expertise-apictl (ADR-012). No database, no real cosign/age — the
# signing/encryption tools are PATH stubs, so these tests cover the
# bootstrap, preflight, and artifact-validation shell logic only. The .NET
# verbs' behavior is covered by tests/ExpertiseApi.Tests (unit + integration).
#
#  1. backup with no backup.env → error pointing at backup-init.
#  2. backup-init without --install-deps and missing tools → error listing them.
#  3. backup-init with stubbed tools → generates keys (chmod 600), recipients,
#     backup.env (chmod 600), and reports OK; re-run preserves existing keys.
#  4. restore refuses an artifact with unexpected members BEFORE any
#     signature/decryption work (tar allowlist).
#  5. restore without an artifact argument → usage error.
#  6. --help mentions the new subcommands.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
APICTL="${SCRIPT_DIR}/scripts/expertise-apictl"

PASS=0
FAIL=0

assert() {
  local name="$1"; shift
  if "$@"; then
    PASS=$((PASS+1))
  else
    printf 'FAIL: %s\n' "${name}" >&2
    FAIL=$((FAIL+1))
  fi
}

assert_contains() {
  local name="$1" haystack="$2" needle="$3"
  case "${haystack}" in
    *"${needle}"*) PASS=$((PASS+1)) ;;
    *) printf 'FAIL: %s (missing: %s)\n' "${name}" "${needle}" >&2; FAIL=$((FAIL+1)) ;;
  esac
}

# ---------------------------------------------------------------------------
# Sandbox: isolated HOME + config dir so no real operator state is touched.
# ---------------------------------------------------------------------------
SANDBOX="$(mktemp -d)"
trap 'rm -rf "${SANDBOX}"' EXIT
export HOME="${SANDBOX}/home"
export XDG_CONFIG_HOME="${SANDBOX}/config"
mkdir -p "${HOME}" "${XDG_CONFIG_HOME}"

STUB_DIR="${SANDBOX}/stubs"
mkdir -p "${STUB_DIR}"

write_stubs() {
  # ssh-keygen is real everywhere (OpenSSH) — only the age tools are stubbed.
  cat > "${STUB_DIR}/age-keygen" <<'EOF'
#!/usr/bin/env bash
out=""
while [[ $# -gt 0 ]]; do case "$1" in -o) out="$2"; shift 2 ;; *) shift ;; esac; done
printf '# created: stub\n# public key: age1stubstubstubstub\nAGE-SECRET-KEY-1STUB\n' > "${out}"
EOF
  cat > "${STUB_DIR}/age" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
  chmod +x "${STUB_DIR}/age-keygen" "${STUB_DIR}/age"
}

# ---------------------------------------------------------------------------
# 1. backup with no backup.env → actionable error naming backup-init.
#    (Stubs on PATH so the missing-tools check passes and preflight reaches
#    the config check.)
# ---------------------------------------------------------------------------
write_stubs
out="$(PATH="${STUB_DIR}:${PATH}" "${APICTL}" backup 2>&1)"
rc=$?
assert   "no-config exits nonzero" test "${rc}" -ne 0
assert_contains "no-config names backup-init" "${out}" "backup-init"

# ---------------------------------------------------------------------------
# 2. backup-init without --install-deps and a PATH missing age →
#    error listing the missing tools (never auto-installs without the flag).
# ---------------------------------------------------------------------------
out="$("${APICTL}" backup-init 2>&1)"
rc=$?
if command -v age >/dev/null 2>&1; then
  printf 'SKIP: missing-tools test — real age present on host PATH\n'
else
  assert   "backup-init missing tools exits nonzero" test "${rc}" -ne 0
  assert_contains "backup-init names --install-deps" "${out}" "--install-deps"
fi

# ---------------------------------------------------------------------------
# 3. backup-init with stubbed tools → keys, recipients, backup.env; re-run
#    preserves keys.
# ---------------------------------------------------------------------------
if ! command -v jq >/dev/null 2>&1; then
  printf 'SKIP: backup-init happy path — jq not on host PATH\n'
else
  out="$(PATH="${STUB_DIR}:${PATH}" "${APICTL}" backup-init 2>&1)"
  rc=$?
  cfg="${XDG_CONFIG_HOME}/expertise-api/backup"
  assert "backup-init exits zero" test "${rc}" -eq 0
  assert "signing key created" test -f "${cfg}/backup_signing_key"
  assert "allowed_signers created" grep -q '^expertise-backup ssh-ed25519 ' "${cfg}/allowed_signers"
  assert "age identity created" test -f "${cfg}/age-identity.txt"
  assert "recipients extracted" grep -q '^age1' "${cfg}/age-recipients.txt"
  assert "backup.env created" test -f "${cfg}/backup.env"
  # GNU stat first (-c), BSD fallback (-f). Order matters: BSD's -f flag also
  # EXISTS on GNU stat but means "filesystem status" — it exits 0 with garbage,
  # so probing -f first breaks on Linux (caught by CI on the first run).
  perms="$(stat -c '%a' "${cfg}/backup.env" 2>/dev/null || stat -f '%Lp' "${cfg}/backup.env")"
  assert "backup.env chmod 600" test "${perms}" = "600"

  cp "${cfg}/age-identity.txt" "${cfg}/age-identity.txt.orig"
  out="$(PATH="${STUB_DIR}:${PATH}" "${APICTL}" backup-init 2>&1)"
  assert "re-run exits zero" test $? -eq 0
  assert "re-run preserves identity" cmp -s "${cfg}/age-identity.txt" "${cfg}/age-identity.txt.orig"
  assert_contains "re-run reports preserved" "${out}" "preserved"
fi

# ---------------------------------------------------------------------------
# 4. restore rejects an artifact with unexpected members before any crypto.
# ---------------------------------------------------------------------------
if command -v jq >/dev/null 2>&1; then
  bad_dir="${SANDBOX}/bad-artifact"
  mkdir -p "${bad_dir}"
  : > "${bad_dir}/manifest.json"
  : > "${bad_dir}/manifest.json.sig"
  : > "${bad_dir}/payload.tar.gz.age"
  : > "${bad_dir}/evil.sh"
  tar -cf "${SANDBOX}/bad.tar" -C "${bad_dir}" manifest.json manifest.json.sig payload.tar.gz.age evil.sh
  out="$(PATH="${STUB_DIR}:${PATH}" "${APICTL}" restore "${SANDBOX}/bad.tar" 2>&1)"
  rc=$?
  assert   "bad artifact exits nonzero" test "${rc}" -ne 0
  assert_contains "bad artifact names the refusal" "${out}" "unexpected artifact contents"
else
  printf 'SKIP: artifact allowlist test — jq not on host PATH\n'
fi

# ---------------------------------------------------------------------------
# 4b. secrets-file resolution honors the install wrapper's recorded path
#     (custom-prefix installs put secrets.env outside the XDG default). The
#     resolved path surfaces in the "ConnectionStrings... not set" error, so
#     asserting on the message pins the resolution order without needing a DB.
# ---------------------------------------------------------------------------
if command -v jq >/dev/null 2>&1; then
  prefix="${SANDBOX}/install-prefix"
  mkdir -p "${prefix}/bin" "${XDG_CONFIG_HOME}/expertise-api"
  printf 'EXPERTISE_API_PREFIX=%s\n' "${prefix}" > "${XDG_CONFIG_HOME}/expertise-api/install.env"
  cat > "${prefix}/launch-expertise-api.sh" <<EOF
#!/usr/bin/env bash
SECRETS_FILE="${prefix}/custom-secrets.env"
EOF
  printf 'ConnectionStrings__DefaultConnection=""\n' > "${prefix}/custom-secrets.env"
  out="$(PATH="${STUB_DIR}:${PATH}" "${APICTL}" backup 2>&1)"
  rc=$?
  assert   "wrapper-secrets backup exits nonzero" test "${rc}" -ne 0
  assert_contains "error names the wrapper-recorded secrets path" "${out}" "${prefix}/custom-secrets.env"

  # Env override wins over the wrapper-recorded path.
  out="$(PATH="${STUB_DIR}:${PATH}" EXPERTISE_API_SECRETS_FILE="${SANDBOX}/override.env" "${APICTL}" backup 2>&1)"
  assert_contains "env override wins" "${out}" "${SANDBOX}/override.env"
else
  printf 'SKIP: secrets-resolution tests — jq not on host PATH\n'
fi

# ---------------------------------------------------------------------------
# 5. restore without an artifact → usage error.
# ---------------------------------------------------------------------------
out="$(PATH="${STUB_DIR}:${PATH}" "${APICTL}" restore 2>&1)"
rc=$?
assert   "restore without artifact exits nonzero" test "${rc}" -ne 0
assert_contains "restore usage names ARTIFACT" "${out}" "ARTIFACT"

# ---------------------------------------------------------------------------
# 6. --help mentions the new subcommands.
# ---------------------------------------------------------------------------
out="$("${APICTL}" --help 2>&1)"
assert_contains "help mentions backup-init" "${out}" "backup-init"
assert_contains "help mentions restore" "${out}" "restore ARTIFACT"

# ---------------------------------------------------------------------------
printf '\n%d passed, %d failed\n' "${PASS}" "${FAIL}"
[[ "${FAIL}" -eq 0 ]] || exit 1
