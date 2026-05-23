using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ExpertiseApi.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ExpertiseApi.Auth;

internal class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration,
    IOptionsMonitor<AgentUserAgentOptions> agentUaOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expectedKey = configuration["Auth:ApiKey"];
        if (string.IsNullOrEmpty(expectedKey))
        {
            Logger.LogWarning("Authentication failed: {Reason}", "API key not configured on server");
            return Task.FromResult(AuthenticateResult.Fail("API key not configured"));
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            Logger.LogWarning("Authentication failed: {Reason}", "missing Authorization header");
            return Task.FromResult(AuthenticateResult.Fail("Missing Authorization header"));
        }

        var header = authHeader.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("Authentication failed: {Reason}", "invalid Authorization scheme");
            return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization scheme"));
        }

        var providedKey = header["Bearer ".Length..].Trim();
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, providedHash))
        {
            Logger.LogWarning("Authentication failed: {Reason}", "invalid API key");
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        var defaultTenant = configuration["Auth:ApiKeyDefaults:DefaultTenant"] ?? "legacy";
        var defaultPrincipal = configuration["Auth:ApiKeyDefaults:DefaultPrincipal"] ?? "api-client";

        // Issue the new draft + read scopes; LegacyWriteScope kept for one release cycle so
        // any caller still configured against the pre-rebuild scope name continues to pass
        // the WriteAccess policy. Both are normalized to expertise.write.draft via
        // JwtTenantContextEvents.ExpandScopeClosure.
        var rawScopes = new[]
        {
            AuthConstants.ReadScope,
            AuthConstants.WriteDraftScope,
            AuthConstants.LegacyWriteScope
        };
        var expandedScopes = JwtTenantContextEvents.ExpandScopeClosure(rawScopes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, defaultPrincipal),
            new("sub", defaultPrincipal)
        };
        foreach (var scope in expandedScopes)
            claims.Add(new Claim(AuthConstants.ScopeClaimType, scope));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);

        // Part D C6: ApiKey is non-interactive by construction (single shared secret, no
        // user attribution beyond the configured default principal). Default to Service.
        // The header+UA-allowlist path still permits self-attestation to Agent for the
        // developer-skill case (dev API key + matching UA), per ActorClassResolver.
        var (actorClass, rawHeader) = ActorClassResolver.Resolve(
            Context,
            principal,
            expandedScopes,
            authMethod: SchemeName,
            agentUaOptions.CurrentValue.Patterns,
            schemeDefault: ActorClass.Service,
            Logger);

        var tenantContext = new TenantContext(
            Tenant: defaultTenant,
            Principal: principal,
            Agent: null,
            Scopes: expandedScopes,
            ActorClass: actorClass,
            AuthMethod: SchemeName,
            ActorClassHeader: rawHeader);
        Context.SetTenantContext(tenantContext);

        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
