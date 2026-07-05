using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// ADR-013 hub-side acceptance for origin attribution on <c>POST /expertise/batch</c>:
///   1. A registered sync client (azp in Sync:KnownInstances) gets a SERVER-derived
///      OriginInstanceId; the body-supplied OriginAuthorPrincipal is stored as
///      informational context; a write.draft credential lands the entry as Draft in
///      its own tenant — the supply-chain control needs no sync-specific code.
///   2. An UNREGISTERED caller gets no OriginInstanceId even when the body tries to
///      assert origin — the payload is never trusted for identity.
/// </summary>
[Collection("Postgres")]
public class SyncOriginAttributionTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SyncOriginAttributionTests(PostgresFixture fixture) => _fixture = fixture;

    private const string SpokeClientId = "spoke-alpha-client";
    private const string SpokeInstanceId = "spoke-alpha";

    private JwtApiFactory NewFactory() => new(
        _fixture.ConnectionString,
        new Dictionary<string, string?>
        {
            [$"Sync:KnownInstances:{SpokeClientId}"] = SpokeInstanceId,
        });

    // Domain is per-test: the shared JwtApiFactory embedding mock returns an identical
    // vector for every input, so two tests writing the same domain would semantic-dedup
    // against each other (dedup is domain-scoped by design).
    private static object BatchItem(string domain, string title, string? originAuthor = null) => new
    {
        domain,
        title,
        body = $"body of {title}",
        entryType = "Pattern",
        severity = "Info",
        source = "sync",
        originAuthorPrincipal = originAuthor,
    };

    [Fact]
    public async Task RegisteredSyncClient_GetsServerDerivedOriginInstanceId_AndLandsAsDraft()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        // client_credentials shape: sub == azp → ActorClass.Service (ADR-008).
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: ["expertise.write.draft", "expertise.read"],
            sub: SpokeClientId,
            azp: SpokeClientId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var title = $"synced entry {Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/expertise/batch",
            new[] { BatchItem("origin-attribution-registered", title, originAuthor: "alice@spoke-alpha") });

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "batch response was: {0}", responseBody);

        await using var db = NewContext();
        var entry = await db.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Title == title);

        entry.OriginInstanceId.Should().Be(SpokeInstanceId, "derived server-side from the authenticated azp");
        entry.OriginAuthorPrincipal.Should().Be("alice@spoke-alpha");
        entry.Tenant.Should().Be("test", "the hub assigns the spoke's tenant from the token");
        entry.ReviewState.Should().Be(ReviewState.Draft,
            "a write.draft credential cannot land anything pre-approved — THE ADR-013 supply-chain control");
    }

    [Fact]
    public async Task UnregisteredCaller_GetsNoOriginInstanceId_EvenWhenBodyAssertsOrigin()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: ["expertise.write.draft", "expertise.read"],
            sub: "ordinary-user",
            azp: "not-a-registered-spoke");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var title = $"ordinary entry {Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/expertise/batch",
            new[] { BatchItem("origin-attribution-unregistered", title, originAuthor: "mallory@fake-origin") });

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "batch response was: {0}", responseBody);

        await using var db = NewContext();
        var entry = await db.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Title == title);

        entry.OriginInstanceId.Should().BeNull(
            "an unmapped client id yields no origin attribution; the body cannot self-certify one");
        entry.OriginAuthorPrincipal.Should().Be("mallory@fake-origin",
            "the informational field is stored verbatim — it grants nothing and is hygienized on read");
    }

    private ExpertiseDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.UseVector())
            .Options;
        return new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
    }
}
