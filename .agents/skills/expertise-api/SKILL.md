---
name: expertise-api
description: Search, create, approve, and reject expertise entries in the agent-expertise-api HTTP service. Use when the user asks to record a tribal-knowledge note, look up prior decisions, find similar entries, approve a teammate's draft, or reject one with a reason. Wraps a small curl toolkit under scripts/.
---

## When to use

Invoke this skill when the user wants to:

- **Search** existing expertise — by keyword (`/expertise/search`), by semantic similarity (`/expertise/search/semantic`), or by filter (`domain`, `tags`, `entryType`, `severity`).
- **Read** a specific entry by id (`/expertise/{id}`).
- **Create** a new draft entry (`POST /expertise`).
- **Approve** a draft (`POST /expertise/{id}/approve`).
- **Reject** a draft with a reason (`POST /expertise/{id}/reject`).

Do **not** invoke this skill for: API design questions, schema decisions, scope-policy reasoning, or repo-internals questions — those are covered by `references/DESIGN.md`, which you should load on demand only if the user's question requires it.

## Prerequisites

The skill scripts require two environment variables. The skill will fail loudly if either is missing.

| Variable | Purpose | Example |
| --- | --- | --- |
| `EXPERTISE_API_BASE_URL` | Origin of the API. No trailing slash. | `https://expertise.example.com` |
| `EXPERTISE_API_TOKEN` | Bearer token. JWT for OIDC mode, or `dev:{tenant}:{scopes}` for LocalDev. | `dev:teamA:expertise.read+expertise.write.draft` |

If the user has not set them, prompt for the values once and offer to write them to `~/.config/expertise-api/secrets.env`:

```sh
mkdir -p ~/.config/expertise-api
cat > ~/.config/expertise-api/secrets.env <<'EOF'
EXPERTISE_API_BASE_URL=https://expertise.example.com
EXPERTISE_API_TOKEN=...
EOF
chmod 600 ~/.config/expertise-api/secrets.env
```

The scripts source this file automatically when present, so env vars do not need to be exported per-shell.

**Trust model.** The secrets file is sourced via POSIX `.` (dot), so anyone with write access to it can execute arbitrary code in the invoker's shell. Keep it `chmod 600` on a user-owned path. The bearer token is also passed to `curl` as a command-line `-H` argument and is therefore briefly visible in `ps auxww` / `/proc/<pid>/cmdline` to other local users on multi-user systems; this matches common `curl` usage but is worth noting if you share the workstation.

## Toolkit

All scripts live under this skill's `scripts/` directory and emit machine-readable JSON on stdout. They `set -euo pipefail` and report `curl`/HTTP-status errors on stderr with the response body.

| Script | What it does | Args |
| --- | --- | --- |
| `scripts/search.sh` | Filter-style listing (`GET /expertise`) when called with no `--q`; full-text search (`GET /expertise/search?q=`) when called with `--q`. | `--q TEXT`, `--domain D`, `--tags a,b`, `--entry-type T`, `--severity S`, `--include-deprecated` |
| `scripts/search-semantic.sh` | Semantic vector search (`GET /expertise/search/semantic?q=`). | `--q TEXT` (required), `--limit N` (1-100, default 10), `--include-deprecated` |
| `scripts/get.sh` | Fetch one entry (`GET /expertise/{id}`). | `<id>` (positional, GUID) |
| `scripts/create.sh` | Create a draft (`POST /expertise`). Reads JSON body from stdin or `--file PATH`. | `--file PATH` _or_ pipe JSON via stdin |
| `scripts/approve.sh` | Approve a draft (`POST /expertise/{id}/approve`). | `<id>` |
| `scripts/reject.sh` | Reject a draft with a reason (`POST /expertise/{id}/reject`). | `<id>` `<reason>` (reason: 1–2000 chars) |
| `scripts/skill-smoke-test.sh` | Round-trip smoke test (create → get → search → approve → reject path) against a local instance. | — |

### Examples

