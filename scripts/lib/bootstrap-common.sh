#!/usr/bin/env bash
# ===========================================================================
# scripts/lib/bootstrap-common.sh
#
# Shared bootstrap library for `scripts/install.sh --install-deps` per-OS
# modules. Function definitions only; no top-level work. Sourced by
# scripts/install.sh after arg parsing, before preflight().
#
# Public contract consumed by per-OS modules (bootstrap-macos.sh,
# bootstrap-debian.sh, bootstrap-rhel.sh):
#
#   bc_require_install_deps_flag    # err unless caller-set INSTALL_DEPS=1
#   bc_refuse_xtrace                # err if `set -x` is active (password-leak guard)
#   bc_refuse_root                  # err if EUID=0 (per-user secrets / brew incompat)
#   bc_sudo_probe                   # populate _BC_SUDO_OK; never prompts
#   bc_sudo_ensure                  # err if sudo isn't usable; single `sudo -v` prompt
#   bc_generate_db_password         # echo a 192-bit base64 password (NO trailing newline)
#   bc_write_connection_string_if_absent HOST PORT DB USER PASSWORD
#                                   # idempotent secrets.env injection;
#                                   # never overwrites an existing line
#   bc_append_install_deps_history TOKEN_CSV_TAKEN TOKEN_CSV_SKIPPED
#                                   # one line per --install-deps run
#
# Required caller-defined symbols:
#   log()                           # stdout helper from install.sh
#   warn()                          # stderr warning helper
#   err()                           # stderr + exit 1 helper
#   PREFIX                          # install prefix
#   CONFIG_DIR                      # secrets-file directory
#   SECRETS_FILE                    # absolute path to secrets.env
#   INSTALL_DEPS                    # 0|1
#   UPGRADE_DEPS                    # 0|1
#
# Per-design-review (shell-expert + security-review-expert HIGH):
#  - Password never enters argv (no `psql -c "ALTER ROLE ... PASSWORD 'literal'"`).
#  - Password never enters logs (path-only logging; never echo the value).
#  - Generation guarded against `set -x` xtrace leak.
#  - secrets.env updates are atomic (mktemp same-fs + rename); mode 600 preserved.
#  - Re-runs never rotate the password (detect-then-skip).
#  - Audit trail appended to `${PREFIX}/.install-deps-history`.
# ===========================================================================

# Sourced-library sentinel. If executed directly, fail loudly rather than
# silently exit (the sourcer's `set -e` would otherwise mask intent).
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  printf 'bootstrap-common.sh is a library; source it from install.sh\n' >&2
  exit 64
fi

# ---------------------------------------------------------------------------
# Flag-interaction guards
# ---------------------------------------------------------------------------

bc_require_install_deps_flag() {
  if (( ${UPGRADE_DEPS:-0} == 1 && ${INSTALL_DEPS:-0} == 0 )); then
    err "--upgrade-deps requires --install-deps (refusing silent no-op; pass both flags or neither)"
  fi
}

bc_refuse_xtrace() {
  # `set -x` leaks the generated password line to stderr. Refuse rather
  # than try to set +x mid-flight, because a parent shell may re-enable
  # it. Document the constraint and exit.
  case $- in
    *x*) err "refuse to bootstrap with xtrace (set -x) enabled — it would leak the generated DB password" ;;
  esac
}

bc_refuse_root() {
  if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "run --install-deps as your normal user (Homebrew refuses root; per-user secrets ownership matters under sudo)"
  fi
}

# ---------------------------------------------------------------------------
# sudo discipline (single-prompt; cached-creds aware)
# ---------------------------------------------------------------------------

# Caller-visible flag: 1 if `sudo` is available without a prompt right now.
# Set by bc_sudo_probe; consumed by per-OS modules to decide whether to
# emit the one-line summary of upcoming privileged actions.
_BC_SUDO_OK=0

bc_sudo_probe() {
  if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    _BC_SUDO_OK=1
  else
    _BC_SUDO_OK=0
  fi
}

bc_sudo_ensure() {
  # Idempotent: returns 0 silently if sudo already cached, otherwise
  # prompts once.
  if (( _BC_SUDO_OK == 1 )); then return 0; fi
  command -v sudo >/dev/null 2>&1 \
    || err "sudo not found; --install-deps requires sudo for system package installation"
  log "sudo authentication required for: $1"
  sudo -v || err "sudo authentication failed; aborting --install-deps"
  _BC_SUDO_OK=1
}

# ---------------------------------------------------------------------------
# Password generation
#
# Per shell-expert HIGH + security-review HIGH: never let the password
# appear in stdout/stderr/logs. Generation block is run inside a
# `( set +x; ...; )` subshell with stderr-of-subshell muted (xtrace
# emits to stderr).
#
# Primary path: /dev/urandom + base64 (POSIX-portable; no dependency on
# openssl, which is absent from minimal containers and bears its own
# CVE history).
# Fallback:    openssl rand -base64 (when /dev/urandom is unreadable,
#              which is effectively never on supported platforms).
# Refusal:     anything else. We do NOT fall back to $RANDOM, date, etc.
# ---------------------------------------------------------------------------

