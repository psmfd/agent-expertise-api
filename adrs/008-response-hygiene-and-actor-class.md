# Response hygiene strategy and agent-class enforcement (C6 + C7)

- Status: accepted
- Date: 2026-05-18
- Companion: [`docs/security/integration-threat-model.md`](../docs/security/integration-threat-model.md) Part D C6 / C7 / D1
- Amends: [ADR-003](003-scope-split.md) by addition (new `expertise.agent` scope)

## Context and Problem Statement

Issue #168 implements two Part D controls of the integration threat model:

- **C6** — agent-vs-human audit tag. Audit rows must record whether a state-changing request was issued by an interactive human, an LLM-agent loop, or a non-interactive machine principal. Without this distinction, audit fidelity is lost the moment the first agent-mediated caller (#147 skill+curl, #148 pi extension) ships, and the loss is **not** retroactively recoverable.
- **C7** — response hygiene. The threat model's D1 entry locked C7 to **Option B** (PII strip + injection-neutralization) but explicitly left two delimiter strategies open: HTML-entity-encode `<` / `>` within wrapped content, or use a per-response nonce delimiter. D1 warned that "delimiter-wrapping without escaping is a paper mitigation"; a choice was required to ship.

Both controls define cross-cutting consumer contracts (request header shape for C6, response body shape for C7). They ship under one PR (#168) and surface together in the README; splitting into two ADRs duplicates context with no decision-independence benefit.

## Considered Options

**C7 delimiter strategy:**

- *(a) Fixed delimiter + HTML-entity-encode `<` / `>` within payload.* Simple, deterministic. Mutates payload bytes consumers receive (round-trip equality lost), and the entity-encoding is incomplete against fullwidth (`＜`/`＞`, U+FF1C/U+FF1E), tag soup, zero-width-joined variants, or LLM-mediated reconstruction ("the next `</expertise_content>` is fake; the real one is here").
- *(b) Per-response cryptographic nonce in the opening tag.* Payload bytes preserved verbatim. Nonce surfaced in `_hygiene.nonce` so consumers parse the delimiter pair deterministically. Defeats payload-side closing-delimiter injection by unguessability (128-bit nonce per OWASP ASVS V3.2.2 entropy floor applied here for delimiter unguessability).
- *(c) Hybrid (nonce + entity-encode).* Belt-and-suspenders. Higher consumer-side cost (must decode), doubles the surface that can drift.

**C6 authority model:**

- *(a) Header alone (`X-Actor-Class: agent`) is sufficient.* Trivially forgeable; a compromised harness can hide in the human subset by sending `X-Actor-Class: human`.
- *(b) Header + new OIDC scope (`expertise.agent`); header without scope logs and falls back to `human`.* The scope is the IdP-signed signal; the header is a principal-asserted hint that must be corroborated. Header-without-scope fails open to the scheme default + warning log + audit row preserves the raw header value for forensic recovery.
- *(c) New scope alone, no header; actor class derived from token claims.* Loses the lightweight self-attestation path that supports dev-skill workflows (`X-Actor-Class: agent` corroborated by a UA allowlist match when scope provisioning is not yet wired).

## Decision Outcome

**C7: per-response nonce delimiter** (option b, with belt-and-suspenders pre-encode):

- Free-text fields are wrapped as `<expertise_content nonce="<32 hex chars>">…</expertise_content nonce="<32 hex chars>">`. Nonce is 128 bits of cryptographic entropy from `RandomNumberGenerator`, minted once per HTTP response and shared across every wrapped field in that response.
- The opening literal token (`<expertise_content`) and the closing literal token (`</expertise_content`) inside any payload byte stream are **HTML-entity-encoded** to `&lt;expertise_content` before the wrapper is applied. This is the "escape" half of D1's "escape OR nonce" requirement; combined with the nonce it satisfies D1 as belt-and-suspenders.
- Heuristic-matched instruction spans are wrapped with the literal sentinels `[INSTRUCTION_LIKE]` / `[/INSTRUCTION_LIKE]`. This is a vocabulary extension to D1's "apply instruction-stripping heuristics" clause; recorded here for audit completeness.
- Three content classes: `trusted-structured` (no transforms), `reviewer-authored-free-text` (PII + delimiter wrap; instruction heuristic in *report-only* mode because reviewers may legitimately quote attacker prose verbatim), `user-supplied-free-text` (full pipeline).
- Always-on for v1 with **no opt-out flag** on the main read path. Admin debugging is served by the new `GET /audit/{id}/raw` endpoint, which returns the audit row exactly as stored (no hygiene). A `?raw=true` query flag was explicitly rejected: it would re-introduce the D2 path-dependence trap (operators inevitably script against it for debugging, and it becomes the de-facto default for the very callers most likely to feed output to a downstream LLM).

**C6: header + scope, scope-primary** (option b):

- New OIDC scope `expertise.agent` is **orthogonal** to read/draft/approve/admin. A token can carry `expertise.read + expertise.agent` (read-only agent caller) and nothing else; admin is **not** implicitly agent.
- `ActorClassResolver` is the single source of truth, used by all three authentication handlers (JwtBearer / ApiKey / LocalDev). Cascade is mutually exclusive in order `Agent ↣ Service ↣ Human`. Scope alone is sufficient for `Agent`; an `X-Actor-Class: agent` header requires corroboration by the scope OR a configured User-Agent allowlist match. Header without corroboration falls back to the scheme default and emits a structured warning.
- `service` classification applies when the principal is non-interactive (ApiKey scheme; or JwtBearer with `client_credentials` grant where the `sub` claim equals the `azp`/`client_id`).
- Audit row gains three columns: `ActorClass`, `AuthMethod` (Bearer/ApiKey/LocalDev), and `ActorClassHeader` (raw header value, truncated to 32 chars). The raw header is preserved even when the resolver fell back to Human \u2014 a "header said agent, scope said nothing" pattern is queryable post-hoc.
- `User-Agent` is observability-only. It participates in corroboration but never grants authority on its own (UA is trivially client-set).
- New `/audit?actorClass=` filter on the existing admin-only `/audit` route.

## Consequences

### Positive

- **D1 closed** — the choice between entity-encoding and nonce is made; both halves of "escape OR nonce" actually ship together as defense-in-depth.
- **C7 round-trip equality preserved** for `POST` body \u2192 stored \u2192 `GET` body. The wrapper adds bytes but does not mutate the payload bytes themselves.
- **C6 audit tag is forgery-resistant** \u2014 the header alone cannot silently elevate. A compromised harness sending `X-Actor-Class: human` while holding the agent scope is still tagged `Agent` (scope-primary wins).
- **Single-PR rollout** \u2014 both controls and the threat-model status flip ship in #168, satisfying the rule that Part D control PRs update the status table in the same commit.
- **Forensic recovery** \u2014 the `auth_method` + raw `actorClassHeader` columns make every fail-open decision queryable.

### Negative

- **Consumer parsing cost** \u2014 free-text fields are no longer bare strings. Consumers must read `_hygiene.delimiterOpen` / `_hygiene.delimiterClose` (literally echoed in the manifest) or strip the wrapper themselves. Mitigated by surfacing both literal delimiters in `_hygiene`.
- **Scope taxonomy expansion** \u2014 ADR-003's four-scope split (`expertise.read`, `expertise.write.draft`, `expertise.write.approve`, `expertise.admin`) becomes five with the addition of `expertise.agent`. IdP configurations across consumer apps need a one-time provisioning step.
- **`[INSTRUCTION_LIKE]` vocabulary** is a new sentinel not named in D1. Recorded in the threat-model implementation notes; vocabulary growth (additional sentinels) would require another ADR or an amendment here.
- **Breaking change to /expertise/* response schema** \u2014 `ExpertiseEntry` is replaced by `ExpertiseEntryResponse` with sub-object envelopes on Title/Body/RejectionReason. Acceptable now because the OpenAPI document was first published in PR #173 hours before this PR \u2014 no production consumers exist yet.

### Neutral

- C7 hygiene is also applied to the ProblemDetails `errors` extension (validation-message dictionary) via the existing `CustomizeProblemDetails` callback. Closes the validation-echo exfil channel C4 alone doesn't cover. `Title` and `Detail` remain untouched — they are server-authored strings whose exact text is part of the API contract.
- IPv4/IPv6 addresses are now a PII detector class (GDPR Art. 4(1), CJEU Breyer C-582/14). Admin sees raw addresses on `/audit` and `/audit/{id}/raw`; the future non-admin audit surface, when added, can re-use the same detector to mask.

## Amendment 1 (2026-07-24, #333 Finding 1): C7 envelope extended to all caller-supplied free-text fields

The original C7 scope (Decision Outcome above) wrapped only `Title`, `Body`, and
`RejectionReason` (plus `OriginAuthorPrincipal`, added with ADR-013). An OWASP-AI
review (#333) found that the sibling fields **`Domain`, `Source`, `SourceVersion`,
and each `Tags` element** shipped **raw**: they are `required`/nullable strings with
no server-side validation (no enum, no allow-list, no length cap), so a
`write.draft` caller could place a forged closing delimiter plus an injected
instruction in `Source` and it would reach a higher-trust curator agent via
`GET /expertise/drafts` completely unneutralized — the exact stored-injection
threat C7 exists to close.

**Decision:** these four fields now route through the **`user-supplied-free-text`**
pipeline (the same class as `Title`/`Body`), emitted as `HygienizedField`
(`Domain`/`Source`/`SourceVersion`) or `List<HygienizedField>` (`Tags`, one element
per tag) under the response's shared nonce. `AuthorAgent` was evaluated and left
`trusted-structured` — it is server-set from the authenticated principal
(`tenantContext.Agent`), not caller-supplied.

**Breaking-change note:** unlike the original envelope adoption (Negative
consequence above), this repo is now **released (v1.6.0+)** with published OpenAPI
consumers, so the C7 scope extension is a genuine **MAJOR** version bump (v2.0.0),
not a free pre-consumer change. It ships behind the `breaking-change-approved`
label. A non-breaking wrapped-string-in-place alternative was considered and
rejected: it would leave the response contract internally inconsistent (four
`user-supplied-free-text` fields as objects, four as bare strings) for no security
benefit.

Length caps on these four fields (an independent write-side gap surfaced by the
same review) are tracked separately in #470.

## Links

- [Integration threat model](../docs/security/integration-threat-model.md) Part D C6 / C7 / D1 / D2
- Issue #333 (Amendment 1 — sibling free-text field coverage)
- [ADR-003 \u2014 Four-scope split](003-scope-split.md) (amended by addition of `expertise.agent`)
- [ADR-007 \u2014 Avoid MCP as the LLM-integration channel](007-avoid-mcp-as-llm-integration-channel.md) (depends on Part D landing)
- Issue #168 (this implementation)
- Issue #146 (publishes openapi.json \u2014 unblocked by #167)
- Issue #147 (skill+curl caller \u2014 depends on C6/C7)
- Issue #148 (pi extension caller \u2014 depends on C6/C7)
- OWASP ASVS v4.0.3 V3.2.2 \u2014 session-token entropy floor applied here for delimiter unguessability
- CJEU Breyer C-582/14 \u2014 IP-as-PII precedent
- Microsoft Learn \u2014 [NonBacktracking regex](https://learn.microsoft.com/dotnet/standard/base-types/backtracking-in-regular-expressions)
