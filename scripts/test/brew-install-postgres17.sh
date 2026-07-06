#!/usr/bin/env bash
#
# brew-install-postgres17.sh — install PostgreSQL 17 + pgvector via Homebrew for the
# macOS install-smoke CI jobs, self-healing a corrupt pre-cached bottle.
#
# Homebrew has published a BROKEN `postgresql@17` bottle for some arch/OS combos
# (observed: 17.10 arm64_sequoia) whose tarball is missing `postgres.bki`, the
# catalog bootstrap seed. The failure is not a local extraction glitch — the
# poured bottle genuinely lacks the file — so the formula's own post_install
# `initdb` fails during `brew install` with:
#     initdb: error: file ".../share/postgresql@17/postgres.bki" does not exist
# A plain reinstall re-pours the same broken bottle and fails identically. The
# only reliable repair is to BUILD FROM SOURCE, which regenerates postgres.bki
# from the catalog headers during `make`.
#
# `postgresql@17` is keg-only, so its bin/ is exported via GITHUB_PATH for
# subsequent workflow steps. Shared by all three macOS smoke jobs (ci.yml x2 +
# install-smoke-from-release.yml) so the heal lives in one place.
#
# Exit codes: 0 ok · 1 postgresql@17 still broken after source rebuild.

set -euo pipefail

log() { printf '[brew-pg17] %s\n' "$*"; }

# Detect the catalog seed file under whatever share subdir the keg uses.
bki_present() {
  find "$(brew --prefix postgresql@17)/share" -name postgres.bki 2>/dev/null | grep -q .
}

brew update >/dev/null

# Install postgresql@17 FIRST and heal it BEFORE pgvector, so pgvector builds
# against a repaired keg (a later postgresql@17 rebuild would wipe pgvector's
# dropped files). Tolerate a non-zero exit here: a broken bottle makes the
# formula's post_install initdb fail, but the real success criterion is
# bki_present below, which drives the repair.
brew install postgresql@17 || log "postgresql@17 install returned non-zero (likely broken-bottle post_install); verifying"

if ! bki_present; then
  log "postgresql@17 is missing postgres.bki (broken upstream bottle) — rebuilding from source"
  # --build-from-source compiles postgres, which generates postgres.bki and runs
  # a working post_install. Slow (several minutes) but the only real repair.
  HOMEBREW_NO_INSTALL_FROM_API=1 brew reinstall --build-from-source postgresql@17
  bki_present || {
    echo "::error::postgresql@17 still missing postgres.bki after source rebuild"
    exit 1
  }
  log "source rebuild OK — postgres.bki present"
fi

brew install pgvector

# Export the keg-only bin/ for later steps (GITHUB_PATH is the cross-step channel).
if [ -n "${GITHUB_PATH:-}" ]; then
  echo "$(brew --prefix postgresql@17)/bin" >> "$GITHUB_PATH"
fi

log "postgresql@17 + pgvector ready"
