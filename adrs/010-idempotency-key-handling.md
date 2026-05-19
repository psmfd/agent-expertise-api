# Idempotency-Key handling and replay semantics (C3)

- Status: accepted
- Date: 2026-05-19
- Companion: [`docs/security/integration-threat-model.md`](../docs/security/integration-threat-model.md) Part D C3
- Tracking issue: [#188](https://github.com/TheSemicolon/agent-expertise-api/issues/188)
- Follow-ups: [#205](https://github.com/TheSemicolon/agent-expertise-api/issues/205) (skill caller), [#206](https://github.com/TheSemicolon/agent-expertise-api/issues/206) (pi extension caller)

## Context and Problem Statement

Part D of the integration threat model enumerates eight server-side controls (C1–C8) that the four-alternative integration stack (ADR-007) presumes. Seven are landed; C3 is the only un-tracked control.

C3 requires that replays of tool-mediated POST writes (network retry, agent harness re-invocation, manual `curl` re-run after timeout) **must not duplicate entries or audit rows**. The mitigation is a server-side `Idempotency-Key` header with a 24h dedup window, returning the original response byte-for-byte plus an `Idempotency-Replay: true` header on replays. Without this, every retry from the four-alternative integration stack (#147 skill, #148 pi extension, plain loopback HTTP) is a candidate for a duplicate entry that the curator then has to triage.

The decision surface is wider than the issue body suggests. Beyond the obvious shape (table + middleware + GC sweep), the substantive design questions are:

1. **Enforcement strictness** — hard-require (400 if header absent) vs soft-require (feature-flagged, default off). The threat-model row only flips to ✅ when the control is *enforced*, not merely *available*.
2. **Concurrency primitive** — what serializes two simultaneous requests with the same `(tenant, key)` arriving 5 ms apart.
3. **Request hash inputs** — the hash that decides "same request vs key reuse with different payload" must defend against `(tenant, key)` collisions, cross-method collisions (approve vs reject for same id), and cross-principal collisions within a tenant.
4. **Response capture plumbing** — `IResult` is executed against `HttpResponse.Body`; capturing the bytes for replay is the trickiest piece.
5. **Store lifetime** — singleton (issue body's suggestion) collides with `AddDbContext` scoping unless the store deliberately avoids EF.
6. **Response body size cap** — what happens to a pathologically large response, and how big is "pathological" for this API.

All six are cross-cutting consumer contracts or one-way doors. ADR-008's precedent (response hygiene shipped two controls, C6 + C7, in one ADR because the decision-independence benefit was zero) applies here: C3 is a single control but the six decisions above are interdependent, and a future maintainer asking "why ON CONFLICT instead of advisory lock?" or "why soft-require?" needs to find the answer in `adrs/`, not by reading a filter class.

## Considered Options

### Enforcement strictness

- *(a) Hard-require day one.* `Idempotency-Key` header is mandatory on the three POSTs; absence returns 400 with ProblemDetails citing the IETF draft. C3 row flips to ✅ on first deploy. Breaks every existing caller that does not yet send the header.
- *(b) Soft-require with `Idempotency:RequireKey` feature flag, default `false`.* Server dedups whenever a key is present; absence is permitted under the default. C3 row flips to ✅ for opt-in callers immediately and to "fully enforced" when the flag flips. Requires discipline to ever flip the flag.
- *(c) Soft-require for one release, then hard-require by removing the flag.* Hybrid: ships the soft path but commits to the flip in a time-boxed manner via a follow-up issue.

### Middleware placement

- *(a) ASP.NET Core `IEndpointFilter` chained per-endpoint via `RequireIdempotency()` extension.*
- *(b) Global pipeline middleware with method + path allowlist.*
- *(c) Per-handler manual check at the top of each POST handler.*

### Store lifetime

- *(a) Singleton over `NpgsqlDataSource`, raw parameterized SQL, no EF Core involvement on the hot path.*
- *(b) Scoped over `ExpertiseDbContext`, participating in EF change-tracking.*
- *(c) Singleton factory that resolves a scoped DbContext per call via `IDbContextFactory<T>`.*

### Request hash inputs

- *(a) SHA-256 over `method ‖ route-template ‖ tenant ‖ principal-sub ‖ raw body bytes`, hex-encoded.*
- *(b) SHA-256 over canonicalized JSON body only.*
- *(c) SHA-256 over `method ‖ path+query ‖ raw body bytes`.*

### Concurrency primitive

- *(a) `INSERT … ON CONFLICT (tenant, key) DO NOTHING` to reserve a stub row; losers `SELECT … FOR UPDATE` on the row inside the same transaction as the winner and block until winner commits, then read the persisted response (or 409 on hash mismatch).*
- *(b) Postgres advisory lock keyed on `hashtext(tenant ‖ key)`.*
- *(c) `INSERT … ON CONFLICT DO NOTHING` + losers poll the row with short backoff (50/100/200 ms, ≤ 2 s budget) for the persisted response.*

### Response capture

- *(a) Inside the endpoint filter, swap `HttpContext.Response.Body` for a size-capped `MemoryStream`, `await next(ctx)`, materialize the returned `IResult` by calling `result.ExecuteAsync(ctx)` against the buffer, snapshot `(StatusCode, Headers, Body)`, then copy buffer back to the original `Stream`; persist via `HttpContext.Response.OnCompleted` so the client is not blocked on the idempotency UPDATE round-trip.*
- *(b) `IResultExecutor` interception.*
- *(c) Pipeline-middleware-level response-body wrap (ASP.NET response-caching style).*

### Response body size cap

- *(a) 64 KiB, hard cap; on overflow store `(status_code, body_hash)` only and on replay return `Idempotency-Replay: true` with no body and a `Warning` header.*
- *(b) 256 KiB.*
- *(c) 1 MiB.*

## Decision Outcome

### Enforcement strictness: option (b) soft-require with a binding flip path

Chosen because two first-party callers (#147 skill, #148 pi extension) are merged, advertised in the README, and emit zero `Idempotency-Key` headers today. Hard-require day-one would 400 every write from both surfaces until a coordinated patch lands across three repos in lockstep — the exact "breaks any existing curl-by-hand caller" footgun the issue body flags.

To prevent the flag from quietly staying `false` forever (option a's legitimate concern), this ADR records the flip path as a binding commitment:

1. Ship C3 with `Idempotency:RequireKey = false`. Server stores and dedups whenever a key is present.
2. Land #205 (skill caller) and #206 (pi extension caller). Both inject `Idempotency-Key` on POST writes.
3. After both callers ship and a one-release deprecation window has passed, flip `Idempotency:RequireKey` default to `true`. The flip is tracked as a separate follow-up issue (to be filed when #205 + #206 close) and is a single-line config change.
4. The follow-up flip issue is a **blocker** for the next release after #205 + #206 close — not a "nice to have."

`Idempotency-Replay: true` semantics, 409-on-mismatch behaviour, tenant scoping, and storage are **independent of the flag**. C3 is functionally satisfied for opt-in callers from day one of this PR. The threat-model row flips to ✅ in this PR with a parenthetical noting "enforcement gated on `Idempotency:RequireKey`; soft-require during caller migration window."

### Middleware placement: option (a) per-endpoint `IEndpointFilter`

Endpoint filters run inside the auth / rate-limit / model-binding pipeline and have access to `EndpointFilterInvocationContext` for inspecting the bound request and endpoint metadata. Global middleware would force a method + path allowlist and lose endpoint metadata cleanly; per-handler manual checks duplicate ~80 lines across three sites. Registered via a `RequireIdempotency()` extension method on `RouteHandlerBuilder`, applied to exactly three routes — explicitly **not** to `POST /expertise/batch` (out of scope per the issue; revisiting batch requires amending this ADR).

### Store lifetime: option (a) singleton over `NpgsqlDataSource`

The issue body's "singleton" suggestion is correct, but only if the store deliberately avoids `ExpertiseDbContext` (which is scoped). Option (b) reintroduces a captive-dependency risk if a future maintainer wires the store into a scoped service. Option (c) (`IDbContextFactory<T>`) forces a repo-wide DI registration change for one consumer.

The singleton store depends only on:

- `NpgsqlDataSource` (singleton, registered via `AddNpgsqlDataSource` and shared with EF via `UseNpgsql(dataSource)`),
- `TimeProvider`,
- `IOptions<IdempotencyOptions>`,
- `ILogger<NpgsqlIdempotencyStore>`.

An architecture test asserts that `NpgsqlIdempotencyStore`'s constructor parameters are all singleton-resolvable, so future drift surfaces in CI rather than in production.

### Request hash inputs: option (a) include method, route-template, tenant, principal-sub, raw body bytes

Including `method` and `route-template` prevents key reuse across `/approve` vs `/reject` for the same `{id}`. Including `tenant` is technically redundant with the `(tenant, key)` primary key but provides defense-in-depth against a `TenantContext` population bug. Including `principal-sub` defends against two users in the same tenant racing the same key — Stripe's published behaviour. Raw bytes (not canonicalized JSON) matches IETF draft-06 §2.4 ("same bytes ⇒ same hash"), avoids a JSON-canonicalization dependency, and avoids the canonicalization step itself becoming an attack surface.

Hash is SHA-256, hex-encoded, stored as `bytea(32)`.

### Concurrency primitive: option (a) `INSERT … ON CONFLICT DO NOTHING` + `SELECT … FOR UPDATE`

Winner inserts a stub row with `status_code = NULL` and holds the row-level lock implicitly for the duration of the handler. Losers `SELECT … FOR UPDATE` on the same `(tenant, key)` and block until winner's transaction commits, then read the persisted response (or 409 on hash mismatch). One Postgres-native primitive; no advisory-lock leakage on dropped connections (lock dies with the txn); no 50/100/200 ms polling loop; `statement_timeout` already bounds worst-case waiter latency.

PgBouncer caveat: `FOR UPDATE` requires an explicit transaction held across statements. The store takes one `NpgsqlConnection` via `dataSource.OpenConnectionAsync()`, opens a transaction, performs claim → handle → commit in a single method without crossing an `IAsyncEnumerable` or other awaitable boundary that could surrender the connection. This is safe under PgBouncer transaction-pooling mode. Documented as a constraint in the store's XML doc.

### Response capture: option (a) `MemoryStream` swap inside the endpoint filter

Endpoint filters return `object?` (the `IResult`) *before* it is executed against the response stream. This is the one place where the result can be intercepted, rendered once into a size-capped buffer, persisted, and re-emitted, without the stream-wrapping fragility of pipeline-level capture or the `Results.*` evolution risk of `IResultExecutor` interception.

Persistence is registered on `HttpContext.Response.OnCompleted` so the client is not blocked on the idempotency UPDATE. Failure to persist is logged with metric `expertise_idempotency_persist_failed_total` (Prometheus) and means "next retry re-executes the handler" — the intended failure mode, since re-executing is safer than serving a phantom cached response we cannot prove was ever sent.

**Filter exception-safety and double-execution avoidance** (refined post-spike, 2026-05-19):

The stream swap must be wrapped in `try { ... } finally { ctx.HttpContext.Response.Body = original; ctx.HttpContext.Features.Set(originalBodyFeature); }`. Without this, an unhandled exception unwinds past `await next(ctx)`; `UseExceptionHandler` (registered at pipeline level, outside any endpoint filter) then writes its recovery `ProblemDetails` into the orphaned `MemoryStream` instead of the wire, hanging the client. `UseStatusCodePages` for empty 4xx/5xx has the same hazard. Restoration on exception means **500-from-thrown-exception responses are NOT captured by the filter** — the next retry re-executes the handler. This is the intended failure mode (safe over silent); `Results.Problem(statusCode: 500)` returned from the handler IS captured because it is an `IResult`, not a throw.

After manually invoking `result.ExecuteAsync(ctx)` against the buffer, the filter MUST return a sentinel no-op `IResult` (`Results.Empty` or a custom `NoOpResult`). Endpoint filters return `object?` and the framework invokes `ExecuteAsync` on the returned value **after** the filter chain unwinds; returning the original result causes it to be executed twice (once against the buffer by the filter, once against the restored real body by the framework).

**Capture mechanism:** prefer `HttpContext.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer))` over direct `Response.Body = buffer` assignment. The Features.Set pattern composes correctly with `SendFileAsync` / `StartAsync` / pipe-writer code paths (none of which the three target endpoints use today, but the future-proofed shape costs nothing). Snapshot `Response.Headers` separately into a dictionary before copy-back — hygiene and customizer header additions live there, not in the body stream.

An architecture test asserts that every route attached via `RequireIdempotency()` returns a JSON-serializable `IResult` (not `Results.Stream` / `Results.File` / SSE / chunked-transfer). Drift surfaces in CI rather than at runtime.

### Response cache policy: status-class-based (refined post-spike, 2026-05-19)

Cache **2xx** and **deterministic 4xx (400, 409, 422)**. Do **not** cache **5xx** (transient by assumption — caching locks out recovery for the 24h TTL) or **429** (owns its own `Retry-After` contract; idempotency cache would conflict).

Caching 4xx is required by the byte-equality replay contract this ADR asserts: the dedup branch in `CreateEntry` returns `Results.Conflict(ExpertiseEntryResponse.From(existing, hygiene))` — a 4xx with a hygienized DTO body whose `_hygiene.nonce` is fixed at original-request time. Re-executing the handler on replay would mint a fresh nonce and break byte-equality. Per IETF `draft-ietf-httpapi-idempotency-key-header-06` §2.5 and Stripe's published behaviour, replays return the original final response regardless of status class.

Transient-class statuses (5xx, 429) are explicitly excluded because the operational contract for those is "retry should succeed"; pinning a transient failure for 24h converts a recoverable hiccup into a hard outage from the client's perspective.

**`traceId` on replay:** the response body carries the *original* request's `traceId` (captured by `CustomizeProblemDetails` at original-request time and frozen in the buffered bytes). Operators correlating logs by `traceId` on a replay will land on the *original* request's log window. This is the intended behaviour — the causal trace is the original handler invocation — and is documented on the `Idempotency-Replay: true` response header in the OpenAPI description.

### Response body size cap: option (a) 64 KiB hard cap

The three target endpoints return a single `ExpertiseEntryResponse` (~1–2 KiB observed); 64 KiB is roughly 30× headroom. On overflow the store records `(status_code, response_body_hash)` only; on replay the server returns the cached status code with no body, plus `Idempotency-Replay: true` and a `Warning: 299 - "Idempotent response truncated; original body not replayable"` header. This is documented behaviour, not a silent degradation.

If `POST /expertise/batch` later joins the idempotency surface, the cap may need revisiting — amend this ADR rather than silently raising the constant.

### Consequences

- **Good**, because the threat-model row C3 flips to ✅ in this PR for the opt-in path, satisfying the spine of the Part D close even before #205 / #206 ship.
- **Good**, because the soft-require flip path is committed in writing (this ADR + a tracked follow-up issue), preventing the flag from becoming a permanent operator footgun.
- **Good**, because the `INSERT … ON CONFLICT` + `FOR UPDATE` concurrency model is one Postgres primitive — no advisory locks, no polling loop, no second lock-manager surface to reason about.
- **Good**, because the singleton-over-`NpgsqlDataSource` store unifies the connection pool with EF Core and avoids the scoped-DbContext captive-dependency trap; architecture test prevents future drift.
- **Good**, because endpoint-filter placement keeps the idempotency surface narrow (3 routes, not global), and the `RequireIdempotency()` extension is the single point of attach drift can be caught at.
- **Bad**, because soft-require means the C3 row is not "fully enforced ✅" until #205 + #206 + the flag-flip issue all ship. The threat-model implementation matrix gains a footnote.
- **Bad**, because the 64 KiB response cap is a magic constant that may surprise a future maintainer adding `POST /expertise/batch`; mitigation is the explicit ADR-amendment requirement.
- **Bad**, because response-stream interception (even via `IEndpointFilter` + `MemoryStream` swap) requires careful exception-path discipline (try/finally body restoration, sentinel `IResult` return, `IHttpResponseBodyFeature` swap rather than bare `Response.Body =`). The spike (3× dotnet-expert consensus, 2026-05-19) confirmed the interaction with `IResponseHygiene` (ADR-008) is clean *because hygiene applies at the DTO-mapper and `CustomizeProblemDetails` layers — not at the response-stream layer — so bytes hitting the swapped buffer are already hygienized*. The hazard is exception unwind through `UseExceptionHandler` / `UseStatusCodePages` re-executing into the orphaned buffer if restoration discipline is skipped. Integration test asserts byte-equality on replay including the frozen hygiene nonce, frozen `_hygiene` manifest, frozen `traceId`, and the delimiter-token pre-encode evidence; architecture test asserts `RequireIdempotency()` attaches only to JSON-result routes.
- **Bad**, because 500-from-thrown-exception responses are not captured (the throw unwinds past `await next(ctx)` and `UseExceptionHandler` writes to the restored real body). The next retry re-executes the handler. This is the intended failure mode (safe over silent). Handler-emitted `Results.Problem(statusCode: 500)` IS captured because it is an `IResult` rather than a throw.
- **Bad**, because fire-and-forget persistence on `OnCompleted` means a process crash between handler completion and DB write loses the row, causing the retry to re-execute. This is the intended failure mode (safe over silent), but it is a known limitation surfaced via the `expertise_idempotency_persist_failed_total` metric for ops visibility.

## Implementation notes (non-normative)

The full implementation plan — file-change list, integration test surface, GC `BackgroundService` modeled on `MigrationStateRefresher`, OpenAPI operation transformer for the `Idempotency-Key` parameter and `Idempotency-Replay` response header — lives in the #188 PR body. This ADR captures only the decisions; the mechanics may evolve under maintenance without ADR amendment, provided the decisions above hold.
