using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ExpertiseApi.Tests.Infrastructure;

/// <summary>
/// Mints test JWTs signed with an in-memory RSA key. Used by integration tests that exercise
/// the OIDC path without spinning up a real IdP. The matching public key is registered with
/// the API via <see cref="JwtApiFactory"/>.
/// </summary>
public static class JwtTokenMinter
{
    public const string TestIssuer = "https://test-issuer.local/";
    public const string TestAudience = "test-audience";
    public const string TestSchemeName = "TestIssuer";

    // ADR-014 static-LAN issuer: CompoundRole tenancy with scopes carried in a
    // `roles` array claim (TenantSource=CompoundRole, ScopeClaims=["roles"],
    // RoleSeparator=":"). Distinct issuer URL so the policy scheme routes by `iss`;
    // shares SigningKey so JwtApiFactory can inject one OIDC config per scheme.
    public const string CompoundRoleIssuer = "https://static-issuer.local/";
    public const string CompoundRoleSchemeName = "StaticLan";

    public static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048))
    {
        KeyId = "test-key-1"
    };

    /// <summary>
    /// Mints a static-LAN CompoundRole token exactly as <c>scripts/mint_token.py</c> emits:
    /// each scope becomes a <c>roles</c> array entry of the form <c>{tenant}:{scope}</c>.
    /// <paramref name="scopes"/> are fully-qualified scope strings (e.g.
    /// <see cref="AuthConstants.WriteDraftScope"/>), matching the confirmed ADR-014 contract.
    /// </summary>
    public static string MintCompoundRole(
        string tenant,
        IEnumerable<string> scopes,
        string? sub = null,
        TimeSpan? expiresIn = null)
    {
        var handler = new JsonWebTokenHandler();

        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub ?? "static-lan-client",
            ["roles"] = scopes.Select(s => $"{tenant}:{s}").ToArray()
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = CompoundRoleIssuer,
            Audience = TestAudience,
            Claims = claims,
            Expires = DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        };

        return handler.CreateToken(descriptor);
    }

    public static string Mint(
        string tenant,
        IEnumerable<string> scopes,
        string? sub = null,
        IEnumerable<string>? groups = null,
        TimeSpan? expiresIn = null,
        string scopeClaim = "scope",
        string? issuer = null,
        string? audience = null,
        string? azp = null)
    {
        var handler = new JsonWebTokenHandler();

        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub ?? "test-principal",
            [scopeClaim] = string.Join(' ', scopes)
        };

        // client_credentials-shaped tokens: azp carries the client id. Pass
        // sub == azp to have ActorClassResolver classify the caller as Service.
        if (azp is not null)
            claims["azp"] = azp;

        if (groups is not null)
            claims["groups"] = groups.ToArray();

        // Insert the tenant via group mapping (matches JwtApiFactory's GroupToTenantMapping)
        if (!claims.ContainsKey("groups"))
            claims["groups"] = new[] { $"group-{tenant}" };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? TestIssuer,
            Audience = audience ?? TestAudience,
            Claims = claims,
            Expires = DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        };

        return handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Public-only JWKS JSON for <see cref="SigningKey"/> — the shape an embedded-key
    /// (ADR-015) issuer's <c>jwks.json</c> carries. Tests write this to a temp file and point
    /// <c>Auth:Oidc:Issuers:N:JwksPath</c> at it so the real production embedded-key branch in
    /// <c>AuthExtensions.RegisterJwtBearer</c> loads and validates against it.
    /// </summary>
    public static string StaticJwksJson()
    {
        var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(SigningKey);
        var doc = new
        {
            keys = new[]
            {
                new { kty = "RSA", kid = jwk.Kid, use = "sig", alg = SecurityAlgorithms.RsaSha256, n = jwk.N, e = jwk.E }
            }
        };
        return JsonSerializer.Serialize(doc);
    }
}
