using System.Collections.Concurrent;
using System.Text.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services.Sync;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// ADR-013 spoke-side acceptance, end-to-end against an in-process stub hub:
///   1. The worker pushes exactly the Approved + shared-tenant + non-deprecated
///      entries past the cursor, oldest first, with a bearer token from the stub
///      token endpoint, and advances the SyncState cursor.
///   2. A second cycle pushes nothing (cursor is durable).
///   3. A transient hub failure (HTTP 500) leaves the cursor untouched; a healthy
///      retry delivers the same page (at-least-once).
///   4. The repository feed method filters correctly (drafts, private tenants,
///      deprecated, rejected excluded).
/// Owns its container so cursor state cannot interfere with other suites.
/// </summary>
public class SyncWorkerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("sync_worker")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync().AsTask();

    // ---- Stub hub ---------------------------------------------------------

    private sealed class StubHub : IAsyncDisposable
    {
        public readonly ConcurrentQueue<(string Authorization, List<SyncBatchItem> Items)> Batches = new();
        public volatile bool FailNextBatch;
        private WebApplication? _app;

        public string BaseUrl { get; private set; } = "";

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.Urls.Add("http://127.0.0.1:0");

            _app.MapPost("/token", () => Results.Json(new { access_token = "stub-token", expires_in = 3600 }));

            _app.MapPost("/expertise/batch", async (HttpRequest request) =>
            {
                if (FailNextBatch)
                {
                    FailNextBatch = false;
                    return Results.StatusCode(500);
                }

                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync();
                var items = JsonSerializer.Deserialize(body, SyncJsonContext.Default.ListSyncBatchItem)!;
                Batches.Enqueue((request.Headers.Authorization.ToString(), items));

                var verdicts = items.Select((_, i) => new SyncBatchResult
                {
                    Index = i,
                    Status = "Created",
                    Id = Guid.NewGuid(),
                }).ToList();
                return Results.Json(verdicts, SyncJsonContext.Default.ListSyncBatchResult);
            });

            await _app.StartAsync();
            BaseUrl = _app.Urls.Single();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
                await _app.DisposeAsync();
        }
    }

    // ---- Tests ------------------------------------------------------------

    [Fact]
    public async Task SyncOnce_PushesSharedApprovedEntries_AdvancesCursor_AndIsIdempotent()
    {
        await WipeAsync();
        var (visible, _) = await SeedAsync();
        await using var hub = new StubHub();
        await hub.StartAsync();
        await using var provider = BuildWorkerServices(hub.BaseUrl);
        var worker = BuildWorker(provider);

        await worker.SyncOnceAsync(CancellationToken.None);

        hub.Batches.Should().HaveCount(1);
        hub.Batches.TryDequeue(out var batch).Should().BeTrue();
        batch.Authorization.Should().Be("Bearer stub-token");
        batch.Items.Select(i => i.Title).Should().Equal(visible.Select(e => e.Title),
            "the feed is ordered (UpdatedAt, Id) ascending and includes exactly the shared+Approved+non-deprecated set");
        batch.Items.Should().OnlyContain(i => i.OriginAuthorPrincipal != null,
            "the spoke forwards its local author as origin context");

        await using (var db = NewContext())
        {
            var state = await db.SyncStates.SingleAsync();
            state.LastSyncedId.Should().Be(visible[^1].Id);
            state.LastSyncedUpdatedAt.Should().Be(visible[^1].UpdatedAt);
            state.LastSuccessAt.Should().NotBeNull();
        }

        // Second cycle: nothing new — no POST at all.
        await worker.SyncOnceAsync(CancellationToken.None);
        hub.Batches.Should().BeEmpty("the cursor is durable; an unchanged feed must not re-push");
    }

    [Fact]
    public async Task TransientHubFailure_LeavesCursorUntouched_ThenRetriesSamePage()
    {
        await WipeAsync();
        var (visible, _) = await SeedAsync();
        await using var hub = new StubHub();
        await hub.StartAsync();
        await using var provider = BuildWorkerServices(hub.BaseUrl);
        var worker = BuildWorker(provider);

        hub.FailNextBatch = true;
        await worker.SyncOnceAsync(CancellationToken.None);

        hub.Batches.Should().BeEmpty("the 500 response carries no verdicts");
        await using (var db = NewContext())
        {
            (await db.SyncStates.SingleOrDefaultAsync())?.LastSyncedId.Should().Be(Guid.Empty,
                "a failed page must not advance the cursor");
        }

        await worker.SyncOnceAsync(CancellationToken.None);
        hub.Batches.Should().HaveCount(1, "the same page is retried on the next cadence (at-least-once)");
        hub.Batches.TryDequeue(out var retried).Should().BeTrue();
        retried.Items.Should().HaveCount(visible.Count);
    }

    [Fact]
    public async Task RepositoryFeed_FiltersAndPages()
    {
        await WipeAsync();
        var (visible, _) = await SeedAsync();
        await using var provider = BuildWorkerServices("http://unused.local");
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();

        var all = await repo.ListSharedApprovedUpdatedAfterAsync(DateTime.MinValue, Guid.Empty, 100);
        all.Select(e => e.Id).Should().Equal(visible.Select(e => e.Id),
            "only shared + Approved + non-deprecated entries qualify, ordered (UpdatedAt, Id)");

        // Keyset: page of 1, then resume strictly after it.
        var first = await repo.ListSharedApprovedUpdatedAfterAsync(DateTime.MinValue, Guid.Empty, 1);
        first.Should().HaveCount(1);
        var rest = await repo.ListSharedApprovedUpdatedAfterAsync(first[0].UpdatedAt, first[0].Id, 100);
        rest.Select(e => e.Id).Should().Equal(visible.Skip(1).Select(e => e.Id));
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task<(List<ExpertiseEntry> Visible, List<ExpertiseEntry> Hidden)> SeedAsync()
    {
        await using var db = NewContext();

        ExpertiseEntry Make(string title, string tenant, ReviewState state, DateTime? deprecatedAt = null) => new()
        {
            Domain = "sync-test",
            Title = title,
            Body = $"body of {title}",
            EntryType = EntryType.Pattern,
            Severity = Severity.Info,
            Source = "human",
            Tenant = tenant,
            AuthorPrincipal = "author@spoke.local",
            ReviewState = state,
            ReviewedBy = state == ReviewState.Approved ? "reviewer@spoke.local" : null,
            ReviewedAt = state == ReviewState.Approved ? DateTime.UtcNow : null,
            DeprecatedAt = deprecatedAt,
            Embedding = new Vector(new float[384]),
        };

        var hidden = new List<ExpertiseEntry>
        {
            Make("private approved", "team-alpha", ReviewState.Approved),
            Make("shared draft", "shared", ReviewState.Draft),
            Make("shared rejected", "shared", ReviewState.Rejected),
            Make("shared deprecated", "shared", ReviewState.Approved, deprecatedAt: DateTime.UtcNow),
        };

        // Inserted one-by-one so UpdatedAt (now()) strictly increases — makes the
        // expected (UpdatedAt, Id) feed order deterministic.
        var visible = new List<ExpertiseEntry>();
        foreach (var title in new[] { "sync me first", "sync me second", "sync me third" })
        {
            var e = Make(title, "shared", ReviewState.Approved);
            db.ExpertiseEntries.Add(e);
            await db.SaveChangesAsync();
            visible.Add(e);
        }

        db.ExpertiseEntries.AddRange(hidden);
        await db.SaveChangesAsync();

        var ordered = await db.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Tenant == "shared" && e.ReviewState == ReviewState.Approved && e.DeprecatedAt == null)
            .OrderBy(e => e.UpdatedAt).ThenBy(e => e.Id)
            .ToListAsync();
        return (ordered, hidden);
    }

    private async Task WipeAsync()
    {
        await using var db = NewContext();
        await db.SyncStates.ExecuteDeleteAsync();
        await db.ExpertiseAuditLogs.ExecuteDeleteAsync();
        await db.ExpertiseEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    private ExpertiseDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_container.GetConnectionString(), o => o.UseVector())
            .Options;
        return new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
    }

    private ServiceProvider BuildWorkerServices(string hubBaseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging(l => l.ClearProviders());
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantContextAccessor, NoOpTenantContextAccessor>();
        services.AddDbContext<ExpertiseDbContext>(o =>
            o.UseNpgsql(_container.GetConnectionString(), n => n.UseVector()));
        services.AddScoped<IExpertiseRepository, ExpertiseRepository>();
        services.AddHttpClient();
        services.Configure<SyncOptions>(o =>
        {
            o.Enabled = true;
            o.HubUrl = hubBaseUrl;
            o.TokenEndpoint = $"{hubBaseUrl}/token";
            o.ClientId = "spoke-test";
            o.ClientSecret = "secret";
            o.BatchSize = 100;
        });
        return services.BuildServiceProvider();
    }

    private static ExpertiseSyncWorker BuildWorker(ServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        provider.GetRequiredService<IHttpClientFactory>(),
        new HubTokenClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOptionsMonitor<SyncOptions>>(),
            NullLogger<HubTokenClient>.Instance),
        provider.GetRequiredService<IOptionsMonitor<SyncOptions>>(),
        NullLogger<ExpertiseSyncWorker>.Instance);
}
