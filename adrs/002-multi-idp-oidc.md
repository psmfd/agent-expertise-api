# Multi-issuer OIDC via JwtBearerOptions.ValidIssuers allowlist

- Status: superseded by ADR-005
- Date: 2026-04-28

## Context and Problem Statement

The API needs to authenticate callers from two distinct identity providers without forking the codebase: Microsoft Entra ID for the business deployment and Authentik for the homelab/personal deployment. Authentik is being stood up in parallel on the same VPS as the API and will issue tokens for personal agents, CI runners, and humans operating in the homelab. The two IdPs use different audience values, different group-claim shapes, and emit different `iss` strings.

The previous static API key handler is also a dead end for production: it has no scope differentiation, no per-principal rate limiting, no audit identity, and is the structural enabler of the ASI06 vulnerability documented in ADR-003. It must be replaced with proper OIDC validation before the API is exposed to any deployment that isn't a developer's laptop.

How should a single codebase validate JWTs from multiple issuers, map issuer-specific group claims to a consistent `Tenant` value, and stay configurable without code changes when a new tenant or a new issuer is added?

## Considered Options

- Two separate API deployments, each with single-issuer `AddJwtBearer`
- Single deployment with `JwtBearerOptions.ValidIssuers` allowlist plus a per-issuer config block (audience, group-to-tenant mapping, scope claim name)
- Custom `AuthenticationHandler<>` that hand-rolls multi-issuer JWT validation with a per-issuer JWKS cache
- Federate the two IdPs through a third broker (e.g., Keycloak) so the API only sees one issuer

## Decision Outcome

Chosen option: **single deployment with `JwtBearerOptions.ValidIssuers` allowlist and a per-issuer config block.**

Concretely, `appsettings` carries an `Auth:Oidc:Issuers[]` array. Each entry specifies the issuer URL, the expected audience, the scope-claim name (Entra uses `scp`, Authentik uses `scope`), and a `GroupToTenantMapping` table. At startup the API registers `AddJwtBearer` once with `ValidIssuers` populated from the array; on each request, after the framework validates `iss`, `aud`, and signature, a custom token-validated handler looks up the matching issuer entry, walks the principal's group claims through that issuer's mapping, and attaches a `TenantContext { Tenant, Principal, Agent?, Scopes[] }` to `HttpContext.Features`.

Reasons:

- Native ASP.NET Core support. `JwtBearerOptions.ValidIssuers` was added specifically for this scenario. JWKS discovery and caching is handled by `IConfigurationManager<OpenIdConnectConfiguration>` per issuer, refreshed automatically.
- Two deployments (option 1) means two release pipelines, two test matrices, and two opportunities for drift between Entra-shaped and Authentik-shaped behaviour. The actual delta is config, not code.
- A custom handler (option 3) discards the framework's well-tested token validation, JWKS rotation handling, and clock-skew defaults. No upside.
- A federation broker (option 4) adds a runtime dependency, a second hop, and a single point of failure for both deployments. The API would gain nothing it can't get from `ValidIssuers`.

A startup guard hard-fails when `Auth:Mode != "Oidc"` and `ASPNETCORE_ENVIRONMENT != "Development"`. `LocalDev`, `ApiKey`, and `Hybrid` modes exist exclusively to keep developer workflows working during the cutover and are statically prohibited in any non-Development environment.

### Trailing-slash gotcha

Authentik's discovery document includes a trailing slash on the issuer URL (e.g. `https://auth.example.com/application/o/expertise-api/`) and Authentik tokens emit the matching `iss` claim. Entra omits the trailing slash on `https://login.microsoftonline.com/{tenant-id}/v2.0`. `iss` validation is byte-exact. The `Auth:Oidc:Issuers[*].Issuer` value must be copy-pasted from each issuer's `.well-known/openid-configuration` and never normalized.

### Consequences

- Good, because one binary, one Docker image, one Helm chart serves both deployments. Per-deployment differences are values overlays.
- Good, because adding a third issuer (e.g. a future federated partner) is a config change, not a code change.
- Good, because per-issuer `GroupToTenantMapping` keeps the IdP-specific group naming out of the application code — the application only ever sees a normalized `Tenant` string.
- Good, because `expertise.write.approve` and `expertise.admin` scopes are issued by the IdP, so revoking them is an IdP operation, not an API redeploy.
- Bad, because misconfigured `Issuer` strings (trailing-slash mismatch, wrong tenant ID, copy-paste error) fail with confusing validation errors that look like signature problems. Mitigated by integration tests that mint tokens with the configured issuer values and assert success.
- Bad, because the operator now needs to understand both Entra group object IDs and Authentik group slugs to extend the mapping. Mitigated by ADR-001's tenant-as-config-string design and clear `appsettings` examples.
- Bad, because two IdPs means twice the credential rotation and policy drift surface. Acceptable given the deployment topology — the homelab needs Authentik anyway for other services.

## Related

- ADR-001 (tenancy model) — `Tenant` is the value populated from each issuer's `GroupToTenantMapping`.
- ADR-003 (four-scope split) — scope claim names differ between Entra (`scp`) and Authentik (`scope`); the per-issuer config selects the right one.
