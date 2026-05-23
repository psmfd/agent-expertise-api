using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("expertisetest")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var optionsBuilder = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseVector());

        await using var db = new ExpertiseDbContext(optionsBuilder.Options, new NoOpTenantContextAccessor());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().AsTask();
    }
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
