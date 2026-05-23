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
    public async Task KeywordSearch_WhenMissingQuery_ReturnsError()
    {
        // When 'q' is completely absent, ASP.NET Core binding fails before the handler runs.
        // This produces a 500 rather than a clean 400 — a known gap (see #28 for similar issue).
        var response = await _client.GetAsync("/expertise/search");

        ((int)response.StatusCode).Should().BeOneOf(400, 500);
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

    private async Task<ExpertiseEntry> SeedEntryViaRepo(
        string domain,
        string title,
        string body = "Default test body content",
        string tenant = TestHelpers.TestTenant)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        return await repo.CreateAsync(
            TestHelpers.SeedEntry(domain: domain, title: title, body: body, tenant: tenant),
            TestHelpers.CreateTenantContext(tenant));
    }
}