bc_generate_db_password() {
  bc_refuse_xtrace
  local pw=""
  if [[ -r /dev/urandom ]]; then
    pw=$( { head -c 24 /dev/urandom | base64 | tr -d '\n='; } 2>/dev/null )
  fi
  if [[ -z "${pw}" ]] && command -v openssl >/dev/null 2>&1; then
    pw=$( { openssl rand -base64 24 | tr -d '\n='; } 2>/dev/null )
  fi
  [[ -n "${pw}" ]] \
    || err "failed to generate DB password (neither /dev/urandom nor openssl was usable)"
  # No newline. Caller is responsible for writing the value into a
  # file via secured-temp + rename; never echo this elsewhere.
  printf '%s' "${pw}"
}

# ---------------------------------------------------------------------------
# secrets.env injection (idempotent; never-overwrite)
#
# Contract:
#   - If SECRETS_FILE already has a ConnectionStrings__DefaultConnection
#     line (any value), DO NOT touch it. Caller is responsible for
#     skipping role creation in that case so we do not generate a
#     password that disagrees with the existing line.
#   - If absent, generate a password, write the line under umask 077,
#     preserve mode 600 + original owner via atomic rename.
#   - Single-quote the value (base64 alphabet has no `'`; survives
#     future generator changes that introduce metachars).
# ---------------------------------------------------------------------------

# Returns 0 (with log) if injection happened, 1 if the line already
# exists. The password is NOT echoed; the caller never sees it.
bc_write_connection_string_if_absent() {
  local host="$1" port="$2" db="$3" user="$4" pw="$5"
  [[ -n "${SECRETS_FILE:-}" ]] || err "SECRETS_FILE unset; bootstrap-common bug"
  if [[ -L "${SECRETS_FILE}" ]]; then
    err "${SECRETS_FILE} is a symlink — refusing to write (potential write redirection)"
  fi
  if [[ -f "${SECRETS_FILE}" ]] \
     && grep -q '^ConnectionStrings__DefaultConnection=' "${SECRETS_FILE}"; then
    log "secrets: ConnectionStrings__DefaultConnection already set — leaving untouched"
    return 1
  fi
  # Same-filesystem temp file so the rename is atomic.
  local dir tmp
  dir="$(dirname -- "${SECRETS_FILE}")"
  [[ -d "${dir}" ]] || err "secrets dir ${dir} does not exist; ensure_config_stubs should have created it"
  tmp="$(umask 077 && mktemp "${dir}/.secrets.env.XXXXXX")" \
    || err "failed to create temp file in ${dir}"
  if [[ -f "${SECRETS_FILE}" ]]; then
    cat -- "${SECRETS_FILE}" > "${tmp}" \
      || { rm -f -- "${tmp}"; err "failed to read existing ${SECRETS_FILE}"; }
  fi
  # Single-quoted value. The password is never interpolated through a
  # log/echo path; this printf is the only place it transits.
  printf "ConnectionStrings__DefaultConnection='Host=%s;Port=%s;Database=%s;Username=%s;Password=%s'\n" \
    "${host}" "${port}" "${db}" "${user}" "${pw}" >> "${tmp}" \
    || { rm -f -- "${tmp}"; err "failed to append connection string to temp file"; }
  chmod 600 "${tmp}"
  # Preserve original owner under sudo. Best-effort.
  if [[ -f "${SECRETS_FILE}" ]]; then
    local owner=""
    owner=$(stat -f '%u:%g' "${SECRETS_FILE}" 2>/dev/null \
            || stat -c '%u:%g' "${SECRETS_FILE}" 2>/dev/null \
            || true)
    if [[ -n "${owner}" ]]; then
      chown "${owner}" "${tmp}" 2>/dev/null || true
    fi
  fi
  mv -f -- "${tmp}" "${SECRETS_FILE}" \
    || { rm -f -- "${tmp}"; err "failed to rename temp into place at ${SECRETS_FILE} (temp removed)"; }
  log "secrets: wrote ConnectionStrings__DefaultConnection (value not logged)"
  return 0
}

# ---------------------------------------------------------------------------
# Audit trail
#
# Format: `RFC3339 | VERSION | OS | taken=<csv> | skipped=<csv>`
# Operators need this to answer "what did --install-deps do last Tuesday?"
# ---------------------------------------------------------------------------

bc_append_install_deps_history() {
  local taken="$1" skipped="$2"
  local history="${PREFIX}/.install-deps-history"
  if [[ -L "${history}" ]]; then
    warn "${history} is a symlink — refusing to append audit line"
    return 0
  fi
  local ts
  ts=$(date -u +'%Y-%m-%dT%H:%M:%SZ')
  printf '%s | %s | %s | taken=%s | skipped=%s\n' \
    "${ts}" "${NEW_VERSION:-unknown}" "${OS:-unknown}" "${taken:-none}" "${skipped:-none}" \
    >> "${history}" || warn "failed to append to ${history} (non-fatal)"
  # 600, not 644: install timing + version data is information disclosure
  # on shared hosts. Matches the per-user-secret posture of the surrounding
  # install (security-review LOW C).
  chmod 600 "${history}" 2>/dev/null || true
}
