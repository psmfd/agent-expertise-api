using System.Net;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Coverage for <c>GET /expertise/search/hybrid</c> (ADR-016, #428). Uses the
/// content-derived mock embeddings, so semantic distances are all-or-nothing —
/// ranking-quality assertions live in the opt-in retrieval eval harness; these tests
/// pin endpoint semantics (union fusion, filters, validation, score exposure).
/// </summary>
[Collection("Postgres")]
public class HybridSearchEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public HybridSearchEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(_postgres.ConnectionString);
        _client = TestHelpers.CreateAuthenticatedClient(_factory);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        await db.ExpertiseAuditLogs.ExecuteDeleteAsync();
        await db.ExpertiseEntries.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Hybrid_MissingOrEmptyQuery_Returns400()
    {
        (await _client.GetAsync("/expertise/search/hybrid")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync("/expertise/search/hybrid?q=")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Hybrid_KeywordMatch_RanksAboveSemanticOnlyEntries()
    {
        // The keyword-matching entry appears in BOTH arms (keyword hit + semantic
        // candidate); the others appear only in the semantic arm. RRF must rank the
        // dual-arm entry first regardless of mock-embedding distances.
        await SeedEntry("dotnet", "EF Core migration conflict resolution",
            "Rebase and re-scaffold the migration.");
        await SeedEntry("kubernetes", "Pod scheduling strategy",
            "Taints and tolerations for pod scheduling.");
        await SeedEntry("shell", "Trap based cleanup",
            "Use trap EXIT handlers for temp files.");

        var response = await _client.GetAsync("/expertise/search/hybrid?q=migration&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(3, "hybrid is a union — semantic-only candidates still surface");
        json[0].GetProperty("title").GetProperty("value").GetString()
            .Should().Contain("EF Core migration conflict resolution");
        json[0].GetProperty("score").GetDouble().Should().BeGreaterThan(
            json[1].GetProperty("score").GetDouble(),
            "a dual-arm hit accumulates RRF contributions from both arms");
    }

    [Fact]
    public async Task Hybrid_MetadataFilters_ApplyToBothArms()
    {
        await SeedEntry("dotnet", "Migration notes for dotnet", "EF Core migration ordering.");
        await SeedEntry("kubernetes", "Migration notes for clusters", "Cluster migration ordering.");

        var response = await _client.GetAsync("/expertise/search/hybrid?q=migration&domain=dotnet&limit=10");

        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1);
        json[0].HygienizedValue("domain").Should().Contain("dotnet");
    }

    [Fact]
    public async Task Hybrid_LimitClamps_AndScoreIsRrfSum()
    {
        for (var i = 0; i < 5; i++)
            await SeedEntry("dotnet", $"Filler entry {i}", $"Body {i} with no query terms.");

        var response = await _client.GetAsync("/expertise/search/hybrid?q=nomatchterm&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(2, "limit truncates the fused list");
        foreach (var entry in json.EnumerateArray())
        {
            // Semantic-only hits: exactly one arm contributes, so 0 < score <= 1/(K+1).
            entry.GetProperty("score").GetDouble().Should()
                .BeInRange(0, 1.0 / 61, "fused score is the RRF sum, not a raw distance");
        }
    }

    private async Task SeedEntry(string domain, string title, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        await repo.CreateAsync(
            TestHelpers.SeedEntry(domain: domain, title: title, body: body),
            TestHelpers.CreateTenantContext());
    }
}
