namespace ExpertiseApi.Auth;

/// <summary>
/// Resolves <see cref="ITenantContextAccessor.Tenant"/> from the current
/// <see cref="HttpContext"/>'s <see cref="TenantContext"/>. Returns <c>null</c>
/// when no <see cref="HttpContext"/> is available — the EF global query filter
/// treats that as "no filter" (CLI / design-time) and falls back to the explicit
/// repository <c>Where</c> clauses for safety.
/// </summary>
internal sealed class HttpTenantContextAccessor(IHttpContextAccessor httpContextAccessor)
    : ITenantContextAccessor
{
    public string? Tenant =>
        httpContextAccessor.HttpContext?.GetTenantContext()?.Tenant;
}

/// <summary>
/// Always returns <c>null</c>. Used by <see cref="Data.DesignTimeDbContextFactory"/>
/// so <c>dotnet ef</c> tooling can construct a DbContext without an HTTP scope.
/// </summary>
internal sealed class NoOpTenantContextAccessor : ITenantContextAccessor
{
    public string? Tenant => null;
}
