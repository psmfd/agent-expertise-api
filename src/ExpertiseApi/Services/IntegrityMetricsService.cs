using System.Data.Common;
using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace ExpertiseApi.Services;

/// <summary>
/// Surfaces ADR-020 verification state as scrapeable gauges from the API process. The
/// <c>verify</c> CLI is a short-lived process whose own Prometheus counters die
/// unscraped, so it persists its outcome to the database
/// (<c>IntegrityVerificationState</c>, <c>AuditCheckpoints</c>) and this poller
/// re-exposes it: checkpoint staleness bounds the ADR-020 detection gap, last-verify
/// age catches a dead timer/CronJob, and last-verify mismatches is the alerting
/// signal. Gauges read <c>-1</c> until the first checkpoint / verify run exists.
/// Follows the <see cref="Idempotency.IdempotencyGcService"/> background-service
/// style: <see cref="PeriodicTimer"/> cadence, narrowed exception filter, OCE swallow
/// during shutdown.
/// </summary>
internal sealed class IntegrityMetricsService : BackgroundService
{
    // Internal-static-mutable so integration tests can drop the cadence without a
    // production knob (IdempotencyGcService convention).
    internal static TimeSpan? OverrideInterval { get; set; }

    private static readonly Gauge CheckpointAgeGauge = Metrics.CreateGauge(
        "expertise_integrity_checkpoint_age_seconds",
        "Seconds since the newest audit checkpoint was sealed (ADR-020 detection-gap bound); -1 until the first checkpoint exists.");

    private static readonly Gauge LastVerifyAgeGauge = Metrics.CreateGauge(
        "expertise_integrity_last_verify_age_seconds",
        "Seconds since the last integrity verification run finished; -1 until the first run.");

    private static readonly Gauge LastVerifyMismatchesGauge = Metrics.CreateGauge(
        "expertise_integrity_last_verify_mismatches",
        "Total mismatches reported by the last integrity verification run; -1 until the first run.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IntegrityMetricsService> _logger;

    public IntegrityMetricsService(IServiceScopeFactory scopeFactory, ILogger<IntegrityMetricsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        CheckpointAgeGauge.Set(-1);
        LastVerifyAgeGauge.Set(-1);
        LastVerifyMismatchesGauge.Set(-1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var interval = OverrideInterval ?? TimeSpan.FromMinutes(5);
            using var timer = new PeriodicTimer(interval);
            // Refresh once at startup so gauges are meaningful from the first scrape,
            // then on cadence.
            await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "IntegrityMetricsService loop faulted; gauges frozen until process restart");
        }
    }

    // internal (not private) so tests can drive a single refresh deterministically
    // without racing the PeriodicTimer loop (IdempotencyGcService convention, #354).
    internal async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            var now = DateTime.UtcNow;

            var newestCheckpoint = await db.AuditCheckpoints
                .AsNoTracking()
                .OrderByDescending(c => c.Id)
                .Select(c => (DateTime?)c.CreatedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            CheckpointAgeGauge.Set(newestCheckpoint is null ? -1 : (now - newestCheckpoint.Value).TotalSeconds);

            var state = await db.IntegrityVerificationStates
                .AsNoTracking()
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            LastVerifyAgeGauge.Set(state is null ? -1 : (now - state.LastRunAt).TotalSeconds);
            LastVerifyMismatchesGauge.Set(state?.MismatchCount ?? -1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex, "Integrity metrics refresh failed; will retry on next cadence");
        }
    }
}
