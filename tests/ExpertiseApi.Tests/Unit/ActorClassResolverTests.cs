using System.Security.Claims;
using ExpertiseApi.Auth;
using ExpertiseApi.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// Truth-table coverage of <see cref="ActorClassResolver.Resolve"/>. Pins the Part D C6
/// trust model (scope-primary, header-corroborating, UA observability-only) so a future
/// refactor can't silently invert it.
/// </summary>
public class ActorClassResolverTests
{
    private static readonly string[] DefaultAllowlist = ["pi-coding-agent", "claude-code", "codex-cli"];
    private static readonly string[] EmptyAllowlist = [];

    private static (ActorClass cls, string? hdr) Resolve(
        string? headerValue = null,
        string? userAgent = null,
        bool hasAgentScope = false,
        string? sub = "test-principal",
        IReadOnlyCollection<string>? allowlist = null,
        string authMethod = "Bearer",
        ActorClass schemeDefault = ActorClass.Human)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (headerValue is not null)
            http.Request.Headers[AuthConstants.Headers.ActorClass] = headerValue;
        if (userAgent is not null)
            http.Request.Headers.UserAgent = userAgent;

        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (hasAgentScope)
            scopes.Add(AuthConstants.AgentScope);

        var claims = new List<Claim>();
        if (sub is not null) claims.Add(new Claim("sub", sub));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        return ActorClassResolver.Resolve(
            http, principal, scopes, authMethod,
            allowlist ?? DefaultAllowlist, schemeDefault, NullLogger.Instance);
    }

    // ---- Scope is authoritative for Agent ----

    [Fact]
    public void ScopeAlone_NoHeader_NoUa_ReturnsAgent()
    {
        var (cls, _) = Resolve(hasAgentScope: true);
        cls.Should().Be(ActorClass.Agent);
    }

    [Fact]
    public void ScopeWithMatchingUaAndAgentHeader_ReturnsAgent()
    {
        var (cls, hdr) = Resolve(hasAgentScope: true, headerValue: "agent", userAgent: "pi-coding-agent/0.5.0");
        cls.Should().Be(ActorClass.Agent);
        hdr.Should().Be("agent");
    }

    [Fact]
    public void ScopeWithHumanDowngradeHeader_StillReturnsAgent()
    {
        // Scope-primary: a compromised agent harness cannot self-downgrade to Human
        // by sending X-Actor-Class: human while holding the token-bound agent scope.
        var (cls, hdr) = Resolve(hasAgentScope: true, headerValue: "human", userAgent: "pi-coding-agent/0.5.0");
        cls.Should().Be(ActorClass.Agent);
        hdr.Should().Be("human");
    }

    // ---- Header alone is insufficient ----

    [Fact]
    public void AgentHeader_NoScope_NoUaMatch_FallsBackToHuman()
    {
        var (cls, hdr) = Resolve(headerValue: "agent", userAgent: "Mozilla/5.0");
        cls.Should().Be(ActorClass.Human);
        hdr.Should().Be("agent");
    }

    [Fact]
    public void AgentHeader_NoScope_WithUaMatch_ReturnsAgent()
    {
        // UA corroborates the header even without scope — supports the dev-skill case
        // (API key + matching UA, no scope provisioning required for local development).
        var (cls, hdr) = Resolve(headerValue: "agent", userAgent: "pi-coding-agent/0.5.0");
        cls.Should().Be(ActorClass.Agent);
        hdr.Should().Be("agent");
    }

    [Fact]
    public void AgentHeader_NoScope_WithEmptyAllowlist_FallsBackToHuman()
    {
        var (cls, _) = Resolve(headerValue: "agent", userAgent: "pi-coding-agent/0.5.0", allowlist: EmptyAllowlist);
        cls.Should().Be(ActorClass.Human);
    }

    [Fact]
    public void AgentHeader_NoScope_PrefixWildcardMatches_ReturnsAgent()
    {
        var (cls, _) = Resolve(headerValue: "agent", userAgent: "curl/8.7.1", allowlist: new[] { "curl/*" });
        cls.Should().Be(ActorClass.Agent);
    }

    [Fact]
    public void AgentHeader_NoScope_PrefixWildcardDoesNotMatchMiddle_FallsBackToHuman()
    {
        var (cls, _) = Resolve(headerValue: "agent", userAgent: "Mozilla curl/8 derp", allowlist: new[] { "curl/*" });
        cls.Should().Be(ActorClass.Human);
    }

    // ---- Service classification ----

    [Fact]
    public void NoHeader_ApiKeyScheme_DefaultService_ReturnsService()
    {
        var (cls, _) = Resolve(authMethod: "ApiKey", schemeDefault: ActorClass.Service);
        cls.Should().Be(ActorClass.Service);
    }

    [Fact]
    public void ExplicitServiceHeader_ReturnsService()
    {
        var (cls, hdr) = Resolve(headerValue: "service");
        cls.Should().Be(ActorClass.Service);
        hdr.Should().Be("service");
    }

    [Fact]
    public void ServiceHeader_OverridesHumanDefault()
    {
        var (cls, _) = Resolve(headerValue: "service", schemeDefault: ActorClass.Human);
        cls.Should().Be(ActorClass.Service);
    }

    // ---- Human is the default ----

    [Fact]
    public void NoHeader_NoScope_HumanDefault_ReturnsHuman()
    {
        var (cls, hdr) = Resolve();
        cls.Should().Be(ActorClass.Human);
        hdr.Should().BeNull();
    }

    [Fact]
    public void ExplicitHumanHeader_ReturnsHuman()
    {
        var (cls, _) = Resolve(headerValue: "human");
        cls.Should().Be(ActorClass.Human);
    }

    [Fact]
    public void BogusHeader_NoScope_FallsBackToSchemeDefault()
    {
        // Unknown header values are not treated as an "agent" assertion; they're noise.
        var (cls, hdr) = Resolve(headerValue: "bogus-class");
        cls.Should().Be(ActorClass.Human);
        hdr.Should().Be("bogus-class");
    }

    // ---- Header truncation ----

    [Fact]
    public void OverlongHeader_IsTruncatedTo32Chars()
    {
        var longValue = new string('x', 200);
        var (_, hdr) = Resolve(headerValue: longValue);
        hdr.Should().HaveLength(32);
    }

    // ---- Case-insensitivity ----

    [Theory]
    [InlineData("AGENT")]
    [InlineData("Agent")]
    [InlineData("aGeNt")]
    public void AgentHeader_IsCaseInsensitive(string headerValue)
    {
        var (cls, _) = Resolve(headerValue: headerValue, userAgent: "pi-coding-agent/0.5.0");
        cls.Should().Be(ActorClass.Agent);
    }

    [Fact]
    public void UaMatch_IsCaseInsensitive()
    {
        var (cls, _) = Resolve(headerValue: "agent", userAgent: "PI-CODING-AGENT/0.5.0");
        cls.Should().Be(ActorClass.Agent);
    }
}
