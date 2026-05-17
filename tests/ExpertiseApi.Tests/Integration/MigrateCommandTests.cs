using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using ExpertiseApi.Auth;
using ExpertiseApi.Cli;
using ExpertiseApi.Data;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Issue #144 acceptance:
///   1. Fresh-install case  — migrate applies all migrations against an empty DB.
///   2. Upgrade case        — migrate applies just the new migration when the
///                            DB already carries earlier ones.
///   3. Idempotency         — re-running migrate on an up-to-date DB is a no-op
///                            and exits 0.
///   4. Failure-is-fatal    — Npgsql / EF failure surfaces as exit 1 (used by
///                            install.sh / install.ps1 to abort the install
///                            before service restart).
///
/// This class owns its Postgres container rather than reusing PostgresFixture
/// because PostgresFixture migrates the DB to HEAD in InitializeAsync — that
/// already-applied state would mask cases (1) and (2).
/// </summary>
public class MigrateCommandTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("migrate_cmd")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_FreshDatabase_AppliesAllMigrations_AndReturnsZero()
    {
        // Arrange — verify no migrations applied yet.
        await using (var probe = NewContext())
        {
            var pending = await probe.Database.GetPendingMigrationsAsync();
            pending.Should().NotBeEmpty("the test container starts with an empty database");
        }

        // Act
        await using var app = BuildAppForMigrate();
        var exitCode = await MigrateCommand.RunAsync(app);

        // Assert
        exitCode.Should().Be(0);
        await using var db = NewContext();
        var afterPending = await db.Database.GetPendingMigrationsAsync();
        afterPending.Should().BeEmpty("migrate must apply every migration on a fresh database");

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Migrate_AlreadyUpToDate_IsNoOp_AndReturnsZero()
    {
        // Arrange — bring the DB to HEAD first via the real migrator.
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
        }

        // Act
        await using var app = BuildAppForMigrate();
        var exitCode = await MigrateCommand.RunAsync(app);

        // Assert
        exitCode.Should().Be(0, "idempotency: re-running migrate on an up-to-date DB is a no-op");
    }

    [Fact]
    public async Task Migrate_PartiallyApplied_AppliesOnlyTheRemaining()
    {
        // Arrange — derive a baseline dynamically (second-to-last migration)
        // so the test stays valid as the migration set grows. Hardcoding a
        // specific migration name would silently degrade when that migration
        // is renamed or becomes the final one.
        await using var setup = NewContext();
        var all = setup.Database.GetMigrations().ToList();
        all.Count.Should().BeGreaterThan(1,
            "this test only meaningfully runs when there are >=2 migrations to step between");
        var baseline = all[^2];

        var migrator = setup.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(baseline);

        var stillPending = (await setup.Database.GetPendingMigrationsAsync()).ToList();
        stillPending.Should().NotBeEmpty("we deliberately stopped at the second-to-last migration");

        // Act
        await using var app = BuildAppForMigrate();
        var exitCode = await MigrateCommand.RunAsync(app);

        // Assert
        exitCode.Should().Be(0);
        await using var verify = NewContext();
        var pendingAfter = await verify.Database.GetPendingMigrationsAsync();
        pendingAfter.Should().BeEmpty("migrate must apply every remaining migration");
    }

    [Fact]
    public async Task Migrate_DatabaseUnreachable_ReturnsOne()
    {
        // Failure-is-fatal acceptance criterion: a connection-refused error
        // must surface as exit 1 so install.{sh,ps1} can abort the install
        // before restarting the service.
        const string unreachable = "Host=127.0.0.1;Port=1;Database=stub;Username=stub;Password=stub;Timeout=2";

        await using var app = BuildAppForMigrate(connectionString: unreachable);
        var exitCode = await MigrateCommand.RunAsync(app);

        exitCode.Should().Be(1, "any DB failure must propagate a non-zero exit code");
    }

    // ---- Helpers ---------------------------------------------------------

    private ExpertiseDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_container.GetConnectionString(), o => o.UseVector())
            .Options;
        return new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
    }

    /// <summary>
    /// Builds a minimal WebApplication whose DI graph contains exactly what
    /// <see cref="MigrateCommand.RunAsync"/> needs (ILoggerFactory + a scoped
    /// ExpertiseDbContext wired to the test container). Avoids constructing
    /// the full Program.cs pipeline so the test stays focused on the verb's
    /// contract rather than re-validating bootstrap.
    /// </summary>
    private WebApplication BuildAppForMigrate(string? connectionString = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<ITenantContextAccessor, NoOpTenantContextAccessor>();
        builder.Services.AddDbContext<ExpertiseDbContext>(options =>
            options.UseNpgsql(connectionString ?? _container.GetConnectionString(), o => o.UseVector()));

        return builder.Build();
    }
}
