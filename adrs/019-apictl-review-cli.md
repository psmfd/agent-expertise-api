# apictl drafts/review CLI — local HTTP layer, token handling, and terminal-render safety

- Status: accepted
- Date: 2026-07-25
- Companion: [ADR-018](018-approval-separation-of-duties.md) (the server gate this CLI is the human arm of), [`docs/security/integration-threat-model.md`](../docs/security/integration-threat-model.md) Part D
- Relates to: [ADR-008](008-response-hygiene-and-actor-class.md) (response hygiene), [ADR-010](010-idempotency-key.md) (Idempotency-Key), issue #485 (this CLI), issue #486 (skill `api_curl` argv token leak)

## Context and Problem Statement

ADR-018 closed the server side of the human-review invariant (author ≠ reviewer). The operator still had no first-class tool for the review itself: listing the Draft queue and approving/rejecting entries meant hand-rolled `curl` with a privileged `expertise.write.approve` bearer. Issue #485 specifies `expertise-apictl drafts [--json]` and `expertise-apictl review` (interactive approve/reject/skip/view loop over `GET /expertise/drafts`), designed by a three-agent fan-out and re-reviewed by a four-agent plan review.

Four decisions in that design deserve a record because each rejects a plausible-looking alternative.

## Decision Outcome

### 1. apictl-local HTTP code (duplication over a shared lib)

The skill's `.agents/skills/expertise-api/scripts/lib/common.sh` already implements token resolution and an `api_curl` helper — but `scripts/install.sh` ships only `scripts/`, so at runtime on an installed host the `.agents/` tree does not exist. Sourcing it would work in the repo checkout and break on every real install. The review commands therefore carry their own minimal HTTP layer inside `expertise-apictl` (~60 lines). The alternative — extracting a shared lib that `install.sh` also ships — is deliberately deferred: it couples the installer artifact list to the skill tree for one consumer, and the duplicated surface is small and stable. Revisit only if a third consumer appears.

### 2. Bearer via `curl --config`, never argv

`common.sh`'s `api_curl` passes `-H "Authorization: Bearer $TOKEN"` in argv, visible to any local process via `ps`/`/proc` (#486). `_api_http` instead writes the header into a config file under a 700-perm `mktemp -d` workdir and invokes `curl --config`; the file is removed immediately after each call, with a single script-global EXIT trap as the abnormal-exit backstop (bash **replaces** EXIT traps, so per-call traps would leak every earlier call's bearer file — the trap is registered exactly once).

Supporting constraints, all load-bearing:

- **Token charset guard.** Inside curl's double-quoted config values only `\\ \" \t \n \r \v` are escapes and `#` starts a comment — a corrupted token containing `"`, `\`, `#`, or whitespace would silently truncate the header. Legitimate bearer tokens (JWT base64url, `dev:{tenant}:{scopes}`) contain none of these, so the guard fails loudly instead of escaping.
- **No curl verbose modes, ever.** `-v`/`--trace*` print the Authorization header in cleartext, defeating the design via terminal scrollback/session logs. This is a standing constraint on `_api_http`; any future debug flag must redact the header specifically.
- **Env-only token resolution** (`EXPERTISE_API_TOKEN`, else `EXPERTISE_API_TOKEN_FILE`). Deliberately **not** auto-sourced from `~/.config/expertise-api/secrets.env`: `install.sh` already owns that path for **server** config (connection string, ONNX paths) — a different schema than the skill's client contract that documents the same filename. Auto-sourcing would conflate the two; the divergence is documented rather than papered over.
- **Alternative noted, not adopted:** curl ≥ 8.3 `--variable %ENV` + `--expand-header` would keep the token off disk entirely. Version-gated (2023-09), so it is a future improvement behind a capability probe, not a baseline.

### 3. Codepoint-aware terminal sanitization (jq, not tr)

Draft content is agent-written and untrusted. Server-side hygiene (ADR-008) wraps and redacts but deliberately does **not** strip terminal control bytes — a stored `ESC]0;…` or an RTL override would execute in the reviewer's terminal exactly when a human is deciding whether to trust the content (CWE-150 prompt spoofing, defeating the very review ADR-018 mandates). Everything rendered by `drafts`/`review` passes through `_sanitize_tty`, which drops per line: C0 controls except tab, DEL, C1 (U+0080–U+009F, the 8-bit CSI/OSC alternates), and the bidi/format overrides (U+200E/F, U+202A–202E, U+2066–2069).

The obvious implementation — `LC_ALL=C tr -d '\200-\237'` for the C1 range — is **wrong**: 0x80–0x9F are legitimate UTF-8 continuation bytes (every curly quote and em-dash contains one), and byte-level deletion corrupts them (verified empirically during plan review). The sanitizer is therefore a jq `explode | map(select(…)) | implode` codepoint filter; jq is already a hard dependency. Accepted residual gap: homoglyph/confusable spoofing is out of scope for a terminal CLI.

### 4. TOCTOU guard on approve/reject

The review loop buffers the drafts array once; an entry can be PATCHed between render and approve while remaining `Draft`, so the `xmin` concurrency token never fires and the reviewer would approve content they never saw. Immediately before each approve/reject, the CLI re-fetches the queue and compares the server-computed `IntegrityHash` to the rendered one; a mismatch (or disappearance) cancels the action. The re-fetch goes through `GET /expertise/drafts` — `GET /expertise/{id}` deliberately never surfaces Drafts, so a single-entry precondition read is not available. A server-side `If-Match`/expected-hash body field on `/approve` would be the airtight fix; the client-side hash check narrows the window from session-length to milliseconds and is judged sufficient for the A2 solo-operator deployment.

## Other Recorded Choices

- **Interactive gating:** `review` requires a controlling terminal, probed by opening `/dev/tty` (not `[ -t 0 ]` — stdin redirection and tty availability diverge in both directions); non-interactive invocation exits **2**. Prompts read from `/dev/tty` so stdin stays free; scripting consumers use `drafts --json`.
- **`Shared` visibility escalation:** approving with `Shared` broadens blast radius from one tenant to all tenants, so it demands a distinct consequence-naming confirmation (type `shared`) beyond the approve action itself.
- **JWT preflight (advisory):** the token's payload is decoded locally (jq `@base64d` — the `base64` CLI's `-d`/`-D` flags differ across GNU/BSD) and a warning is emitted when it looks service-shaped (`sub == azp`/`client_id`), since ADR-018 expects a human reviewer credential. Fails soft on non-JWT tokens (LocalDev `dev:` format, opaque tokens) — the server owns real authorization.
- **Fresh `Idempotency-Key` per action** (ADR-010): each POST mints its own UUID; the test suite asserts keys are distinct across actions, not merely present.
- **Windows parity:** `expertise-apictl.ps1` does not gain `drafts`/`review` — consistent with the existing Unix-only `backup*`/`reembed` surface. Classified not-a-thing until Windows A2 review demand exists.

## Consequences

- Reviewers get a safe default path; the raw-`curl` alternative remains possible but is no longer necessary.
- The skill's `api_curl` argv leak is **not** fixed by this ADR — that is #486, tracked separately against `common.sh`.
- `tests/review/test-apictl-review.sh` (mock HTTP server + pty-driven interactive session) is the regression suite for every constraint above, wired as a step in `ci.yml`.
