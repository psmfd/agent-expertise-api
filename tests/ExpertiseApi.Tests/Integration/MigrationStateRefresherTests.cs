using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Services.Health;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Functional coverage for the migration-state refresher (#354). The refresher polls
/// <c>GetPendingMigrationsAsync</c> and publishes the readiness snapshot that
/// <c>PendingMigrationHealthCheck</c> reads, but had zero tests. Drives
/// <see cref="MigrationStateRefresher.RefreshOnceAsync"/> directly (internal seam) against
/// a migrated and an un-migrated container so both snapshot branches are exercised.
/// Owns two containers because migration is one-way.
/// </summary>
public class MigrationStateRefresherTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _migrated = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("migstate_ok").WithUsername("test").WithPassword("test").Build();

    private readonly PostgreSqlContainer _pending = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("migstate_pending").WithUsername("test").WithPassword("test").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_migrated.StartAsync(), _pending.StartAsync());
        // Migrate only the first; the second stays schema-less so every migration is pending.
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_migrated.GetConnectionString(), o => o.UseVector()).Options;
        await using var db = new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _migrated.DisposeAsync().AsTask();
        await _pending.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task RefreshOnce_OnUpToDateSchema_PublishesNoPending()
    {
        await using var provider = BuildProvider(_migrated.GetConnectionString());
        var state = new MigrationState();
        var refresher = new MigrationStateRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(), state,
            NullLogger<MigrationStateRefresher>.Instance);

        await refresher.RefreshOnceAsync(CancellationToken.None);

        state.HasPendingMigrations.Should().BeFalse("the container is migrated to HEAD");
        state.Pending.Should().BeEmpty();
        state.LastCheckedUtc.Should().NotBeNull("a successful poll stamps the check time");
    }

    [Fact]
    public async Task RefreshOnce_OnSchemaLessDatabase_PublishesPending()
    {
        await using var provider = BuildProvider(_pending.GetConnectionString());
        var state = new MigrationState();
        var refresher = new MigrationStateRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(), state,
            NullLogger<MigrationStateRefresher>.Instance);

        await refresher.RefreshOnceAsync(CancellationToken.None);

        state.HasPendingMigrations.Should().BeTrue("no migrations have been applied");
        state.Pending.Should().NotBeEmpty("every migration in the assembly is pending");
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContextAccessor, NoOpTenantContextAccessor>();
        services.AddDbContext<ExpertiseDbContext>(o => o.UseNpgsql(connectionString, x => x.UseVector()));
        return services.BuildServiceProvider();
    }
}
