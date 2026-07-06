#!/usr/bin/env bash
#
# brew-install-postgres17.sh — install PostgreSQL 17 + pgvector via Homebrew for the
# macOS install-smoke CI jobs, self-healing a corrupt pre-cached bottle.
#
# The `postgresql@17` bottle on current macOS runner images fails at service
# start with:
#     initdb: error: file "/opt/homebrew/share/postgresql@17/postgres.bki" does not exist
# Diagnosed root cause: postgresql@17 is KEG-ONLY, but its bottle's initdb/server
# resolve their share dir to the top-level prefix `$(brew --prefix)/share/
# postgresql@17` — which keg-only installs leave unpopulated. postgres.bki DOES
# exist inside the keg (`$(brew --prefix postgresql@17)/share/...`); initdb just
# looks where keg-only didn't link it. Two repairs, in order of likelihood:
#   1. Force-link the keg into the prefix so initdb + the running server find
#      their share dir (fixes the path-mismatch — the observed failure).
#   2. If postgres.bki is genuinely absent from the keg too (a truly broken
#      bottle), rebuild from source, which regenerates it during `make`.
#
# `postgresql@17` is keg-only, so its bin/ is exported via GITHUB_PATH for
# subsequent workflow steps. Shared by all three macOS smoke jobs (ci.yml x2 +
# install-smoke-from-release.yml) so the heal lives in one place.
#
# Exit codes: 0 ok · 1 postgresql@17 still broken after repair.

set -euo pipefail

log() { printf '[brew-pg17] %s\n' "$*"; }

# Path to postgres.bki inside the keg, or empty if genuinely absent.
find_bki() { find "$(brew --prefix postgresql@17)/share" -name postgres.bki 2>/dev/null | head -1; }

brew update >/dev/null

# Tolerate a non-zero exit: the keg-only path mismatch fails the formula's
# post_install initdb, but the file is present and the link/rebuild below is the
# real fix. Install postgresql@17 BEFORE pgvector so pgvector lands in a
# repaired keg.
brew install postgresql@17 || log "postgresql@17 install returned non-zero (keg-only post_install initdb); repairing"

if [ -z "$(find_bki)" ]; then
  log "postgres.bki genuinely absent from keg — rebuilding from source"
  HOMEBREW_NO_INSTALL_FROM_API=1 brew reinstall --build-from-source postgresql@17
  [ -n "$(find_bki)" ] || { echo "::error::postgres.bki still absent after source rebuild"; exit 1; }
  log "source rebuild OK"
fi

# Force-link the keg-only formula so $(brew --prefix)/share/postgresql@17 (where
# initdb and the server look) resolves to the keg's share. --overwrite yields to
# postgresql@17 over any preinstalled postgresql@14 links (the smoke targets 17).
log "force-linking keg-only postgresql@17 so initdb finds its share dir"
brew link --overwrite --force postgresql@17

brew install pgvector || log "pgvector install returned non-zero; the Start step verifies CREATE EXTENSION"

# Export the keg-only bin/ for later steps (GITHUB_PATH is the cross-step channel).
if [ -n "${GITHUB_PATH:-}" ]; then
  echo "$(brew --prefix postgresql@17)/bin" >> "$GITHUB_PATH"
fi

log "postgresql@17 + pgvector ready"
