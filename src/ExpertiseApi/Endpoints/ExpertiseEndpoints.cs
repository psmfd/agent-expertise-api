using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Endpoints.Filters;
using ExpertiseApi.Hygiene;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using ExpertiseApi.Services.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace ExpertiseApi.Endpoints;

internal static class ExpertiseEndpoints
{
    public static RouteGroupBuilder MapExpertiseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/expertise")
            .WithTags("Expertise")
            .RequireAuthorization();

        group.MapGet("/", ListEntries)
            .RequireAuthorization("ReadAccess")
            .RequireRateLimiting("expertise-read")
            .WithSummary("List approved expertise entries")
            .WithDescription("Returns approved entries scoped to the caller's tenant plus any `shared` entries. " +
                             "Optional filters: `domain`, `tags` (CSV), `entryType`, `severity`, `includeDeprecated`. " +
                             "Drafts and rejected entries are excluded \u2014 reviewers see those via GET /expertise/drafts.")
            .Produces<List<ExpertiseEntryResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/drafts", ListDrafts)
            .RequireAuthorization(AuthConstants.Policies.WriteApproveAccess)
            .RequireRateLimiting("expertise-read")
            .WithSummary("List draft + rejected entries for review")
            .WithDescription("Reviewer-only queue (`expertise.write.approve` scope). Returns Draft and Rejected entries " +
                             "in the caller's tenant; shared entries are never surfaced (they bypass the draft state).")
            .Produces<List<ExpertiseEntryResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/{id:guid}", GetEntry)
            .RequireAuthorization("ReadAccess")
            .RequireRateLimiting("expertise-read")
            .WithSummary("Fetch a single entry by id")
            .WithDescription("Returns 200 with the entry if it is visible to the caller's tenant (own tenant or shared); " +
                             "404 otherwise. Drafts are not surfaced through this endpoint.")
            .Produces<ExpertiseEntryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/", CreateEntry)
            .RequireAuthorization("WriteAccess")
            .RequireRateLimiting("expertise-write")
            .RequireIdempotency()
            .Accepts<CreateExpertiseRequest>("application/json")
            .WithSummary("Create a new expertise entry (Draft by default)")
            .WithDescription("Creates an entry in Draft state in the caller's tenant. Optional `tenant: \"shared\"` requires " +
                             "`expertise.write.approve` and bypasses the draft queue (created as Approved). " +
                             "Returns 409 if a near-duplicate is detected by the dedup service.")
            .Produces<ExpertiseEntryResponse>(StatusCodes.Status201Created)
            .Produces<ExpertiseEntryResponse>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPatch("/{id:guid}", UpdateEntry)
            .RequireAuthorization("WriteAccess")
            .RequireRateLimiting("expertise-write")
            .Accepts<UpdateExpertiseRequest>("application/json")
            .WithSummary("Partially update an entry")
            .WithDescription("Only the supplied fields are modified. If `title` or `body` change the embedding is regenerated. " +
                             "Changing `visibility` (Private <-> Shared) is a publish/unpublish action and requires `expertise.write.approve` " +
                             "even for the entry's original writer; a no-op (supplying the current value) does not require the elevated scope. " +
                             "Returns 409 (ConcurrentConflict) when the entry was modified by another request between read and write.")
            .Produces<ExpertiseEntryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapDelete("/{id:guid}", DeleteEntry)
            .RequireAuthorization("WriteAccess")
            .RequireRateLimiting("expertise-write")
            .WithSummary("Soft-delete an entry")
            .WithDescription("Marks the entry as deprecated. Soft-deleting a `shared` entry requires `expertise.write.approve`.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/batch", CreateBatch)
            .RequireAuthorization("WriteAccess")
            .RequireRateLimiting("expertise-batch")
            .Accepts<List<CreateExpertiseRequest>>("application/json")
            .WithSummary("Batch ingest up to 100 entries with per-item failure isolation")
            .WithDescription("Returns 200 with `BatchEntryResult[]` when every item is Created, or 207 (Multi-Status) when any item is " +
                             "Duplicate / Rejected / Failed. Embedding generation is batched into a single ONNX call; deduplication " +
                             "runs an HNSW-indexed nearest-neighbour query per item. Partial failures in one phase do not roll back " +
                             "successful items. Rate-limited under a dedicated stricter policy than single writes (#333 Finding 4).")
            .Produces<List<BatchEntryResult>>(StatusCodes.Status200OK)
            .Produces<List<BatchEntryResult>>(StatusCodes.Status207MultiStatus)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/{id:guid}/approve", ApproveEntry)
            .RequireAuthorization(AuthConstants.Policies.WriteApproveAccess)
            .RequireRateLimiting("expertise-write")
            .RequireIdempotency()
            .Accepts<ApproveExpertiseRequest>("application/json")
            .WithSummary("Approve a draft entry (reviewer-only)")
            .WithDescription("Transitions the entry from Draft to Approved with the supplied `Visibility` " +
                             "(defaults to Private). Returns 409 if the entry is not currently in Draft state.")
            .Produces<ExpertiseEntryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/{id:guid}/reject", RejectEntry)
            .RequireAuthorization(AuthConstants.Policies.WriteApproveAccess)
            .RequireRateLimiting("expertise-write")
            .RequireIdempotency()
            .Accepts<RejectExpertiseRequest>("application/json")
            .WithSummary("Reject a draft entry (reviewer-only)")
            .WithDescription("Transitions the entry from Draft to Rejected. A non-empty `rejectionReason` (\u2264 2000 chars) is required.")
            .Produces<ExpertiseEntryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return group;
    }

    private static async Task<IResult> ListEntries(
        HttpContext httpContext,
        IExpertiseRepository repo,
        IResponseHygiene hygiene,
        [FromQuery] string? domain,
        [FromQuery] string? tags,
        [FromQuery] EntryType? entryType,
        [FromQuery] Severity? severity,
        [FromQuery] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        // Reads always default to ReviewState = Approved. Reviewers see drafts and rejected
        // entries via GET /expertise/drafts (which requires write.approve).
        var tenantContext = httpContext.RequireTenantContext();
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var entries = await repo.ListAsync(tenantContext, domain, tagList, entryType, severity, includeDeprecated, ct);
        return Results.Ok(ExpertiseEntryResponse.FromMany(entries, hygiene));
    }

    private static async Task<IResult> ListDrafts(
        HttpContext httpContext,
        IExpertiseRepository repo,
        IResponseHygiene hygiene,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
        var entries = await repo.ListDraftsAsync(tenantContext, ct);
        return Results.Ok(ExpertiseEntryResponse.FromMany(entries, hygiene));
    }

    private static async Task<IResult> GetEntry(
        Guid id,
        HttpContext httpContext,
        IExpertiseRepository repo,
        IResponseHygiene hygiene,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
        var entry = await repo.GetByIdAsync(id, tenantContext, ct);
        return entry is null ? Results.NotFound() : Results.Ok(ExpertiseEntryResponse.From(entry, hygiene));
    }

    private static bool IsRequestValid(CreateExpertiseRequest request) =>
        !string.IsNullOrWhiteSpace(request.Domain) &&
        !string.IsNullOrWhiteSpace(request.Title) &&
        !string.IsNullOrWhiteSpace(request.Body) &&
        !string.IsNullOrWhiteSpace(request.Source);

    // MaxBodyLength derivation (#429 method, re-based by ADR-017): the embedding
    // window is EmbeddingModelInfo.MaximumTokens (6144) and the ONNX connector
    // silently truncates beyond it with no signal. BuildInputText embeds
    // "{title} {body}" in one pass; reserving ~70 worst-case tokens for a
    // 200-char Title plus specials leaves ~6,000 for Body. Measured density
    // across the 60 longest real entries is 2.97–4.24 chars/token, so 16,000
    // chars lands at ≈5,390 worst-case Body tokens — fully embedded with
    // headroom even at maximum density. Re-derive if the model or ceiling
    // changes (ADR-017 ground-truth tables are on issue #437).
    private const int MaxBodyLength = 16_000;

    private static string? BodyLengthError(string? body) =>
        body is { Length: > MaxBodyLength }
            ? $"Body exceeds maximum length of {MaxBodyLength} characters (got {body.Length}). " +
              "Content past the embedding window is invisible to semantic search; shorten the body or split the entry."
            : null;

    // MaxTitleLength (#436): Title shares the embedding window with Body
    // (BuildInputText embeds "{title} {body}"); the MaxBodyLength derivation
    // reserves ~70 worst-case tokens for Title, which 200 chars comfortably
    // respects (observed corpus max is 142). Unchanged by the ADR-017 ceiling
    // raise — titles are a display/scan surface, not a content dump.
    private const int MaxTitleLength = 200;

    private static string? TitleLengthError(string? title) =>
        title is { Length: > MaxTitleLength }
            ? $"Title exceeds maximum length of {MaxTitleLength} characters (got {title.Length}). " +
              "Title shares the embedding window with Body; keep titles concise and put detail in the body."
            : null;

    private static string? LengthError(CreateExpertiseRequest request) =>
        TitleLengthError(request.Title) ?? BodyLengthError(request.Body);

    private static async Task<IResult> CreateEntry(
        CreateExpertiseRequest request,
        HttpContext httpContext,
        IExpertiseRepository repo,
        EmbeddingService embeddingService,
        DeduplicationService dedup,
        IResponseHygiene hygiene,
        IOptionsMonitor<SyncOptions> syncOptions,
        CancellationToken ct)
    {
        if (!IsRequestValid(request))
            return Results.Problem("Domain, Title, Body, and Source are required.", statusCode: 400);

        if (LengthError(request) is { } lengthError)
            return Results.Problem(lengthError, statusCode: 400);

        var tenantContext = httpContext.RequireTenantContext();

        // Validate optional Tenant override: only "shared" is permitted, and only for write.approve callers.
        if (request.Tenant is not null)
        {
            if (!string.Equals(request.Tenant, "shared", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(
                    "Only Tenant=\"shared\" may be specified; all other tenants are server-assigned.",
                    statusCode: 400);

            if (!tenantContext.Scopes.Contains(AuthConstants.WriteApproveScope))
                return Results.Problem(
                    "Creating shared entries requires expertise.write.approve.",
                    statusCode: 403);
        }

        var embedding = await embeddingService.GenerateEmbeddingAsync(
            EmbeddingService.BuildInputText(request.Title, request.Body), ct);

        var (isDuplicate, existing) = await dedup.CheckAsync(request, embedding, tenantContext, ct);
        if (isDuplicate && existing is not null)
            return Results.Conflict(ExpertiseEntryResponse.From(existing, hygiene));

        var originInstanceId = ResolveOriginInstanceId(tenantContext, syncOptions.CurrentValue);
        var created = await repo.CreateAsync(BuildEntry(request, embedding, tenantContext, originInstanceId), tenantContext, ct);
        return Results.Created($"/expertise/{created.Id}", ExpertiseEntryResponse.From(created, hygiene));
    }

    private static async Task<IResult> UpdateEntry(
        Guid id,
        UpdateExpertiseRequest request,
        HttpContext httpContext,
        IExpertiseRepository repo,
        EmbeddingService embeddingService,
        IResponseHygiene hygiene,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();

        if ((TitleLengthError(request.Title) ?? BodyLengthError(request.Body)) is { } lengthError)
            return Results.Problem(lengthError, statusCode: 400);

        var needsReembed = request.Title is not null || request.Body is not null;

        var (outcome, updated) = await repo.UpdateAsync(id, tenantContext, async entry =>
        {
            if (request.Domain is not null) entry.Domain = request.Domain;
            if (request.Tags is not null) entry.Tags = request.Tags;
            if (request.Title is not null) entry.Title = request.Title;
            if (request.Body is not null) entry.Body = request.Body;
            if (request.EntryType is not null) entry.EntryType = request.EntryType.Value;
            if (request.Severity is not null) entry.Severity = request.Severity.Value;
            if (request.Source is not null) entry.Source = request.Source;
            if (request.SourceVersion is not null) entry.SourceVersion = request.SourceVersion;
            if (request.Visibility is not null) entry.Visibility = request.Visibility.Value;

            if (needsReembed)
            {
                entry.Embedding = await embeddingService.GenerateEmbeddingAsync(
                    EmbeddingService.BuildInputText(entry.Title, entry.Body), ct);
            }
        }, ct);

        return outcome switch
        {
            WriteOutcome.Success => Results.Ok(ExpertiseEntryResponse.From(updated!, hygiene)),
            WriteOutcome.NotFound => Results.NotFound(),
            WriteOutcome.InsufficientScope => Results.Problem(
                title: "Insufficient scope",
                detail: "Changing Visibility or modifying a shared entry requires expertise.write.approve.",
                statusCode: StatusCodes.Status403Forbidden),
            WriteOutcome.ConcurrentConflict => Results.Problem(
                title: "Concurrent modification",
                detail: "The entry was modified by another request. Reload and retry.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Batch ingest uses per-phase and per-item failure isolation. An unexpected " +
                        "exception in embedding generation, deduplication, or per-entry create must mark " +
                        "the affected items as Failed and continue processing the rest. The error is " +
                        "captured in the BatchEntryResult and surfaced to the caller via the 207 response.")]
    private static async Task<IResult> CreateBatch(
        List<CreateExpertiseRequest> requests,
        HttpContext httpContext,
        IExpertiseRepository repo,
        EmbeddingService embeddingService,
        DeduplicationService dedup,
        ILoggerFactory loggerFactory,
        IOptionsMonitor<SyncOptions> syncOptions,
        CancellationToken ct)
    {
        const int MaxBatchSize = 100;
        var tenantContext = httpContext.RequireTenantContext();
        var originInstanceId = ResolveOriginInstanceId(tenantContext, syncOptions.CurrentValue);

        if (requests is null || requests.Count == 0)
            return Results.Problem("Request body must contain at least one entry.", statusCode: 400);

        if (requests.Count > MaxBatchSize)
            return Results.Problem($"Batch size exceeds maximum of {MaxBatchSize} entries.", statusCode: 400);

        var logger = loggerFactory.CreateLogger("ExpertiseApi.Endpoints.BatchIntake");
        var results = new BatchEntryResult[requests.Count];

        // Phase 1: Validate and collect
        var validItems = new List<(int Index, CreateExpertiseRequest Request)>();
        for (var i = 0; i < requests.Count; i++)
        {
            if (!IsRequestValid(requests[i]))
            {
                results[i] = new BatchEntryResult(i, BatchEntryStatus.Rejected, null,
                    "Domain, Title, Body, and Source are required.");
                continue;
            }

            if (LengthError(requests[i]) is { } lengthError)
            {
                results[i] = new BatchEntryResult(i, BatchEntryStatus.Rejected, null, lengthError);
                continue;
            }

            // Validate optional Tenant override per item.
            if (requests[i].Tenant is not null)
            {
                if (!string.Equals(requests[i].Tenant, "shared", StringComparison.OrdinalIgnoreCase))
                {
                    results[i] = new BatchEntryResult(i, BatchEntryStatus.Rejected, null,
                        "Only Tenant=\"shared\" may be specified; all other tenants are server-assigned.");
                    continue;
                }

                if (!tenantContext.Scopes.Contains(AuthConstants.WriteApproveScope))
                {
                    results[i] = new BatchEntryResult(i, BatchEntryStatus.Rejected, null,
                        "Creating shared entries requires expertise.write.approve.");
                    continue;
                }
            }

            validItems.Add((i, requests[i]));
        }

        if (validItems.Count == 0)
            return Results.Json(results.ToList(), statusCode: 207);

        // Phase 2: Batch embed — single ONNX call for all valid items
        // Phase 3: Batch dedup — bulk queries per domain instead of per item
        IReadOnlyList<Vector> embeddings;
        IReadOnlyList<(bool IsDuplicate, ExpertiseEntry? Existing)> dedupResults;

        try
        {
            var texts = validItems.Select(v => EmbeddingService.BuildInputText(v.Request.Title, v.Request.Body));
            embeddings = await embeddingService.GenerateBatchAsync(texts, ct);
        }
        catch (OperationCanceledException)
        {
            foreach (var (index, _) in validItems)
                results[index] = new BatchEntryResult(index, BatchEntryStatus.Failed, null, "Request was cancelled.");

            return Results.Json(results.ToList(), statusCode: 207);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or HttpRequestException
                                      or TimeoutException
                                      or IOException
                                      or ArgumentException)
        {
            // Narrowed from `catch (Exception)` to satisfy CodeQL
            // cs/catch-of-all-exceptions. The IEmbeddingGenerator abstraction
            // is pluggable, so we cover the realistic failure surface across
            // both the local ONNX backend (InvalidOperationException for
            // session/state errors, IOException for model-file issues,
            // ArgumentException / ArgumentOutOfRangeException from BERT
            // tokenizer pre-processing on pathological input — lone surrogates,
            // sequences exceeding positional limits) and any HTTP-backed
            // backend (HttpRequestException, TimeoutException).
            // Process-fatal exceptions (OOM, AVE) and OperationCanceledException
            // (handled by the sibling catch above) propagate by exclusion.
            logger.LogWarning(ex, "Batch embedding generation failed");

            foreach (var (index, _) in validItems)
                results[index] = new BatchEntryResult(index, BatchEntryStatus.Failed, null, "Batch could not be processed.");

            return Results.Json(results.ToList(), statusCode: 207);
        }

        try
        {
            var validRequests = validItems.Select(v => v.Request).ToList();
            dedupResults = await dedup.CheckBatchAsync(validRequests, embeddings, tenantContext, ct);
        }
        catch (OperationCanceledException)
        {
            foreach (var (index, _) in validItems)
                results[index] = new BatchEntryResult(index, BatchEntryStatus.Failed, null, "Request was cancelled.");

            return Results.Json(results.ToList(), statusCode: 207);
        }
        catch (Exception ex) when (ex is DbException
                                      or DbUpdateException
                                      or InvalidOperationException
                                      or ArgumentException)
        {
            // Narrowed from `catch (Exception)` to satisfy CodeQL
            // cs/catch-of-all-exceptions. CheckBatchAsync issues bulk DB
            // queries via the repo and may surface Npgsql/EF errors
            // (DbException / DbUpdateException), DI/state errors
            // (InvalidOperationException), or argument-shape mismatches
            // (ArgumentException — thrown explicitly when embeddings.Count
            // does not match requests.Count). Process-fatal and OCE propagate.
            logger.LogWarning(ex, "Batch deduplication failed");

            foreach (var (index, _) in validItems)
                results[index] = new BatchEntryResult(index, BatchEntryStatus.Failed, null, "Batch could not be processed.");

            return Results.Json(results.ToList(), statusCode: 207);
        }

        // Phase 4: Create non-duplicate entries
        for (var j = 0; j < validItems.Count; j++)
        {
            var (index, request) = validItems[j];
            var embedding = embeddings[j];
            var (isDuplicate, existing) = dedupResults[j];

            if (isDuplicate && existing is not null)
            {
                results[index] = new BatchEntryResult(index, BatchEntryStatus.Duplicate, existing.Id, null);
                continue;
            }

            try
            {
                var created = await repo.CreateAsync(BuildEntry(request, embedding, tenantContext, originInstanceId), tenantContext, ct);
                results[index] = new BatchEntryResult(index, BatchEntryStatus.Created, created.Id, null);
            }
            catch (OperationCanceledException)
            {
                for (var k = j; k < validItems.Count; k++)
                    results[validItems[k].Index] = new BatchEntryResult(validItems[k].Index, BatchEntryStatus.Failed, null, "Request was cancelled.");
                break;
            }
            catch (Exception ex) when (ex is DbException
                                          or DbUpdateException
                                          or InvalidOperationException)
            {
                // Narrowed from `catch (Exception)` per CodeQL
                // cs/catch-of-all-exceptions. repo.CreateAsync ultimately calls
                // db.SaveChangesAsync, which raises DbUpdateException for
                // constraint violations, DbException for transport-level
                // Npgsql errors, and InvalidOperationException for tenant-mismatch
                // guard trips (line ~145). Process-fatal and OCE propagate.
                logger.LogWarning(ex, "Batch entry {Index} failed", index);
                results[index] = new BatchEntryResult(index, BatchEntryStatus.Failed, null, "Entry could not be created.");
            }
        }

        var resultList = results.ToList();
        var allCreated = resultList.All(r => r.Status == BatchEntryStatus.Created);
        return allCreated
            ? Results.Ok(resultList)
            : Results.Json(resultList, statusCode: 207);
    }

    private static ExpertiseEntry BuildEntry(
        CreateExpertiseRequest request,
        Vector embedding,
        TenantContext tenantContext,
        string? originInstanceId)
    {
        var authorPrincipal = tenantContext.Principal.FindFirst("sub")?.Value
                          ?? tenantContext.Principal.Identity?.Name
                          ?? "unknown";
        var isShared = string.Equals(request.Tenant, "shared", StringComparison.OrdinalIgnoreCase);
        return new ExpertiseEntry
        {
            Domain = request.Domain,
            Tags = request.Tags ?? [],
            Title = request.Title,
            Body = request.Body,
            EntryType = request.EntryType,
            Severity = request.Severity,
            Source = request.Source,
            SourceVersion = request.SourceVersion,
            Embedding = embedding,
            Tenant = request.Tenant ?? tenantContext.Tenant!,
            AuthorPrincipal = authorPrincipal,
            AuthorAgent = tenantContext.Agent,
            // ADR-013 origin attribution. OriginInstanceId is SERVER-derived (authenticated
            // client → Sync:KnownInstances) — the body's claim about its own origin is never
            // trusted. OriginAuthorPrincipal is informational body input, truncated like
            // ActorClassHeader (ADR-008 precedent) rather than rejected.
            OriginInstanceId = originInstanceId,
            OriginAuthorPrincipal = request.OriginAuthorPrincipal is { Length: > 256 } long_
                ? long_[..256]
                : request.OriginAuthorPrincipal,
            // Shared entries bypass the draft queue (which is scoped to the writing tenant
            // and never surfaces shared drafts). Create them directly as Approved to avoid
            // a permanently unapprovable stranded draft.
            ReviewState = isShared ? ReviewState.Approved : ReviewState.Draft,
            ReviewedBy = isShared ? authorPrincipal : null,
            ReviewedAt = isShared ? DateTime.UtcNow : null,
        };
    }

    /// <summary>
    /// Maps the authenticated client identifier (azp/appid/client_id, surfaced as
    /// <see cref="TenantContext.Agent"/>) to a configured origin instance id
    /// (<c>Sync:KnownInstances</c>, ADR-013). Null for callers that are not
    /// registered sync spokes — i.e., every ordinary caller.
    /// </summary>
    private static string? ResolveOriginInstanceId(TenantContext tenantContext, SyncOptions sync) =>
        tenantContext.Agent is { } client && sync.KnownInstances.TryGetValue(client, out var instance)
            ? instance
            : null;

    private static async Task<IResult> DeleteEntry(
        Guid id,
        HttpContext httpContext,
        IExpertiseRepository repo,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
        var outcome = await repo.SoftDeleteAsync(id, tenantContext, ct);
        return outcome switch
        {
            WriteOutcome.Success => Results.NoContent(),
            WriteOutcome.NotFound => Results.NotFound(),
            WriteOutcome.InsufficientScope => Results.Problem(
                "Soft-deleting a shared entry requires the expertise.write.approve scope.",
                statusCode: 403),
            _ => Results.Problem("Unexpected outcome from soft-delete.", statusCode: 500)
        };
    }

    private static async Task<IResult> ApproveEntry(
        Guid id,
        HttpContext httpContext,
        IExpertiseRepository repo,
        IResponseHygiene hygiene,
        ApproveExpertiseRequest? request,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
        var visibility = request?.Visibility ?? Visibility.Private;

        var (outcome, entry) = await repo.ApproveAsync(id, tenantContext, visibility, ct);
        return outcome switch
        {
            WriteOutcome.Success => Results.Ok(ExpertiseEntryResponse.From(entry!, hygiene)),
            WriteOutcome.NotFound => Results.NotFound(),
            WriteOutcome.InvalidState => Results.Problem(
                "Entry is not in Draft state and cannot be approved.",
                statusCode: 409),
            WriteOutcome.ConcurrentConflict => Results.Problem(
                "Entry was modified concurrently. Retry.",
                statusCode: 409),
            _ => Results.Problem("Unexpected outcome from approve.", statusCode: 500)
        };
    }

    private static async Task<IResult> RejectEntry(
        Guid id,
        RejectExpertiseRequest request,
        HttpContext httpContext,
        IExpertiseRepository repo,
        IResponseHygiene hygiene,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RejectionReason))
            return Results.Problem("rejectionReason is required.", statusCode: 400);
        if (request.RejectionReason.Length > MaxRejectionReasonLength)
            return Results.Problem(
                $"rejectionReason exceeds maximum length of {MaxRejectionReasonLength} characters.",
                statusCode: 400);

        var tenantContext = httpContext.RequireTenantContext();
        var (outcome, entry) = await repo.RejectAsync(id, tenantContext, request.RejectionReason, ct);
        return outcome switch
        {
            WriteOutcome.Success => Results.Ok(ExpertiseEntryResponse.From(entry!, hygiene)),
            WriteOutcome.NotFound => Results.NotFound(),
            WriteOutcome.InvalidState => Results.Problem(
                "Entry is not in Draft state and cannot be rejected.",
                statusCode: 409),
            WriteOutcome.ConcurrentConflict => Results.Problem(
                "Entry was modified concurrently. Retry.",
                statusCode: 409),
            _ => Results.Problem("Unexpected outcome from reject.", statusCode: 500)
        };
    }

    private const int MaxRejectionReasonLength = 2000;
}

