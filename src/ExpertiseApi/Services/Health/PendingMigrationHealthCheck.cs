using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Data.Common;

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
        // Narrowed from `catch (Exception)` to satisfy CodeQL cs/catch-of-all-exceptions
        // and to align with the safer-by-default posture:
        //
        //   * OperationCanceledException propagates by exclusion — ASP.NET Core
        //     health-check framework relies on cancellation propagation to
        //     respect HealthCheckOptions.Timeout. Swallowing it here would let
        //     a runaway probe pin a DB connection past the configured budget.
        //   * Process-fatal exceptions (OOM, AVE, stack overflow) propagate so
        //     the host can crash deterministically rather than masquerading
        //     as "DB unreachable."
        //   * DbException covers Npgsql.PostgresException / NpgsqlException
        //     (connection refused, auth failed, schema missing __EFMigrationsHistory).
        //   * InvalidOperationException covers EF/DI misconfiguration
        //     (e.g., DbContext disposed, no provider configured).
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // DB unreachable / __EFMigrationsHistory missing — surface as Unhealthy so
            // the operator distinguishes "can't tell" from "behind schema". The
            // DbContext check will likely also be Unhealthy in this case, providing
            // a corroborating signal. The ex argument is consumed by the framework
            // for its diagnostics payload; the response writer
            // (WriteStatusOnlyPlainText in HealthEndpoints.cs) ensures the message
            // and stack trace never reach the wire.
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
