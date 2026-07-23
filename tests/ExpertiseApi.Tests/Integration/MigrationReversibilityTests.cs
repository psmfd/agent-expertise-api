using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Integration;

public class MigrationReversibilityTests : IAsyncLifetime
{
    private const string BaselineMigration = "20260416062516_AddTitleLowerIndex";
    private const string TargetMigration = "20260428204727_AddTenantAuditFields";
    private const string ActorClassMigration = "20260518232947_AddAuditActorClassFields";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("migration_reversibility")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync().AsTask();

    [Fact]
    public async Task AddTenantAuditFields_AppliesAndRollsBack_Cleanly()
    {
        await using var db = NewContext();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

        // Arrange — apply migrations up to the baseline (state before TargetMigration).
        await migrator.MigrateAsync(BaselineMigration);

        // Act — apply TargetMigration.
        await migrator.MigrateAsync(TargetMigration);

        var afterUp = await db.Database.GetAppliedMigrationsAsync();
        afterUp.Should().Contain(TargetMigration);

        // The new columns and table should exist after Up().
        await AssertColumnsExist(db, expected: true);
        await AssertAuditTableExists(db, expected: true);

        // Act — roll back to baseline.
        await migrator.MigrateAsync(BaselineMigration);

        var afterDown = await db.Database.GetAppliedMigrationsAsync();
        afterDown.Should().NotContain(TargetMigration);

        // The new columns and table should be gone after Down().
        await AssertColumnsExist(db, expected: false);
        await AssertAuditTableExists(db, expected: false);

        // And TargetMigration should now be reported as pending again.
        var pending = await db.Database.GetPendingMigrationsAsync();
        pending.Should().Contain(TargetMigration);
    }

    [Fact]
    public async Task AddAuditActorClassFields_AppliesAndRollsBack_Cleanly()
    {
        await using var db = NewContext();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

        // Arrange — apply migrations up to and including the prior baseline
        // (TargetMigration / AddTenantAuditFields). ActorClassMigration is next.
        await migrator.MigrateAsync(TargetMigration);

        // Act — apply the C6 migration.
        await migrator.MigrateAsync(ActorClassMigration);
        var afterUp = await db.Database.GetAppliedMigrationsAsync();
        afterUp.Should().Contain(ActorClassMigration);

        await AssertActorClassColumnsExist(db, expected: true);

        // Roll back: should leave AddTenantAuditFields applied and the C6 columns gone.
        await migrator.MigrateAsync(TargetMigration);
        var afterDown = await db.Database.GetAppliedMigrationsAsync();
        afterDown.Should().NotContain(ActorClassMigration);
        afterDown.Should().Contain(TargetMigration);

        await AssertActorClassColumnsExist(db, expected: false);
    }

    [Fact]
    public async Task SwapEmbeddingModelTo512Dim_NullsPopulated384DimData_AndRetypes()
    {
        // ADR-017. The empty-DB apply path is exercised by every PostgresFixture
        // test for free; THIS test covers the only path where the migration's
        // internal ordering matters — an UPGRADE over populated 384-dim rows.
        // pgvector's typmod cast rejects a naive ALTER COLUMN TYPE over live
        // vectors, so a migration that "works" on fresh CI databases would break
        // the first real production upgrade. Seeding uses raw SQL because the
        // current EF model already reflects vector(512).
        await using var db = NewContext();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync(SwapBaselineMigration);

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO "ExpertiseEntries"
                    ("Domain","Tags","Title","Body","EntryType","Severity","Source",
                     "Tenant","Visibility","AuthorPrincipal","ReviewState","Embedding")
                VALUES ('mig','{}','migration seed','populated 384-dim row','Pattern','Info','human',
                        'legacy','Private','test','Approved',
                        (SELECT array_agg(0.5)::vector(384) FROM generate_series(1,384)));
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync(SwapMigration);

        (await ScalarLong(db, """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = 'ExpertiseEntries' AND column_name = 'Embedding'
              AND udt_name = 'vector';
            """)).Should().Be(1);
        (await ScalarLong(db, """SELECT atttypmod FROM pg_attribute WHERE attrelid = '"ExpertiseEntries"'::regclass AND attname = 'Embedding';"""))
            .Should().Be(512, "the column typmod must carry the new dimension");
        (await ScalarLong(db, """SELECT COUNT(*) FROM "ExpertiseEntries" WHERE "Embedding" IS NOT NULL;"""))
            .Should().Be(0, "old-model vectors are garbage in the new space and must be nulled");
        (await ScalarLong(db, """SELECT COUNT(*) FROM "ExpertiseEntries";"""))
            .Should().Be(1, "the row itself must survive — only its embedding is invalidated");
        (await ScalarLong(db, """SELECT COUNT(*) FROM pg_indexes WHERE tablename = 'ExpertiseEntries' AND indexname = 'IX_ExpertiseEntries_Embedding' AND indexdef LIKE '%hnsw%';"""))
            .Should().Be(1, "the HNSW index must be recreated after the retype");

        // Down(): the column type reverts; the destroyed vectors do NOT come
        // back (forward-only in practice — ADR-017 rollback section).
        await migrator.MigrateAsync(SwapBaselineMigration);
        (await ScalarLong(db, """SELECT atttypmod FROM pg_attribute WHERE attrelid = '"ExpertiseEntries"'::regclass AND attname = 'Embedding';"""))
            .Should().Be(384);
        (await ScalarLong(db, """SELECT COUNT(*) FROM "ExpertiseEntries" WHERE "Embedding" IS NOT NULL;"""))
            .Should().Be(0, "Down() cannot restore destroyed vectors — documented, deliberate");
    }

    private const string SwapBaselineMigration = "20260723135538_EmbeddingMetadataSingletonGuard";
    private const string SwapMigration = "20260723141427_SwapEmbeddingModelTo512Dim";

    private static async Task<long> ScalarLong(ExpertiseDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task AssertActorClassColumnsExist(ExpertiseDbContext db, bool expected)
    {
        foreach (var column in new[] { "ActorClass", "AuthMethod", "ActorClassHeader" })
        {
            var exists = await ColumnExists(db, "ExpertiseAuditLogs", column);
            exists.Should().Be(expected, $"column '{column}' should{(expected ? "" : " not")} exist");
        }
    }

    private ExpertiseDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_container.GetConnectionString(), o => o.UseVector())
            .Options;
        return new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
    }

    private static async Task AssertColumnsExist(ExpertiseDbContext db, bool expected)
    {
        var newColumns = new[]
        {
            "Tenant", "Visibility", "AuthorPrincipal", "AuthorAgent", "IntegrityHash",
            "ReviewState", "ReviewedBy", "ReviewedAt", "RejectionReason"
        };

        foreach (var column in newColumns)
        {
            var exists = await ColumnExists(db, "ExpertiseEntries", column);
            exists.Should().Be(expected, $"column '{column}' should{(expected ? "" : " not")} exist");
        }
    }

    private static async Task AssertAuditTableExists(ExpertiseDbContext db, bool expected)
    {
        var exists = await TableExists(db, "ExpertiseAuditLogs");
        exists.Should().Be(expected, $"ExpertiseAuditLogs should{(expected ? "" : " not")} exist");
    }

    private static async Task<bool> ColumnExists(ExpertiseDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_name = @table AND column_name = @column;
            """;
        AddParam(cmd, "@table", table);
        AddParam(cmd, "@column", column);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    private static async Task<bool> TableExists(ExpertiseDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @table;
            """;
        AddParam(cmd, "@table", table);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
