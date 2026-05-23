using System.Net;
using System.Net.Http.Headers;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Integration;

[Collection("Postgres")]
public class AuthEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;

    public AuthEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_postgres.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Health_WithNoAuth_Returns200()
    {
        var client = TestHelpers.CreateUnauthenticatedClient(_factory);
        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/expertise")]
    [InlineData("/expertise/search?q=test")]
    public async Task ProtectedEndpoints_WithNoAuth_Return401(string path)
    {
        var client = TestHelpers.CreateUnauthenticatedClient(_factory);
        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/expertise")]
    [InlineData("/expertise/search?q=test")]
    public async Task ProtectedEndpoints_WithValidKey_Return2xx(string path)
    {
        var client = TestHelpers.CreateAuthenticatedClient(_factory);
        var response = await client.GetAsync(path);

        ((int)response.StatusCode).Should().BeInRange(200, 299);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithWrongKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "wrong-key");

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithMalformedScheme_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Basic dGVzdDp0ZXN0");

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
