#!/usr/bin/env bash
#
# scripts/lib/prefix-validation.sh — shared --prefix safety guard.
#
# Sourced by scripts/install.sh and scripts/uninstall.sh. Exposes three
# functions:
#
#   normalize_prefix PATH        — lexical normalization; rejects '..',
#                                   embedded whitespace, line-end chars.
#                                   Prints the normalized path on stdout.
#   validate_prefix  PATH        — runs two-tier blocklist + component
#                                   check. Errors out via `err` (which
#                                   the sourcing script must define).
#   validate_prefix_ancestors PATH
#                                — TOCTOU-safe ancestor walk for
#                                   --system mode: stat every directory
#                                   component from / down to the leaf
#                                   parent and refuse if any ancestor
#                                   (a) is not owned by root (uid 0),
#                                   (b) is group-writable without the
#                                       sticky bit, or
#                                   (c) is world-writable without the
#                                       sticky bit, or
#                                   (d) is itself a symlink (symlinked
#                                       ancestors are the primary TOCTOU
#                                       redirection vector).
#                                   Reusable by macOS LaunchDaemon path
#                                   (#145).
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
# PR B (#223) pre-PR review.
# Ancestor-walk added 2026-06-11 (issue #242) to close the prefix-parent
# TOCTOU gap: a non-root-owned or writable ancestor directory can be
# swapped for a symlink between validation and rm -rf, redirecting the
# deletion to an attacker-chosen path.
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

# validate_prefix_ancestors PATH
#
# Walk every ancestor directory of PATH (from / down to the parent of PATH)
# and refuse if any component:
#   (a) is not owned by root (uid 0) — a non-root-owned ancestor can be
#       replaced by a symlink at any time by its owner, redirecting a
#       subsequent rm -rf to an attacker-chosen path.
#   (b) is group-writable without the sticky bit — group members can
#       rename/replace the directory entry.
#   (c) is world-writable without the sticky bit — any user can do the
#       same.
#   (d) is itself a symlink — a symlinked intermediate component IS the
#       TOCTOU redirection vector; POSIX pathname resolution follows
#       symlinks in intermediate components, so only symlinks INSIDE a
#       recursively-deleted tree are removed without following.
#
# Stat invocations use lstat semantics (no symlink following):
#   Linux : stat -c '%u %a'    → "uid octal-perms"  e.g. "0 755"  "0 1777"
#   macOS : stat -f '%u %Mp%Lp' → same format        e.g. "0 755"  "0 1777"
#   Both default to lstat (macOS BSD stat never follows; GNU stat -c does
#   not follow either — only stat -L does on Linux). The [ -L ] symlink
#   check runs first so a symlink-typed entry is caught before stat.
#
# Only called for INSTALL_SCOPE=system. User-mode operators own $HOME and
# control the entire ancestor chain — the cost is unnecessary there.
#
# Caller must define err() to exit non-zero with the supplied message.
validate_prefix_ancestors() {
  local prefix="$1"

  # Determine the OS once so the inner loop stays cheap.
  local _os
  _os="$(uname -s)"

  # Build the list of ancestors: strip the prefix itself (we only check
  # ancestors, not the leaf — validate_prefix already checks the leaf for
  # symlinks). Walk from / down to the immediate parent of the prefix.
  # We accumulate components by stripping one level at a time from prefix.
  local component
  component="${prefix%/*}"   # strip last component (leaf of prefix path)
  # component is now the parent directory. We will walk upward to / and
  # collect paths, then check them in order (top-down).

  # Collect ancestors into a newline-separated string (bash 3.2: no arrays
  # at file scope in sourced libs; we use a local variable list instead).
  local ancestor_list=""
  local p="$component"
  while [ -n "$p" ] && [ "$p" != "/" ]; do
    ancestor_list="${p}${ancestor_list:+
${ancestor_list}}"
    p="${p%/*}"
    # When p has no more slashes (top-level component stripped), the next
    # dirname is "/".
    if [ -z "$p" ]; then
      p="/"
    fi
  done
  # Always include root itself (we want to start the walk at /).
  ancestor_list="/${ancestor_list:+
