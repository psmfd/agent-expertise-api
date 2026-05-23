# Per-issuer JwtBearer schemes behind a Bearer policy scheme

- Status: accepted
- Date: 2026-04-30
- Supersedes: ADR-002

## Context and Problem Statement

ADR-002 chose multi-issuer OIDC via a single `AddJwtBearer` registration with `JwtBearerOptions.ValidIssuers` populated from `Auth:Oidc:Issuers[]`. During implementation we discovered that a flat `ValidIssuers` / `ValidAudiences` configuration permits cross-issuer audience contamination: a token validly signed by Authentik whose `aud` happens to equal the Entra audience would pass validation, because the framework checks `aud` against the union of allowed audiences without pinning each audience to its issuing IdP.

We need audience validation to be pinned per-issuer. An Authentik token carrying an Entra audience must fail.

## Considered Options

- Keep flat `ValidIssuers` and accept the cross-issuer audience risk
- Register one named `JwtBearer` scheme per issuer; use a `Bearer` policy scheme to route tokens by their `iss` claim to the matching named scheme
- Wrap the framework's validator with a custom post-validation handler that re-checks the issuer/audience pair

## Decision Outcome

Chosen option: **per-issuer named `JwtBearer` scheme behind a `Bearer` policy scheme.**

For each entry in `Auth:Oidc:Issuers[]` the API registers a named `JwtBearer` scheme with that issuer's specific `Authority`, `ValidAudiences`, and `MetadataAddress`. A `Bearer` policy scheme is registered as the default authentication scheme; its `ForwardDefaultSelector` reads the incoming token's `iss` claim (unvalidated) and forwards to the matching named scheme, which then performs the full signature, issuer, audience, and lifetime validation. Endpoints declare `RequireAuthorization()` against `Bearer`.

This preserves ADR-002's underlying decision (multi-IdP OIDC, single deployment, native framework auth, config-driven issuer list) and adds per-issuer audience pinning.

Reasons:

- Cross-issuer audience contamination is structurally impossible — each named scheme validates its own narrow audience list.
- Adding a new issuer remains a single entry in `Auth:Oidc:Issuers[]`. The registration is loop-driven.
- The framework's JWKS rotation, clock-skew defaults, and signature validation are unchanged.
- Custom validation wrapping (option 3) discards more framework behaviour than is necessary; a policy-scheme indirection is a single line of code per issuer.

## Consequences

- Good, because audience contamination across IdPs cannot happen.
- Good, because ADR-002's operational benefits (one binary, one image, config-driven additions) are preserved.
- Bad, because the policy-scheme indirection adds one routing decision in the auth pipeline (`SelectScheme` in `AuthExtensions.cs`). The routing reads the unvalidated `iss` claim — safe because the forwarded scheme then validates the signature, but worth understanding when reading the auth code.
- Bad, because the trailing-slash gotcha from ADR-002 still applies — issuer strings are byte-exact in both routing and validation. Mitigated by integration tests that mint tokens with the configured issuer values.

## Related

- ADR-002 (multi-IdP OIDC) — superseded by this ADR.
- ADR-001 (tenancy model).
- ADR-003 (four-scope split).
