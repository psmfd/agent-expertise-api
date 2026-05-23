using Microsoft.AspNetCore.Authorization;

namespace ExpertiseApi.Auth;

internal sealed class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

internal sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ScopeAuthorizationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
        var tenantContext = httpContext?.GetTenantContext();

        // No TenantContext — auth pipeline didn't run or the principal is unmapped.
        // Leaving the requirement unsatisfied causes the authorization middleware to
        // return 403 (or 401 if the principal is unauthenticated).
        if (tenantContext is null || tenantContext.Tenant is null)
            return Task.CompletedTask;

        if (tenantContext.Scopes.Contains(requirement.Scope))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
