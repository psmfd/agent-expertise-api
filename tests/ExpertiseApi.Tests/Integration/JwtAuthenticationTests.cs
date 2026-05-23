using System.Net;
using System.Net.Http.Headers;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Integration;

[Collection("Postgres")]
public class JwtAuthenticationTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public JwtAuthenticationTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ValidJwt_WithReadScope_ReturnsOk()
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-test"]);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-test"],
            expiresIn: TimeSpan.FromMinutes(-5));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongAudience_Returns401()
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-test"],
            audience: "wrong-audience");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownIssuer_Returns401()
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-test"],
            issuer: "https://other-issuer.local/");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ValidToken_NoMappedTenant_Returns403()
    {
        // Group "group-unmapped" is not in the JwtApiFactory's GroupToTenantMapping config
        // (only group-test and group-shared are configured). Token validates but the
        // ScopeAuthorizationHandler refuses to satisfy the requirement when Tenant is null.
        var token = JwtTokenMinter.Mint(
            tenant: "doesnt-matter",
            scopes: [ExpertiseApi.Auth.AuthConstants.ReadScope],
            groups: ["group-unmapped"]);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ValidToken_InsufficientScope_Returns403()
    {
        // Mint with no scope at all — token validates but ScopeRequirement fails.
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [],
            groups: ["group-test"]);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
