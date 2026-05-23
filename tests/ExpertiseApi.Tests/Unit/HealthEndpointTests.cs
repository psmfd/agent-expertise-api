using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Net;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// Endpoint-shape regression tests for the /health/live, /health/ready, and /health
/// (back-compat alias) endpoints introduced in issue #143 / T4.
///
/// Uses <see cref="ApiFactory"/> with a deliberately bad Postgres connection
/// string so AddDbContextCheck fails fast, exercising the 503 path on /ready
/// without requiring Testcontainers / Docker. /live must remain 200 because
/// its HealthCheckOptions.Predicate matches no checks at all.
/// </summary>
[Collection("SequentialApiFactory")]
public class HealthEndpointTests
{
    // Port 1 is reserved (TCPMUX) and not bound on any sane host, so Npgsql's
    // initial TCP connect fails immediately rather than waiting for the default
    // 15s connect timeout. Keeps the test under a second.
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=stub;Username=stub;Password=stub;Timeout=2;Command Timeout=2";

    [Fact]
    public async Task HealthLive_Returns200_EvenWithBrokenDependencies()
    {
        // /health/live is liveness-only: Predicate matches zero checks, so the
        // response is independent of DB, ONNX model, and migration state.
        // systemd WatchdogSec= and k8s livenessProbe rely on this contract.
        using var factory = new ApiFactory(UnreachableConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_Returns503_WhenDbUnreachable()
    {
        // /health/ready aggregates every check tagged "ready". With an
        // unreachable Postgres, AddDbContextCheck reports Unhealthy, which the
        // default HealthCheckOptions translates to 503. k8s readinessProbe
        // observes this and removes the pod from Service endpoints.
        using var factory = new ApiFactory(UnreachableConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task HealthRoot_IsAliasFor_HealthReady()
    {
        // /health is the back-compat alias for /health/ready. Pre-existing
        // probes / monitors must observe identical semantics after the cutover
        // so no coordinated rollout is required (issue #143 acceptance).
        using var factory = new ApiFactory(UnreachableConnectionString);
        using var client = factory.CreateClient();

        var rootResponse = await client.GetAsync(new Uri("/health", UriKind.Relative));
        var readyResponse = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        rootResponse.StatusCode.Should().Be(readyResponse.StatusCode);
    }

    [Fact]
    public async Task HealthReady_Returns503_WhenAnyCheckIsDegraded()
    {
        // Issue #143 acceptance: pending migrations → 503 on /health/ready.
        // PendingMigrationHealthCheck returns HealthStatus.Degraded for that
        // state, and the ASP.NET Core framework default maps Degraded → 200,
        // so HealthEndpoints.MapHealthEndpoints overrides ResultStatusCodes
        // to surface Degraded as 503. This test isolates the Degraded → 503
        // routing by:
        //   (1) clearing the production health-check registrations (db, onnx,
        //       migrations) so they cannot mask the assertion — the unit-test
        //       env has no reachable DB, which would otherwise make the
        //       AddDbContextCheck return Unhealthy and 503 regardless of the
        //       ResultStatusCodes override, leaving the override untested.
        //   (2) registering a single AlwaysDegradedCheck tagged "ready" so the
        //       aggregate HealthReport.Status is exactly Degraded.
        //
        // With the ResultStatusCodes override in place, the response is 503.
        // Without it, the framework default maps Degraded → 200 and this test
        // fails — that is the regression guard.
        using var outerFactory = new ApiFactory("Host=127.0.0.1;Port=5432;Database=stub;Username=stub;Password=stub");
        using var factory = outerFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<HealthCheckServiceOptions>(options =>
                    options.Registrations.Clear());
                services.AddHealthChecks().AddCheck(
                    "always-degraded-stub",
                    new AlwaysDegradedCheck(),
                    failureStatus: HealthStatus.Degraded,
                    tags: ["ready"]);
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class AlwaysDegradedCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Degraded("stub: always degraded for routing assertion"));
    }

    [Fact]
    public async Task HealthReady_OutputCache_CollapsesConcurrentProbes()
    {
        // Issue #158: /health/ready is AllowAnonymous and previously ran the
        // underlying checks per probe — an unauthenticated DoS amplifier. The
        // "health-ready" OutputCache policy (Expire 2s) caps execution to one
        // per 2s window. This test proves the cache is wired in front of the
        // health-check pipeline by:
        //   (1) replacing the production registrations with a single counter
        //       check tagged "ready".
        //   (2) firing N concurrent /health/ready probes within the 2s cache
        //       window, then asserting the counter incremented exactly once.
        // Without CacheOutput on the endpoint this assertion fails with
        // Count == N — the regression guard.
        var counter = new CountingHealthCheck();
        using var outerFactory = new ApiFactory(UnreachableConnectionString);
        using var factory = outerFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<HealthCheckServiceOptions>(options =>
                    options.Registrations.Clear());
                services.AddHealthChecks().AddCheck(
                    "counter-stub",
                    counter,
                    failureStatus: HealthStatus.Unhealthy,
                    tags: ["ready"]);
            });
        });

        using var client = factory.CreateClient();

        const int probeCount = 20;
        var probes = Enumerable.Range(0, probeCount)
            .Select(_ => client.GetAsync(new Uri("/health/ready", UriKind.Relative)))
            .ToArray();
        var responses = await Task.WhenAll(probes);

        foreach (var response in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        // OutputCache lock semantics collapse concurrent misses to a single
        // executor invocation; tolerate a small slack ( == 2 ) for the race
        // where a probe arrives in the narrow window between the producer
        // returning and the cache entry being published. Anything beyond 2
        // would indicate the cache is not in front of the pipeline at all,
        // which is the regression this test guards against.
        counter.Count.Should().BeInRange(1, 2);
    }

    private sealed class CountingHealthCheck : IHealthCheck
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(HealthCheckResult.Healthy("counted"));
        }
    }
}
