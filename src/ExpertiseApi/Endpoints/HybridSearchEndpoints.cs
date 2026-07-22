using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Hygiene;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ExpertiseApi.Endpoints;

internal static class HybridSearchEndpoints
{
    public static RouteGroupBuilder MapHybridSearchEndpoints(this WebApplication app)
    {
        // Shares the semantic-search rate-limit policy: every hybrid call runs one ONNX
        // inference (the semantic arm), so it must not offer a cheaper path around that
        // budget (ADR-016).
        var group = app.MapGroup("/expertise/search/hybrid")
            .WithTags("Search")
            .RequireAuthorization("ReadAccess")
            .RequireRateLimiting("semantic-search");

        group.MapGet("/", HybridSearch)
            .WithSummary("Hybrid search — keyword and semantic arms fused with Reciprocal Rank Fusion")
            .WithDescription("Runs the keyword (`websearch_to_tsquery`) and semantic (pgvector cosine) searches over the top " +
                             $"{RankFusion.CandidatePoolSize} candidates each and fuses them with Reciprocal Rank Fusion " +
                             $"(k={RankFusion.K}; ties break newest-first). Covers both exact identifiers and paraphrases — " +
                             "the recommended default search for agent callers (ADR-016). `score` is the fused RRF sum, " +
                             "comparable only within one response. Optional structured filters: `domain`, `tags` " +
                             "(comma-separated, all must match), `entryType`, `severity`. Returns the top `limit` " +
                             "(clamped 1–100, default 10) entries. Subject to the `semantic-search` rate-limit policy.")
            .Produces<List<ExpertiseEntryResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return group;
    }

    private static async Task<IResult> HybridSearch(
        HttpContext httpContext,
        IExpertiseRepository repo,
        EmbeddingService embeddingService,
        IResponseHygiene hygiene,
        [FromQuery] string q,
        [FromQuery] string? domain = null,
        [FromQuery] string? tags = null,
        [FromQuery] EntryType? entryType = null,
        [FromQuery] Severity? severity = null,
        [FromQuery] int limit = 10,
        [FromQuery] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.Problem("Query parameter 'q' is required.", statusCode: 400);

        var tenantContext = httpContext.RequireTenantContext();
        var clampedLimit = Math.Clamp(limit, 1, 100);
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // Sequential on purpose: both arms share the scoped DbContext, which is not
        // thread-safe; at this corpus size the second round trip is immaterial (ADR-016).
        var keywordRanked = await repo.KeywordSearchAsync(
            q, tenantContext, includeDeprecated, RankFusion.CandidatePoolSize,
            domain, tagList, entryType, severity, ct);

        var queryVector = await embeddingService.GenerateQueryEmbeddingAsync(q, ct);
        var semanticRanked = await repo.SemanticSearchAsync(
            queryVector, tenantContext, RankFusion.CandidatePoolSize, includeDeprecated,
            domain, tagList, entryType, severity, ct);

        var fused = RankFusion.ReciprocalRankFusion(keywordRanked, semanticRanked, clampedLimit);
        return Results.Ok(ExpertiseEntryResponse.FromMany(fused, hygiene));
    }
}
