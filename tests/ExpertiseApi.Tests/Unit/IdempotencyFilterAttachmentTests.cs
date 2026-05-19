using ExpertiseApi.Endpoints.Filters;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// Architecture guard for the Part D C3 control: exactly the three target
/// POSTs (POST /expertise, POST /expertise/{id}/approve,
/// POST /expertise/{id}/reject) carry the
/// <see cref="RequireIdempotencyMetadata"/> marker, and nothing else does.
/// <para>
/// Drift catches: (a) a future maintainer adds the filter to
/// /expertise/batch (out of scope per ADR-010) without an ADR; (b) a future
/// maintainer removes it from one of the three target routes; (c) a future
/// maintainer attaches it to a non-POST route.
/// </para>
/// </summary>
[Collection("Postgres")]
public class IdempotencyFilterAttachmentTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public IdempotencyFilterAttachmentTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);
        // Trigger host build so EndpointDataSource is populated.
        _ = _factory.Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void Idempotency_metadata_attached_to_exactly_the_three_target_posts()
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var attributedRoutes = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<RequireIdempotencyMetadata>() is not null)
            .Select(e => new
            {
                Method = e.Metadata.GetMetadata<HttpMethodMetadata>() is { HttpMethods.Count: > 0 } m ? m.HttpMethods[0] : "?",
                Pattern = e.RoutePattern.RawText ?? "?"
            })
            .OrderBy(x => x.Pattern, StringComparer.Ordinal)
            .ToList();

        attributedRoutes.Should().HaveCount(3, "ADR-010 attaches the filter to exactly three POSTs");
        attributedRoutes.Should().AllSatisfy(r => r.Method.Should().Be("POST"));

        var patterns = attributedRoutes.Select(r => r.Pattern).ToList();
        patterns.Should().Contain(p => p.EndsWith("/expertise/", StringComparison.Ordinal) || p.EndsWith("/expertise", StringComparison.Ordinal));
        patterns.Should().Contain(p => p.Contains("/approve", StringComparison.Ordinal));
        patterns.Should().Contain(p => p.Contains("/reject", StringComparison.Ordinal));

        // Negative: /expertise/batch is explicitly excluded per ADR-010.
        attributedRoutes.Should().NotContain(r => r.Pattern.Contains("/batch", StringComparison.Ordinal));
    }
}
