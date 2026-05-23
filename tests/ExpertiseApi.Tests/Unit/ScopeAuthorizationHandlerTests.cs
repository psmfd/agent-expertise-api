using System.Security.Claims;
using ExpertiseApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace ExpertiseApi.Tests.Unit;

public class ScopeAuthorizationHandlerTests
{
    [Fact]
    public async Task Succeeds_WhenScopeIsPresent()
    {
        var ctx = HttpCtxWithTenant("test", AuthConstants.ReadScope);
        var (handler, authCtx) = HandlerFor(ctx, AuthConstants.ReadScope);

        await handler.HandleAsync(authCtx);

        authCtx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_WhenScopeIsAbsent()
    {
        var ctx = HttpCtxWithTenant("test", AuthConstants.ReadScope);
        var (handler, authCtx) = HandlerFor(ctx, AuthConstants.AdminScope);

        await handler.HandleAsync(authCtx);

        authCtx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Fails_WhenTenantIsNull()
    {
        var ctx = HttpCtxWithTenant(null, AuthConstants.AdminScope);
        var (handler, authCtx) = HandlerFor(ctx, AuthConstants.ReadScope);

        await handler.HandleAsync(authCtx);

        authCtx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Fails_WhenTenantContextIsAbsent()
    {
        var ctx = new DefaultHttpContext();
        var (handler, authCtx) = HandlerFor(ctx, AuthConstants.ReadScope);

        await handler.HandleAsync(authCtx);

        authCtx.HasSucceeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthConstants.AdminScope, AuthConstants.ReadScope)]
    [InlineData(AuthConstants.AdminScope, AuthConstants.WriteDraftScope)]
    [InlineData(AuthConstants.AdminScope, AuthConstants.WriteApproveScope)]
    [InlineData(AuthConstants.AdminScope, AuthConstants.AdminScope)]
    [InlineData(AuthConstants.WriteApproveScope, AuthConstants.WriteDraftScope)]
    [InlineData(AuthConstants.WriteApproveScope, AuthConstants.ReadScope)]
    [InlineData(AuthConstants.WriteDraftScope, AuthConstants.ReadScope)]
    public async Task ExpandedClosure_SatisfiesImpliedScope(string heldScope, string requirementScope)
    {
        // Closure expansion is the responsibility of the issuer (handler/event); these tests
        // verify that once a token's scopes are expanded by JwtTenantContextEvents, the
        // ScopeAuthorizationHandler honors the expansion.
        var expanded = JwtTenantContextEvents.ExpandScopeClosure(new[] { heldScope });
        var ctx = HttpCtxWithTenant("test", [.. expanded]);
        var (handler, authCtx) = HandlerFor(ctx, requirementScope);

        await handler.HandleAsync(authCtx);

        authCtx.HasSucceeded.Should().BeTrue();
    }

    private static DefaultHttpContext HttpCtxWithTenant(string? tenant, params string[] scopes)
    {
        var ctx = new DefaultHttpContext();
        ctx.SetTenantContext(new TenantContext(
            Tenant: tenant,
            Principal: new ClaimsPrincipal(new ClaimsIdentity("Test")),
            Agent: null,
            Scopes: scopes.ToHashSet()));
        return ctx;
    }

    private static (ScopeAuthorizationHandler handler, AuthorizationHandlerContext ctx) HandlerFor(
        HttpContext httpContext,
        string requirementScope)
    {
        var requirement = new ScopeRequirement(requirementScope);
        var authCtx = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            httpContext);
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return (new ScopeAuthorizationHandler(accessor), authCtx);
    }
}
