using System.Security.Claims;
using ExpertiseApi.Models;

namespace ExpertiseApi.Auth;

/// <summary>
/// Resolves the <see cref="ActorClass"/> for the current request from a combination of
/// OIDC scope, the <c>X-Actor-Class</c> header, the <c>User-Agent</c> header, and the
/// authentication scheme. Single source of truth for the Part D C6 trust model so all
/// three authentication handlers (JwtBearer / ApiKey / LocalDev) agree.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Trust model: scope-primary, header-corroborating, UA observability-only.</strong>
/// </para>
/// <list type="bullet">
///   <item>The <c>expertise.agent</c> OIDC scope is the cryptographically-bound signal
///   (signed by the IdP). It is the authoritative source for <see cref="ActorClass.Agent"/>.</item>
///   <item>The <c>X-Actor-Class</c> header is a principal-asserted hint. An assertion of
///   <c>agent</c> requires corroboration by EITHER the scope OR the UA allowlist; otherwise
///   it falls back to the scheme default and emits a warning.</item>
///   <item>The <c>User-Agent</c> header is observability-only (trivially client-set). It
///   participates in corroboration but cannot grant agent attribution on its own.</item>
///   <item>The three classes are mutually exclusive in order
///   <see cref="ActorClass.Agent"/> &#x21A3; <see cref="ActorClass.Service"/> &#x21A3;
///   <see cref="ActorClass.Human"/>. The first that matches wins.</item>
/// </list>
/// <para>
/// <strong>Failure mode: fail-open to scheme default with audit trail.</strong> A header
/// without corroborating scope/UA does not 401 \u2014 it logs a warning and tags as Human
/// (or Service for machine schemes). The raw header is persisted to the audit row's
/// <c>ActorClassHeader</c> column so a "header said agent, scope said nothing" pattern is
/// queryable post-hoc. Fail-closed would penalize misconfigured legitimate callers and
/// could be weaponized by stripping one's own scope to DoS the agent path.
/// </para>
/// <para>
/// See <c>docs/security/integration-threat-model.md</c> Part D C6 and ADR-008.
/// </para>
/// </remarks>
internal static class ActorClassResolver
{
    /// <summary>
    /// Inspects request headers + scope set + scheme to decide actor classification.
    /// </summary>
    /// <param name="http">Current HTTP context (header and connection access).</param>
    /// <param name="principal">Authenticated principal (subject / azp claim access).</param>
    /// <param name="scopes">Expanded scope closure for the principal.</param>
    /// <param name="authMethod">Authentication scheme name (Bearer / ApiKey / LocalDev).</param>
    /// <param name="agentUserAgents">Configured UA allowlist patterns.</param>
    /// <param name="schemeDefault">Default actor class for this scheme when no signal is present.</param>
    /// <param name="logger">Logger for fail-open warnings.</param>
    /// <returns>Resolved actor class and the raw <c>X-Actor-Class</c> header value (null if absent).</returns>
    public static (ActorClass ActorClass, string? RawHeader) Resolve(
        HttpContext http,
        ClaimsPrincipal principal,
        IReadOnlySet<string> scopes,
        string authMethod,
        IReadOnlyCollection<string> agentUserAgents,
        ActorClass schemeDefault,
        ILogger logger)
    {
        var rawHeader = http.Request.Headers.TryGetValue(AuthConstants.Headers.ActorClass, out var headerValues)
            ? headerValues.ToString()
            : null;
        var userAgent = http.Request.Headers.UserAgent.ToString();

        var hasAgentScope = scopes.Contains(AuthConstants.AgentScope);
        var uaCorroborates = MatchesAllowlist(userAgent, agentUserAgents);

        // Mutually-exclusive cascade in order Agent ↣ Service ↣ Human.

        // 1. Agent: requires expertise.agent scope OR (header=agent AND UA corroborates).
        //    Scope alone is sufficient (token-bound signal); header alone is not.
        if (hasAgentScope)
        {
            // Scope wins regardless of any X-Actor-Class: human downgrade attempt from a
            // compromised harness trying to hide its activity in the human-tagged subset.
            return (ActorClass.Agent, TruncateHeader(rawHeader));
        }

        if (string.Equals(rawHeader, "agent", StringComparison.OrdinalIgnoreCase))
        {
            if (uaCorroborates)
                return (ActorClass.Agent, TruncateHeader(rawHeader));

            // Header asserts agent but neither scope nor UA corroborates: fail-open to
            // scheme default + warning. Raw header is persisted on the audit row for triage.
            logger.LogWarning(
                "X-Actor-Class: agent from principal {Principal} (scheme {AuthMethod}) " +
                "rejected: no {AgentScope} scope and User-Agent {UserAgent} not in allowlist. " +
                "Classifying as {SchemeDefault}. (Part D C6)",
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst("sub")?.Value
                    ?? principal.Identity?.Name
                    ?? "<anonymous>",
                authMethod,
                AuthConstants.AgentScope,
                userAgent,
                schemeDefault);
        }

        // 2. Service: explicit header or scheme default.
        if (string.Equals(rawHeader, "service", StringComparison.OrdinalIgnoreCase))
            return (ActorClass.Service, TruncateHeader(rawHeader));
        if (schemeDefault == ActorClass.Service)
            return (ActorClass.Service, TruncateHeader(rawHeader));

        // 3. Human: explicit header or fall-through default.
        return (ActorClass.Human, TruncateHeader(rawHeader));
    }

    private static bool MatchesAllowlist(string ua, IReadOnlyCollection<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(ua) || patterns.Count == 0)
            return false;

        foreach (var pattern in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (prefix.Length > 0 && ua.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (ua.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Truncate the raw header to 32 chars before persisting to the audit row. The header
    /// is principal-asserted text; we cap it to bound log/DB cardinality without losing
    /// the values we actually care about (<c>agent</c>/<c>human</c>/<c>service</c>).
    /// </summary>
    private static string? TruncateHeader(string? raw)
    {
        if (raw is null)
            return null;
        return raw.Length <= 32 ? raw : raw[..32];
    }
}
