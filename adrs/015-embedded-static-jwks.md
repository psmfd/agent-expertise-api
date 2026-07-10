# Embed static signing keys directly (drop the HTTPS discovery fetch) for the LAN OIDC issuer

- Status: accepted
- Date: 2026-07-10
- Supersedes: [ADR-014](014-lightweight-oidc-static-jwks.md) — specifically its **Option-C** metadata-delivery mechanism (static discovery doc + HTTPS `jwks_uri` fetch). ADR-014's rejection of a full IdP (Options A/B) and its token/scope/actor-class model are unchanged and carried forward by reference.
- Companion: [ADR-003](003-scope-split.md), [ADR-005](005-multi-issuer-jwt-policy-scheme.md), [ADR-008](008-response-hygiene-and-actor-class.md), [ADR-011](011-deployment-artifact-format.md)
- Tracking issue: [#382](https://github.com/psmfd/agent-expertise-api/issues/382)
- Surfaced by: 2026-07-10 A2-integration research (multi-agent fan-out across `expertise-api-owner`, `dotnet-expert`, `docker-expert`) into how the ADR-014 pattern composes with the A2 native-service install surface.

## Context and Problem Statement

ADR-014 adopted a lightweight OIDC issuer for A2/LAN M2M consumption and chose **Option C**: serve a static `.well-known/openid-configuration` + `jwks.json` over HTTPS and let the API's `JwtBearer` scheme fetch them via `options.Authority`. Option C was chosen expressly because "it requires no change to the authentication code." Option D (embed the signing keys directly) was described and **deferred** with an explicit trigger: "revisit if the HTTPS-metadata requirement becomes friction."

Researching how Option C actually lands on the A2 native-service surface (systemd `--user` on Linux, launchd on macOS) surfaced that the HTTPS-metadata fetch imposes a real, per-platform **internal-CA-trust burden on the API host**:

- The API must trust the internal ACME CA (e.g. step-ca) root to fetch the issuer's HTTPS metadata. .NET delegates that trust decision entirely to the OS, with **no shared mechanism** across platforms: Linux uses OpenSSL (`update-ca-certificates` / `SSL_CERT_FILE`), while macOS validates exclusively through the Apple Security framework/Keychain (`security add-trusted-cert`) — and .NET 10 removed OpenSSL interop on macOS entirely, so there is no env-var path there at all.
- The fetch is **lazy** (first authenticated request), so a missing trust step surfaces as a **500 on the first protected request while `/health` and `/metrics` stay green** — a boot-green/broken-on-first-call blind spot that the existing `EnforceOidcIssuersGuard` does not cover.

The "no code change" saving of Option C is therefore paid back, with interest, as divergent per-OS operational plumbing and a latent runtime failure mode. The Option-D trigger has fired.

## Considered Options

- **Keep Option C (status quo).** No code change, keeps zero-downtime `kid` rotation (publish a new `jwks.json`, picked up within the refresh window). Cost: the per-platform CA-trust burden and lazy-fetch 500 blind spot above; requires a live HTTPS metadata endpoint.
- **Adopt Option D — embed `IssuerSigningKeys` from a local JWKS file.** The API loads `jwks.json` from a local path at startup and validates against it directly; no `Authority`, no discovery fetch, no `jwks_uri`, no metadata TLS handshake. Cost: a small `AuthExtensions` source change (now made) and loss of zero-downtime rotation (rotation becomes edit-`jwks.json` + restart).

## Decision Outcome

Adopt **Option D**. Per-issuer configuration gains `JwksPath`: when set, `AuthExtensions.RegisterJwtBearer` loads the file's keys into `TokenValidationParameters.IssuerSigningKeys`, preloads `options.Configuration` with the pinned issuer, nulls the `ConfigurationManager`, and leaves `Authority` unset — so the issuer never contacts the network. `Issuer` remains required (it pins `ValidIssuer` / must byte-match `iss`). Cloud issuers (Entra, Authentik) with no `JwksPath` keep the Option-C discovery path unchanged — the choice is per-issuer, not global.

A `JwksPath` that is missing, unreadable, malformed, or empty **fails startup closed** (`LoadStaticSigningKeys`, run at service-configuration time) — the embedded-key analogue of `EnforceOidcIssuersGuard`, closing the lazy-fetch blind spot: an embedded-key instance cannot boot green with unusable keys.

## Consequences

- **Good** — the internal-CA-trust requirement on the API host is **eliminated**: there is no metadata TLS handshake to trust, so the divergent Linux/macOS trust plumbing (and .NET 10's macOS-OpenSSL removal) stops mattering for the auth path. CA trust remains only on the **consumer** side (each VM trusts the proxy's cert to reach the API over TLS).
- **Good** — no HTTPS `.well-known`/`jwks.json` endpoint to stand up; the reverse proxy's only remaining job is terminating TLS on the API's own endpoint.
- **Good** — failure is **fail-closed at startup**, not a first-request 500 with a green `/health`.
- **Good** — the token/scope/actor-class model, ADR-005 multi-issuer plumbing, and the offline-mint workflow are otherwise unchanged; the regression guard exercises the real embedded-key path.
- **Bad** — a small auth-code change was required (now landed + reviewed) — the exact thing Option C optimised against.
- **Bad** — loss of zero-downtime `kid` rotation: rotating a key is now edit-`jwks.json` + service restart, not "publish a new static file." Tolerable while the roster is small (the same regime ADR-014 already assumed for manual rotation).
- **Bad** — the `jwks.json` file must be distributed to the API's config directory with integrity (a tampered **public** JWKS can only deny service, not forge tokens — a lower-blast-radius problem than a compromised CA root).
- **Revisit if** — the roster/rotation churn outgrows restart-based key rolls, or a synchronous revocation requirement appears; escalate to Option B (Ory Hydra) per ADR-014, which remains config-only on the API side.

## Implementation notes (non-normative)

- Config surface: `Auth:Oidc:Issuers:N:JwksPath` (local path). The shipped `appsettings.json` `LanStatic` issuer (index 2) carries a `JwksPath` placeholder instead of relying on discovery.
- `scripts/mint_token.py`: `keygen` (per-client RS256 keypair) / `build-jwks` (public `jwks.json`) / `sign` (offline token). The discovery-doc emission Option C needed is dropped.
- `deploy/lan-static-oidc/`: a **native** `Caddyfile` (API inbound TLS only) + a containerized step-ca `compose.yaml` + a runbook whose CA-trust steps are **consumer-side**.
- Doc-sync on landing: this ADR; ADR-014 status → superseded; `appsettings.json` example; README §Archetype A2; README directory tree.
