#!/usr/bin/env bash
#
# Pre-flight validator for PR title, branch name, and base branch.
# Mirrors the rules enforced by .github/workflows/lint-pr-title.yml plus the
# branch-naming and target-branch conventions from agent-framework github-flow.md.
#
# Usage:
#   scripts/validate-pr.sh [--title "<title>"] [--branch "<branch>"] [--base "<base>"]
#                          [--verbose] [--help]
#
# When --branch is omitted it is auto-detected from `git rev-parse --abbrev-ref HEAD`.
# When --base is omitted it defaults to "dev" (per github-flow.md).
# --title is required (it lives in the PR UI, not in git state).
#
# Exit codes:
#   0  all checks passed (warnings are informational only)
#   1  one or more errors found
#   2  precondition failure (missing args, not in a git repo)
#

set -euo pipefail

# --- Output helpers (per agent-framework script-output-conventions.md) ---
# Full helper set kept for consistency across framework scripts; skip() and info()
# are unused in this script but remain part of the canonical signature.
error_count=0
warn_count=0
VERBOSE=false

ok()    { echo "OK    [$1] $2"; }
# shellcheck disable=SC2317  # part of canonical helper set
skip()  { echo "SKIP  [$1] $2"; }
warn()  { echo "WARN  [$1] $2"; ((warn_count++)) || true; }
# shellcheck disable=SC2317  # part of canonical helper set
info()  { echo "INFO  $*"; }
err()   { echo "ERROR [$1] $2" >&2; ((error_count++)) || true; }
detail(){ if $VERBOSE; then echo "      $*"; fi; }

usage() {
  sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
}

# --- Argument parsing ---
TITLE=""
BRANCH=""
BASE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --title)   TITLE="${2:-}"; shift 2 ;;
    --branch)  BRANCH="${2:-}"; shift 2 ;;
    --base)    BASE="${2:-}"; shift 2 ;;
    --verbose|-v) VERBOSE=true; shift ;;
    --help|-h) usage; exit 0 ;;
    *) err "args" "Unknown argument: $1"; usage >&2; exit 2 ;;
  esac
done

if [[ -z "$TITLE" ]]; then
  err "args" "--title is required"
  usage >&2
  exit 2
fi

# --- Auto-detect branch from git when not explicit ---
if [[ -z "$BRANCH" ]]; then
  if git rev-parse --git-dir >/dev/null 2>&1; then
    BRANCH=$(git rev-parse --abbrev-ref HEAD)
    detail "auto-detected branch: $BRANCH"
  else
    err "args" "--branch not given and current directory is not a git repository"
    exit 2
  fi
fi

# --- Default base ---
if [[ -z "$BASE" ]]; then
  BASE="dev"
  detail "default base: dev"
fi

echo "PR Validation"
echo "=================================="
echo "title:  $TITLE"
echo "branch: $BRANCH"
echo "base:   $BASE"
echo ""

# --- Conventional Commits types (matches lint-pr-title.yml `types`) ---
TYPES_REGEX='(feat|fix|docs|chore|refactor|test|ci|style|perf)'

# --- Check 1: title format ---
# Matches the workflow's enforcement: <type>(<scope>)?!?: <subject>
# where the subject portion satisfies the workflow's subjectPattern: ^[a-z].+[^.]$.
TITLE_REGEX="^${TYPES_REGEX}(\\([a-z0-9._-]+\\))?!?: [a-z][^.]*[^.]$"

if [[ "$TITLE" =~ $TITLE_REGEX ]]; then
  ok "title-format" "Conventional Commits with lowercase subject (no trailing period)"
else
  err "title-format" "Title does not conform to Conventional Commits format"
  detail "expected: <type>(<scope>)?!?: <lowercase-subject-no-period>"
  detail "types:    feat|fix|docs|chore|refactor|test|ci|style|perf"
  detail "got:      '$TITLE'"

  # Specific diagnostics — most common failures
  if [[ ! "$TITLE" =~ ^${TYPES_REGEX} ]]; then
    detail "hint:     title must start with one of the allowed types"
  elif [[ ! "$TITLE" =~ ^${TYPES_REGEX}(\([a-z0-9._-]+\))?!?:\  ]]; then
    detail "hint:     missing ': ' (colon + space) between type/scope and subject"
  else
    SUBJECT="${TITLE#*: }"
    if [[ "$SUBJECT" =~ ^[A-Z] ]]; then
      detail "hint:     subject starts uppercase — lowercase ALL acronyms and proper nouns at the start (e.g. 'PATCH' → 'patch', 'JWT' → 'jwt', 'PostgreSQL' → 'postgresql')"
    fi
    if [[ "$SUBJECT" =~ \.$ ]]; then
      detail "hint:     subject ends with a period — remove it"
    fi
  fi
fi

# --- Check 2: branch format (per github-flow.md) ---
BRANCH_REGEX="^${TYPES_REGEX}/[a-z0-9][a-z0-9-]*$"

if [[ "$BRANCH" =~ $BRANCH_REGEX ]]; then
  ok "branch-format" "<type>/kebab-case-description"
else
  # Don't error on dev/main/HEAD — those are normal local-state branches that
  # the user may be validating titles against before creating a feature branch.
  case "$BRANCH" in
    dev|main|HEAD)
      warn "branch-format" "On '$BRANCH' — create a feature branch before opening the PR (per github-flow.md)" ;;
    *)
      err "branch-format" "Branch '$BRANCH' does not match <type>/kebab-case-description"
      detail "expected: <type>/<lowercase-kebab-case>"
      detail "types:    feat|fix|docs|chore|refactor|test|ci|style|perf"
      detail "got:      '$BRANCH'"
      ;;
  esac
fi

# --- Check 3: base branch (per github-flow.md) ---
case "$BASE" in
  dev)
    ok "base-branch" "Targeting integration branch (dev)" ;;
  main)
    warn "base-branch" "Targeting main directly — only release promotions from dev should target main (per github-flow.md)" ;;
  *)
    warn "base-branch" "Targeting unusual base '$BASE' — verify this is intentional" ;;
esac

# --- Summary ---
echo ""
echo "=================================="
if (( error_count > 0 )); then
  echo "FAIL — ${error_count} error(s), ${warn_count} warning(s)"
  exit 1
else
  echo "PASS — 0 errors, ${warn_count} warning(s)"
  exit 0
fi
