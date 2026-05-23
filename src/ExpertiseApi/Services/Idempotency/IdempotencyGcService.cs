using System.Data.Common;
using Microsoft.Extensions.Options;
using Prometheus;

namespace ExpertiseApi.Services.Idempotency;

/// <summary>
/// Background sweeper that deletes expired <c>idempotency_records</c> rows on a
/// fixed cadence (default 1 hour, configurable via
/// <c>Idempotency:GcInterval</c>). Modeled on
/// <c>MigrationStateRefresher</c> for consistency with the wider
/// background-service style: <see cref="PeriodicTimer"/> for cadence,
/// narrowed exception filter (issue #185), top-level catch surfacing a
/// fault-state log entry on unexpected exceptions, and OCE swallow during
/// shutdown.
/// </summary>
internal sealed class IdempotencyGcService : BackgroundService
{
    // Internal-static-mutable so integration tests can drop the cadence to
    // sub-second for the sweep test without exposing the knob in
    // production configuration (matches MigrationStateRefresher convention).
    internal static TimeSpan? OverrideInterval { get; set; }

    private static readonly Counter SweepCounter = Metrics.CreateCounter(
        "expertise_idempotency_gc_sweep_total",
        "Total number of idempotency GC sweep cycles executed.",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    private static readonly Counter RowsDeletedCounter = Metrics.CreateCounter(
        "expertise_idempotency_gc_rows_deleted_total",
        "Total number of idempotency rows deleted by the GC sweep.");

    private readonly IIdempotencyStore _store;
    private readonly IOptionsMonitor<IdempotencyOptions> _options;
    private readonly ILogger<IdempotencyGcService> _logger;

    public IdempotencyGcService(
        IIdempotencyStore store,
        IOptionsMonitor<IdempotencyOptions> options,
        ILogger<IdempotencyGcService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // First sweep deferred by one interval so a process restart loop
            // does not amplify DB load.
            var interval = OverrideInterval ?? _options.CurrentValue.GcInterval;
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        // Narrowed exception catch (issue #185 precedent). Process-fatal
        // exceptions propagate; everything else turns into a loud log and a
        // metric increment so ops sees the fault even if the process keeps
        // running.
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(
                ex,
                "IdempotencyGcService loop faulted; sweep will not run until process restart");
            SweepCounter.WithLabels("faulted").Inc();
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - _options.CurrentValue.Ttl;
            var deleted = await _store.SweepExpiredAsync(cutoff, ct).ConfigureAwait(false);
            RowsDeletedCounter.Inc(deleted);
            SweepCounter.WithLabels("ok").Inc();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Idempotency GC sweep complete: deleted {Rows} rows older than {Cutoff:O}",
                    deleted,
                    cutoff.UtcDateTime);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Narrowed to the realistic DB-side failure surface; same shape as
        // MigrationStateRefresher.RefreshOnceAsync. Process-fatal exceptions
        // bubble through to ExecuteAsync's top-level handler.
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            SweepCounter.WithLabels("error").Inc();
            _logger.LogWarning(ex, "Idempotency GC sweep failed; will retry on next cadence");
        }
    }
}
