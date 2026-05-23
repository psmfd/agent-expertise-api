using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ExpertiseApi.Tests.Infrastructure;

/// <summary>
/// Test-side <see cref="IStartupFilter"/> that auto-injects an
/// <c>Idempotency-Key</c> request header on every POST that doesn't
/// already carry one. Mirrors what the skill caller (PR #211) and the
/// pi extension caller (PR #212) do client-side; required because the
/// hard-require flip (ADR-010 amendment, 2026-05-19) makes the header
/// mandatory on the three POST writes and the suite has hundreds of
/// pre-existing <c>PostAsJsonAsync</c> call sites that don't supply one.
///
/// Implementation chose server-side middleware (rather than a
/// <see cref="DelegatingHandler"/> on the HttpClient) so that
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}.WithWebHostBuilder"/>
/// — which returns the base factory type and bypasses any subclass
/// HttpClient customisation — still picks up auto-injection. Several
/// tests (e.g. <c>ResponseHygieneIntegrationTests</c>,
/// <c>HealthEndpointTests</c>) build factories via <c>WithWebHostBuilder</c>
/// for unrelated reasons; the startup-filter approach catches all paths.
///
/// Opt-out: any test that wants to observe the truly-no-header server
/// behaviour can set the <see cref="SkipMarkerHeader"/> request header.
/// The middleware strips the marker before forwarding so it never reaches
/// the IdempotencyEndpointFilter (which would otherwise treat it as part
/// of the request hash inputs in some future revision).
/// </summary>
internal sealed class AutoIdempotencyKeyStartupFilter : IStartupFilter
{
    internal const string SkipMarkerHeader = "X-Test-Skip-Auto-Idempotency-Key";
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (HttpContext context, Func<Task> nextMiddleware) =>
            {
                bool skipRequested = context.Request.Headers.ContainsKey(SkipMarkerHeader);
                if (skipRequested)
                {
                    // Strip before any downstream filter or hash captures it.
                    context.Request.Headers.Remove(SkipMarkerHeader);
                }
                else if (HttpMethods.IsPost(context.Request.Method)
                         && !context.Request.Headers.ContainsKey(IdempotencyKeyHeader))
                {
                    context.Request.Headers[IdempotencyKeyHeader] = Guid.NewGuid().ToString("N");
                }

                await nextMiddleware();
            });
            next(app);
        };
    }
}
