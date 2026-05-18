# Avoid MCP as the LLM-integration channel for agent-expertise-api

- Status: accepted
- Date: 2026-05-18
- Companion: [`docs/security/integration-threat-model.md`](../docs/security/integration-threat-model.md)

## Context and Problem Statement

`agent-expertise-api` exposes a structured-knowledge HTTP API intended to be consumed by LLM-agent harnesses (pi, Claude Code, Codex CLI). The most-publicized integration channel for this class of system is the Model Context Protocol (MCP). A two-pass security review (session 2026-05-16) catalogued 16 threats (M1–M16) against MCP — 10 base findings anchored to public CVEs and disclosures, six additional findings introduced by the 2025-11-25 spec revision. The project needed a durable decision on whether to expose the API via MCP, via alternative patterns, or both.

## Considered Options

- **MCP server (first-party).** Implement an MCP server that wraps the API. Maximum reach (any MCP-compatible client). Inherits the M1–M16 threat surface; pattern-by-pattern mitigations are mostly client-side or require ratifying experimental spec features (M16) and active maintenance against spec churn (Part E).
- **Four-alternative stack — in-tree pi extension, skill+curl, OpenAPI doc, plain loopback HTTP.** Four narrower channels each targeting a specific harness or caller class. Each pattern's threat surface collapses to a subset of M1–M16, with the rest mitigated by architecture (no transport ambient session, no server-initiated sampling, no `elicitation` channel). Eight server-side controls (Part D of the threat model) are presumed by these patterns; each control is tracked against a concrete integration-backlog issue.
- **Both — MCP server alongside the alternatives.** Maximum reach with maximum threat surface. Doubles the maintenance burden; the security posture is no better than the weakest channel.

## Decision Outcome

Chosen option: **Four-alternative stack** (in-tree pi extension #148, skill+curl #147, OpenAPI doc #146, plain loopback HTTP — falls out of #146 + #167). MCP is **not** offered as a first-party channel.

### Why not MCP

- Two RCE-class CVEs in the MCP tooling ecosystem within six months of the spec ratification (CVE-2025-6514 in `mcp-remote`, CVE-2025-49596 in MCP Inspector) indicate that the implementation ecosystem is not yet mature enough for a security-sensitive backend to assume the threat boundary.
- Spec revision 2025-11-25 introduced six new finding classes (M11–M16) including Streamable HTTP DNS-rebind / session hijack (M13) and Client ID Metadata Documents SSRF (M15). Each requires active mitigation work that is *not* required by the four alternatives.
- Pre-LLM-context tool-output mitigation (M5/M10) has no canonical chokepoint in the MCP transport. The alternative patterns either collapse this concern (patterns 3, 4 are not LLM-mediated transports) or have a documented chokepoint (pi's `tool_result` middleware for patterns 1 / 2).
- Spec churn around the experimental `tasks` feature (SEP-1686, M16) and the soft-deprecation of `sampling.includeContext` (Part E) indicate that the spec surface a first-party MCP server would have to track is not yet stable.

### What this decision presumes

- Eight server-side controls (Part D) are implemented or in-flight under tracked issues. The decision is *contingent* on those controls landing; the threat-model document records implementation status per-control.
- The C7 (response hygiene) scope decision — Option A (PII-only) vs Option B (PII + injection-neutralization) — is recorded in the threat-model document's "Open decisions" section. Option B is the recommended choice because it closes the pattern-1 / pattern-2 asymmetry on LLM01.
- Each integration PR updates the Part D control-status table in the same commit that implements a control, so the threat-model artifact does not drift from reality.

## Consequences

### Positive

- Four narrower attack surfaces are easier to reason about and patch than one broad MCP surface.
- Each integration pattern targets a specific harness or caller class with idiomatic ergonomics for that harness.
- Avoids the maintenance burden of tracking MCP spec evolution (current revision 2025-11-25; prior 2025-03; 2024-11-05 already deprecated).

### Negative

- No out-of-the-box compatibility with MCP-only clients (some commercial IDE integrations, third-party MCP catalogs). Mitigated by the OpenAPI document (pattern 3) being consumable by any OpenAPI-aware tooling.
- Three artifact paths to maintain (pi extension + skill + OpenAPI doc) instead of one MCP server. Mitigated by hand-written Typebox schemas v1 (the maintenance cost is dominated by hand-tuned LLM-facing tool descriptions regardless of source) and by `Microsoft.Extensions.ApiDescription.Server` build-time emission of `openapi.json` (single source of truth derived from the .NET code).
- Decision is contingent on Part D controls landing. Until they do, the threat-model document records `❌ Not implemented` against each control and the integration-backlog sequencing (PR ordering) respects the per-issue gates.

### Revisit triggers

Re-open this ADR only if **all** of the following hold:

1. The MCP specification has had at least one minor revision after 2025-11-25 with no new finding classes added.
2. The MCP tooling ecosystem (`mcp-remote`, Inspector, language SDKs) has had ≥12 months with no RCE-class CVE.
3. A first-party demand exists from a harness that is MCP-only and that this project intends to support.

The conjunctive AND across these three triggers is **deliberately a high bar**. Given the disclosure rate documented in "Why not MCP" above (two RCE-class CVEs within ≈6 months of spec ratification), trigger (2) alone may take multiple years to satisfy. Trigger (3) in isolation — a first-party MCP-only demand — is **insufficient** to re-open this decision; it must coincide with (1) and (2) maturity. This asymmetry is intentional: the cost of re-litigating without ecosystem maturity exceeds the cost of declining individual integration requests.

Until all three triggers are met, the decision stands and is not subject to ad-hoc re-litigation.
