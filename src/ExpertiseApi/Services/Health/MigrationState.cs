using System.Collections.Immutable;
using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace ExpertiseApi.Services.Health;

/// <summary>
/// Singleton snapshot of the pending-migration state, written by
/// <see cref="MigrationStateRefresher"/> and read by
/// <see cref="PendingMigrationHealthCheck"/>.
///
/// Decouples the per-probe readiness signal from a per-probe DB round-trip.
/// Issue #158: <c>/health/ready</c> is <c>AllowAnonymous</c> and was previously
/// running <c>db.Database.GetPendingMigrationsAsync()</c> on every probe — a
/// trivial DoS amplifier. The state is now polled on a fixed cadence by the
/// refresher and read O(1) here.
/// </summary>
internal interface IMigrationState
{
    /// <summary>
    /// <c>true</c> until the refresher has completed at least one successful
    /// poll, then reflects the most recent observation. Conservative default:
    /// readiness reports <c>Degraded</c> during the boot window before the
    /// first poll completes, preventing a race where /health/ready briefly
    /// reports Healthy on a schema that has not yet been checked.
    /// </summary>
    bool HasPendingMigrations { get; }

    /// <summary>
    /// Operator-facing diagnostic. Used as the <c>HealthCheckResult</c>
    /// description; never reaches the wire on /health/* because
    /// <c>HealthEndpoints.WriteStatusOnlyPlainText</c> pins the body to the
    /// aggregate status.
    /// </summary>
    string Diagnostic { get; }

    /// <summary>
    /// Snapshot of the pending migration IDs the last refresh observed.
    /// Empty when <see cref="HasPendingMigrations"/> is false.
    /// </summary>
    ImmutableArray<string> Pending { get; }

    /// <summary>
    /// UTC timestamp of the last refresh attempt (success or failure).
    /// <c>null</c> until the first attempt completes.
    /// </summary>
    DateTimeOffset? LastCheckedUtc { get; }
}

/// <summary>
/// Mutable singleton backing <see cref="IMigrationState"/>. All fields are
/// <c>volatile</c> / atomic-reference assignment so health-check reads on the
/// request-pool threads observe the most recent write from the refresher
/// without locking. <see cref="Set"/> publishes the three fields with a single
/// store of an immutable record; subsequent reads see either the old triple or
/// the new triple, never a torn mix.
/// </summary>
internal sealed class MigrationState : IMigrationState
{
    // Treat the three observable fields as one immutable triple so updates
    // are torn-write-free under volatile-reference semantics. Field-level
    // volatility would allow a reader to observe `HasPendingMigrations=false`
    // alongside a stale `Diagnostic`; one reference assignment fixes that.
    private volatile Snapshot _snapshot = new(
        HasPendingMigrations: true,
        Diagnostic: "Migration state has not yet been checked (boot window).",
        Pending: ImmutableArray<string>.Empty,
        LastCheckedUtc: null);

    public bool HasPendingMigrations => _snapshot.HasPendingMigrations;
    public string Diagnostic => _snapshot.Diagnostic;
    public ImmutableArray<string> Pending => _snapshot.Pending;
    public DateTimeOffset? LastCheckedUtc => _snapshot.LastCheckedUtc;

    public void Set(bool hasPending, string diagnostic, ImmutableArray<string> pending)
    {
        _snapshot = new Snapshot(hasPending, diagnostic, pending, DateTimeOffset.UtcNow);
    }

    private sealed record Snapshot(
        bool HasPendingMigrations,
        string Diagnostic,
        ImmutableArray<string> Pending,
        DateTimeOffset? LastCheckedUtc);
}

