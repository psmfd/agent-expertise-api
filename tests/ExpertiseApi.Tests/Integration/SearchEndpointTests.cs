using System.Net;
using System.Text.Json;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Integration;

[Collection("Postgres")]
public class SearchEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public SearchEndpointTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task KeywordSearch_WhenEmptyQuery_Returns400()
    {
        var response = await _client.GetAsync("/expertise/search?q=");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task KeywordSearch_WhenMissingQuery_Returns400()
    {
        // When 'q' is completely absent, binding throws BadHttpRequestException before
        // the handler runs; UnhandledExceptionLogger maps it to the exception's own
        // status code instead of a generic 500 (#329).
        var response = await _client.GetAsync("/expertise/search");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task KeywordSearch_WhenMissingQuery_ProblemDetailsCarriesTraceId()
    {
        var response = await _client.GetAsync("/expertise/search");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("status").GetInt32().Should().Be(400);
        json.TryGetProperty("traceId", out _).Should().BeTrue(
            "the AddProblemDetails customizer must fire on the binding-failure path");
    }

    [Fact]
    public async Task KeywordSearch_RespectsLimit()
    {
        await SeedEntryViaRepo("dotnet", "Migration ordering first entry",
            "Notes about migration ordering in EF Core, entry one.");
        await SeedEntryViaRepo("dotnet", "Migration ordering second entry",
            "Notes about migration ordering in EF Core, entry two.");
        await SeedEntryViaRepo("dotnet", "Migration ordering third entry",
            "Notes about migration ordering in EF Core, entry three.");

        var response = await _client.GetAsync("/expertise/search?q=migration&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task KeywordSearch_SupportsWebsearchSyntax()
    {
        await SeedEntryViaRepo("dotnet", "EF Core migration conflict resolution",
            "When running migrations against a shared database, conflicts can arise.");
        await SeedEntryViaRepo("kubernetes", "Pod scheduling strategy",
            "Kubernetes pod scheduling uses taints and tolerations.");

        // websearch_to_tsquery: -negation excludes; malformed operator input must not throw.
        var negated = await _client.GetAsync("/expertise/search?q=migration%20-kubernetes");
        negated.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await negated.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().BeGreaterThan(0);

        var malformed = await _client.GetAsync("/expertise/search?q=%22unbalanced%20AND%20OR%20(");
        malformed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task KeywordSearch_ReturnsMatchingEntries()
    {
        await SeedEntryViaRepo("dotnet", "EF Core migration conflict resolution",
            "When running migrations against a shared database, conflicts can arise.");
        await SeedEntryViaRepo("kubernetes", "Pod scheduling strategy",
            "Kubernetes pod scheduling uses taints and tolerations.");

        var response = await _client.GetAsync("/expertise/search?q=migration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().BeGreaterThan(0);
        json[0].GetProperty("domain").GetString().Should().Be("dotnet");
    }

    [Fact]
    public async Task KeywordSearch_ExcludesDeprecatedByDefault()
    {
        var active = await SeedEntryViaRepo("shared", "Active migration guide",
            "Database migration best practices.");
        var deprecated = await SeedEntryViaRepo("shared", "Old migration guide",
            "Deprecated migration approach.");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        await repo.SoftDeleteAsync(deprecated.Id, TestHelpers.CreateTenantContext(deprecated.Tenant));

        var response = await _client.GetAsync("/expertise/search?q=migration");

        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("id").GetGuid().Should().Be(active.Id);
    }

    [Fact]
    public async Task KeywordSearch_IncludesDeprecatedWhenRequested()
    {
        await SeedEntryViaRepo("shared", "Active migration guide",
            "Database migration best practices.");
        var deprecated = await SeedEntryViaRepo("shared", "Old migration guide",
            "Deprecated migration approach.");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        await repo.SoftDeleteAsync(deprecated.Id, TestHelpers.CreateTenantContext(deprecated.Tenant));

        var response = await _client.GetAsync("/expertise/search?q=migration&includeDeprecated=true");

        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task KeywordSearch_NoResults_ReturnsEmptyArray()
    {
        await SeedEntryViaRepo("shared", "Unrelated entry", "Nothing about the search term.");

        var response = await _client.GetAsync("/expertise/search?q=xyznonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task KeywordSearch_FiltersByDomain()
    {
        await SeedEntryViaRepo("dotnet", "Caching strategy for dotnet services",
            "Response caching guidance for dotnet minimal APIs.");
        await SeedEntryViaRepo("kubernetes", "Caching strategy for cluster workloads",
            "Response caching guidance for kubernetes ingress layers.");

        var response = await _client.GetAsync("/expertise/search?q=caching&domain=dotnet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("domain").GetString().Should().Be("dotnet");
    }

    [Fact]
    public async Task KeywordSearch_FiltersByTagsEntryTypeAndSeverity()
    {
        await SeedEntryViaRepo("dotnet", "Caching pitfall with stale keys",
            "Stale cache keys cause subtle caching bugs.",
            entryType: EntryType.Caveat, severity: Severity.Warning, tags: ["cache", "redis"]);
        await SeedEntryViaRepo("dotnet", "Caching pattern for hot paths",
            "Memoize hot-path caching lookups.",
            entryType: EntryType.Pattern, severity: Severity.Info, tags: ["cache"]);

        var byTags = await _client.GetAsync("/expertise/search?q=caching&tags=cache,redis");
        byTags.StatusCode.Should().Be(HttpStatusCode.OK);
        var tagsJson = await byTags.Content.ReadJsonElementAsync();
        tagsJson.GetArrayLength().Should().Be(1, "both requested tags must match (AND semantics)");
        tagsJson[0].GetProperty("title").GetProperty("value").GetString().Should().Contain("Caching pitfall with stale keys");

        var byTypeAndSeverity = await _client.GetAsync("/expertise/search?q=caching&entryType=Pattern&severity=Info");
        byTypeAndSeverity.StatusCode.Should().Be(HttpStatusCode.OK);
        var typeJson = await byTypeAndSeverity.Content.ReadJsonElementAsync();
        typeJson.GetArrayLength().Should().Be(1);
        typeJson[0].GetProperty("title").GetProperty("value").GetString().Should().Contain("Caching pattern for hot paths");
    }

    [Fact]
    public async Task KeywordSearch_InvalidEnumFilter_Returns400()
    {
        var response = await _client.GetAsync("/expertise/search?q=caching&entryType=NotAType");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unparseable enum filter is a binding failure surfaced as 400, not a 500");
    }

    private async Task<ExpertiseEntry> SeedEntryViaRepo(
        string domain,
        string title,
        string body = "Default test body content",
        string tenant = TestHelpers.TestTenant,
        EntryType entryType = EntryType.Pattern,
        Severity severity = Severity.Info,
        string[]? tags = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        var entry = TestHelpers.SeedEntry(
            domain: domain, title: title, body: body,
            entryType: entryType, severity: severity, tenant: tenant);
        if (tags is not null)
            entry.Tags = [.. tags];
        return await repo.CreateAsync(entry, TestHelpers.CreateTenantContext(tenant));
    }
}
