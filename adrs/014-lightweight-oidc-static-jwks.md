# Static JWKS + offline-minted JWTs as the lightweight OIDC issuer for A2/LAN consumption

- Status: superseded by [ADR-015](015-embedded-static-jwks.md) (metadata-delivery mechanism only — the lightweight-issuer decision over a full IdP stands; ADR-015 flips Option C → Option D)
- Date: 2026-07-08
- Companion: [ADR-003](003-scope-split.md) (scope semantics that gate what a minted token can do), [ADR-005](005-multi-issuer-jwt-policy-scheme.md) (multi-issuer JWT plumbing reused verbatim), [ADR-008](008-response-hygiene-and-actor-class.md) (actor-class resolution from the token), [ADR-011](011-deployment-artifact-format.md) (the A2 native-service install this pairs with), [ADR-013](013-aggregator-upsync.md) (distinct: hub↔spoke up-sync keeps `client_credentials` on a shared IdP — NOT in scope here)
- Tracking issue: [#382](https://github.com/psmfd/agent-expertise-api/issues/382)
- Surfaced by: 2026-07-08 A2-networked-consumers design session (multi-agent fan-out across `expertise-api-owner`, `dotnet-expert`, `general-purpose`). User challenge — "are you taking the lightest-weight approach?" — drove the pivot away from a full IdP.

## Context and Problem Statement

An A2 native-service instance (ADR-011) is to be consumed over the LAN by a small, fixed roster (~3–5) of machine-to-machine (M2M) agent/VM clients. Outside `Development`, `AuthExtensions.EnforceModeGuard` hard-fails any `Auth:Mode` other than `Oidc`, and the install wrapper defaults `ASPNETCORE_ENVIRONMENT=Production`. There is no shared-secret path: a networked instance MUST validate OIDC JWTs.

The naive reading is "stand up an identity provider." But the API only ever **validates** tokens — it never initiates an interactive flow; every client is M2M. The question this ADR answers: what is the lightest-weight issuer that produces tokens this codebase accepts **unmodified**, without over-provisioning a full IdP whose flow engine, admin UI, worker, and database run almost entirely unused just to mint machine tokens?

## Considered Options

- **A. Full IdP as sole issuer (Authentik).** Real `client_credentials` endpoint, admin UI, revocation, rotation. Footprint post-2025.10 (Redis removed): `server` + `worker` + Postgres, ~2–3 GB RAM, 3–4 containers, its own upgrade/patch cadence. Everything the API needs (custom `{tenant}:{scope}` claims via Python Scope Mappings) is expressible, but ~99% of the product is unused for pure token minting.
- **B. Headless OAuth2/OIDC server (Ory Hydra).** No user management; `client_credentials` skips login/consent. Custom claims for client_credentials require a **token-hook webhook** (an extra small service). Footprint: Hydra (~5 MB image, ~0.3–1 GB RAM) + a database (reuse the existing Postgres 17; SQLite is dev-only) + the token-hook service. Real `/oauth2/revoke`.
- **C. Static discovery doc + static JWKS, no IdP daemon; JWTs minted offline.** The API's per-issuer `JwtBearer` scheme (ADR-005) uses `options.Authority`, which performs standard `.well-known/openid-configuration` + `jwks_uri` discovery — those can be **flat JSON files** served over HTTPS by the reverse proxy already required for TLS. Per-client RS256 keypairs; tokens minted by a ~40-line script carrying `iss`/`aud`/`exp` and a `roles` claim of `{tenant}:{scope}` values. No token/introspection/userinfo endpoint is ever touched. Footprint delta over the API alone: **zero new daemons** (reuses the proxy + internal CA needed for TLS regardless). Verified against the code and the test minter (`JwtTokenMinter`): no `sub`/`jti`/`nonce`/`azp`/`typ` claim is required.
- **D. (variant of C) Embed `TokenValidationParameters.IssuerSigningKeys` directly; drop the HTTP discovery fetch entirely.** Lightest possible — no HTTPS metadata endpoint at all — but the current code sets `Authority`, so this requires a source change in `AuthExtensions`. Deferred; see Sub-decisions.

## Decision Outcome

**Chosen: Option C**, for the small-fixed-roster LAN case, with **Option B (Hydra) documented as the escalation path** and Option A rejected as disproportionate at this scale.

Chosen because the trade-offs C accepts are cheap in exactly this context — a fixed, small client roster on a trusted LAN — while the marginal infrastructure cost is effectively zero: the only running pieces (a reverse proxy + an internal CA) are already required to terminate TLS for the API regardless, versus the ≈2–3 GB / 3–4 dedicated containers a full IdP (Option A) would add purely to mint tokens. Decisively, **it requires no change to the authentication code**: `Auth:Mode=Oidc` with a single `Issuers[]` entry (`TenantSource: CompoundRole`, `RoleSeparator: ":"`) pointed at the static issuer URL satisfies `EnforceOidcIssuersGuard` and validates offline-minted tokens as-is. The existing scope semantics (ADR-003) and actor-class resolution (ADR-008) apply unchanged: a minted token carrying `expertise.agent` is tagged `ActorClass=agent`; a token minted with only `{tenant}:expertise.read` cannot write. The supply-chain control is the existing authorization layer, not new issuer logic.

The non-negotiable operational contract: **offline minting has no synchronous revocation.** The only levers are token `exp`, a per-client `kid` (drop one client's public key from the JWKS to revoke just that client on the next JWKS refresh), and a re-mint job. This is acceptable for ~3–5 LAN-trusted clients where a revocation event is rare and "kill the key, re-mint" is an acceptable manual runbook — and it is the explicit trigger for escalating to Option B.

### Sub-decisions

**TLS via an internal ACME CA (step-ca), not Let's Encrypt staging.** `RequireHttpsMetadata=true` is never overridden in the codebase, so the static discovery/JWKS host MUST be HTTPS, and LAN clients consume the API over HTTPS. LE (staging or prod) can't satisfy HTTP-01/TLS-ALPN-01 on a NAT'd home LAN, and DNS-01 demands a public domain + a DNS-API token for a purely-internal service. step-ca provides the identical "untrusted-by-default root you must distribute" property with no public dependency and no rate limits, and HTTP-01/TLS-ALPN-01 work LAN-to-LAN. `Caddy` (zero-config challenge, also serves the static JWKS files) over Traefik, since the DNS-01 rationale that favored Traefik evaporates once LE is dropped. LE-staging remains a documented one-line swap for a future public-exposure rehearsal.

**Per-client RS256 keypair, shared public JWKS.** One private key per client so a single client is revocable by removing its public key (`kid`) from `jwks.json` without invalidating the others. The private keys are the crown jewels of this design — more sensitive than any single token — and MUST live off the API host, permission-restricted, encrypted at rest.

**Custom claim = `roles` array of `{tenant}:{scope}` strings.** Since we mint the token, we control the claim shape; `Auth:Oidc:Issuers[].ScopeClaims` is pinned to whatever a decoded real token shows before config is committed (the one item the design fan-out could not confirm from docs: `roles` vs `scp`, and fully-qualified `expertise.write.draft` vs short `write.draft`). `expertise.write.approve` is never minted for an unattended M2M client — this is ADR-003's general prohibition on granting `write.approve` to long-lived non-interactive service principals (ADR-013's spoke credential is a prior *application* of that same rule, not its origin).

**Option D deferred, gated on a code change + its own follow-up.** Setting `IssuerSigningKeys` directly (no HTTP discovery, no HTTPS-metadata requirement, no proxy-served JWKS) is lighter still but touches `AuthExtensions`. Not adopted now to keep this a **zero-code-change** deployment pattern; revisit if the HTTPS-metadata requirement becomes friction (e.g. an air-gapped instance where even an internal CA is unwanted).

**Relationship to ADR-013.** The aggregator up-sync path deliberately keeps `client_credentials` on a shared IdP (hub↔spoke federation, `sub==azp` ⇒ `ActorClass=Service`). This ADR governs the **local-consumption** issuer only — the `Auth:Oidc:Issuers[]` schemes that *validate inbound* bearer tokens — and does not alter ADR-013. The two are orthogonal: a spoke's *outbound* sync credential is acquired via the `Sync:TokenEndpoint`/`Sync:ClientId` token-acquisition client, **not** an `Issuers[]` entry, so a spoke that also serves local LAN agents has exactly **one** `Issuers[]` entry (the static issuer). It is a **hub** instance that legitimately carries **two** independent `Issuers[]` entries — the shared-IdP entry that validates incoming spoke pushes, plus a static-issuer entry for its own local LAN agents.

## Consequences

- **Good** — zero net new daemons: the only running extras (reverse proxy + internal CA) are already required for TLS, so the issuer's marginal footprint is ≈0, versus the ≈2–3 GB of dedicated IdP infrastructure Option A would add.
- **Good** — no source change: the ADR-005 multi-issuer plumbing, ADR-003 scopes, and ADR-008 actor-class all apply to minted tokens unmodified.
- **Good** — no IdP database, no IdP upgrade/patch cadence, no admin UI attack surface.
- **Good** — per-client `kid` gives granular (if asynchronous) revocation; audience pinning bounds blast radius to this one API.
- **Bad** — no synchronous revocation; token `exp` is the incident-response window, so short TTL + a re-mint job is load-bearing, not optional.
- **Bad** — the offline signing key is a high-value standing secret whose custody discipline is entirely on the operator; its compromise mints arbitrary tokens for any client until the key is rolled.
- **Bad** — manual rotation runbook (keygen → rebuild JWKS with kid overlap → re-mint → redeploy) that a real OP would automate; tolerable only while the roster is small.
- **Bad** — the internal-CA root must be trusted in two places (API host OS store + every consumer VM); a missed trust step fails closed at TLS.
- **Revisit if** — client count or churn outgrows manual rotation, a genuine synchronous "kill this token now" requirement appears, or clients stop being fully LAN-trusted; escalate to Option B (Hydra), which is config-only on the API side (repoint `Issuers[].Issuer` at Hydra's public URL, no auth-code change).

## Implementation notes (non-normative)

- Reference artifacts drafted alongside this ADR (scratchpad, pre-repo): `docker-compose.yml` (Caddy + step-ca), `Caddyfile`, `mint_token.py` (keygen / build-jwks / sign), `RUNBOOK.md` (10-step end-to-end), and the Hydra-variant compose sketch for the Option-B escalation.
- Static discovery doc minimal fields (.NET parses regardless of content-type): `{"issuer": "<Authority, byte-exact>", "jwks_uri": "<.../jwks.json>"}`.
- JWKS refresh: `Microsoft.IdentityModel` defaults are 12 h automatic / 5 min forced-refresh cooldown; on a `kid` miss the triggering request 401s and the next picks up new keys — keep old+new `kid` in the JWKS for one `exp` window for zero-401 rotation.
- API-side config surfaces if this lands in-repo: `appsettings.json` `_comment` example for a static issuer; README §Archetype A2 gains a "networked consumers" subsection; `install.sh` secrets stub gains `AllowedHosts` + `ForwardedHeaders__KnownNetworks__0` guidance (both required for a reverse-proxied LAN bind, per the same design session).
- Doc-sync on landing: this ADR + README §Archetype A2 + `appsettings.json` example + (if a helper ships in-tree) the mint script under `scripts/`. No agent/rule catalog surfaces are touched.
