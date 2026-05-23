#!/usr/bin/env bash
#
# scripts/lib/prefix-validation.sh — shared --prefix safety guard.
#
# Sourced by scripts/install.sh and scripts/uninstall.sh. Exposes two
# functions:
#
#   normalize_prefix PATH        — lexical normalization; rejects '..',
#                                   embedded whitespace, line-end chars.
#                                   Prints the normalized path on stdout.
#   validate_prefix  PATH        — runs two-tier blocklist + component
#                                   check. Errors out via `err` (which
#                                   the sourcing script must define).
#
# Required caller-defined symbols:
#
#   err()                       — must exit non-zero with the message
#   INSTALL_SCOPE               — "user" | "system" (drives symlink trap)
#   ALLOW_SYSTEM_PREFIX         — 0 | 1 (component check bypass)
#
# Design locked 2026-05-22 via shell-expert + security-review pre-review;
# extracted from scripts/uninstall.sh (PR #243) into a shared library on
# 2026-05-22 to close the install/uninstall asymmetry surfaced by the
# PR B (#223) pre-PR security review.
#

normalize_prefix() {
  local p="$1"
  # Reject embedded whitespace / line-ending characters. Most often a CR
  # smuggled in via a Windows clipboard paste; also catches accidental
  # tabs and newlines. Bash treats NUL as a string terminator so we do
  # not need to handle it explicitly.
  case "$p" in
    *$'\t'*|*$'\r'*|*$'\n'*) err "--prefix may not contain whitespace or line-ending characters" ;;
  esac
  # Reject parent-directory traversal entirely. The four patterns are
  # exhaustive only because absolute paths are '/'-anchored — if this
  # helper is ever reused for relative paths, extend accordingly.
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
  [[ "$p" = /* ]]        || err "--prefix must be an absolute path"
  [[ "$p" != "/" ]]      || err "--prefix may not be /"
  [[ "$p" != "$HOME" ]]  || err "--prefix may not be \$HOME ($HOME)"

  # Symlinked prefix is refused in --system mode (TOCTOU defense for
  # multi-user hosts). User-mode operators owning $HOME do not pay this
  # cost.
  if [[ "${INSTALL_SCOPE}" == "system" && -L "$p" ]]; then
    err "--prefix is a symlink ($p); pass the resolved path explicitly under --system"
  fi

  # Two-tier blocklist. Always-on, regardless of --allow-system-prefix.
  local blocked_exact=(
    /
    /home /root /Users         # user-home parent containers
    /opt /usr/local            # common system install parents (descendants allowed)
    /mnt /media /Volumes /Network  # mount-point parents
    /tmp /var /usr /srv /run /cores /.vol /host /rootfs  # exact-block; descendants gated by prefix/component checks below
  )
  local blocked_prefix=(
    # POSIX system subtrees
    /bin /sbin /etc /lib /lib64 /boot /dev /proc /sys
    # FHS /usr subtrees — /usr/local is the exact-only carve-out above.
    /usr/bin /usr/sbin /usr/lib /usr/libexec /usr/share /usr/include
    # FHS /var/lib (canonical writable system-state location; off-limits to us).
    /var/lib
    # Immutable squashfs mounts (snap packages).
    /snap
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
  if (( ${ALLOW_SYSTEM_PREFIX:-0} == 0 )); then
    if [[ "/$p/" != *"/expertise-api/"* ]]; then
      err "--prefix '$p' must contain 'expertise-api' as a path component (or pass --allow-system-prefix to skip this check)"
    fi
  fi
}
