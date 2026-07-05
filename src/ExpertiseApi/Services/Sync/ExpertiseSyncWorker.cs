using System.Data.Common;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;

namespace ExpertiseApi.Services.Sync;

/// <summary>
/// Spoke-side up-sync worker (ADR-013). On each tick, pages newly Approved entries in
/// the <c>shared</c> tenant past the persisted <see cref="SyncState"/> cursor and POSTs
/// them to the hub's existing <c>/expertise/batch</c> endpoint as the configured
/// client_credentials principal.
///
/// Delivery is AT-LEAST-ONCE by design: <c>/batch</c> sits outside Idempotency-Key
/// scope (ADR-010), so replays are absorbed by the hub's tenant-scoped dedup and come
/// back as <c>Duplicate</c> — which this worker counts as success. The cursor advances
/// only when every item in a page lands as Created/Duplicate/Rejected; any transient
/// <c>Failed</c> item (or HTTP/token failure) aborts the tick without advancing, so the
/// whole page retries next cadence. <c>Rejected</c> is a permanent per-item validation
/// verdict — retrying cannot fix it, so it is logged loudly, counted, and skipped.
///
/// Sync failure must never affect API availability: the worker degrades to logs +
/// Prometheus counters and retries next tick (same two-tier narrowed-catch shape as
/// <see cref="Idempotency.IdempotencyGcService"/> / <c>MigrationStateRefresher</c>).
/// The DbContext is scope-resolved per tick (MigrationStateRefresher precedent) for
/// the <see cref="SyncState"/> cursor; entry reads go through
/// <see cref="IExpertiseRepository.ListSharedApprovedUpdatedAfterAsync"/>.
/// </summary>
internal sealed class ExpertiseSyncWorker : BackgroundService
{
    internal const string HttpClientName = "hub-sync";

    // Test hook, matching IdempotencyGcService / MigrationStateRefresher convention.
    internal static TimeSpan? OverrideInterval { get; set; }

    private static readonly Counter CycleCounter = Metrics.CreateCounter(
        "expertise_sync_cycles_total",
        "Total up-sync cycles executed.",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    private static readonly Counter ItemCounter = Metrics.CreateCounter(
        "expertise_sync_items_total",
        "Total entries processed by up-sync, by hub verdict.",
        new CounterConfiguration { LabelNames = new[] { "status" } });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HubTokenClient _tokenClient;
    private readonly IOptionsMonitor<SyncOptions> _options;
    private readonly ILogger<ExpertiseSyncWorker> _logger;

    public ExpertiseSyncWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        HubTokenClient tokenClient,
        IOptionsMonitor<SyncOptions> options,
        ILogger<ExpertiseSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _tokenClient = tokenClient;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // First cycle deferred by one interval so a restart loop cannot hammer
            // the hub (IdempotencyGcService precedent).
            var interval = OverrideInterval ?? _options.CurrentValue.Interval;
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "ExpertiseSyncWorker loop faulted; sync will not run until process restart");
            CycleCounter.WithLabels("faulted").Inc();
        }
    }

    internal async Task SyncOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();

            var state = await db.SyncStates.FirstOrDefaultAsync(ct).ConfigureAwait(false)
                        ?? db.SyncStates.Add(new SyncState()).Entity;

            var batchSize = Math.Clamp(_options.CurrentValue.BatchSize, 1, 100);
            var pushedAnything = false;

            while (!ct.IsCancellationRequested)
            {
                var page = await repo.ListSharedApprovedUpdatedAfterAsync(
                    state.LastSyncedUpdatedAt, state.LastSyncedId, batchSize, ct).ConfigureAwait(false);
                if (page.Count == 0)
                    break;

                var verdicts = await PushPageAsync(page, ct).ConfigureAwait(false);
                if (verdicts is null)
                {
                    // Transient failure — leave the cursor alone; the page retries
                    // next tick and the hub's dedup absorbs any partial creates.
                    CycleCounter.WithLabels("retry").Inc();
                    return;
                }

                var last = page[^1];
                state.LastSyncedUpdatedAt = last.UpdatedAt;
                state.LastSyncedId = last.Id;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                pushedAnything = true;

                if (page.Count < batchSize)
                    break;
            }

            var completed = await db.SyncStates.FirstAsync(ct).ConfigureAwait(false);
            completed.LastSuccessAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            CycleCounter.WithLabels("ok").Inc();
            if (pushedAnything && _logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Up-sync cycle complete; cursor at {Cursor:O}/{Id}", completed.LastSyncedUpdatedAt, completed.LastSyncedId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbException
                                      or DbUpdateException
                                      or InvalidOperationException
                                      or TimeoutException)
        {
            CycleCounter.WithLabels("error").Inc();
            _logger.LogWarning(ex, "Up-sync cycle failed; will retry on next cadence");
        }
    }

    /// <summary>
    /// POSTs one page to the hub. Returns the per-item verdicts, or null when the page
    /// must be retried wholesale (HTTP/token/transport failure, or any item Failed).
    /// </summary>
    private async Task<List<SyncBatchResult>?> PushPageAsync(List<ExpertiseEntry> page, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var items = page.Select(e => new SyncBatchItem
        {
            Domain = e.Domain,
            Title = e.Title,
            Body = e.Body,
            EntryType = e.EntryType.ToString(),
            Severity = e.Severity.ToString(),
            Source = e.Source,
            Tags = e.Tags.Count > 0 ? e.Tags : null,
            SourceVersion = e.SourceVersion,
            OriginAuthorPrincipal = e.AuthorPrincipal,
        }).ToList();

        try
        {
            var token = await _tokenClient.GetAccessTokenAsync(ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(opts.HubUrl!), "/expertise/batch"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(items, SyncJsonContext.Default.ListSyncBatchItem),
                Encoding.UTF8,
                "application/json");

            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            // 200 (all created) and 207 (mixed) both carry per-item verdicts; anything
            // else is a transport/auth-level failure and the page retries.
            if (response.StatusCode is not System.Net.HttpStatusCode.OK and not System.Net.HttpStatusCode.MultiStatus)
            {
                _logger.LogWarning("Hub rejected sync batch with HTTP {Status}; page will retry", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var verdicts = JsonSerializer.Deserialize(body, SyncJsonContext.Default.ListSyncBatchResult);
            if (verdicts is null || verdicts.Count != page.Count)
            {
                _logger.LogWarning("Hub batch response shape mismatch ({Got} verdicts for {Sent} items); page will retry",
                    verdicts?.Count ?? 0, page.Count);
                return null;
            }

            var anyFailed = false;
            foreach (var v in verdicts)
            {
                ItemCounter.WithLabels(v.Status.ToUpperInvariant()).Inc();
                switch (v.Status)
                {
                    case "Failed":
                        anyFailed = true;
                        break;
                    case "Rejected":
                        // Permanent validation verdict — skipping is deliberate; the
                        // entry is named so a curator/operator can chase it.
                        _logger.LogWarning("Hub permanently rejected entry {EntryId} during sync: {Error}",
                            page[v.Index].Id, v.Error ?? "(no detail)");
                        break;
                    default:
                        break;
                }
            }

            return anyFailed ? null : verdicts;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                      or TaskCanceledException
                                      or JsonException
                                      or InvalidOperationException)
        {
            // TaskCanceledException doubles as HttpClient's timeout signal; genuine
            // shutdown cancellation is rethrown by the ct check in the caller loop.
            if (ex is TaskCanceledException && ct.IsCancellationRequested)
                throw;
            _logger.LogWarning(ex, "Sync push failed; page will retry on next cadence");
            return null;
        }
    }
}