${ancestor_list}}"

  # Walk each ancestor (in order from / down to the leaf parent).
  local checked_path
  # Process line-by-line using a here-string loop (bash 3.2 safe; no
  # readarray/mapfile). We read from a subshell-safe here-doc.
  while IFS= read -r checked_path; do
    [ -n "$checked_path" ] || continue

    # (d) Symlink check — lstat via [ -L ]. Catches symlinked ancestors
    # before we even call stat. A symlinked ancestor is the exact attack
    # vector: it can be atomically replaced to redirect rm -rf.
    if [ -L "$checked_path" ]; then
      err "--system ancestor is a symlink: $checked_path — an attacker can redirect rm -rf by swapping this symlink; pass the fully-resolved path"
    fi

    # Get uid and octal permissions via lstat.
    # Linux: stat -c '%u %a' uses lstat (no -L = no follow on GNU stat).
    # macOS: stat -f '%u %Mp%Lp' uses lstat by default (BSD stat never
    #        follows without explicit -L). %Mp = high octal digit (sticky/
    #        setuid/setgid), %Lp = low 9-bit octal (rwxrwxrwx). Combined
    #        output matches Linux '%a' format: e.g. "0 755" or "0 1777".
    local stat_out uid perms
    case "$_os" in
      Linux*)
        stat_out="$(stat -c '%u %a' "$checked_path" 2>/dev/null)" || \
          err "cannot stat ancestor: $checked_path"
        ;;
      Darwin*)
        stat_out="$(stat -f '%u %Mp%Lp' "$checked_path" 2>/dev/null)" || \
          err "cannot stat ancestor: $checked_path"
        ;;
      *)
        err "validate_prefix_ancestors: unsupported OS $(uname -s)"
        ;;
    esac

    uid="${stat_out%% *}"
    perms="${stat_out##* }"

    # (a) Ownership: must be root (uid 0).
    if [ "$uid" != "0" ]; then
      err "--system ancestor not owned by root (uid=$uid): $checked_path — a non-root owner can replace the directory with a symlink"
    fi

    # Parse sticky bit and group/world write bits from octal permissions.
    # Format is 3 or 4 octal digits: [sticky][user][group][other]
    # e.g. "755" or "1777". The sticky-bit digit is 1 when present.
    # We need to check group (digit -2) and other (digit -1) write bits,
    # plus the sticky digit to determine whether write permission is safe.

    # Strip any high digits beyond the 4-digit octal representation.
    # GNU stat may emit more digits for special modes; take the rightmost 4.
    local trimmed_perms
    if [ "${#perms}" -gt 4 ]; then
      trimmed_perms="${perms: -4}"     # last 4 chars (bash 3.2: space before -)
    else
      trimmed_perms="$perms"
    fi

    # Extract the four positional digits (pad with leading 0 if only 3).
    local sticky_digit group_digit other_digit
    if [ "${#trimmed_perms}" -eq 4 ]; then
      sticky_digit="${trimmed_perms%???}"   # first char of 4
      group_digit="${trimmed_perms:2:1}"   # third char (0-indexed)
      other_digit="${trimmed_perms:3:1}"   # fourth char
    else
      # 3-digit: no explicit sticky digit → sticky=0
      sticky_digit="0"
      group_digit="${trimmed_perms:1:1}"   # second char
      other_digit="${trimmed_perms:2:1}"   # third char
    fi

    # Sticky bit is set when sticky_digit is odd (1, 3, 5, 7).
    # Values: 1=sticky, 2=sgid, 3=sticky+sgid, 4=suid, 5=suid+sticky, etc.
    local sticky_set=0
    case "$sticky_digit" in
      1|3|5|7) sticky_set=1 ;;
    esac

    # Group-write bit is set when group_digit has bit 1 (values 2,3,6,7).
    # (b) Group-writable without sticky.
    case "$group_digit" in
      2|3|6|7)
        if [ "$sticky_set" -eq 0 ]; then
          err "--system ancestor is group-writable without sticky bit: $checked_path (perms=$perms) — group members can replace the directory entry"
        fi
        ;;
    esac

    # World-write bit is set when other_digit has bit 1 (values 2,3,6,7).
    # (c) World-writable without sticky.
    case "$other_digit" in
      2|3|6|7)
        if [ "$sticky_set" -eq 0 ]; then
          err "--system ancestor is world-writable without sticky bit: $checked_path (perms=$perms) — any user can replace the directory entry"
        fi
        ;;
    esac

  done <<EOF
${ancestor_list}
EOF
}
