using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Hygiene;
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
            .WithDescription("PostgreSQL full-text search (`websearch_to_tsquery`, cover-density ranked) over Approved entries " +
                             "visible to the caller's tenant. Query string `q` is required and supports web-search syntax " +
                             "(quoted phrases, OR, -negation). Returns the top `limit` (clamped 1–100, default 50) matches. " +
                             "Optional structured filters: `domain`, `tags` (comma-separated, all must match), `entryType`, " +
                             "`severity`. Set `includeDeprecated=true` to surface soft-deleted entries.")
            .Produces<List<ExpertiseEntryResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return group;
    }

    private static async Task<IResult> KeywordSearch(
        HttpContext httpContext,
        IExpertiseRepository repo,
        IResponseHygiene hygiene,
        [FromQuery] string q,
        [FromQuery] string? domain = null,
        [FromQuery] string? tags = null,
        [FromQuery] EntryType? entryType = null,
        [FromQuery] Severity? severity = null,
        [FromQuery] bool includeDeprecated = false,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.Problem("Query parameter 'q' is required.", statusCode: 400);

        var tenantContext = httpContext.RequireTenantContext();
        var clampedLimit = Math.Clamp(limit, 1, 100);
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var results = await repo.KeywordSearchAsync(q, tenantContext, includeDeprecated, clampedLimit, domain, tagList, entryType, severity, ct);
        return Results.Ok(ExpertiseEntryResponse.FromMany(results, hygiene));
    }
}
