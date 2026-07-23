using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Integration;

[Collection("Postgres")]
public class ExpertiseEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public ExpertiseEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(_postgres.ConnectionString);
        _client = TestHelpers.CreateAuthenticatedClient(_factory);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        // FK ON DELETE RESTRICT requires audit logs be removed before their entries.
        await db.ExpertiseAuditLogs.ExecuteDeleteAsync();
        await db.ExpertiseEntries.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task List_WhenEmpty_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetById_WhenNotFound_Returns404()
    {
        var response = await _client.GetAsync($"/expertise/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_WhenExists_ReturnsEntry()
    {
        var seeded = await SeedEntryViaRepo("dotnet", "Test entry");

        var response = await _client.GetAsync($"/expertise/{seeded.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("id").GetGuid().Should().Be(seeded.Id);
        json.GetProperty("title").GetProperty("value").GetString().Should().Contain("Test entry");
    }

    [Fact]
    public async Task List_WithDomainFilter_ReturnsOnlyMatchingDomain()
    {
        await SeedEntryViaRepo("dotnet", "DotNet entry");
        await SeedEntryViaRepo("kubernetes", "K8s entry");

        var response = await _client.GetAsync("/expertise?domain=dotnet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("domain").GetString().Should().Be("dotnet");
    }

    [Fact]
    public async Task List_WithEntryTypeFilter_ReturnsOnlyMatchingType()
    {
        await SeedEntryViaRepo("shared", "Pattern entry", entryType: EntryType.Pattern);
        await SeedEntryViaRepo("shared", "Caveat entry", entryType: EntryType.Caveat);

        var response = await _client.GetAsync("/expertise?entryType=Caveat");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("entryType").GetString().Should().Be("Caveat");
    }

    [Fact]
    public async Task List_WithTagsFilter_ReturnsOnlyEntriesWithAllRequestedTags()
    {
        // Closes the audit's #1 gap: the ?tags= filter (tags.All array-containment over a
        // text[] column) had no HTTP test — the exact untested shape-class that let the
        // batch-dedup bug ship. Complements the DB-less translation guard
        // (RepositoryQueryTranslationTests) with an end-to-end correctness assertion.
        await SeedWithTags("has both", "postgres", "ef-core");
        await SeedWithTags("missing one", "postgres");
        await SeedWithTags("different set", "ef-core", "kubernetes");

        var response = await _client.GetAsync("/expertise?tags=postgres,ef-core");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1, "tags.All requires EVERY requested tag be present on the entry");
        json[0].GetProperty("title").GetProperty("value").GetString().Should().Contain("has both");
    }

    [Fact]
    public async Task List_ExcludesDeprecatedByDefault()
    {
        var entry = await SeedEntryViaRepo("shared", "Active entry");
        var deprecated = await SeedEntryViaRepo("shared", "Deprecated entry");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        // Soft-deleted entry was seeded with tenant "test" — match it on the call.
        await repo.SoftDeleteAsync(deprecated.Id, TestHelpers.CreateTenantContext(deprecated.Tenant));

        var response = await _client.GetAsync("/expertise");

        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("id").GetGuid().Should().Be(entry.Id);
    }

    [Fact]
    public async Task List_IncludesDeprecatedWhenRequested()
    {
        await SeedEntryViaRepo("shared", "Active entry");
        var deprecated = await SeedEntryViaRepo("shared", "Deprecated entry");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        await repo.SoftDeleteAsync(deprecated.Id, TestHelpers.CreateTenantContext(deprecated.Tenant));

        var response = await _client.GetAsync("/expertise?includeDeprecated=true");

        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Delete_WhenExists_Returns204()
    {
        var entry = await SeedEntryViaRepo("shared", "To delete");

        var response = await _client.DeleteAsync($"/expertise/{entry.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WhenNotFound_Returns404()
    {
        var response = await _client.DeleteAsync($"/expertise/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateEntry_WithNullDomain_Returns400()
    {
        var payload = new { title = "Test", body = "Test body", entryType = "Pattern", severity = "Info", source = "test" };
        var response = await _client.PostAsJsonAsync("/expertise", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEntry_WithWhitespaceTitle_Returns400()
    {
        var payload = new { domain = "shared", title = "   ", body = "Test body", entryType = "Pattern", severity = "Info", source = "test" };
        var response = await _client.PostAsJsonAsync("/expertise", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEntry_WithOverlongBody_Returns400()
    {
        // 16000 is the embedding-window cap (#429 method, re-based by ADR-017).
        var payload = new { domain = "shared", title = "Overlong", body = new string('a', 16001), entryType = "Pattern", severity = "Info", source = "test" };
        var response = await _client.PostAsJsonAsync("/expertise", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("maximum length of 16000");
    }

    [Fact]
    public async Task CreateEntry_WithBodyAtLimit_Returns201()
    {
        var payload = new { domain = "shared", title = "At limit", body = new string('a', 16000), entryType = "Pattern", severity = "Info", source = "test" };
        var response = await _client.PostAsJsonAsync("/expertise", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateEntry_WithOverlongTitle_Returns400()
    {
        // 200 is the #436 title cap (MaxTitleLength in ExpertiseEndpoints).
        var payload = new { domain = "shared", title = new string('t', 201), body = "Body ok", entryType = "Pattern", severity = "Info", source = "test" };
        var response = await _client.PostAsJsonAsync("/expertise", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("maximum length of 200");
    }

    [Fact]
    public async Task CreateEntry_WithTitleAtLimit_Returns201()
    {
        var payload = new { domain = "shared", title = new string('t', 200), body = "Body ok", entryType = "Pattern", severity = "Info", source = "test" };
        var response = await _client.PostAsJsonAsync("/expertise", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateEntry_WithOverlongTitle_Returns400()
    {
        var entry = await SeedEntryViaRepo("shared", "Title patch target");

        var response = await _client.PatchAsJsonAsync($"/expertise/{entry.Id}", new { title = new string('t', 201) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("maximum length of 200");
    }

    [Fact]
    public async Task UpdateEntry_WithOverlongBody_Returns400()
    {
        var entry = await SeedEntryViaRepo("shared", "Patch target");

        var response = await _client.PatchAsJsonAsync($"/expertise/{entry.Id}", new { body = new string('b', 16001) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("maximum length of 16000");
    }

    private async Task<ExpertiseEntry> SeedEntryViaRepo(
        string domain = "shared",
        string title = "Test",
        EntryType entryType = EntryType.Pattern,
        string tenant = TestHelpers.TestTenant)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExpertiseRepository>();
        return await repo.CreateAsync(
            TestHelpers.SeedEntry(domain: domain, title: title, entryType: entryType, tenant: tenant),
            TestHelpers.CreateTenantContext(tenant));
    }

    // Seeds an Approved entry with an explicit tag set (SeedEntry hardcodes ["test"]).
    private async Task SeedWithTags(string title, params string[] tags)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var entry = TestHelpers.SeedEntry(domain: "tag-filter", title: title);
        entry.Tags = [.. tags];
        db.ExpertiseEntries.Add(entry);
        await db.SaveChangesAsync();
    }
}
