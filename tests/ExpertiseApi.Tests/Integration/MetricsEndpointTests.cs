using System.Net;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Integration;

[Collection("Postgres")]
public class MetricsEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;

    public MetricsEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_postgres.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Metrics_WithNoAuth_Returns200()
    {
        var client = TestHelpers.CreateUnauthenticatedClient(_factory);
        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusFormat()
    {
        var client = TestHelpers.CreateUnauthenticatedClient(_factory);
        var response = await client.GetAsync("/metrics");

        response.Content.Headers.ContentType!.MediaType.Should().StartWith("text/plain");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("http_request_duration_seconds");
    }
}
