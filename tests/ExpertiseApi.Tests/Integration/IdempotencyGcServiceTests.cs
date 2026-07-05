using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Services.Idempotency;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Functional coverage for the idempotency GC sweep (#354). The periodic sweep DELETE
/// runs against real Postgres but the loop had zero tests — only the store's reserve/replay
/// path was covered indirectly. Drives <see cref="IdempotencyGcService.SweepOnceAsync"/>
/// directly (internal seam) so the cutoff computation and the raw DELETE are both exercised
/// deterministically, without racing the PeriodicTimer.
/// </summary>
public class IdempotencyGcServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("idem_gc")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_container.GetConnectionString(), o => o.UseVector())
            .Options;
        await using var db = new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
        await db.Database.MigrateAsync();

        _dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task SweepOnce_DeletesRowsOlderThanTtl_AndKeepsFreshRows()
    {
        var store = new NpgsqlIdempotencyStore(_dataSource);

        // Two reserved rows (created_at defaults to now()).
        await store.TryReserveAsync("tenant-a", "expired-key", "hash1", TimeSpan.FromHours(24), CancellationToken.None);
        await store.TryReserveAsync("tenant-a", "fresh-key", "hash2", TimeSpan.FromHours(24), CancellationToken.None);

        // Age the first row past a 24h TTL by rewriting its created_at directly.
        await using (var cmd = _dataSource.CreateCommand(
            "UPDATE idempotency_records SET created_at = now() - interval '48 hours' WHERE key = 'expired-key';"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var options = Substitute.For<IOptionsMonitor<IdempotencyOptions>>();
        options.CurrentValue.Returns(new IdempotencyOptions { Ttl = TimeSpan.FromHours(24) });
        var service = new IdempotencyGcService(store, options, NullLogger<IdempotencyGcService>.Instance);

        await service.SweepOnceAsync(CancellationToken.None);

        (await RowExists("expired-key")).Should().BeFalse("a row older than the TTL is swept");
        (await RowExists("fresh-key")).Should().BeTrue("a row within the TTL survives");
    }

    private async Task<bool> RowExists(string key)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT count(*) FROM idempotency_records WHERE key = @key;");
        cmd.Parameters.AddWithValue("key", key);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        return count > 0;
    }
}
