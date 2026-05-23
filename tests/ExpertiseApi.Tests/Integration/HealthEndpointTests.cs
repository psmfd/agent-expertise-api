using System.Net;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Postgres-backed integration coverage for the live/ready endpoint split
/// introduced in issue #143 / T4. Unit-level shape regressions live in
/// <c>tests/Unit/HealthEndpointTests.cs</c>; this class exercises the happy
/// path against a real Postgres so we cover the <c>AddDbContextCheck</c>
/// success branch that the unit suite (stub conn string) cannot.
/// </summary>
[Collection("Postgres")]
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;

    public HealthEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_postgres.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task HealthLive_Returns200_WithHealthyStack()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_Returns200_WithHealthyStack()
    {
        // PostgresFixture seeds the schema via EnsureCreated/migrations before
        // the API factory boots, so all three "ready" checks (db, onnx mock,
        // migrations) report Healthy and /health/ready returns 200.
        var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthRoot_AliasReturnsSameStatusAsReady()
    {
        // /health is the back-compat alias for /health/ready. Pre-existing
        // probes / monitors must observe identical semantics after the cutover
        // (issue #143 acceptance criteria).
        var client = _factory.CreateClient();
        var rootResponse = await client.GetAsync(new Uri("/health", UriKind.Relative));
        var readyResponse = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        rootResponse.StatusCode.Should().Be(readyResponse.StatusCode);
        rootResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
