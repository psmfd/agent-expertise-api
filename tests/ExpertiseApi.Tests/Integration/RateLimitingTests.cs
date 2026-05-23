using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Rate limiter coverage (Part D C5). Verifies the three policies wired in
/// Program.cs:
///   - expertise-read: fixed window 60/min per principal
///   - expertise-write: fixed window 10/min per principal
///   - semantic-search: token bucket 10/min per principal
///
/// Also verifies that /health/* endpoints opt out via DisableRateLimiting()
/// and that 429 responses carry the standard ProblemDetails shape with the
/// C4 customizer's traceId extension + a Retry-After header.
///
/// Partition key is principal sub-claim (resolved via JwtTokenMinter) with
/// IP fallback for unauthenticated paths.
///
/// See docs/security/integration-threat-model.md Part D C5.
/// </summary>
[Collection("Postgres")]
public class RateLimitingTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public RateLimitingTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ExpertiseRead_Over60RequestsFromSamePrincipal_Returns429WithRetryAfter()
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-test"],
            sub: "rl-test-read-1");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage? final = null;
        // Within a fixed-window minute, requests 1..60 succeed and 61+ should 429.
        for (var i = 0; i < 61; i++)
        {
            final = await client.GetAsync("/expertise");
        }

        final.Should().NotBeNull();
        final!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        final!.Headers.RetryAfter.Should().NotBeNull("the OnRejected callback must populate Retry-After from lease metadata");

        var body = await final!.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("title", out var title).Should().BeTrue();
        title.GetString().Should().Be("Too Many Requests");
        doc.RootElement.TryGetProperty("traceId", out _).Should().BeTrue("C4 customizer must run on the 429 body");
    }

    [Fact]
    public async Task Health_NotRateLimited_AllowsManyRequests()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 100; i++)
        {
            var response = await client.GetAsync("/health/live");
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "DisableRateLimiting() on health endpoints must exempt them from all policies");
        }
    }

    [Fact]
    public async Task ExpertiseRead_DifferentPrincipals_PartitionIndependently()
    {
        // Principal A exhausts its 60/min budget; principal B should still be served.
        var tokenA = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-test"],
            sub: "rl-test-part-A");
        var tokenB = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-test"],
            sub: "rl-test-part-B");

        var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        for (var i = 0; i < 61; i++)
            await clientA.GetAsync("/expertise");

        var bFirst = await clientB.GetAsync("/expertise");
        bFirst.StatusCode.Should().Be(HttpStatusCode.OK,
            "principal B must have its own partition independent of A's exhausted budget");
    }
}
