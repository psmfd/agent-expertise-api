using System.Net;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Verifies that the /query debug UI and its backing static files are gated to the
/// Development environment per issue #124 (the page stores the bearer token in
/// localStorage and would be XSS-exfiltratable if shipped to production).
/// </summary>
[Collection("Postgres")]
public class QueryEndpointGatingTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _devFactory = null!;
    private WebApplicationFactory<Program> _prodFactory = null!;

    public QueryEndpointGatingTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        // WebApplicationFactory<TEntryPoint> defaults to ASPNETCORE_ENVIRONMENT=Development;
        // we explicitly override to Production for the gating assertion.
        //
        // The Production environment activates the API's startup guards
        // (EnforceOidcIssuersGuard in particular). Auth:Mode defaults to Oidc outside
        // Development and requires at least one issuer whose Issuer URL does not
        // start with '<TODO'. We're not exercising auth here — only /query routing —
        // so we supply a syntactically-valid placeholder issuer just to satisfy the
        // startup guard.
        _devFactory = new ApiFactory(_postgres.ConnectionString);
        _prodFactory = _devFactory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:Mode", "Oidc");
            b.UseSetting("Auth:Oidc:Issuers:0:Name", "test-issuer");
            b.UseSetting("Auth:Oidc:Issuers:0:Issuer", "https://example.test/");
            b.UseSetting("Auth:Oidc:Issuers:0:Audience", "test-audience");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _devFactory.DisposeAsync();

    [Fact]
    public async Task Query_Route_Returns404_InProduction()
    {
        var client = _prodFactory.CreateClient();
        var response = await client.GetAsync("/query");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task QueryHtml_StaticFile_Returns404_InProduction()
    {
        // wwwroot/query.html must NOT be reachable via UseStaticFiles either.
        // Both the /query route AND the underlying /query.html asset must be gated.
        var client = _prodFactory.CreateClient();
        var response = await client.GetAsync("/query.html");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Query_Route_ReturnsHtml_InDevelopment()
    {
        var client = _devFactory.CreateClient();
        var response = await client.GetAsync("/query");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }
}