/// <summary>
/// Polls EF Core's pending-migration list at startup and on a fixed cadence,
/// writing each observation into <see cref="MigrationState"/>. Runs as a
/// <see cref="BackgroundService"/> so unhandled exceptions in the refresh
/// loop are surfaced through the host's logging pipeline (rather than
/// disappearing as <c>TaskScheduler.UnobservedTaskException</c>) and the
/// host can apply its configured <c>BackgroundServiceExceptionBehavior</c>.
///
/// Startup behaviour: <see cref="BackgroundService.ExecuteAsync"/> is
/// dispatched on a thread-pool thread by the host runner; it does not block
/// the host startup sequence. The boot-window readiness state remains the
/// conservatively-Degraded default until the first poll lands.
///
/// Cadence: 5 minutes. The signal changes only when a deploy applies new
/// migrations or when an operator runs <c>expertise-api migrate</c> out of
/// band; sub-minute precision is not useful, and once-per-five-minutes is a
/// negligible DB load (≈ 12 queries/hour/pod against
/// <c>__EFMigrationsHistory</c>).
///
/// Failure handling: transient DB errors during a refresh (DbException,
/// InvalidOperationException, TimeoutException) do NOT overwrite the last
/// good observation. The cached state survives a DB outage, which is the
/// correct behaviour — schema lag is independent of momentary DB
/// reachability, and <see cref="AddDbContextCheck"/> already covers the
/// "DB down" signal. Process-fatal exceptions (OOM, AVE) propagate so the
/// host crashes deterministically. Any other unexpected exception is caught
/// at the loop top level, logged at Error, and the snapshot is rewritten
/// to a "refresher faulted" diagnostic so /ready reports Degraded rather
/// than serving a stale-but-Healthy signal indefinitely.
/// </summary>
internal sealed class MigrationStateRefresher : BackgroundService
{
    // Internal so tests can substitute a sub-second cadence without exposing
    // the knob in production configuration. Default chosen to be cheap enough
    // that we don't need a configuration surface. Mutable-static caveat: this
    // is a process-wide knob; tests that mutate it should serialise via xUnit
    // [Collection] to avoid cross-test races.
    internal static TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MigrationState _state;
    private readonly ILogger<MigrationStateRefresher> _logger;

    public MigrationStateRefresher(
        IServiceScopeFactory scopeFactory,
        MigrationState state,
        ILogger<MigrationStateRefresher> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial poll happens immediately so readiness state converges as
        // fast as the DB allows. The timer then paces subsequent polls.
        try
        {
            await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        // Top-level catch so an unexpected exception (e.g., NullReferenceException
        // from a bad future refactor) does not silently kill the refresher and
        // leave the snapshot pinned to a stale-but-Healthy value. Log loud, then
        // rewrite the snapshot so /health/ready reports Degraded with the fault
        // diagnostic.
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(
                ex,
                "MigrationStateRefresher loop faulted; readiness will report Degraded until process restart");
            _state.Set(
                hasPending: true,
                diagnostic: $"Migration state refresher faulted: {ex.GetType().Name}. Snapshot stale.",
                pending: ImmutableArray<string>.Empty);
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();

            var pending = await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false);
            var pendingList = pending.ToImmutableArray();

            if (pendingList.IsEmpty)
            {
                _state.Set(
                    hasPending: false,
                    diagnostic: "Schema is up to date.",
                    pending: ImmutableArray<string>.Empty);
            }
            else
            {
                _state.Set(
                    hasPending: true,
                    diagnostic: $"{pendingList.Length} pending migration(s): {string.Join(", ", pendingList)}",
                    pending: pendingList);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown; do not overwrite state.
            throw;
        }
        // Narrowed to the same set as PendingMigrationHealthCheck's original
        // catch (Npgsql/EF errors + DI misconfiguration), plus TimeoutException
        // for the Npgsql command-timeout path (which is neither DbException
        // nor InvalidOperationException). Process-fatal exceptions propagate
        // through this filter and are caught by ExecuteAsync's top-level
        // handler so the snapshot is rewritten to a fault state.
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            // Preserve the last good observation; only log. If this is the
            // first attempt, the boot-window default ("not yet checked")
            // remains, which conservatively reports Degraded on /ready.
            _logger.LogWarning(
                ex,
                "Pending-migration refresh failed; readiness state preserved from last successful poll (LastCheckedUtc={LastCheckedUtc})",
                _state.LastCheckedUtc);
        }
    }
}
