#!/usr/bin/env bash
#
# uninstall.sh — remove agent-expertise-api native OS service install.
#
# Dry-run by default. Use --yes to apply, --purge to also remove user data
# (logs, models, secrets). Postgres data is NEVER touched (out of scope).
#
# Usage:
#   scripts/uninstall.sh [--yes] [--purge] [--prefix DIR]
#                        [--system] [--allow-system-prefix]
#                        [--dry-run] [--help]
#
# Flags:
#   --yes                  Apply changes (default is implicit dry-run print).
#   --purge                Also remove models, logs, config (requires --yes).
#   --prefix DIR           Override install root. Validated against a blocked-
#                          roots prefix list and required to contain an
#                          'expertise-api' path component (unless
#                          --allow-system-prefix is also passed).
#   --system               Use system-scope path layout (Linux only).
#   --allow-system-prefix  Skip the 'expertise-api must be a path component'
#                          check. Does NOT permit deleting blocked system
#                          roots (/, /etc, /opt, ...) — those are always
#                          rejected.
#   --dry-run              Force dry-run even with --yes; print the actions
#                          that would run but execute none of them. Useful
#                          for verification and as the primary safety hook
#                          for the test harness under tests/uninstall/.
#   --help, -h             Show this help and exit 0.
#

set -euo pipefail

log()  { printf '[uninstall] %s\n' "$1"; }
warn() { printf '[uninstall] WARN: %s\n' "$1" >&2; }
err()  { printf '[uninstall] ERROR: %s\n' "$1" >&2; exit 1; }

APPLY=0
PURGE=0
INSTALL_SCOPE="user"
PREFIX_OVERRIDE=""
ALLOW_SYSTEM_PREFIX=0
DRY_RUN=0

usage() { sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes)                  APPLY=1; shift ;;
    --purge)                PURGE=1; shift ;;
    --prefix)               PREFIX_OVERRIDE="${2:?--prefix needs a path}"; shift 2 ;;
    --system)               INSTALL_SCOPE="system"; shift ;;
    --allow-system-prefix)  ALLOW_SYSTEM_PREFIX=1; shift ;;
    --dry-run)              DRY_RUN=1; shift ;;
    --help|-h)              usage; exit 0 ;;
    *)                      err "unknown flag: $1 (try --help)" ;;
  esac
done
# Touch ALLOW_SYSTEM_PREFIX so shellcheck sees it as referenced; the
# real consumer is scripts/lib/prefix-validation.sh sourced below.
: "${ALLOW_SYSTEM_PREFIX}"

if (( PURGE == 1 && APPLY == 0 )); then
  err "--purge requires --yes (refusing destructive op without explicit confirmation)"
fi

# OS detection (subset of install.sh)
case "$(uname -s)" in
  Darwin) OS=macos ;;
  Linux)  if grep -qiE '(microsoft|wsl)' /proc/version 2>/dev/null; then OS=wsl; else OS=linux; fi ;;
  *)      err "unsupported OS: $(uname -s) — use uninstall.ps1 on Windows" ;;
esac

# Guard: macOS system-scope is not yet implemented. LaunchDaemon support
# (root-owned /Library/LaunchDaemons/) is tracked by #145. Hard-error here
# so uninstall.sh --system cannot silently target the wrong service path.
# Exit 2 = precondition failure per scripts/script-output-conventions.md.
if [ "${OS}" = "macos" ] && [ "${INSTALL_SCOPE}" = "system" ]; then
  printf '[uninstall] ERROR: --system is not supported on macOS (see #145 for LaunchDaemon support). Use the default user-LaunchAgent mode (omit --system).\n' >&2
  exit 2
fi

# ----------------------------------------------------------------------------
# Prefix normalization + validation
#
# Implementation lives in scripts/lib/prefix-validation.sh so install.sh
# and uninstall.sh share the same guard (extracted from this file during
# PR B / #223 pre-PR review). Required caller-defined symbols:
#   err(), INSTALL_SCOPE, ALLOW_SYSTEM_PREFIX.
# ----------------------------------------------------------------------------

# shellcheck source=lib/prefix-validation.sh disable=SC1091
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/prefix-validation.sh"

if [[ -n "${PREFIX_OVERRIDE}" ]]; then
  PREFIX_OVERRIDE="$(normalize_prefix "${PREFIX_OVERRIDE}")"
  validate_prefix "${PREFIX_OVERRIDE}"
fi

# Path layout (must mirror install.sh)
if [[ -n "${PREFIX_OVERRIDE}" ]]; then
  PREFIX="${PREFIX_OVERRIDE}"
  if [[ "${OS}" == "macos" ]]; then
    LOG_DIR="${HOME}/Library/Logs/expertise-api"
    CONFIG_DIR="${PREFIX}"
  elif [[ "${INSTALL_SCOPE}" == "system" ]]; then
    LOG_DIR="/var/log/expertise-api"
    CONFIG_DIR="/etc/expertise-api"
  else
    LOG_DIR="${XDG_STATE_HOME:-${HOME}/.local/state}/expertise-api"
    CONFIG_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api"
  fi
elif [[ "${OS}" == "macos" ]]; then
  PREFIX="${HOME}/Library/Application Support/expertise-api"
  LOG_DIR="${HOME}/Library/Logs/expertise-api"
  CONFIG_DIR="${HOME}/Library/Application Support/expertise-api"
else
  if [[ "${INSTALL_SCOPE}" == "system" ]]; then
    PREFIX="/opt/expertise-api"; LOG_DIR="/var/log/expertise-api"; CONFIG_DIR="/etc/expertise-api"
  else
    PREFIX="${HOME}/.local/share/expertise-api"
    LOG_DIR="${XDG_STATE_HOME:-${HOME}/.local/state}/expertise-api"
    CONFIG_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api"
  fi
