# LAN static-OIDC edge for an A2 native-service install (ADR-015)

Stand up an internal TLS edge so a small, fixed roster of LAN M2M clients can consume
an A2 native-service expertise-api instance, using **offline-minted JWTs validated
against a local embedded JWKS** — no IdP daemon, no HTTPS metadata fetch.

## Topology

```
LAN consumer VM ──HTTPS──►  Caddy (native OS service)  ──HTTP loopback──►  expertise-api
  (offline-minted JWT)       terminates TLS, proxies                       (A2 native service,
                             127.0.0.1:8080                                  install.sh)
                                    ▲ cert via ACME
                             step-ca (container, compose.yaml)

  mint_token.py ── OFF the API host ──►  jwks.json (copied to the API host)
                                         + client tokens (handed to each VM)
```

Key ADR-015 property: **the API host trusts no CA** — it never fetches issuer metadata.
Only the **consumer VMs** trust step-ca's root (to reach the API's HTTPS endpoint).

## Prerequisites

- Docker or Podman (for step-ca). With Podman: `DOCKER_HOST` + `TESTCONTAINERS_RYUK_DISABLED` per the testing guide.
- Caddy installed natively: `apt install caddy` (Debian 13) or `brew install caddy` (macOS).
- Python 3 + `pip install jwcrypto` on the (off-host) minting machine.
- The API already installed as an A2 service via `scripts/install.sh`.

## Steps

### 1. Start the internal CA

```bash
cd deploy/lan-static-oidc
docker compose up -d           # or: podman compose up -d
```

### 2. Export step-ca's root

```bash
docker compose exec step-ca step ca root /home/step/root_ca.crt
docker compose cp step-ca:/home/step/root_ca.crt ./step-root.crt
sudo cp ./step-root.crt /etc/caddy/step-root.crt      # where the Caddyfile references it
```

### 3. Run Caddy natively

Put `Caddyfile` at `/etc/caddy/Caddyfile` (edit the hostname), then run Caddy as an OS
service (`systemctl enable --now caddy` on Debian; `brew services start caddy` on macOS).
Native Caddy reaches the API at `127.0.0.1:8080` and step-ca at `localhost:9000`.

### 4. Trust step-ca on each CONSUMER VM (not the API host)

This is the only CA-trust step, and it lives on the clients:

- **Debian 13 (Trixie):**
  ```bash
  sudo cp step-root.crt /usr/local/share/ca-certificates/expertise-lan-ca.crt
  sudo update-ca-certificates
  ```
- **macOS:**
  ```bash
  sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain step-root.crt
  ```

### 5. Mint keys + build the JWKS (off the API host)

```bash
export OIDC_ISSUER="https://auth.lan.example/"   # any stable string; must byte-match the API's Issuer + the iss claim
./scripts/mint_token.py keygen --client vm-alpha
./scripts/mint_token.py build-jwks --out oidc/jwks.json
```

Copy `oidc/jwks.json` to the API host's config dir — `~/.config/expertise-api/jwks.json`
on Linux, `~/Library/Application Support/expertise-api/jwks.json` on macOS.

### 6. Configure the API service

Install (or re-run) with a LAN bind, and add the issuer to `secrets.env`:

```bash
# Edge co-located (this runbook's topology — Caddy proxies to loopback):
scripts/install.sh                       # default bind 127.0.0.1:8080 is correct
# Edge on a DIFFERENT host only: bind the LAN address and accept plaintext
# between edge and API (requires the explicit override):
# scripts/install.sh --bind 0.0.0.0:8080 --allow-plaintext-bind
```
```sh
# Service secrets.env — ~/.config/expertise-api/secrets.env on Linux,
# ~/Library/Application Support/expertise-api/secrets.env on macOS:
Auth__Oidc__Issuers__2__Issuer=https://auth.lan.example/
Auth__Oidc__Issuers__2__JwksPath=<config-dir-above>/jwks.json   # absolute path, no ~ expansion
AllowedHosts=expertise.lan.example
ForwardedHeaders__KnownNetworks__0=<proxy-subnet-cidr>
```

Index `2` is the shipped `LanStatic` issuer (`appsettings.json`); `Name`/`Audience`/
`ScopeClaims`/`TenantSource`/`RoleSeparator` are already correct. A blank/unreadable
`JwksPath` (or a still-`<TODO…>` `Issuer`) **fails startup closed** — by design.

### 7. Restart and verify

```bash
scripts/expertise-apictl restart

# Mint a client token and verify a PROTECTED endpoint — NOT just /health, which stays
# green regardless of auth config:
TOKEN=$(./scripts/mint_token.py sign --client vm-alpha --tenant team-alpha --scopes read --ttl-days 7)
curl -sS https://expertise.lan.example/expertise -H "Authorization: Bearer $TOKEN" | head
```

A `200` (or a valid empty list) confirms end-to-end auth. A `401`/`500` means the token,
issuer config, or JWKS is wrong — check the service logs.

## Rotation & revocation (ADR-015 tradeoff)

There is **no synchronous revocation** — a token is valid until its `exp`. Keep `--ttl-days`
short and re-mint on a schedule. To roll a key or revoke a client: re-run `keygen` /
`build-jwks`, copy the new `jwks.json`, and **restart the API** (`expertise-apictl restart`).
Embedded JWKS is loaded at startup, so rotation is not zero-downtime — acceptable for the
small-roster LAN case this targets. If churn outgrows restarts or you need "kill this token
now," escalate to Ory Hydra (ADR-014 Option B), config-only on the API side.
