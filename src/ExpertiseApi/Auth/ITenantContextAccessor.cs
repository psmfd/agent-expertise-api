namespace ExpertiseApi.Auth;

/// <summary>
/// Provides the tenant identifier for the current execution scope. Drives the
/// <c>HasQueryFilter</c> on <see cref="Data.ExpertiseDbContext"/> as defense-in-depth
/// against accidental cross-tenant reads.
/// <para>
/// Returns <c>null</c> when no HTTP request scope is active (CLI commands,
/// design-time DbContext factory, repository calls outside the request pipeline).
/// In that case the global query filter short-circuits and applies no tenant
/// predicate — the explicit <c>Where</c> clauses constructed inside
/// <see cref="Data.IExpertiseRepository"/> remain the primary safeguard.
/// </para>
/// </summary>
internal interface ITenantContextAccessor
{
    string? Tenant { get; }
}
