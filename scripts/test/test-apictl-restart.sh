#!/usr/bin/env bash
#
# test-apictl-restart.sh — dispatcher for the cross-platform apictl restart
# race regression test. Selects the per-OS harness or exits 0 with a
# "skipped" message on unsupported hosts.
#
# Validates issue #141: launchctl bootout race on macOS and, by extension,
# atomic-restart-primitive parity on Linux (systemctl --user restart).
#
# Usage:
#   bash scripts/test/test-apictl-restart.sh
#
# Exit codes:
#   0  — test passed, or skipped on unsupported OS
#   1+ — test failed (per-iteration failure or cleanup failure)

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

case "$(uname -s)" in
  Darwin) exec bash "${ROOT}/scripts/test/test-apictl-restart-macos.sh" ;;
  Linux)  exec bash "${ROOT}/scripts/test/test-apictl-restart-linux.sh" ;;
  *)
    echo "[test-apictl-restart] skipped: unsupported OS ($(uname -s))"
    exit 0
    ;;
esac