fi

BIN_DIR="${PREFIX}/bin"
MODEL_DIR="${PREFIX}/models"

# ----------------------------------------------------------------------------
# Action helper
#
# Direct argv invocation (no `eval`). Callers attach `|| true` and
# `2>/dev/null` at the call site as caller policy, not dispatcher policy.
#
# --dry-run forces a print-only mode even with --yes. Without --yes the
# script prints "would: …" lines; with --yes --dry-run it prints
# "DRY-RUN: …" lines (different prefix so the test harness can tell them
# apart from the implicit pre-confirmation dry-run).
# ----------------------------------------------------------------------------
do_action() {
  if (( DRY_RUN == 1 )); then
    printf '[uninstall] DRY-RUN: %s\n' "$*"
    return 0
  fi
  if (( APPLY == 1 )); then
    log "exec: $*"
    "$@"
  else
    log "would: $*"
  fi
}

if (( DRY_RUN == 1 )); then
  log "DRY RUN (--dry-run forced; no actions will execute regardless of --yes)"
elif (( APPLY == 0 )); then
  log "DRY RUN (use --yes to apply, --yes --purge to also remove user data)"
fi

# Pre-action plan summary — free forensics, cheap user sanity check.
log "plan:"
log "  PREFIX     = ${PREFIX}"
log "  BIN_DIR    = ${BIN_DIR}"
log "  MODEL_DIR  = ${MODEL_DIR}"
log "  LOG_DIR    = ${LOG_DIR}"
log "  CONFIG_DIR = ${CONFIG_DIR}"
if (( PURGE == 1 )); then
  log "  mode       = purge (remove binaries + user data)"
else
  log "  mode       = remove binaries only (user data preserved)"
fi

# Stop service first.
#
# Note: the `systemctl --user list-unit-files` / `[[ -f PLIST ]]` probes
# below execute even under --dry-run because they are pure read-only
# queries. This means the --dry-run plan reflects host state at
# dry-run time, not at apply time — intentional and documented in
# README. Do not wrap these probes in do_action.
case "${OS}" in
  linux|wsl)
    if systemctl --user list-unit-files expertise-api.service 2>/dev/null | grep -q expertise-api; then
      do_action systemctl --user stop expertise-api.service 2>/dev/null || true
      do_action systemctl --user disable expertise-api.service 2>/dev/null || true
      do_action rm -f "${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user/expertise-api.service"
      # `daemon-reload` can fail when no user-DBUS session exists (e.g.
      # uninstall over SSH without `loginctl enable-linger`). That's a
      # degraded-cleanup case, not a destructive one — the unit file is
      # already removed by the preceding `rm -f`. Tolerate the failure
      # so the script proceeds to delete the binary tree below.
      do_action systemctl --user daemon-reload 2>/dev/null || true
    else
      log "systemd unit not present — skipping service teardown"
    fi ;;
  macos)
    LABEL="com.thesemicolon.expertise-api"
    PLIST="${HOME}/Library/LaunchAgents/${LABEL}.plist"
    if [[ -f "${PLIST}" ]]; then
      # Teardown sequence (#286, semantics verified on a CI macOS runner):
      #
      # launchd override entries are PERSISTENT: BOTH `launchctl enable` and
      # `launchctl disable` write an entry into the LaunchDatabase that shows
      # up in `launchctl print-disabled gui/UID`, and no launchctl subcommand
      # removes an override entry without root. The only way to leave no
      # stale state is to never write one — so uninstall runs no
      # enable/disable at all, and install.sh only runs `enable` when the
      # label is actually listed as disabled.
      #
      # 1. `launchctl bootout gui/UID/LABEL` — stops and unregisters the
      #    service. Tolerates the service never having been loaded (|| true).
      # 2. `rm -f PLIST` — after this, launchd has no path to re-load it.
      #
      # If a disabled-override exists (operator ran `launchctl disable` by
      # hand), it is left in place: removing it requires root, it is harmless
      # once the plist is gone, and install.sh clears it on the next install.
      do_action launchctl bootout "gui/$(id -u)/${LABEL}" 2>/dev/null || true
      do_action rm -f "${PLIST}"
    else
      log "launchd plist not present — skipping service teardown"
    fi ;;
esac

# Remove binary tree. `rm -rf` swallows ENOENT under `-f`, so we do not
# precheck `[[ -d ... ]]` — that would only widen the TOCTOU window
# between the existence test and the deletion.
do_action rm -rf "${BIN_DIR}"

# Wrapper script (idempotent under `-f`).
# Wrapper script (idempotent under `-f`). Cover both the post-#223 location
# (${PREFIX}/launch-expertise-api.sh) and the legacy in-BIN_DIR location.
do_action rm -f "${PREFIX}/launch-expertise-api.sh"
do_action rm -f "${PREFIX}/bin/launch-expertise-api.sh"

if (( PURGE == 1 )); then
  log "purge: removing user data"
  for d in "${MODEL_DIR}" "${LOG_DIR}" "${CONFIG_DIR}" "${PREFIX}"; do
    do_action rm -rf "${d}"
  done
else
  log "preserved: ${MODEL_DIR}        (use --purge to remove)"
  log "preserved: ${LOG_DIR}          (use --purge to remove)"
  log "preserved: ${CONFIG_DIR}       (secrets — use --purge to remove)"
fi

log "uninstall complete (Postgres database NOT touched — drop manually if desired)"
