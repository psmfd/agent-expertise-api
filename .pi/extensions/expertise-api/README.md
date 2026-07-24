# pi extension: `expertise-api`

In-tree pi extension that exposes the agent-expertise-api as **typed tools** the
LLM can call directly — no shelling to `curl`.

| Tool | HTTP | Scope required |
| --- | --- | --- |
| `expertise_search` | `GET /expertise` (filter) or `/expertise/search?q=` | `expertise.read` |
| `expertise_search_semantic` | `GET /expertise/search/semantic?q=` | `expertise.read` |
| `expertise_search_hybrid` | `GET /expertise/search/hybrid?q=` (keyword + semantic fused via RRF — recommended default) | `expertise.read` |
| `expertise_get` | `GET /expertise/{id}` | `expertise.read` |
| `expertise_create` | `POST /expertise` | `expertise.write.draft` |
| `expertise_update` | `PATCH /expertise/{id}` | `expertise.write.draft` |
| `expertise_approve` | `POST /expertise/{id}/approve` | `expertise.write.approve` |
| `expertise_reject` | `POST /expertise/{id}/reject` | `expertise.write.approve` |
| `expertise_delete` | `DELETE /expertise/{id}` | `expertise.write.draft` (own-tenant) or `expertise.write.approve` (shared) |

Pairs with the action skill at [`.agents/skills/expertise-api/`](../../../.agents/skills/expertise-api/SKILL.md), which provides the curl-based equivalent for harnesses that do not run pi (Claude Code, Codex CLI).

## Install (pi)

Auto-discovery picks up `.pi/extensions/expertise-api/index.ts` in any cwd that contains it. For repos that want it globally, symlink into `~/.pi/agent/extensions/`:

```sh
ln -s "$(pwd)/.pi/extensions/expertise-api" ~/.pi/agent/extensions/expertise-api
```

Or add the path to `~/.pi/settings.json`:

```jsonc
{
  "extensions": [
    "/absolute/path/to/agent-expertise-api/.pi/extensions/expertise-api"
  ]
}
```

Confirm with `/tools` inside pi — eight tools named `expertise_*` should appear.

## Env contract

Same as the action skill. Set either via shell environment or
`~/.config/expertise-api/secrets.env` (auto-sourced on extension load):

```sh
mkdir -p ~/.config/expertise-api
cat > ~/.config/expertise-api/secrets.env <<'EOF'
EXPERTISE_API_BASE_URL=https://expertise.example.com
EXPERTISE_API_TOKEN_FILE=/path/to/token.jwt
EOF
chmod 600 ~/.config/expertise-api/secrets.env
```

`EXPERTISE_API_TOKEN_FILE` (recommended) points at a file holding the token —
no bearer literal in the env file. Setting `EXPERTISE_API_TOKEN=...` directly
also works and wins when both are present (#464).

The token reaches `fetch()` via an HTTP header — it is **not** visible in `ps`/`/proc` like the skill's `curl`-based path.

## Idempotency on writes

The server **requires** an `Idempotency-Key` header on every POST to `/expertise`, `/expertise/{id}/approve`, and `/expertise/{id}/reject` as of 2026-05-19 ([ADR-010 Amendment 1](../../../adrs/010-idempotency-key-handling.md#amendment-1--hard-require-flip-2026-05-19)); missing keys are rejected with HTTP 400. The `apiCall` helper auto-injects a fresh UUID v4 (`crypto.randomUUID()`) on every POST it issues, so extension callers get this for free — GET, PATCH, and DELETE are not augmented. A caller-supplied `Idempotency-Key` in `init.headers` is preserved verbatim, useful for tool handlers that own a retry loop and want to pin one key across attempts to hit the server-side replay cache instead of double-writing. Caller-supplied keys are shape-validated client-side (1–255 printable-ASCII chars, no whitespace; mirrors the server-side validator) and an invalid value throws synchronously.

## Local development

```sh
cd .pi/extensions/expertise-api
npm ci
npm run typecheck   # tsc --noEmit (also runs in CI)
```

The `package.json` pins `@earendil-works/pi-coding-agent` to a version that ships with the upstream `Type` / `ExtensionAPI` shapes used by `index.ts`. Bump deliberately and re-run `npm run typecheck` after any pi-coding-agent upgrade.

## Schemas

Hand-written `Type.Object(...)` from `typebox` for v1 (issue [#148](https://github.com/psmfd/agent-expertise-api/issues/148)). Codegen from the published OpenAPI document (`/openapi/v1.json` or the release-asset `openapi.json`) is a follow-up; the hand-written schemas only need to round-trip the public-facing fields, which are stable per ADR-008.

## Out of scope (tracked separately)

- Slash-command shortcuts (`/expertise-search`, `/expertise-create`, `/expertise-approve`) — shipped at `.pi/prompts/` (issue [#149](https://github.com/psmfd/agent-expertise-api/issues/149)).
- npm/git package publish for cross-repo install — follow-up once the extension stabilises.
- Typebox-schema codegen from `openapi.json` — follow-up.
