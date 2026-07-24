using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Depth coverage for <c>POST /expertise/batch</c> (#351). The endpoint shipped a
/// runtime-fatal EF translation bug (<c>FindExactMatchesAsync</c>'s
/// <c>ToLowerInvariant()</c>) that no test caught because the only two batch tests
/// were single-item, all-<c>Created</c>, 200-OK cases. These drive the multi-item
/// 207 shape, per-item verdicts, shared-tenant scope escalation, authz, and size
/// boundaries.
/// <para>
/// Robust against the content-independent mock embedding generator (tracked for a
/// proper fix in #353): the <c>Duplicate</c> branch uses an EXACT-match duplicate
/// (same domain+title+body — independent of embedding similarity, and the exact
/// path that re-exercises <c>FindExactMatchesAsync</c>), and every <c>Created</c>
/// item uses a per-test-unique domain so the mock's identical vectors cannot
/// collapse it into a false semantic duplicate.
/// </para>
/// </summary>
[Collection("Postgres")]
public class BatchEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public BatchEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        await db.ExpertiseAuditLogs.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.ExpertiseEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientWithScopes(params string[] scopes)
    {
        var token = JwtTokenMinter.Mint(tenant: "test", scopes: scopes, groups: ["group-test"]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ExpertiseEntry> SeedApproved(string domain, string title, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var entry = TestHelpers.SeedEntry(
            domain: domain, title: title, body: body, tenant: "test", reviewState: ReviewState.Approved);
        db.ExpertiseEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private static object Item(string domain, string title, string body, string? tenant = null) => new
    {
        domain,
        title,
        body,
        entryType = "Pattern",
        severity = "Info",
        source = "test",
        tenant,
    };

    private static string UniqueDomain() => $"batch-{Guid.NewGuid():N}";

    /// <summary>Mirror of the server's <c>BatchEntryResult</c> — Status arrives as the enum name.</summary>
    private sealed record BatchResult(int Index, string Status, Guid? Id, string? Error);

    // ---- Multi-item verdicts ---------------------------------------------

    [Fact]
    public async Task Batch_MixedItems_Returns207_WithCorrectPerItemVerdicts()
    {
        var existing = await SeedApproved("dup-domain", "existing title", "existing body");
        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var freshDomain = UniqueDomain();

        var response = await client.PostAsJsonAsync("/expertise/batch", new[]
        {
            Item("dup-domain", "existing title", "existing body"), // exact duplicate -> Duplicate(existing.Id)
            Item(freshDomain, "brand new", "brand new body"),      // unique domain  -> Created
            Item(freshDomain, "", "blank title body"),             // invalid        -> Rejected
        });

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var results = (await response.Content.ReadFromJsonAsync<List<BatchResult>>())!;
        results.Should().HaveCount(3);

        results[0].Status.Should().Be("Duplicate");
        results[0].Id.Should().Be(existing.Id, "an exact title+body match collapses onto the existing entry");
        results[1].Status.Should().Be("Created");
        results[1].Id.Should().NotBeNull();
        results[2].Status.Should().Be("Rejected");
        results[2].Id.Should().BeNull();
        results[2].Error.Should().NotBeNullOrWhiteSpace();

        // The Created item actually persisted as a Draft in the caller's tenant.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var created = await db.ExpertiseEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == results[1].Id);
        created.Tenant.Should().Be("test");
        created.ReviewState.Should().Be(ReviewState.Draft);
    }

    [Fact]
    public async Task Batch_AgainstPopulatedDomainCorpus_DedupsPerItem_WithoutUnboundedScan()
    {
        // #333 Finding 4: the batch dedup path used to load EVERY embedding in the domain
        // into memory and brute-force cosine distance in C# (O(batchSize x domainSize),
        // unbounded as a domain grows). It now issues one HNSW-indexed
        // FindNearestInDomainAsync per item — the same DB-side query the single-create
        // path uses. This drives that path against a domain corpus larger than any prior
        // batch fixture to prove it stays correct and bounded with a populated index.
        //
        // The content-derived mock embedding generator is all-or-nothing (identical vs
        // orthogonal), so a GRADED near-dup cannot be produced here — the semantic
        // threshold itself is validated by the EXPERTISE_EVAL DedupThresholdEvalTests
        // against the real model. This test proves the exact-match verdict and the
        // per-item semantic MISS (a distinct item is not a false duplicate) both hold
        // when the domain is well populated.
        const string domain = "populated-corpus-domain";
        for (var i = 0; i < 30; i++)
            await SeedApproved(domain, $"corpus title {i}", $"corpus body number {i}");
        var dupTarget = await SeedApproved(domain, "corpus duplicate anchor", "anchor body");

        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await client.PostAsJsonAsync("/expertise/batch", new[]
        {
            Item(domain, "corpus duplicate anchor", "anchor body"), // exact dup of dupTarget
            Item(domain, "genuinely new in corpus", "unrelated new body"), // distinct -> Created
        });

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var results = (await response.Content.ReadFromJsonAsync<List<BatchResult>>())!;
        results.Should().HaveCount(2);
        results[0].Status.Should().Be("Duplicate");
        results[0].Id.Should().Be(dupTarget.Id);
        results[1].Status.Should().Be("Created", "a distinct item is not a false semantic duplicate even in a populated domain");
        results[1].Id.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_AllValidDistinctItems_Returns200()
    {
        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);

        var response = await client.PostAsJsonAsync("/expertise/batch", new[]
        {
            Item(UniqueDomain(), "one", "body one"),
            Item(UniqueDomain(), "two", "body two"),
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "200 only when every item is Created");
        var results = (await response.Content.ReadFromJsonAsync<List<BatchResult>>())!;
        results.Should().OnlyContain(r => r.Status == "Created");
    }

    // ---- Shared-tenant override scope escalation -------------------------

    [Fact]
    public async Task Batch_OverlongBodyItem_RejectedWithoutFailingSiblings()
    {
        var freshDomain = UniqueDomain();
        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);

        var response = await client.PostAsJsonAsync("/expertise/batch", new[]
        {
            Item(freshDomain, "within cap", "a normal-sized body"),
            Item(freshDomain, "over cap", new string('a', 16001)), // #429 MaxBodyLength = 16000 (ADR-017)
        });

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var results = (await response.Content.ReadFromJsonAsync<List<BatchResult>>())!;
        results[0].Status.Should().Be("Created");
        results[1].Status.Should().Be("Rejected");
        results[1].Error.Should().Contain("maximum length of 16000");
    }

    [Fact]
    public async Task Batch_SharedOverride_ByWriteDraftCaller_ItemRejected()
    {
        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);

        var response = await client.PostAsJsonAsync("/expertise/batch", new[]
        {
            Item(UniqueDomain(), "shared attempt", "body", tenant: "shared"),
        });

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var results = (await response.Content.ReadFromJsonAsync<List<BatchResult>>())!;
        results[0].Status.Should().Be("Rejected");
        results[0].Error.Should().Contain("approve", "shared creation requires expertise.write.approve");
    }

    [Fact]
    public async Task Batch_SharedOverride_ByWriteApproveCaller_ItemCreatedAsShared()
    {
        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);
        var domain = UniqueDomain();

        var response = await client.PostAsJsonAsync("/expertise/batch", new[]
        {
            Item(domain, "shared entry", "shared body", tenant: "shared"),
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = (await response.Content.ReadFromJsonAsync<List<BatchResult>>())!;
        results[0].Status.Should().Be("Created");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var created = await db.ExpertiseEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == results[0].Id);
        created.Tenant.Should().Be("shared");
        created.ReviewState.Should().Be(ReviewState.Approved, "shared entries bypass the tenant-scoped draft queue");
    }

    // ---- Authorization ----------------------------------------------------

    [Fact]
    public async Task Batch_WithNoAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/expertise/batch", new[] { Item(UniqueDomain(), "t", "b") });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Batch_WithReadOnlyScope_Returns403()
    {
        var client = ClientWithScopes(AuthConstants.ReadScope);
        var response = await client.PostAsJsonAsync("/expertise/batch", new[] { Item(UniqueDomain(), "t", "b") });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "WriteAccess requires expertise.write.draft");
    }

    // ---- Size boundaries --------------------------------------------------

    [Fact]
    public async Task Batch_ExceedingMaxSize_Returns400()
    {
        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var domain = UniqueDomain();
        var items = Enumerable.Range(0, 101).Select(i => Item(domain, $"t{i}", $"b{i}")).ToArray();

        var response = await client.PostAsJsonAsync("/expertise/batch", items);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "MaxBatchSize is 100");
    }

    [Fact]
    public async Task Batch_EmptyArray_Returns400()
    {
        var client = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await client.PostAsJsonAsync("/expertise/batch", Array.Empty<object>());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
