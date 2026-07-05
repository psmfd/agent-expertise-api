#pragma warning disable SKEXP0070

using ExpertiseApi.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;
using Pgvector;
using Serilog;

namespace ExpertiseApi.Tests.Infrastructure;

/// <summary>
/// API factory configured for OIDC authentication. The JWT path is exercised end-to-end
/// against an in-memory RSA signing key — no JWKS HTTP fetch is performed because we
/// preload <see cref="OpenIdConnectConfiguration"/> on the named JwtBearer scheme via
/// <c>PostConfigure</c>.
/// </summary>
public class JwtApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string?>? _extraSettings;

    public JwtApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? extraSettings = null)
    {
        _connectionString = connectionString;
        _extraSettings = extraSettings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:Mode", "Oidc");

        // Test-specific configuration overlays (e.g. Sync:KnownInstances for
        // ADR-013 origin-attribution tests).
        if (_extraSettings is not null)
        {
            foreach (var (key, value) in _extraSettings)
                builder.UseSetting(key, value);
        }

        // Override DefaultConnection so any DI consumer that reads from
        // IConfiguration (e.g. the singleton NpgsqlDataSource backing
        // IIdempotencyStore per ADR-010) sees the testcontainer connection
        // string. The DbContext is also explicitly re-registered below — we
        // do both because the two consumers acquire the value through
        // different paths.
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);

        // Configure a single test issuer mirroring Authentik-style config
        // (TenantSource = Groups, ScopeClaim = scope).
        builder.UseSetting("Auth:Oidc:Issuers:0:Name", JwtTokenMinter.TestSchemeName);
        builder.UseSetting("Auth:Oidc:Issuers:0:Issuer", JwtTokenMinter.TestIssuer);
        builder.UseSetting("Auth:Oidc:Issuers:0:Audience", JwtTokenMinter.TestAudience);
        builder.UseSetting("Auth:Oidc:Issuers:0:ScopeClaims:0", "scope");
        builder.UseSetting("Auth:Oidc:Issuers:0:TenantSource", "Groups");
        builder.UseSetting("Auth:Oidc:Issuers:0:GroupClaim", "groups");
        builder.UseSetting("Auth:Oidc:Issuers:0:GroupToTenantMapping:group-test", "test");
        builder.UseSetting("Auth:Oidc:Issuers:0:GroupToTenantMapping:group-shared", "shared");
        // Second tenant for cross-tenant isolation tests.
        builder.UseSetting("Auth:Oidc:Issuers:0:GroupToTenantMapping:group-other", "other-team");

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            var silentLogger = new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();
            logging.AddSerilog(silentLogger, dispose: true);
        });

        builder.ConfigureServices(services =>
        {
            // Inject pre-built OpenIdConnectConfiguration so JwtBearer skips the JWKS HTTP fetch.
            services.PostConfigure<JwtBearerOptions>(JwtTokenMinter.TestSchemeName, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = JwtTokenMinter.TestIssuer
                };
                options.Configuration.SigningKeys.Add(JwtTokenMinter.SigningKey);
                options.TokenValidationParameters.IssuerSigningKey = JwtTokenMinter.SigningKey;
                options.MetadataAddress = null!;
                // Suppress the default ConfigurationManager so no code path can trigger
                // a metadata fetch on https://test-issuer.local/.
                options.ConfigurationManager = null;
            });

            // Replace DbContext with test container connection.
            var dbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ExpertiseDbContext>));
            if (dbDescriptor is not null)
                services.Remove(dbDescriptor);

            services.AddDbContext<ExpertiseDbContext>(options =>
                options.UseNpgsql(_connectionString, o => o.UseVector()));

            // Replace ONNX embedding generator with a deterministic mock.
            var embeddingDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
            if (embeddingDescriptor is not null)
                services.Remove(embeddingDescriptor);

            var mockGenerator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
            mockGenerator.GenerateAsync(
                    Arg.Any<IEnumerable<string>>(),
                    Arg.Any<EmbeddingGenerationOptions?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var inputs = callInfo.ArgAt<IEnumerable<string>>(0).ToList();
                    var result = new GeneratedEmbeddings<Embedding<float>>();
                    foreach (var _ in inputs)
                    {
                        var vector = new float[384];
                        for (var i = 0; i < 384; i++)
                            vector[i] = (float)(new Random(42 + i).NextDouble() * 2 - 1);
                        result.Add(new Embedding<float>(vector));
                    }
                    return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(result);
                });

            services.AddSingleton(mockGenerator);
            // Auto-inject Idempotency-Key on POSTs server-side so the
            // suite's pre-existing PostAsJsonAsync call sites still work
            // under the hard-require flip (ADR-010, 2026-05-19).
            services.AddTransient<IStartupFilter, AutoIdempotencyKeyStartupFilter>();
        });
    }
}
