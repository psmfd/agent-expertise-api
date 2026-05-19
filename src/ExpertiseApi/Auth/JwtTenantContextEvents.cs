using System.Security.Claims;
using ExpertiseApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExpertiseApi.Auth;

/// <summary>
/// Builds <see cref="TenantContext"/> after JWT validation. Shared across all configured
/// JwtBearer schemes — each scheme is registered with its <see cref="OidcIssuerOptions"/>
/// in the scheme's <see cref="JwtBearerEvents.OnTokenValidated"/>.
/// </summary>
internal static class JwtTenantContextEvents
{
    public static Task BuildTenantContext(TokenValidatedContext ctx, OidcIssuerOptions issuer)
    {
        if (ctx.Principal is null)
        {
            ctx.Fail("Principal missing after token validation.");
            return Task.CompletedTask;
        }

        var rawScopes = ExtractRawScopes(ctx.Principal, issuer);

        string? tenant;
        HashSet<string> scopes;

        if (issuer.TenantSource == TenantSource.CompoundRole)
        {
            (tenant, scopes) = ParseCompoundRoles(rawScopes, issuer.RoleSeparator);
        }
        else
        {
            tenant = MapTenantFromGroups(ctx.Principal, issuer);
            scopes = [.. rawScopes];
        }

        var expanded = ExpandScopeClosure(scopes);

        var agent = ctx.Principal.FindFirst("appid")?.Value
                    ?? ctx.Principal.FindFirst("azp")?.Value
                    ?? ctx.Principal.FindFirst("client_id")?.Value;

        // Part D C6: resolve actor class from scope + header + UA. Bearer scheme defaults
        // to Human (interactive user) unless the principal is a client_credentials machine
        // (azp == client_id but no `sub` user claim), in which case Service is the right
        // default. ActorClassResolver still honours an X-Actor-Class: agent header as long
        // as expertise.agent scope OR a UA-allowlist match corroborates it.
        var hasUserSubject = !string.IsNullOrEmpty(ctx.Principal.FindFirst("sub")?.Value)
                             && !string.Equals(ctx.Principal.FindFirst("sub")?.Value, agent, StringComparison.Ordinal);
        var schemeDefault = hasUserSubject ? ActorClass.Human : ActorClass.Service;

        var agentUaOptions = ctx.HttpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<AgentUserAgentOptions>>().CurrentValue;
        var resolverLogger = ctx.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ActorClassResolver).FullName!);

        var (actorClass, rawHeader) = ActorClassResolver.Resolve(
            ctx.HttpContext,
            ctx.Principal,
            expanded,
            authMethod: AuthExtensions.BearerScheme,
            agentUaOptions.Patterns,
            schemeDefault,
            resolverLogger);

        var tenantContext = new TenantContext(
            Tenant: tenant,
            Principal: ctx.Principal,
            Agent: agent,
            Scopes: expanded,
            ActorClass: actorClass,
            AuthMethod: AuthExtensions.BearerScheme,
            ActorClassHeader: rawHeader);

        ctx.HttpContext.SetTenantContext(tenantContext);
        return Task.CompletedTask;
    }

    private static IEnumerable<string> ExtractRawScopes(ClaimsPrincipal principal, OidcIssuerOptions issuer)
    {
        foreach (var claim in issuer.ScopeClaims)
        {
            // Entra `scp` is space-separated; Authentik `scope` is space-separated.
            // Entra `roles` is repeated claim per value (one per array entry).
            var values = principal.FindAll(claim)
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v));

            foreach (var value in values)
            {
                foreach (var scope in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return scope;
            }
        }
    }

    internal static (string? Tenant, HashSet<string> Scopes) ParseCompoundRoles(
        IEnumerable<string> rawScopes,
        string separator)
    {
        string? tenant = null;
        var scopes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in rawScopes)
        {
            var sepIndex = raw.IndexOf(separator, StringComparison.Ordinal);
            if (sepIndex < 0)
            {
                // No separator — treat as a tenant-less scope. Skip; compound role tokens
                // must always carry tenant context.
                continue;
            }

            var roleTenant = raw[..sepIndex];
            var roleScope = raw[(sepIndex + separator.Length)..];

            if (string.IsNullOrWhiteSpace(roleTenant) || string.IsNullOrWhiteSpace(roleScope))
                continue;

            // First role wins on tenant. A token claiming multiple tenants is rejected
            // (policy decision: machine credentials are scoped to a single tenant).
            if (tenant is null)
                tenant = roleTenant;
            else if (!string.Equals(tenant, roleTenant, StringComparison.Ordinal))
                continue;

            scopes.Add(roleScope);
        }

        return (tenant, scopes);
    }

    private static string? MapTenantFromGroups(ClaimsPrincipal principal, OidcIssuerOptions issuer)
    {
        return principal.FindAll(issuer.GroupClaim)
            .Select(c => issuer.GroupToTenantMapping.TryGetValue(c.Value, out var tenant) ? tenant : null)
            .FirstOrDefault(t => t is not null);
    }

    /// <summary>
    /// Expands the scope set per the implication hierarchy: admin ⊇ approve ⊇ draft ⊇ read.
    /// Also normalizes the legacy <see cref="AuthConstants.LegacyWriteScope"/> to
    /// <see cref="AuthConstants.WriteDraftScope"/>.
    /// </summary>
    public static HashSet<string> ExpandScopeClosure(IEnumerable<string> scopes)
    {
        var result = new HashSet<string>(scopes, StringComparer.Ordinal);

        if (result.Remove(AuthConstants.LegacyWriteScope))
            result.Add(AuthConstants.WriteDraftScope);

        if (result.Contains(AuthConstants.AdminScope))
        {
            result.Add(AuthConstants.WriteApproveScope);
            result.Add(AuthConstants.WriteDraftScope);
            result.Add(AuthConstants.ReadScope);
        }
        else if (result.Contains(AuthConstants.WriteApproveScope))
        {
            result.Add(AuthConstants.WriteDraftScope);
            result.Add(AuthConstants.ReadScope);
        }
        else if (result.Contains(AuthConstants.WriteDraftScope))
        {
            result.Add(AuthConstants.ReadScope);
        }

        return result;
    }
}
