using ExpertiseApi.Auth;
using ExpertiseApi.Cli;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pgvector;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Functional coverage for the <c>reembed</c> and <c>rehash</c> maintenance verbs (#354).
/// Both run hand-rolled cursor-paged LINQ (<c>IgnoreQueryFilters().OrderBy(Id).Where(Id &gt;
/// last)</c>) against real Postgres and back the restore/seed runbook, yet had ZERO
/// functional tests — the exact "raw query against real Postgres, never executed by CI"
/// risk class. Owns its container (MigrateCommandTests pattern).
/// </summary>
public class CliMaintenanceCommandTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("cli_maint")
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

    [Fact]
    public async Task Reembed_RegeneratesEveryEntry_AcrossTenantsAndBatchBoundary_AndStampsMetadata()
    {
        // 5 entries across two tenants seeded with a deliberately WRONG (all-zero) embedding
        // so reembed must overwrite each. batch-size 2 over 5 rows exercises 3 pages of the
        // keyset loop, including the final short page.
        var seeded = new List<ExpertiseEntry>();
        for (var i = 0; i < 5; i++)
        {
            await using var db = NewContext();
            var entry = TestHelpers.SeedEntry(
                domain: "reembed", title: $"entry {i}", body: $"body {i}",
                tenant: i % 2 == 0 ? "team-a" : "team-b");
            entry.Embedding = new Vector(new float[512]);
            db.ExpertiseEntries.Add(entry);
            await db.SaveChangesAsync();
            seeded.Add(entry);
        }

        await using var app = BuildApp();
        await ReembedCommand.RunAsync(app, ["reembed", "--batch-size", "2"]);

        await using var verify = NewContext();
        foreach (var s in seeded)
        {
            var reloaded = await verify.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == s.Id);
            var expected = TestHelpers.CreateContentEmbedding(EmbeddingService.BuildInputText(s.Title, s.Body));
            reloaded.Embedding!.ToArray().Should().Equal(expected,
                "reembed must regenerate every entry across all tenants and every page");
        }

        var metadata = await verify.EmbeddingMetadata.SingleAsync();
        metadata.ModelName.Should().Be("jina-embeddings-v2-small-en");
        metadata.Dimensions.Should().Be(512);
        metadata.LastReembedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task Reembed_OverwritesStaleEmbeddingMetadata()
    {
        // #455: a pre-existing metadata row describing a PREVIOUS model must be
        // corrected by reembed — after a full reembed the stored vectors are, by
        // construction, the current model's. The pre-fix get-or-create only
        // stamped LastReembedAt and left the stale identity in place.
        await using (var db = NewContext())
        {
            db.EmbeddingMetadata.Add(new EmbeddingMetadata
            {
                ModelName = "previous-model",
                Dimensions = 999,
                LastReembedAt = DateTime.UtcNow.AddDays(-30)
            });
            await db.SaveChangesAsync();
        }

        await using (var app = BuildApp())
            await ReembedCommand.RunAsync(app, ["reembed"]);

        await using var verify = NewContext();
        var metadata = await verify.EmbeddingMetadata.SingleAsync();
        metadata.ModelName.Should().Be("jina-embeddings-v2-small-en");
        metadata.Dimensions.Should().Be(512);
        metadata.LastReembedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task EmbeddingMetadata_SecondRow_IsRejectedByDbSingletonGuard()
    {
        // #455: the singleton invariant is enforced at the DB level
        // (UX_EmbeddingMetadata_Singleton) so a get-or-create race duplicates
        // loudly instead of silently.
        await using (var db = NewContext())
        {
            db.EmbeddingMetadata.Add(new EmbeddingMetadata { ModelName = "m1", Dimensions = 1 });
            await db.SaveChangesAsync();
        }

        await using var second = NewContext();
        second.EmbeddingMetadata.Add(new EmbeddingMetadata { ModelName = "m2", Dimensions = 2 });
        var act = () => second.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the unique constant-expression index must reject a second metadata row");
    }

    [Fact]
    public async Task Rehash_BackfillsNullHashes_AndIsIdempotent()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            await using var db = NewContext();
            var entry = TestHelpers.SeedEntry(domain: "rehash", title: $"h {i}", body: $"b {i}");
            entry.IntegrityHash = null; // simulate pre-rehash rows
            db.ExpertiseEntries.Add(entry);
            await db.SaveChangesAsync();
            ids.Add(entry.Id);
        }

        await using (var app = BuildApp())
            await RehashCommand.RunAsync(app, ["rehash", "--batch-size", "2"]);

        await using (var verify = NewContext())
        {
            foreach (var id in ids)
            {
                var entry = await verify.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(x => x.Id == id);
                entry.IntegrityHash.Should().Be(IntegrityHashService.Compute(entry),
                    "rehash backfills the canonical hash");
            }
            (await verify.ExpertiseEntries.IgnoreQueryFilters().CountAsync(e => e.IntegrityHash == null))
                .Should().Be(0);
        }

        // Idempotent: a second run must not change any already-populated hash.
        var before = await SnapshotHashes(ids);
        await using (var app = BuildApp())
            await RehashCommand.RunAsync(app, ["rehash"]);
        var after = await SnapshotHashes(ids);
        after.Should().Equal(before, "rehash only touches IntegrityHash == null rows");
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task<List<string>> SnapshotHashes(IEnumerable<Guid> ids)
    {
        await using var db = NewContext();
        var idList = ids.ToList();
        return await db.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => idList.Contains(e.Id))
            .OrderBy(e => e.Id)
            .Select(e => e.IntegrityHash!)
            .ToListAsync();
    }

    private ExpertiseDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_container.GetConnectionString(), o => o.UseVector())
            .Options;
        return new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ITenantContextAccessor, NoOpTenantContextAccessor>();
        builder.Services.AddDbContext<ExpertiseDbContext>(o =>
            o.UseNpgsql(_container.GetConnectionString(), x => x.UseVector()));

        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ci.ArgAt<IEnumerable<string>>(0).ToList();
                var res = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var input in inputs)
                    res.Add(new Embedding<float>(TestHelpers.CreateContentEmbedding(input)));
                return Task.FromResult(res);
            });
        builder.Services.AddSingleton(generator);
        builder.Services.AddScoped<EmbeddingService>();

        return builder.Build();
    }
}
