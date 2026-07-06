#!/usr/bin/env bash
#
# check-coverage.sh — coverage regression ratchet (#355).
#
# Reads the root line-rate / branch-rate from a Cobertura report and fails if
# either has dropped below the committed floor in .coverage-baseline. This is a
# REGRESSION ratchet, not an aspirational target: the floors sit a few points
# below the measured value so normal variation never trips CI, but deleting a
# test file (or a chunk of tests) does. Bump the floors UP when coverage
# improves — never down without a recorded reason.
#
# Usage:
#   scripts/check-coverage.sh [COBERTURA_XML] [SEARCH_DIR]
#     COBERTURA_XML  explicit path to coverage.cobertura.xml
#     SEARCH_DIR     directory to search when the path is omitted (default: test-results)
#
# Exit codes: 0 pass, 1 regression below floor, 2 precondition failure.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BASELINE_FILE="${REPO_ROOT}/.coverage-baseline"

ok()   { printf 'OK    [%s] %s\n' "$1" "$2"; }
info() { printf 'INFO  %s\n' "$*"; }
err()  { printf 'ERROR [%s] %s\n' "$1" "$2" >&2; }
fatal(){ err "$1" "$2"; exit 2; }

xml="${1:-}"
search_dir="${2:-test-results}"

if [[ -z "$xml" ]]; then
  xml="$(find "$search_dir" -name coverage.cobertura.xml 2>/dev/null | head -1 || true)"
fi
[[ -n "$xml" && -f "$xml" ]] || fatal "coverage-file" "no coverage.cobertura.xml found (looked in '${search_dir}')"
[[ -f "$BASELINE_FILE" ]] || fatal "baseline" "missing ${BASELINE_FILE}"

# The root <coverage line-rate="..." branch-rate="..."> attributes are the first
# occurrences in the document; nested package/class elements repeat them.
# grep -m1 stops after the first matching line and exits 0 — do NOT pipe grep to
# `head`, which closes the pipe early and gives grep a SIGPIPE that fails the
# pipeline under `set -o pipefail` (GNU grep on CI is stricter than BSD grep).
extract() {
  grep -o -m1 "$1=\"[0-9.]*\"" "$xml" | sed "s/$1=\"//; s/\"//"
}
line_raw="$(extract line-rate)"
branch_raw="$(extract branch-rate)"
[[ -n "$line_raw" && -n "$branch_raw" ]] || fatal "parse" "could not read line-rate/branch-rate from ${xml}"

# Cobertura rates are 0..1; convert to a percentage with two decimals.
line_pct="$(awk "BEGIN { printf \"%.2f\", ${line_raw} * 100 }")"
branch_pct="$(awk "BEGIN { printf \"%.2f\", ${branch_raw} * 100 }")"

line_floor="$(grep -E '^line=' "$BASELINE_FILE" | cut -d= -f2)"
branch_floor="$(grep -E '^branch=' "$BASELINE_FILE" | cut -d= -f2)"
[[ -n "$line_floor" && -n "$branch_floor" ]] || fatal "baseline" "baseline must define line= and branch="

info "coverage: line ${line_pct}% (floor ${line_floor}%), branch ${branch_pct}% (floor ${branch_floor}%)"

rc=0
if awk "BEGIN { exit !(${line_pct} < ${line_floor}) }"; then
  err "line-coverage" "line coverage ${line_pct}% is below the floor ${line_floor}% — a test was likely removed"
  rc=1
else
  ok "line-coverage" "${line_pct}% >= ${line_floor}%"
fi
if awk "BEGIN { exit !(${branch_pct} < ${branch_floor}) }"; then
  err "branch-coverage" "branch coverage ${branch_pct}% is below the floor ${branch_floor}%"
  rc=1
else
  ok "branch-coverage" "${branch_pct}% >= ${branch_floor}%"
fi

printf '==================================\n'
if [[ "$rc" -eq 0 ]]; then
  printf 'PASS — coverage at or above floor\n'
else
  printf 'FAIL — coverage regressed below floor (raise a test, or justify lowering .coverage-baseline)\n'
fi
exit "$rc"
