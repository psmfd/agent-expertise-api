#!/usr/bin/env bash
#
# uninstall.sh — remove agent-expertise-api native OS service install.
#
# Dry-run by default. Use --yes to apply, --purge to also remove user data
# (logs, models, secrets). Postgres data is NEVER touched (out of scope).
#
# Usage:
#   scripts/uninstall.sh [--yes] [--purge] [--prefix DIR] [--system] [--help]
#

set -euo pipefail

log()  { printf '[uninstall] %s\n' "$1"; }
warn() { printf '[uninstall] WARN: %s\n' "$1" >&2; }
err()  { printf '[uninstall] ERROR: %s\n' "$1" >&2; exit 1; }

APPLY=0
PURGE=0
INSTALL_SCOPE="user"
PREFIX_OVERRIDE=""

usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes)     APPLY=1; shift ;;
    --purge)   PURGE=1; shift ;;
    --prefix)  PREFIX_OVERRIDE="${2:?--prefix needs a path}"; shift 2 ;;
    --system)  INSTALL_SCOPE="system"; shift ;;
    --help|-h) usage; exit 0 ;;
    *)         err "unknown flag: $1 (try --help)" ;;
  esac
done

if (( PURGE == 1 && APPLY == 0 )); then
  err "--purge requires --yes (refusing destructive op without explicit confirmation)"
fi

# OS detection (subset of install.sh)
case "$(uname -s)" in
  Darwin) OS=macos ;;
  Linux)  if grep -qiE '(microsoft|wsl)' /proc/version 2>/dev/null; then OS=wsl; else OS=linux; fi ;;
  *)      err "unsupported OS: $(uname -s) — use uninstall.ps1 on Windows" ;;
esac

# Path layout (must mirror install.sh)
if [[ -n "${PREFIX_OVERRIDE}" ]]; then
  PREFIX="${PREFIX_OVERRIDE}"
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

# Action helper — print or execute depending on APPLY
do_action() {
  if (( APPLY == 1 )); then
    log "exec: $*"
    # shellcheck disable=SC2294  # intentional: callers pass a single shell string with redirection / || true.
    eval "$@"
  else
    log "would: $*"
  fi
}

if (( APPLY == 0 )); then
  log "DRY RUN (use --yes to apply, --yes --purge to also remove user data)"
fi

# Stop service first
case "${OS}" in
  linux|wsl)
    if systemctl --user list-unit-files expertise-api.service 2>/dev/null | grep -q expertise-api; then
      do_action "systemctl --user stop expertise-api.service 2>/dev/null || true"
      do_action "systemctl --user disable expertise-api.service 2>/dev/null || true"
      do_action "rm -f \"${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user/expertise-api.service\""
      do_action "systemctl --user daemon-reload"
    else
      log "systemd unit not present — skipping service teardown"
    fi ;;
  macos)
    LABEL="com.thesemicolon.expertise-api"
    PLIST="${HOME}/Library/LaunchAgents/${LABEL}.plist"
    if [[ -f "${PLIST}" ]]; then
      do_action "launchctl bootout gui/$(id -u)/${LABEL} 2>/dev/null || true"
      do_action "rm -f \"${PLIST}\""
    else
      log "launchd plist not present — skipping service teardown"
    fi ;;
esac

# Remove binary tree
if [[ -d "${BIN_DIR}" ]]; then
  do_action "rm -rf \"${BIN_DIR}\""
else
  log "binary dir not present — skipping"
fi

# Wrapper script
if [[ -f "${PREFIX}/bin/launch-expertise-api.sh" ]]; then
  do_action "rm -f \"${PREFIX}/bin/launch-expertise-api.sh\""
fi

if (( PURGE == 1 )); then
  log "purge: removing user data"
  for d in "${MODEL_DIR}" "${LOG_DIR}" "${CONFIG_DIR}" "${PREFIX}"; do
    [[ -d "${d}" ]] && do_action "rm -rf \"${d}\""
  done
else
  log "preserved: ${MODEL_DIR}        (use --purge to remove)"
  log "preserved: ${LOG_DIR}          (use --purge to remove)"
  log "preserved: ${CONFIG_DIR}       (secrets — use --purge to remove)"
fi

log "uninstall complete (Postgres database NOT touched — drop manually if desired)"
