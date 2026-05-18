# Integration threat model — agent-expertise-api ↔ LLM agent harnesses

| Field | Value |
|---|---|
| Status | **Target state** — describes the architectural posture that the integration backlog (#146 / #147 / #148 / #149 / #167 / #168) is being driven toward. Not all controls are implemented yet; see [Part D control-status table](#part-d--required-server-side-controls) for live state. |
| Ground truth | MCP specification revision **2025-11-25** (canonical current revision; verified via `modelcontextprotocol.io/specification` 307 redirect). |
| Originating review | Two-pass security review of MCP integration alternatives (session 2026-05-16). |
| Last revision | 2026-05-18 — initial commit (skeleton + framing). Detailed Part A/B findings and Part C matrix pending follow-up citation-verification pass. |
| Review cadence | Re-validate Parts A/B against the latest MCP specification revision on every minor spec bump. Re-validate Part E currency notes at the same cadence. Owner: repo maintainers. |
| Companion ADR | [`adrs/007-avoid-mcp-as-llm-integration-channel.md`](../../adrs/007-avoid-mcp-as-llm-integration-channel.md) |

> **Why this document exists.** `agent-expertise-api` exposes a structured-knowledge HTTP API that is intended to be consumed by LLM-agent harnesses (pi, Claude Code, Codex CLI). The most-publicized integration channel for this class of system is the Model Context Protocol (MCP). After review, MCP was **rejected as the LLM-integration channel for this project** in favor of four alternative patterns. This document records the threat-model basis for that decision so future reviewers do not re-litigate it without fresh evidence, and so the eight server-side controls that the alternatives presume are tracked against concrete implementation issues.

---

## Contents

- [Scope and non-goals](#scope-and-non-goals)
- [The four alternative integration patterns](#the-four-alternative-integration-patterns)
- [Part A — MCP base findings (M1–M10)](#part-a--mcp-base-findings-m1m10)
- [Part B — MCP findings introduced by spec revision 2025-11-25 (M11–M16)](#part-b--mcp-findings-introduced-by-spec-revision-2025-11-25-m11m16)
- [Part C — Pattern-vs-threat matrix](#part-c--pattern-vs-threat-matrix)
- [Part D — Required server-side controls](#part-d--required-server-side-controls)
- [Part E — Currency and deprecation notes](#part-e--currency-and-deprecation-notes)
- [Open decisions](#open-decisions)
- [References](#references)

---

## Scope and non-goals

**In scope.** Architectural threat-model decisions governing how `agent-expertise-api` is reached *by an LLM agent harness*. The threats catalogued in Parts A and B are evaluated against the four alternative integration patterns (Part C) and the eight server-side controls (Part D) that the pattern equivalences are contingent on.

**Out of scope.**

- Re-evaluation of MCP itself. This artifact records the *current* decision against MCP spec revision 2025-11-25. Revisit only on a future spec revision via a fresh review, not by amending this document piecemeal.
- Threats that apply equally to direct human use of the API via curl or browser. The API's standing OIDC / Bearer auth, rate limiting (#167), and ProblemDetails sanitization (#167) handle the human-caller case; this document is concerned with the *agent-mediated* delta.
- Implementation of the controls in Part D. Each is tracked under a concrete issue (column 4 of the Part D table).

---

## The four alternative integration patterns

The four patterns evaluated as MCP alternatives. Each is described by what the LLM-agent harness sees, what code runs in the harness's process, and what trust boundary that crosses.

| # | Pattern | Tracked under | Harness coverage | Trust boundary |
|---|---|---|---|---|
| 1 | **First-party in-tree pi extension** — typed tools registered via pi's `registerTool` API, shipped in the same repo as the API itself. | #148 (depends on #146; layered under by #149) | pi only | Same repo / same release as the API → zero version drift, no third-party publisher in the trust path. |
| 2 | **Skill + curl** — agentskills.io-compliant skill with shell scripts wrapping `curl` against the API. Loadable in pi, Claude Code, and Codex CLI via each harness's skill mechanism. | #147 | pi, Claude Code, Codex CLI | API repo publishes the skill; harness loads it through its own discovery mechanism (Bearer token is the auth surface). |
| 3 | **Published OpenAPI document** — `openapi.json` published in all environments and as a release asset, consumable by any caller that supports OpenAPI tool generation. | #146 (gated by #167) | Any LLM tooling that supports OpenAPI codegen | Static document; trust attaches to the release-asset hash (Part D C8). |
| 4 | **Plain loopback HTTP** — agent loops invoke `curl` directly against the loopback-bound API with no skill or extension abstraction. | (no dedicated issue; falls out of #146 + #167) | Any harness with shell-tool access | Local-only attack surface; loopback bind is the architectural defense (Part D C1). |

**The unique-to-pi property.** A pi extension (pattern 1) can rewrite its own typed-tool return values AND observe / mutate other tools' outputs (`bash`, `read`, etc.) via the `tool_result` event middleware chain documented in `docs/extensions.md` of the pi package. This is **not** unique to in-tree placement — any pi extension (including out-of-tree, npm-distributed) has the same redaction surface. The reason this project ships an *in-tree* extension is **supply-chain trust and zero version drift**: the extension lives in the same repo and release as the API it wraps, so a schema or redaction-rule drift cannot occur, and the user's threat boundary does not expand to a third-party publisher. The redaction capability is a property of *being a pi extension*; the in-tree placement is a property of *who owns the trust*.

---

## Part A — MCP base findings (M1–M10)

> **Status:** section stubs. Full descriptions, OWASP-LLM 2025 sub-category mappings, severity ratings, and CVE/advisory anchors to be folded in during the citation-verification follow-up pass. The anchor set below is known-good and originated in the 2026-05-16 review session that produced #151.

**Known anchors to fold in:**

- **CVE-2025-6514** — `mcp-remote` OS command injection (CVSS 9.6)
- **CVE-2025-49596** — MCP Inspector RCE (CVSS 9.4 per GHSA; not yet in NVD)
- **CVE-2025-53109** / **CVE-2025-53110** — filesystem MCP path traversal / symlink
- **Invariant Labs** — tool-poisoning advisory (2025)
- **Trail of Bits** — "Jumping the line" (2025-04-21)
- **HiddenLayer** — MCP research (2025)

| Finding | One-line summary | OWASP LLM 2025 | Severity | Anchor |
|---|---|---|---|---|
| **M1** | Tool poisoning via attacker-controlled MCP server | LLM01, LLM06 | TBD | Invariant Labs 2025 |
| **M2** | RCE in client tooling (Inspector, mcp-remote) reachable from untrusted server | LLM06 | High | CVE-2025-49596, CVE-2025-6514 |
| **M3** | Filesystem MCP path traversal / symlink escape | LLM06, LLM02 | High | CVE-2025-53109, CVE-2025-53110 |
| **M4** | Command-injection via tool-argument templating | LLM06 | High | CVE-2025-6514 |
| **M5** | Pre-LLM-context tool-output tampering (no canonical chokepoint in MCP transport) | LLM01, LLM02 | TBD | Trail of Bits 2025-04-21 |
| **M6** | Cross-server tool-name shadowing / impersonation | LLM01, LLM06 | TBD | HiddenLayer 2025 |
| **M7** | Consent fatigue / silent re-authorization on tool list change | LLM06 | TBD | TBD |
| **M8** | Server-controlled prompt insertion via `prompts/list` | LLM01 | TBD | TBD |
| **M9** | Sampling escalation — server initiates LLM calls via `sampling/createMessage` | LLM06, LLM10 | TBD | TBD |
| **M10** | Conversation-state exfiltration via verbose tool descriptions | LLM02 | TBD | HiddenLayer 2025 |

*Each row will be expanded in the follow-up pass to a `### M<n>` subsection with mechanism, public-evidence link, severity rationale, and OWASP cross-reference. The matrix in [Part C](#part-c--pattern-vs-threat-matrix) references these rows by ID.*

---

## Part B — MCP findings introduced by spec revision 2025-11-25 (M11–M16)

> **Status:** section stubs. Mechanisms drawn from the 2026-05-16 review session; full prose to be folded in during the citation-verification pass.

| Finding | One-line summary | Spec anchor (2025-11-25) | OWASP LLM 2025 | Severity |
|---|---|---|---|---|
| **M11** | Elicitation message phishing — server-initiated `elicitation/create` allows in-band social engineering | `elicitation` capability | LLM01 | TBD |
| **M12** | Sampling-with-tools amplification + system-prompt poisoning | SEP-1577 (sampling+tools) | LLM01, LLM06 | TBD |
| **M13** | Streamable HTTP DNS-rebind / `Last-Event-ID` replay / session hijack | Streamable HTTP transport | LLM06, LLM07 | TBD |
| **M14** | URL-mode elicitation phishing — clickable URLs in elicitation messages | `elicitation` capability | LLM01 | TBD |
| **M15** | Client ID Metadata Documents AS-side SSRF | `oauth` / CIMD section | LLM06 | TBD |
| **M16** | Experimental `tasks` feature churn risk | SEP-1686 (experimental) | LLM06 | Info |

---

## Part C — Pattern-vs-threat matrix

> **Status:** structure committed; cell-by-cell verdicts to be filled in during the citation-verification follow-up pass. The legend below is canonical.

**Legend.**

| Symbol | Meaning |
|---|---|
| ✅ | Mitigated by architecture (the threat does not apply to this pattern) |
| 🔧 | Requires active control (see Part D row in the cell) |
| ⚠️ | Partial mitigation — residual risk; document in cell rationale |
| ❌ | Not mitigated — pattern is unsuitable if this threat is in scope |
| — | Not applicable |

**Matrix.** Rows = threats (M1–M16); columns = the four alternative patterns. A populated row in the citation-pass will read e.g. "M5 | ✅ | 🔧 C7 | 🔧 C7 | 🔧 C7" with a footnote citation.

| Threat | (1) In-tree pi extension | (2) Skill + curl | (3) OpenAPI doc | (4) Plain loopback HTTP |
|---|---|---|---|---|
| M1 | TBD | TBD | TBD | TBD |
| M2 | TBD | TBD | TBD | TBD |
| M3 | TBD | TBD | TBD | TBD |
| M4 | TBD | TBD | TBD | TBD |
| M5 | TBD | TBD | TBD | TBD |
| M6 | TBD | TBD | TBD | TBD |
| M7 | TBD | TBD | TBD | TBD |
| M8 | TBD | TBD | TBD | TBD |
| M9 | TBD | TBD | TBD | TBD |
| M10 | TBD | TBD | TBD | TBD |
| M11 | TBD | TBD | TBD | TBD |
| M12 | TBD | TBD | TBD | TBD |
| M13 | TBD | TBD | TBD | TBD |
| M14 | TBD | TBD | TBD | TBD |
| M15 | TBD | TBD | TBD | TBD |
| M16 | TBD | TBD | TBD | TBD |

**Pattern-equivalence claim — pending C7 scope decision.** Whether patterns 1 and 2 are equivalent on M5/M10 depends on the [C7 scope decision below](#open-decisions). The matrix cannot be finalized until that decision is recorded.

---

## Part D — Required server-side controls

The four alternative patterns mitigate or sidestep many MCP threats but presume that the API itself enforces the following eight controls. Each control is tracked against a concrete integration-backlog issue.

| Control | Description | Gates pattern | Tracked under | Status |
|---|---|---|---|---|
| **C1** | **Loopback bind by default.** Kestrel binds `127.0.0.1` unless the deployment explicitly opts into non-loopback exposure via env var or Helm value. | (3), (4); hardens (1), (2) | #167 (gates #146) | ❌ Not implemented |
| **C2** | **LocalTrust / OIDC auth posture.** Multi-issuer JWT bearer with policy scheme is the production auth; dev-only ApiKey / LocalDev / Hybrid modes. | All patterns | #12 (existing) | ⚠️ Partial — dev modes wired, OIDC issuers TBD |
| **C3** | **Idempotency keys** on mutating endpoints — `Idempotency-Key` header honored on POST/PATCH/DELETE; replay returns cached response. | (1), (2) — write tools | *No issue yet — gates future write-tool work, not #146–#149* | ❌ Not implemented |
| **C4** | **Sanitized `ProblemDetails`** — strip `Detail` / `Instance` in non-Development; log full detail server-side with correlation ID. | All patterns | #167 (gates #146) | ⚠️ Partial — `AddProblemDetails()` called but not customized |
| **C5** | **Rate limiting** — per-principal policies on read / write / semantic-search endpoints; health endpoints exempt. | All patterns; amplifies for (1), (2) | #167 (gates #146) | ❌ Not implemented |
| **C6** | **Agent-vs-human audit tag** — `actor_class` column on audit rows; `X-Actor-Class` header contract; default `human`. | (1), (2) | #168 (gates #147) | ❌ Not implemented |
| **C7** | **Response hygiene** — see [Open decisions](#open-decisions) for scope (Option A PII-only vs Option B PII + injection-neutralization). | (2), (3), (4); defense-in-depth for (1) | #168 (gates #147) | ❌ Not implemented; **scope decision pending** |
| **C8** | **Pinned artifacts** — `openapi.json.sha256` attached to GitHub Releases; skill / extension fetch contracts verify hashes. | (1), (2), (3) | #167 (gates #146) | ❌ Not implemented |

**Implementation progress: 0/8 controls fully implemented; 2/8 partial.**

Per-PR rule: any PR that lands a control updates the Status column in this table in the same commit, so doc and code stay aligned. The companion ADR records this rule explicitly.

---

## Part E — Currency and deprecation notes

These notes are anchored to MCP specification revision **2025-11-25** and supersede any earlier description.

- **HTTP+SSE transport (2024-11-05) is deprecated.** The current transport is **Streamable HTTP** (introduced 2025-03; ratified in the 2025-11-25 revision). Any tooling or guidance referencing `text/event-stream`-based MCP transport is out of date. This affects M13 (which is specific to Streamable HTTP) but does not introduce a parallel SSE-era threat-class.
- **`sampling.includeContext` `"thisServer"` and `"allServers"` are soft-deprecated.** Implementations are advised to prefer explicit message construction over the implicit-context modes. This is relevant to M9.
- **Dynamic Client Registration (DCR) is downgraded** in favor of **Client ID Metadata Documents** (CIMD). M15 (CIMD SSRF) is the relevant finding; the spec text now points implementations at CIMD as the default path, which means M15 has elevated relevance over an equivalent pre-2025-11-25 review.
- **Experimental `tasks` feature (SEP-1686) is in churn.** M16 captures the maintenance-burden / interface-instability risk; not a security finding per se but tracked here for completeness.

These notes will be re-verified against the spec on every minor revision per the [review cadence](#integration-threat-model--agent-expertise-api--llm-agent-harnesses) at the head of this document.

---

## Open decisions

### D1 — C7 (response hygiene) scope: Option A vs Option B

**Decision needed before #168 implementation can proceed.** This decision determines whether pattern 2 (skill+curl, #147) is *equivalent* to pattern 1 (in-tree pi extension, #148) on M5 / M10 / LLM01 for read-only operations, or whether pattern 1's pre-redaction capability is genuinely load-bearing.

| Option | Scope | Implementation cost | Effect on pattern equivalence |
|---|---|---|---|
| **A — PII-only** | Strip emails, phone numbers, embedded credentials, AWS access keys, GitHub tokens from response bodies. Standard PII-detection patterns. | Low (~50 LOC + tested detector library) | Patterns 1 and 2 equivalent on LLM02. **Pattern 1's pre-redaction remains load-bearing on LLM01** (prompt-injection via stored free-text expertise content). |
| **B — PII + injection-neutralization** *(recommended)* | Option A **plus** delimiter-wrapping of free-text fields (`<expertise_content>…</expertise_content>`), instruction-stripping heuristics (`\bignore previous\b`, role-impersonation patterns), and content-class tagging in the JSON response. | Moderate (~200 LOC + heuristic test corpus) | Patterns 1 and 2 equivalent on **both LLM01 and LLM02** for read-only ops. Pattern 1's pre-redaction becomes defense-in-depth rather than load-bearing. Closes the [path-dependence trap](#d2--path-dependence-trap-skill-pattern-becomes-default) noted below. |

**Recommendation:** **Option B**, carried from the security-review-expert pass on this sequencing. The marginal complexity is small and it closes the path-dependence trap. The decision must be recorded here before the Part C matrix can be finalized and before #168 acceptance criteria are locked.

### D2 — Path-dependence trap (skill pattern becomes default)

Once pattern 2 (#147) ships and works in three harnesses, users have no forcing function to migrate to pattern 1 (#148). If pattern 2 is the lower-mitigation path (Option A above), pattern 2 effectively becomes the project's *default* integration story. Three mitigations are stacked:

1. C7 = Option B (above) closes the asymmetry at the API.
2. Pattern 1 should offer strictly superior ergonomics (typed args, structured errors, response shaping) so migration is rational rather than security-mandated.
3. Pattern 2's README documents the asymmetry and links to this document.

Status: D2 mitigation 1 depends on D1 = Option B. Mitigations 2 and 3 are documentation/UX-side and tracked under #147 acceptance criteria.

---

## References

### Primary

- **MCP specification revision 2025-11-25** — <https://modelcontextprotocol.io/specification>
- **OWASP Top 10 for LLM Applications 2025** — <https://genai.owasp.org/llm-top-10/>

### CVE / advisory (Part A anchors)

- **CVE-2025-6514** — `mcp-remote` OS command injection (CVSS 9.6) *(citation link to be folded in)*
- **CVE-2025-49596** — MCP Inspector RCE (CVSS 9.4 per GHSA) *(citation link to be folded in)*
- **CVE-2025-53109** / **CVE-2025-53110** — filesystem MCP path traversal / symlink *(citation links to be folded in)*

### Research / disclosure (Part A anchors)

- **Invariant Labs** — Tool-poisoning advisory (2025) *(citation link to be folded in)*
- **Trail of Bits** — "Jumping the line" (2025-04-21) *(citation link to be folded in)*
- **HiddenLayer** — MCP research (2025) *(citation link to be folded in)*

### Internal

- ADR-007 — [Avoid MCP as LLM-integration channel](../../adrs/007-avoid-mcp-as-llm-integration-channel.md)
- Integration issues: #146, #147, #148, #149
- Gating issues: #167 (Part D C1/C4/C5/C8 → #146), #168 (Part D C6/C7 → #147)
