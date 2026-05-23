using System.Security.Cryptography;
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

    public static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048))
    {
        KeyId = "test-key-1"
    };

    public static string Mint(
        string tenant,
        IEnumerable<string> scopes,
        string? sub = null,
        IEnumerable<string>? groups = null,
        TimeSpan? expiresIn = null,
        string scopeClaim = "scope",
        string? issuer = null,
        string? audience = null)
    {
        var handler = new JsonWebTokenHandler();

        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub ?? "test-principal",
            [scopeClaim] = string.Join(' ', scopes)
        };

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
}
