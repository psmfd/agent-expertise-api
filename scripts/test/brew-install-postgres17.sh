#!/usr/bin/env bash
#
# brew-install-postgres17.sh — install PostgreSQL 17 + pgvector via Homebrew for the
# macOS install-smoke CI jobs, self-healing a corrupt pre-cached bottle.
#
# GitHub's macOS runner images have shipped a `postgresql@17` keg missing its
# share/ files (notably `postgres.bki`, the catalog bootstrap seed). Because the
# formula still registers as installed, `brew install` skips it and `initdb` then
# fails at service start with:
#     initdb: error: file ".../share/postgresql@17/postgres.bki" does not exist
# When the seed file is absent we force a clean reinstall — clearing the cached
# bottle first in case the cached download is itself the corrupt copy — and
# re-verify, hard-failing loudly rather than letting the confusing initdb error
# surface downstream.
#
# `postgresql@17` is keg-only, so its bin/ is exported via GITHUB_PATH for
# subsequent workflow steps. Shared by all three macOS smoke jobs (ci.yml x2 +
# install-smoke-from-release.yml) so the heal lives in one place.
#
# Exit codes: 0 ok · 1 postgresql@17 still broken after repair.

set -euo pipefail

log() { printf '[brew-pg17] %s\n' "$*"; }

# Detect the catalog seed file under whatever share subdir the keg uses.
bki_present() {
  find "$(brew --prefix postgresql@17)/share" -name postgres.bki 2>/dev/null | grep -q .
}

brew update >/dev/null

# Install postgresql@17 FIRST and heal it BEFORE pgvector, so pgvector drops its
# extension into a repaired sharedir (a later postgresql@17 reinstall would wipe
# pgvector's files).
brew install postgresql@17

if ! bki_present; then
  log "postgresql@17 keg is missing postgres.bki — repairing corrupt runner bottle"
  rm -f "$(brew --cache postgresql@17)" 2>/dev/null || true
  brew reinstall postgresql@17
  bki_present || {
    echo "::error::postgresql@17 still missing postgres.bki after reinstall"
    exit 1
  }
  log "repair OK"
fi

brew install pgvector

# Export the keg-only bin/ for later steps (GITHUB_PATH is the cross-step channel).
if [ -n "${GITHUB_PATH:-}" ]; then
  echo "$(brew --prefix postgresql@17)/bin" >> "$GITHUB_PATH"
fi

log "postgresql@17 + pgvector ready"
