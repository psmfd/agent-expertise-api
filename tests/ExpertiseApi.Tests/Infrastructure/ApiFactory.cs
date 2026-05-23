#pragma warning disable SKEXP0070

using ExpertiseApi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Pgvector;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace ExpertiseApi.Tests.Infrastructure;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public ApiFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Override DefaultConnection so any DI consumer that reads from
        // IConfiguration (e.g. the singleton NpgsqlDataSource backing
        // IIdempotencyStore per ADR-010) sees the testcontainer connection
        // string. The DbContext is also explicitly re-registered below —
        // we do both because the two consumers acquire the value through
        // different paths. Mirrors the same override in JwtApiFactory;
        // required since the hard-require flip (2026-05-19) makes the
        // idempotency store actively engage on every POST in this suite.
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            var silentLogger = new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();
            logging.AddSerilog(silentLogger, dispose: true);
        });

        builder.ConfigureServices(services =>
        {
            // Replace DbContext with test container connection
            var dbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ExpertiseDbContext>));
            if (dbDescriptor is not null)
                services.Remove(dbDescriptor);

            services.AddDbContext<ExpertiseDbContext>(options =>
                options.UseNpgsql(_connectionString, o => o.UseVector()));

            // Replace ONNX embedding generator with a mock that returns 384-dim vectors.
            // AddBertOnnxEmbeddingGenerator opens the model file at registration time,
            // so we must remove the existing registration and substitute it.
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
                        new Random(42).NextBytes(new byte[4]); // deterministic seed
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

        builder.UseSetting("Auth:ApiKey", TestHelpers.TestApiKey);
        // The API key handler defaults Tenant to "legacy"; align the test client with the
        // SeedEntry helper's default tenant ("test") so existing tests don't all fail under
        // the new tenant filter.
        builder.UseSetting("Auth:ApiKeyDefaults:DefaultTenant", TestHelpers.TestTenant);
    }
}
