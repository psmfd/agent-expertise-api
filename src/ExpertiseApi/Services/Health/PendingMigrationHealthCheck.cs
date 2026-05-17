using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ExpertiseApi.Services.Health;

/// <summary>
/// Reports <see cref="HealthStatus.Degraded"/> when EF Core has pending migrations.
///
/// Degraded — not Unhealthy — because:
///   * The app process is functional; queries against tables that exist will succeed.
///   * The operator's action is to run `expertise-api migrate` (Tier-1 issue #144).
///
/// The ASP.NET Core framework default maps <c>Degraded → 200 OK</c>; that default
/// is explicitly overridden in <c>HealthEndpoints.MapHealthEndpoints</c> so /ready
/// returns 503 on Degraded, giving k8s readiness / SCM start-orchestration the
/// same observable signal as Unhealthy without muddling the diagnostic meaning
/// of the two states. If this check is ever wired into a new endpoint, the
/// caller must apply the same ResultStatusCodes override or the Degraded state
/// will be silently swallowed.
///
/// Surfaced separately from <c>AddDbContextCheck</c> (which only proves the
/// server is reachable) because a reachable-but-out-of-date schema is a
/// distinct operational state from "DB down".
/// </summary>
internal sealed class PendingMigrationHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PendingMigrationHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // DbContext is scoped; resolving via IServiceScopeFactory creates a
        // throwaway scope per probe invocation so we don't pin a connection
        // for the lifetime of the singleton check instance. `await using`
        // honours IAsyncDisposable on the scope so any async-disposable
        // scoped services (including DbContext) tear down asynchronously.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();

        IEnumerable<string> pending;
        try
        {
            pending = await db.Database.GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Health check must classify, not crash, on any DB error
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // DB unreachable / __EFMigrationsHistory missing — surface as Unhealthy so
            // the operator distinguishes "can't tell" from "behind schema". The
            // DbContext check will likely also be Unhealthy in this case, providing
            // a corroborating signal.
            return HealthCheckResult.Unhealthy(
                "Unable to query pending migrations (DB unreachable or migration history missing).",
                ex);
        }

        var pendingList = pending.ToList();
        if (pendingList.Count == 0)
        {
            return HealthCheckResult.Healthy("Schema is up to date.");
        }

        return HealthCheckResult.Degraded(
            $"{pendingList.Count} pending migration(s): {string.Join(", ", pendingList)}");
    }
}
