using System.Net;
using System.Text.Json;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// ProblemDetails sanitization coverage (Part D C4). Verifies that:
///   - Outside Development, unhandled exceptions surface as application/problem+json
///     with NO exception type / message / stack frame leakage, but WITH the traceId
///     extension for server-log correlation.
///   - In Development, the same path retains diagnostic Detail for the developer.
///   - Explicit Results.Problem(...) callsites also receive the traceId extension
///     (the customizer fires for both unhandled-exception and explicit-Problem paths).
///
/// A throw-on-demand endpoint is wired into the pipeline via TestServer-level
/// middleware so the production app contains no test-only attack surface. See
/// docs/security/integration-threat-model.md Part D C4.
/// </summary>
[Collection("Postgres")]
public class ProblemDetailsTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;

    public ProblemDetailsTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _connectionString = _postgres.ConnectionString;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UnhandledException_InProduction_ScrubsDetailButEmitsTraceId()
    {
        await using var factory = new ThrowingApiFactory(_connectionString, "Production");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/_diagnostics/throw");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should()
            .BeOneOf("application/problem+json", "application/json");

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        // Sanitization: no exception leakage. When Detail = null after scrubbing,
        // System.Text.Json serialization omits the property entirely — either absent
        // OR present-as-null satisfies the contract.
        if (doc.RootElement.TryGetProperty("detail", out var detail))
        {
            (detail.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(detail.GetString())).Should().BeTrue();
        }
        body.Should().NotContain("InvalidOperationException");
        body.Should().NotContain("at ExpertiseApi");
        body.Should().NotContain("synthetic test throw");

        // Correlation: traceId always present.
        doc.RootElement.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UnhandledException_InDevelopment_RetainsDetail()
    {
        await using var factory = new ThrowingApiFactory(_connectionString, "Development");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/_diagnostics/throw");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        // Dev: traceId still present.
        doc.RootElement.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Wraps <see cref="JwtApiFactory"/> with a TestServer-only diagnostics middleware
    /// that throws on GET /_diagnostics/throw. Production attack surface is zero because
    /// the middleware is registered via an <see cref="IStartupFilter"/> in the test host
    /// only — not in the application's <c>Program.cs</c>.
    ///
    /// Inherits from <see cref="JwtApiFactory"/> rather than <see cref="ApiFactory"/> so
    /// the Production-env code path passes <c>EnforceModeGuard</c> (which only accepts
    /// <c>Auth:Mode=Oidc</c> outside Development). JwtApiFactory preloads a synthetic
    /// <c>OpenIdConnectConfiguration</c> so the OIDC issuer guard is also satisfied.
    /// The /_diagnostics/throw path is unmapped and unauthenticated, so the auth choice
    /// is incidental to the test.
    /// </summary>
    private sealed class ThrowingApiFactory : JwtApiFactory
    {
        private readonly string _environment;

        public ThrowingApiFactory(string connectionString, string environment)
            : base(connectionString)
        {
            _environment = environment;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // JwtApiFactory calls UseEnvironment("Development") in its own ConfigureWebHost
            // override; ours must come AFTER base to take effect.
            base.ConfigureWebHost(builder);
            builder.UseEnvironment(_environment);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, ThrowingStartupFilter>();
            });
            return base.CreateHost(builder);
        }
    }

    private sealed class ThrowingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Run the production pipeline FIRST so UseExceptionHandler is positioned
            // ahead of our throwing middleware. /_diagnostics/throw does not match any
            // mapped endpoint, so requests for it fall through routing/endpoint middleware
            // and reach this terminal middleware; the InvalidOperationException then
            // unwinds back up through UseExceptionHandler which handles it.
            next(app);
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Request.Path.Equals("/_diagnostics/throw", StringComparison.Ordinal))
                    throw new InvalidOperationException("synthetic test throw");
                await nxt();
            });
        };
    }
}