```sh
# Keyword search
./scripts/search.sh --q "pgbouncer transaction mode"

# Filter listing
./scripts/search.sh --domain shared --tags postgres,pgbouncer --severity Warning

# Semantic search
./scripts/search-semantic.sh --q "connection pool advisory locks" --limit 5

# Create from a JSON file
cat > /tmp/entry.json <<'EOF'
{
  "domain": "shared",
  "tags": ["pgbouncer", "postgres"],
  "title": "PgBouncer transaction mode breaks advisory locks",
  "body": "## Symptom\n\nAcquiring a session-level advisory lock fails intermittently...\n",
  "entryType": "Caveat",
  "severity": "Warning",
  "source": "agent-session-2026-05",
  "sourceVersion": "PgBouncer 1.21.0"
}
EOF
./scripts/create.sh --file /tmp/entry.json

# Approve / reject (requires expertise.write.approve)
./scripts/approve.sh 0193b8c4-...
./scripts/reject.sh  0193b8c4-... "Body lacks reproducer; reopen with steps to reproduce."
```

## Scope and error semantics

| Status | Meaning | What the caller should do |
| --- | --- | --- |
| 200 / 201 | Success | Continue. Response body is the entry or list. |
| 400 | Validation error (e.g. missing required field, reason >2000 chars) | Show the ProblemDetails `errors[]` to the user; do not retry. |
| 401 | Missing/invalid token | Re-check `EXPERTISE_API_TOKEN`. Do not auto-retry. |
| 403 | Authenticated but missing scope (e.g. trying to approve without `expertise.write.approve`) | Tell the user which scope is needed; do not auto-retry. |
| 404 | Entry not found _or_ in another tenant (cross-tenant existence is hidden) | Confirm the id with the user. |
| 409 | Concurrency conflict (concurrent approve+reject) _or_ dedup match on create | Re-fetch and reconsider. |
| 429 | Rate-limited | Honour `Retry-After`. |

## Idempotency on writes

The server **requires** an `Idempotency-Key` header on every POST to the
three target endpoints (`/expertise`, `/expertise/{id}/approve`,
`/expertise/{id}/reject`) as of 2026-05-19 (ADR-010 Amendment 1). Missing
keys are rejected with HTTP 400. Replays of a successful key return the
original 2xx body per
[ADR-010](../../../adrs/010-idempotency-key-handling.md). The `api_curl`
helper in `scripts/lib/common.sh` injects this header automatically on
any POST, generated from `uuidgen`. **No caller-side change is required**
for the common case — a single invocation of `create.sh`, `approve.sh`,
or `reject.sh` gets a fresh key and the server treats it as a one-shot.

If a caller wraps the script in its own retry loop (e.g. an outer shell
that re-runs `create.sh` on transient network failure), it must pin one
key across all attempts so the server returns the _original_ response on
replay instead of double-creating. Pre-generate the key and export it:

```sh
export IDEMPOTENCY_KEY="$(uuidgen)"
for _ in 1 2 3; do
    ./scripts/create.sh --file /tmp/entry.json && break
    sleep 2
done
unset IDEMPOTENCY_KEY
```

Keys are scoped to `(tenant, key)` and retained server-side for the
configured TTL (24h default). A literal byte-equal replay returns the
original 2xx body plus `Idempotency-Replay: true`; a _different_ request
body under the same key returns HTTP 409.

## Design reference

For the underlying schema, scope hierarchy, approval state machine, audit log shape, and authentication modes, load `references/DESIGN.md` from this skill directory on demand. It is intentionally not in scope at skill load time.

## Related

- pi extension `expertise-api` (typed tools, same operations): see repo `.pi/extensions/expertise-api/` once shipped (tracked in [#148](https://github.com/TheSemicolon/agent-expertise-api/issues/148)).
- pi prompt templates `/expertise-search`, `/expertise-create`, `/expertise-approve`: tracked in [#149](https://github.com/TheSemicolon/agent-expertise-api/issues/149).
