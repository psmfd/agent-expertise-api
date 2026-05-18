using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ExpertiseApi.Endpoints;

internal static class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/expertise/search")
            .WithTags("Search")
            .RequireAuthorization("ReadAccess")
            .RequireRateLimiting("expertise-read");

        group.MapGet("/", KeywordSearch)
            .WithSummary("Keyword search over entry title + body")
            .WithDescription("PostgreSQL full-text search (`to_tsvector`) over Approved entries visible to the caller's tenant. " +
                             "Query string `q` is required. Set `includeDeprecated=true` to surface soft-deleted entries.")
            .Produces<List<ExpertiseEntry>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return group;
    }

    private static async Task<IResult> KeywordSearch(
        HttpContext httpContext,
        IExpertiseRepository repo,
        [FromQuery] string q,
        [FromQuery] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.Problem("Query parameter 'q' is required.", statusCode: 400);

        var tenantContext = httpContext.RequireTenantContext();
        var results = await repo.KeywordSearchAsync(q, tenantContext, includeDeprecated, ct);
        return Results.Ok(results);
    }
}
