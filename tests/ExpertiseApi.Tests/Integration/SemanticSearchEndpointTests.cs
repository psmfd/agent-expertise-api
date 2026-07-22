using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Coverage for <c>GET /expertise/search/semantic</c> (#356). Before this the endpoint's
/// only test (in <c>TenantIsolationTests</c>) asserted tenant filtering alone — `limit`
/// clamping, `includeDeprecated`, empty-`q` validation, and the <c>semantic-search</c>
/// token-bucket rate limit were all unverified. A per-test <see cref="JwtApiFactory"/>
/// isolates rate-limit state.
/// </summary>
[Collection("Postgres")]
public class SemanticSearchEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public SemanticSearchEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        await db.ExpertiseAuditLogs.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.ExpertiseEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ReadClient()
    {
        var token = JwtTokenMinter.Mint(tenant: "test", scopes: [AuthConstants.ReadScope], groups: ["group-test"]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task SeedApproved(int count, string domain, bool deprecated = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        for (var i = 0; i < count; i++)
        {
            var entry = TestHelpers.SeedEntry(
                domain: domain, title: $"{domain} entry {i} {Guid.NewGuid():N}", body: "semantic body",
                tenant: "shared", reviewState: ReviewState.Approved);
            if (deprecated)
                entry.DeprecatedAt = DateTime.UtcNow;
            db.ExpertiseEntries.Add(entry);
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Semantic_MissingQuery_Returns400()
    {
        var response = await ReadClient().GetAsync("/expertise/search/semantic?q=");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Semantic_FiltersByDomain()
    {
        await SeedApproved(3, "filter-alpha");
        await SeedApproved(2, "filter-beta");

        var response = await ReadClient().GetAsync("/expertise/search/semantic?q=anything&domain=filter-alpha&limit=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement>();
        results.GetArrayLength().Should().Be(3, "the domain filter applies before ranking");
        foreach (var entry in results.EnumerateArray())
            entry.GetProperty("domain").GetString().Should().Be("filter-alpha");
    }

    [Fact]
    public async Task Semantic_FiltersByEntryType()
    {
        await SeedApproved(2, "type-domain");

        var none = await ReadClient().GetAsync("/expertise/search/semantic?q=anything&domain=type-domain&entryType=Requirement&limit=50");
        (await none.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength()
            .Should().Be(0, "seeded entries are Pattern, not Requirement");

        var all = await ReadClient().GetAsync("/expertise/search/semantic?q=anything&domain=type-domain&entryType=Pattern&limit=50");
        (await all.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Semantic_LimitAboveMax_IsClampedTo100()
    {
        await SeedApproved(105, "clamp-domain");

        var response = await ReadClient().GetAsync("/expertise/search/semantic?q=anything&limit=1000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement>();
        results.GetArrayLength().Should().Be(100, "limit is clamped to a maximum of 100 (105 entries exist)");
    }

    [Fact]
    public async Task Semantic_ExcludesDeprecatedByDefault_IncludesWhenRequested()
    {
        await SeedApproved(1, "dep-domain");
        await SeedApproved(1, "dep-domain", deprecated: true);
        var client = ReadClient();

        var byDefault = await client.GetAsync("/expertise/search/semantic?q=body&limit=10");
        (await byDefault.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength()
            .Should().Be(1, "deprecated entries are excluded by default");

        var included = await client.GetAsync("/expertise/search/semantic?q=body&limit=10&includeDeprecated=true");
        (await included.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength()
            .Should().Be(2, "includeDeprecated=true lifts the DeprecatedAt filter");
    }

    [Fact]
    public async Task Semantic_ExceedingTokenBucket_Returns429()
    {
        var client = ReadClient();

        // Token bucket: limit 10, no queue — the 11th request in the window is rejected.
        HttpResponseMessage? final = null;
        for (var i = 0; i < 11; i++)
            final = await client.GetAsync("/expertise/search/semantic?q=x");

        final!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the semantic-search token bucket admits 10 requests then 429s (each call runs ONNX inference)");
    }
}
