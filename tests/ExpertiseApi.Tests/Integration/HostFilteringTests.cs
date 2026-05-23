using System.Net;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// HostFiltering middleware coverage (Part D C1). The middleware is auto-wired
/// by the default WebHost when <c>AllowedHosts</c> is set; <c>appsettings.json</c>
/// tightens the default from <c>"*"</c> to <c>"localhost;127.0.0.1;[::1]"</c>
/// so DNS-rebind attacks against the laptop loopback (and any browser-origin
/// host-header spoofing against the container) are rejected at the edge.
///
/// These tests run against the in-memory TestServer rather than Kestrel, but
/// HostFiltering runs in the middleware pipeline so the asserts are valid.
/// See docs/security/integration-threat-model.md Part D C1.
/// </summary>
[Collection("Postgres")]
public class HostFilteringTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;

    public HostFilteringTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_postgres.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AllowedHost_Localhost_Returns200()
    {
        using var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        // ApiFactory's default base address is http://localhost/, which carries Host: localhost
        // — explicit set documents intent.
        req.Headers.Host = "localhost";
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AllowedHost_LoopbackIpv4_Returns200()
    {
        using var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Host = "127.0.0.1";
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DisallowedHost_ArbitraryDomain_Returns400()
    {
        using var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Host = "evil.example.com";
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
