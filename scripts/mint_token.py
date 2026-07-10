#!/usr/bin/env python3
"""Offline JWT minter + JWKS builder for expertise-api (ADR-015, no IdP daemon).

Runs OFF the API host. Produces (1) a per-client RS256 keypair, (2) a public
`jwks.json` the API loads via `Auth:Oidc:Issuers:N:JwksPath` (embedded-key path —
no HTTPS discovery fetch), and (3) offline-signed tokens for LAN M2M clients.

One RSA keypair PER CLIENT so a single client can be revoked by dropping its key
from jwks.json and restarting the API, without invalidating the others.

Deps:  pip install jwcrypto
Usage:
    # 1. one-time per client: generate a keypair (writes <client>.priv.json)
    ./mint_token.py keygen --client vm-alpha
    # 2. (re)build the public JWKS from ALL *.priv.json in the key dir, then copy
    #    it to the API host and point Auth__Oidc__Issuers__2__JwksPath at it
    ./mint_token.py build-jwks --out oidc/jwks.json
    # 3. mint a token for a client
    ./mint_token.py sign --client vm-alpha \
        --tenant team-alpha --scopes read,write.draft,agent --ttl-days 7

Config contract (verified end-to-end by StaticIssuerCompoundRoleTests):
  Auth:Oidc:Issuers[N] = { Issuer (byte-exact w/ the `iss` below), Audience,
    JwksPath, ScopeClaims:["roles"], TenantSource:"CompoundRole", RoleSeparator:":" }

Security: keep the key dir (default ./keys) OFF the API host, chmod 700, encrypt
at rest. The private keys can mint tokens for any client indefinitely — they are
more sensitive than any single token. There is NO synchronous revocation: a
token is valid until its `exp`, so keep --ttl-days short and re-mint on a
schedule (ADR-015 Consequences).
"""
import argparse, json, os, time, glob, sys
from jwcrypto import jwk, jwt

KEY_DIR = os.environ.get("OIDC_KEY_DIR", "keys")
ISSUER  = os.environ.get("OIDC_ISSUER", "https://auth.lan.example/")  # byte-exact w/ Issuers[N].Issuer
AUDIENCE = os.environ.get("OIDC_AUDIENCE", "expertise-api")

# Scope shorthand -> full expertise-api scope string. CompoundRole parses each
# roles[] entry as "{tenant}:{scope}" splitting on ':' (RoleSeparator).
SCOPE_MAP = {
    "read":    "expertise.read",
    "draft":   "expertise.write.draft",
    "write.draft": "expertise.write.draft",
    "approve": "expertise.write.approve",   # do NOT grant to unattended M2M clients
    "admin":   "expertise.admin",
    "agent":   "expertise.agent",           # tags ActorClass=agent in the audit log
}

def kid_for(client): return f"{client}"

def keygen(a):
    os.makedirs(KEY_DIR, exist_ok=True)
    path = os.path.join(KEY_DIR, f"{a.client}.priv.json")
    if os.path.exists(path) and not a.force:
        sys.exit(f"{path} exists; pass --force to overwrite (rotates the client's key)")
    key = jwk.JWK.generate(kty="RSA", size=2048, kid=kid_for(a.client), use="sig", alg="RS256")
    with open(path, "w") as f: f.write(key.export_private())
    os.chmod(path, 0o600)
    print(f"wrote {path} (kid={kid_for(a.client)})")

def build_jwks(a):
    keys = []
    for p in sorted(glob.glob(os.path.join(KEY_DIR, "*.priv.json"))):
        k = jwk.JWK.from_json(open(p).read())
        keys.append(json.loads(k.export_public()))
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    with open(a.out, "w") as f: json.dump({"keys": keys}, f, indent=2)
    print(f"wrote {a.out} with {len(keys)} key(s): {[k['kid'] for k in keys]}")

def sign(a):
    path = os.path.join(KEY_DIR, f"{a.client}.priv.json")
    key = jwk.JWK.from_json(open(path).read())
    roles = []
    for s in a.scopes.split(","):
        s = s.strip()
        full = SCOPE_MAP.get(s, s)
        roles.append(f"{a.tenant}:{full}")
    now = int(time.time())
    claims = {
        "iss": ISSUER, "aud": AUDIENCE, "sub": a.client,
        "iat": now, "nbf": now, "exp": now + a.ttl_days * 86400,
        "roles": roles,                     # <-- API config: ScopeClaims:["roles"], RoleSeparator:":"
    }
    tok = jwt.JWT(header={"alg": "RS256", "kid": kid_for(a.client), "typ": "JWT"}, claims=claims)
    tok.make_signed_token(key)
    print(tok.serialize())

p = argparse.ArgumentParser(description="Offline JWT minter + JWKS builder (ADR-015).")
sub = p.add_subparsers(required=True)
g = sub.add_parser("keygen"); g.add_argument("--client", required=True); g.add_argument("--force", action="store_true"); g.set_defaults(fn=keygen)
b = sub.add_parser("build-jwks"); b.add_argument("--out", default="oidc/jwks.json"); b.set_defaults(fn=build_jwks)
s = sub.add_parser("sign"); s.add_argument("--client", required=True); s.add_argument("--tenant", required=True); s.add_argument("--scopes", required=True); s.add_argument("--ttl-days", type=int, default=7); s.set_defaults(fn=sign)
a = p.parse_args(); a.fn(a)
