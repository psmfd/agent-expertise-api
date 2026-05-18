using System.Net;
using System.Text.Json;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Integration coverage for the runtime OpenAPI document endpoint (#146).
///
/// Validates:
/// - /openapi/v1.json is reachable WITHOUT a bearer token (anonymous discovery is
///   required because downstream tools must read the spec before they hold credentials).
/// - The document declares the Bearer security scheme via the
///   <see cref="ExpertiseApi.OpenApi.BearerSecuritySchemeTransformer"/>.
/// - The document covers the documented endpoint surface (Expertise / Search / Audit
///   route groups) with per-status responses, proving the per-endpoint
///   <c>.WithSummary</c>/<c>.Produces</c>/<c>.ProducesProblem</c> decorations land.
///
/// The test does NOT verify that ApiExplorer correctly enumerates health checks
/// (the framework excludes <c>MapHealthChecks</c> from the API surface by default;
/// see <see cref="ExpertiseApi.Endpoints.HealthEndpoints"/>).
/// </summary>
[Collection("Postgres")]
public class OpenApiTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private ApiFactory _factory = null!;

    public OpenApiTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_postgres.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task OpenApi_IsReachableWithoutAuth_AndAdvertisesBearerScheme()
    {
        var client = _factory.CreateClient();

        // No Authorization header. The endpoint is .AllowAnonymous() and .DisableRateLimiting().
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // OpenAPI 3.0/3.1: top-level "openapi" version string is required.
        root.TryGetProperty("openapi", out var versionEl).Should().BeTrue();
        versionEl.GetString().Should().StartWith("3.");

        // Bearer scheme advertised under components.securitySchemes.
        root.TryGetProperty("components", out var components).Should().BeTrue();
        components.TryGetProperty("securitySchemes", out var schemes).Should().BeTrue();
        schemes.TryGetProperty("Bearer", out var bearer).Should().BeTrue();
        bearer.GetProperty("type").GetString().Should().Be("http");
        bearer.GetProperty("scheme").GetString().Should().Be("bearer");
        bearer.GetProperty("bearerFormat").GetString().Should().Be("JWT");

        // Document-level security requirement references the Bearer scheme.
        root.TryGetProperty("security", out var security).Should().BeTrue();
        security.ValueKind.Should().Be(JsonValueKind.Array);
        var hasBearerRequirement = security.EnumerateArray()
            .Any(r => r.TryGetProperty("Bearer", out _));
        hasBearerRequirement.Should().BeTrue();
    }

    [Fact]
    public async Task OpenApi_DocumentsDecoratedEndpoints_WithSummariesAndStatusCodes()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths");

        // Spot-check three representative endpoints across the route groups; full
        // matrix coverage would just shadow the per-endpoint .Produces decorations
        // we can read directly in source. Summary checks are non-empty rather than
        // substring-match so harmless copy-edits don't break the suite.
        AssertOperation(paths, "/expertise", "get",
            expectedStatuses: ["200", "401", "403", "429"]);

        AssertOperation(paths, "/expertise", "post",
            expectedStatuses: ["201", "400", "401", "403", "409", "429"]);

        AssertOperation(paths, "/expertise/search/semantic", "get",
            expectedStatuses: ["200", "400", "401", "403", "429"]);
    }

    private static void AssertOperation(
        JsonElement paths,
        string path,
        string verb,
        string[] expectedStatuses)
    {
        paths.TryGetProperty(path, out var pathItem).Should().BeTrue($"path {path} should be documented");
        pathItem.TryGetProperty(verb, out var operation).Should().BeTrue($"{verb.ToUpperInvariant()} {path} should be documented");

        operation.GetProperty("summary").GetString().Should().NotBeNullOrWhiteSpace(
            $"{verb.ToUpperInvariant()} {path} should carry a non-empty summary");

        var responses = operation.GetProperty("responses");
        foreach (var status in expectedStatuses)
        {
            responses.TryGetProperty(status, out _).Should().BeTrue(
                $"{verb.ToUpperInvariant()} {path} should declare response {status}");
        }
    }
}