internal enum BatchEntryStatus { Created, Duplicate, Rejected, Failed }

internal record BatchEntryResult(
    int Index,
    BatchEntryStatus Status,
    Guid? Id,
    string? Error);

internal record CreateExpertiseRequest(
    string Domain,
    string Title,
    string Body,
    EntryType EntryType,
    Severity Severity,
    string Source,
    List<string>? Tags = null,
    string? SourceVersion = null,
    /// <summary>
    /// Optional tenant override. Only <c>"shared"</c> is accepted; all other tenants are
    /// server-assigned from the caller's token. Requires <c>expertise.write.approve</c>.
    /// Shared entries are created directly as <see cref="ReviewState.Approved"/> to avoid
    /// stranded drafts (the draft queue is scoped to the writing tenant and never surfaces
    /// shared entries).
    /// </summary>
    string? Tenant = null,
    /// <summary>
    /// Origin-side author for entries arriving via aggregator up-sync (ADR-013) —
    /// informational reviewer context only. Truncated to 256 characters at write time.
    /// Contrast with <c>OriginInstanceId</c>, which is server-derived from the
    /// authenticated client and never accepted from the body.
    /// </summary>
    string? OriginAuthorPrincipal = null);

internal record UpdateExpertiseRequest(
    string? Domain = null,
    string? Title = null,
    string? Body = null,
    EntryType? EntryType = null,
    Severity? Severity = null,
    string? Source = null,
    List<string>? Tags = null,
    string? SourceVersion = null,
    /// <summary>
    /// Optional Visibility change (Private &lt;-&gt; Shared). When this field is supplied
    /// and the value differs from the entry's current Visibility, the caller must hold
    /// <c>expertise.write.approve</c>. A no-op (Visibility supplied matches current)
    /// does not require the elevated scope.
    /// </summary>
    Visibility? Visibility = null);

internal record ApproveExpertiseRequest(Visibility? Visibility = null);

internal record RejectExpertiseRequest(string RejectionReason);
