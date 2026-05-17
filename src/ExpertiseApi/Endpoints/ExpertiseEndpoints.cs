using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
            .RequireAuthorization("ReadAccess");

        group.MapGet("/drafts", ListDrafts)
            .RequireAuthorization(AuthConstants.Policies.WriteApproveAccess);

        group.MapGet("/{id:guid}", GetEntry)
            .RequireAuthorization("ReadAccess");

        group.MapPost("/", CreateEntry)
            .RequireAuthorization("WriteAccess");

        group.MapPatch("/{id:guid}", UpdateEntry)
            .RequireAuthorization("WriteAccess");

        group.MapDelete("/{id:guid}", DeleteEntry)
            .RequireAuthorization("WriteAccess");

        group.MapPost("/batch", CreateBatch)
            .RequireAuthorization("WriteAccess");

        group.MapPost("/{id:guid}/approve", ApproveEntry)
            .RequireAuthorization(AuthConstants.Policies.WriteApproveAccess);

        group.MapPost("/{id:guid}/reject", RejectEntry)
            .RequireAuthorization(AuthConstants.Policies.WriteApproveAccess);

        return group;
    }

    private static async Task<IResult> ListEntries(
        HttpContext httpContext,
        IExpertiseRepository repo,
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
        return Results.Ok(entries);
    }

    private static async Task<IResult> ListDrafts(
        HttpContext httpContext,
        IExpertiseRepository repo,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
        var entries = await repo.ListDraftsAsync(tenantContext, ct);
        return Results.Ok(entries);
    }

    private static async Task<IResult> GetEntry(
        Guid id,
        HttpContext httpContext,
        IExpertiseRepository repo,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
        var entry = await repo.GetByIdAsync(id, tenantContext, ct);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }

    private static bool IsRequestValid(CreateExpertiseRequest request) =>
        !string.IsNullOrWhiteSpace(request.Domain) &&
        !string.IsNullOrWhiteSpace(request.Title) &&
        !string.IsNullOrWhiteSpace(request.Body) &&
        !string.IsNullOrWhiteSpace(request.Source);

    private static async Task<IResult> CreateEntry(
        CreateExpertiseRequest request,
        HttpContext httpContext,
        IExpertiseRepository repo,
        EmbeddingService embeddingService,
        DeduplicationService dedup,
        CancellationToken ct)
    {
        if (!IsRequestValid(request))
            return Results.Problem("Domain, Title, Body, and Source are required.", statusCode: 400);

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
            return Results.Conflict(existing);

        var created = await repo.CreateAsync(BuildEntry(request, embedding, tenantContext), tenantContext, ct);
        return Results.Created($"/expertise/{created.Id}", created);
    }

    private static async Task<IResult> UpdateEntry(
        Guid id,
        UpdateExpertiseRequest request,
        HttpContext httpContext,
        IExpertiseRepository repo,
        EmbeddingService embeddingService,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
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

            if (needsReembed)
            {
                entry.Embedding = await embeddingService.GenerateEmbeddingAsync(
                    EmbeddingService.BuildInputText(entry.Title, entry.Body), ct);
            }
        }, ct);

        return outcome switch
        {
            WriteOutcome.Success => Results.Ok(updated),
            WriteOutcome.NotFound => Results.NotFound(),
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
        CancellationToken ct)
    {
        const int MaxBatchSize = 100;
        var tenantContext = httpContext.RequireTenantContext();

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
                var created = await repo.CreateAsync(BuildEntry(request, embedding, tenantContext), tenantContext, ct);
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
        TenantContext tenantContext)
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
            // Shared entries bypass the draft queue (which is scoped to the writing tenant
            // and never surfaces shared drafts). Create them directly as Approved to avoid
            // a permanently unapprovable stranded draft.
            ReviewState = isShared ? ReviewState.Approved : ReviewState.Draft,
            ReviewedBy = isShared ? authorPrincipal : null,
            ReviewedAt = isShared ? DateTime.UtcNow : null,
        };
    }

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
        ApproveExpertiseRequest? request,
        CancellationToken ct)
    {
        var tenantContext = httpContext.RequireTenantContext();
        var visibility = request?.Visibility ?? Visibility.Private;

        var (outcome, entry) = await repo.ApproveAsync(id, tenantContext, visibility, ct);
        return outcome switch
        {
            WriteOutcome.Success => Results.Ok(entry),
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
            WriteOutcome.Success => Results.Ok(entry),
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
    string? Tenant = null);

internal record UpdateExpertiseRequest(
    string? Domain = null,
    string? Title = null,
    string? Body = null,
    EntryType? EntryType = null,
    Severity? Severity = null,
    string? Source = null,
    List<string>? Tags = null,
    string? SourceVersion = null);

internal record ApproveExpertiseRequest(Visibility? Visibility = null);

internal record RejectExpertiseRequest(string RejectionReason);
