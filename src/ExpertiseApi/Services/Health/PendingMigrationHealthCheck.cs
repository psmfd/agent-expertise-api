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
///
/// <para>
/// Per-probe cost: O(1) read from the singleton <see cref="IMigrationState"/>
/// snapshot. The actual EF Core query runs in
/// <see cref="MigrationStateRefresher"/> on a fixed cadence, decoupling the
/// readiness signal from per-probe DB load. Issue #158.
/// </para>
/// </summary>
internal sealed class PendingMigrationHealthCheck : IHealthCheck
{
    private readonly IMigrationState _state;

    public PendingMigrationHealthCheck(IMigrationState state)
    {
        _state = state;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_state.HasPendingMigrations)
        {
            return Task.FromResult(HealthCheckResult.Degraded(_state.Diagnostic));
        }
        return Task.FromResult(HealthCheckResult.Healthy(_state.Diagnostic));
    }
}
