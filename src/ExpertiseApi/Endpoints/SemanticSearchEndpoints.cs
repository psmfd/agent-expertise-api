using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Hygiene;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ExpertiseApi.Endpoints;

internal static class SemanticSearchEndpoints
{
    public static RouteGroupBuilder MapSemanticSearchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/expertise/search/semantic")
            .WithTags("Search")
            .RequireAuthorization("ReadAccess")
            .RequireRateLimiting("semantic-search");

        group.MapGet("/", SemanticSearch)
            .WithSummary("Vector similarity search using cosine distance over title+body embeddings")
            .WithDescription("Generates an embedding for `q` via the configured ONNX/SBERT model and returns the top `limit` (clamped 1\u2013100) " +
                             "Approved entries by cosine similarity. Tenant-scoped (own tenant + shared). Subject to the `semantic-search` " +
                             "rate-limit policy (token bucket, 10/min) because each call runs ONNX inference.")
            .Produces<List<ExpertiseEntryResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return group;
    }

    private static async Task<IResult> SemanticSearch(
        HttpContext httpContext,
        IExpertiseRepository repo,
        EmbeddingService embeddingService,
        IResponseHygiene hygiene,
        [FromQuery] string q,
        [FromQuery] int limit = 10,
        [FromQuery] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.Problem("Query parameter 'q' is required.", statusCode: 400);

        var tenantContext = httpContext.RequireTenantContext();
        var clampedLimit = Math.Clamp(limit, 1, 100);
        var queryVector = await embeddingService.GenerateQueryEmbeddingAsync(q, ct);
        var results = await repo.SemanticSearchAsync(queryVector, tenantContext, clampedLimit, includeDeprecated, ct);
        return Results.Ok(ExpertiseEntryResponse.FromMany(results, hygiene));
    }
}
