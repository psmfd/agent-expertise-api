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

if (( PURGE == 1 && APPLY == 0 )); then
  err "--purge requires --yes (refusing destructive op without explicit confirmation)"
fi

# OS detection (subset of install.sh)
case "$(uname -s)" in
  Darwin) OS=macos ;;
  Linux)  if grep -qiE '(microsoft|wsl)' /proc/version 2>/dev/null; then OS=wsl; else OS=linux; fi ;;
  *)      err "unsupported OS: $(uname -s) — use uninstall.ps1 on Windows" ;;
esac

# ----------------------------------------------------------------------------
# Prefix normalization + validation
#
# Design locked 2026-05-22 via shell-expert pre-review. Two key choices:
#
#  1. No realpath/readlink -f. Non-portable on macOS, false-secure on
#     non-existent paths, and resolving symlinks can hide attacks rather
#     than surface them. Instead: reject '..' components outright, then
#     lexically collapse '//' and strip trailing slash.
#
#  2. Prefix-match blocklist (not exact-match). Exact-match lets
#     /var/log, /private/var, /mnt/c/Users slip past. With prefix-match,
#     '/var' blocks every '/var/*' that doesn't carry an expertise-api
#     path component.
#
# --allow-system-prefix relaxes only the component check. The blocklist
# stays unconditional — it answers a different question (is this path
# obviously catastrophic?) than the component check (does this look like
# an expertise-api install?).
# ----------------------------------------------------------------------------

normalize_prefix() {
  local p="$1"
  # Reject parent-directory traversal entirely. No legitimate --prefix needs it.
  case "$p" in
    *"/../"*|*/..|"../"*|"..") err "--prefix may not contain '..'" ;;
  esac
  # Collapse runs of slashes.
  while [[ "$p" == *"//"* ]]; do p="${p//\/\//\/}"; done
  # Strip trailing slash but never the root.
  if [[ "$p" != "/" && "$p" == */ ]]; then p="${p%/}"; fi
  printf '%s\n' "$p"
}

validate_prefix() {
  local p="$1"
  [[ "$p" = /* ]]      || err "--prefix must be an absolute path"
  [[ "$p" != "/" ]]    || err "--prefix may not be /"
  [[ "$p" != "$HOME" ]] || err "--prefix may not be \$HOME ($HOME)"

  # Symlinked prefix is refused in --system mode (TOCTOU defense for
  # multi-user hosts). User-mode operators owning $HOME do not pay this
  # cost.
  if [[ "${INSTALL_SCOPE}" == "system" && -L "$p" ]]; then
    err "--prefix is a symlink ($p); pass the resolved path explicitly under --system"
  fi

  # Two-tier blocklist. Always-on, regardless of --allow-system-prefix.
  #
  #   blocked_exact   = parent containers / mount points. Block the bare dir
  #                     itself but allow descendants (e.g. /Users is rejected
  #                     but /Users/me/path is fine — every macOS HOME lives
  #                     under /Users, so a prefix-match here would block all
  #                     legitimate user installs).
  #   blocked_prefix  = catastrophic system subtrees where nothing under the
  #                     root is a sane install target (/bin, /etc, /System...).
  local blocked_exact=(
    /
    /home /root /Users         # user-home parent containers
    /opt /usr/local            # FHS / common system install parents (descendants allowed)
    /mnt /media /Volumes /Network  # mount-point parents
    /tmp /var /usr /srv /run /snap /cores /.vol /host /rootfs  # exact-block; descendants only blocked by component check
  )
  local blocked_prefix=(
    # POSIX system subtrees
    /bin /sbin /etc /lib /lib64 /boot /dev /proc /sys
    # macOS system subtrees
    /Library /System /Applications /private
    # WSL drive mounts (user code goes here, services should not)
    /mnt/c /mnt/wsl
    # User fat-finger guards (only meaningful when $HOME is set)
    "${HOME:+${HOME}/Desktop}"
    "${HOME:+${HOME}/Documents}"
  )
  local b
  for b in "${blocked_exact[@]}"; do
    [[ -n "$b" ]] || continue
    if [[ "$p" == "$b" ]]; then
      err "--prefix '$p' is a blocked parent/mount path (descendants are allowed; this exact path is not)"
    fi
  done
  for b in "${blocked_prefix[@]}"; do
    [[ -n "$b" ]] || continue
    if [[ "$p/" == "$b/"* ]]; then
      err "--prefix '$p' is under blocked system root '$b' (unconditional; --allow-system-prefix does not relax this)"
    fi
  done

  # Component check: defense-in-depth. Bypassable with --allow-system-prefix
  # for legitimately-named non-default install layouts.
  if (( ALLOW_SYSTEM_PREFIX == 0 )); then
    if [[ "/$p/" != *"/expertise-api/"* ]]; then
      err "--prefix '$p' must contain 'expertise-api' as a path component (or pass --allow-system-prefix to skip this check)"
    fi
  fi
}

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

# Stop service first
case "${OS}" in
  linux|wsl)
    if systemctl --user list-unit-files expertise-api.service 2>/dev/null | grep -q expertise-api; then
      do_action systemctl --user stop expertise-api.service 2>/dev/null || true
      do_action systemctl --user disable expertise-api.service 2>/dev/null || true
      do_action rm -f "${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user/expertise-api.service"
      do_action systemctl --user daemon-reload
    else
      log "systemd unit not present — skipping service teardown"
    fi ;;
  macos)
    LABEL="com.thesemicolon.expertise-api"
    PLIST="${HOME}/Library/LaunchAgents/${LABEL}.plist"
    if [[ -f "${PLIST}" ]]; then
      do_action launchctl bootout "gui/$(id -u)/${LABEL}" 2>/dev/null || true
      do_action rm -f "${PLIST}"
    else
      log "launchd plist not present — skipping service teardown"
    fi ;;
esac

# Remove binary tree
if [[ -d "${BIN_DIR}" ]]; then
  do_action rm -rf "${BIN_DIR}"
else
  log "binary dir not present — skipping"
fi

# Wrapper script
if [[ -f "${PREFIX}/bin/launch-expertise-api.sh" ]]; then
  do_action rm -f "${PREFIX}/bin/launch-expertise-api.sh"
fi

if (( PURGE == 1 )); then
  log "purge: removing user data"
  for d in "${MODEL_DIR}" "${LOG_DIR}" "${CONFIG_DIR}" "${PREFIX}"; do
    [[ -d "${d}" ]] && do_action rm -rf "${d}"
  done
else
  log "preserved: ${MODEL_DIR}        (use --purge to remove)"
  log "preserved: ${LOG_DIR}          (use --purge to remove)"
  log "preserved: ${CONFIG_DIR}       (secrets — use --purge to remove)"
fi

log "uninstall complete (Postgres database NOT touched — drop manually if desired)"
