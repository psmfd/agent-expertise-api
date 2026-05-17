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
        // to surface Degraded as 503. This test registers an additional
        // always-Degraded health check (tagged "ready") to exercise the
        // Degraded → 503 path independently of the real migration check,
        // which requires a reachable DB with a populated migration table.
        //
        // Without the ResultStatusCodes override in HealthEndpoints.cs this
        // test asserts 200 instead of 503; that is the regression guard.
        using var factory = new ApiFactory("Host=127.0.0.1;Port=5432;Database=stub;Username=stub;Password=stub")
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
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
}
