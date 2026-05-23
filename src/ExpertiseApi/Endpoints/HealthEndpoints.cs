using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
namespace ExpertiseApi.Endpoints;

internal static class HealthEndpoints
{
    /// <summary>
    /// Status-only plaintext writer. Mirrors the framework's default behavior
    /// (<c>HealthCheckResponseWriters.WriteMinimalPlaintext</c>, which is
    /// internal and so cannot be referenced directly) but is defined here
    /// explicitly so any future maintainer must consciously replace it before
    /// any per-check descriptions can reach the wire. The descriptions in
    /// <see cref="ExpertiseApi.Services.Health.OnnxModelHealthCheck"/>
    /// (absolute filesystem path) and <see cref="ExpertiseApi.Services.Health.PendingMigrationHealthCheck"/>
    /// (EF migration class names + exception messages) are operator-facing
    /// diagnostics and must not be exposed on the unauthenticated /health/*
    /// endpoints.
    /// </summary>
    private static Task WriteStatusOnlyPlainText(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(report.Status.ToString());
    }
    /// <summary>
    /// Wires the three health endpoints required by Phase-1 A2 (issue #143):
    ///   /health/live  — liveness: 200 while the process responds. Predicate
    ///                   matches no checks so the response is independent of
    ///                   downstream dependencies. systemd <c>WatchdogSec=</c>
    ///                   and k8s livenessProbe should point here.
    ///   /health/ready — readiness: aggregates every check tagged "ready"
    ///                   (DB ping via AddDbContextCheck, ONNX model presence,
    ///                   pending migrations). 503 on Unhealthy or Degraded.
    ///                   k8s readinessProbe and load-balancer health checks
    ///                   should point here.
    ///   /health       — back-compat alias for /health/ready. Pre-existing
    ///                   probes / monitors continue to work without
    ///                   coordinated cutover.
    /// </summary>
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Pin the response writer to a status-only plaintext format. The
        // descriptions emitted by OnnxModelHealthCheck (absolute filesystem path)
        // and PendingMigrationHealthCheck (migration class names, EF exception)
        // are operator-facing diagnostics and must never reach the wire on
        // these unauthenticated endpoints. See WriteStatusOnlyPlainText doc.
        var liveOptions = new HealthCheckOptions
        {
            Predicate = static _ => false,
            ResponseWriter = WriteStatusOnlyPlainText,
        };

        // Override the framework default ResultStatusCodes mapping so
        // HealthStatus.Degraded surfaces as 503 on /health/ready, not the
        // default 200. PendingMigrationHealthCheck reports Degraded for
        // unapplied migrations — if Degraded mapped to 200 here, k8s
        // readinessProbe and the LB health check would route traffic to a
        // pod whose schema is behind, violating issue #143 acceptance.
        // See https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks#customize-output
        var readyOptions = new HealthCheckOptions
        {
            Predicate = static check => check.Tags.Contains("ready"),
            ResponseWriter = WriteStatusOnlyPlainText,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        };

        // NOTE: Health endpoints are intentionally absent from the generated OpenAPI
        // document. MapHealthChecks does not register API-Explorer descriptors
        // (no MethodInfo / ParameterDescriptions), so summary / Produces metadata
        // attached here would be dead weight. The endpoints are documented in the
        // README "API Surface" table and exercised by HealthEndpointTests — those
        // are the actual contract surface for operators.

        app.MapHealthChecks("/health/live", liveOptions)
            .WithTags("Health")
            .AllowAnonymous()
            .DisableRateLimiting();

        // OutputCache (policy "health-ready", 2s) caps the underlying
        // health-check execution rate to one per 2-second window. Issue #158:
        // /health/ready is AllowAnonymous and runs AddDbContextCheck's
        // CanConnectAsync per probe — an unauthenticated DoS amplifier without
        // the cache. /health/live is intentionally NOT cached: its Predicate
        // matches no checks (response is a const string), so caching adds
        // overhead with no benefit.
        app.MapHealthChecks("/health/ready", readyOptions)
            .WithTags("Health")
            .AllowAnonymous()
            .DisableRateLimiting()
            .CacheOutput("health-ready");

        app.MapHealthChecks("/health", readyOptions)
            .WithTags("Health")
            .AllowAnonymous()
            .DisableRateLimiting()
            .CacheOutput("health-ready");
    }
}
