using System.Diagnostics.CodeAnalysis;
using ExpertiseApi.Auth;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace ExpertiseApi.Data;

internal class ExpertiseRepository(
    ExpertiseDbContext db,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ExpertiseRepository> logger) : IExpertiseRepository
{
    /// <summary>
    /// Builds the tenant predicate per ADR-001: a row is visible if its <c>Tenant</c>
    /// matches the caller's, or if it is in the cross-tenant <c>shared</c> namespace.
    /// <para>
    /// <c>internal</c> (not private) so the query-translation test suite
    /// (<c>RepositoryQueryTranslationTests</c>, #352) can build on the exact same
    /// tenant-scoping seam the production reads use, rather than re-deriving it and
    /// risking drift.
    /// </para>
    /// </summary>
    internal static IQueryable<ExpertiseEntry> ApplyTenantFilter(
        IQueryable<ExpertiseEntry> query, TenantContext ctx)
    {
        var tenant = RequireTenant(ctx);
        return query.Where(e => e.Tenant == tenant || e.Tenant == "shared");
    }

    /// <summary>
    /// Reads default to <see cref="ReviewState.Approved"/>. Drafts and Rejected entries
    /// are exposed only via the dedicated <c>/drafts</c> endpoint. <c>internal</c> for the
    /// same test-seam reason as <see cref="ApplyTenantFilter"/> (#352).
    /// </summary>
    internal static IQueryable<ExpertiseEntry> ApplyApprovedReviewFilter(
        IQueryable<ExpertiseEntry> query) =>
            query.Where(e => e.ReviewState == ReviewState.Approved);

    /// <summary>
    /// Defensive guard. If the auth pipeline produced a <see cref="TenantContext"/> with a
    /// null <c>Tenant</c> (unmapped principal), the authorization handler should have
    /// returned 403 before we reached the repository — but if anything slips through, fail
    /// loud rather than running an unbounded query against the full table.
    /// </summary>
    private static string RequireTenant(TenantContext ctx) =>
        ctx.Tenant ?? throw new InvalidOperationException(
            "Repository invoked with TenantContext.Tenant=null. The authorization pipeline " +
            "must reject unmapped principals before any repository call.");

    /// <summary>
    /// Builds an audit log row for a state-changing operation. Caller is responsible for
    /// adding the row to the DbContext alongside its mutation in a single SaveChangesAsync.
    /// </summary>
    private ExpertiseAuditLog BuildAuditRow(
        AuditAction action,
        ExpertiseEntry entry,
        TenantContext ctx,
        string? beforeHash,
        string? afterHash)
    {
        var principal = ctx.Principal.FindFirst("sub")?.Value
                     ?? ctx.Principal.Identity?.Name
                     ?? "system";

        return new ExpertiseAuditLog
        {
            Timestamp = DateTime.UtcNow,
            Action = action,
            EntryId = entry.Id,
            Tenant = entry.Tenant,
            Principal = principal,
            Agent = ctx.Agent,
            // Part D C6: actor-class fields. ActorClass is required (defaults to Human via
            // TenantContext.ActorClass default); AuthMethod/ActorClassHeader are recoverable
            // forensic context that makes the fail-open-to-Human decision queryable.
            ActorClass = ctx.ActorClass,
            AuthMethod = ctx.AuthMethod,
            ActorClassHeader = ctx.ActorClassHeader,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            IpAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString()
        };
    }

    public async Task<ExpertiseEntry?> GetByIdAsync(Guid id, TenantContext ctx, CancellationToken ct)
    {
        // FindAsync would short-circuit through the identity map and bypass the tenant
        // filter; explicit Where + FirstOrDefaultAsync keeps every read tenant-scoped.
        return await ApplyApprovedReviewFilter(ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx))
            .Where(e => e.Id == id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<ExpertiseEntry>> ListAsync(
        TenantContext ctx,
        string? domain,
        List<string>? tags,
        EntryType? entryType,
        Severity? severity,
        bool includeDeprecated,
        CancellationToken ct)
    {
        return await BuildListQuery(ctx, domain, tags, entryType, severity, includeDeprecated).ToListAsync(ct);
    }

    /// <summary>
    /// Composes the conditional <c>GET /expertise</c> filter query. Extracted from
    /// <see cref="ListAsync"/> as an <c>internal</c> seam so the query-translation test
    /// suite (#352) can assert every filter-combination's SQL translatability via
    /// <c>ToQueryString()</c> against the exact production expression tree — the
    /// <c>tags.All(t =&gt; e.Tags.Contains(t))</c> array-containment predicate is the
    /// highest-risk shape (same class as the <c>ToLowerInvariant</c> incident) and must
    /// be tested against the real builder, not a reconstruction that could drift.
    /// </summary>
    internal IQueryable<ExpertiseEntry> BuildListQuery(
        TenantContext ctx,
        string? domain,
        List<string>? tags,
        EntryType? entryType,
        Severity? severity,
        bool includeDeprecated)
    {
        var query = ApplyApprovedReviewFilter(ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx));

        if (!includeDeprecated)
            query = query.Where(e => e.DeprecatedAt == null);

        if (domain is not null)
            query = query.Where(e => e.Domain == domain);

        if (entryType is not null)
            query = query.Where(e => e.EntryType == entryType);

        if (severity is not null)
            query = query.Where(e => e.Severity == severity);

        if (tags is { Count: > 0 })
            query = query.Where(e => tags.All(t => e.Tags.Contains(t)));

        return query.OrderByDescending(e => e.UpdatedAt);
    }

    public async Task<List<ExpertiseEntry>> ListDraftsAsync(TenantContext ctx, CancellationToken ct)
    {
        // Drafts are owned by the writing tenant — no `shared` visibility for the queue.
        var tenant = RequireTenant(ctx);
        return await db.ExpertiseEntries
            .Where(e => e.Tenant == tenant)
            .Where(e => e.ReviewState == ReviewState.Draft || e.ReviewState == ReviewState.Rejected)
            .Where(e => e.DeprecatedAt == null)
            .OrderByDescending(e => e.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<ExpertiseEntry> CreateAsync(ExpertiseEntry entry, TenantContext ctx, CancellationToken ct)
    {
        // Defensive invariant: entry.Tenant must match the caller's token-asserted tenant,
        // with one explicit exception: write.approve callers may create Tenant="shared" entries
        // (which are created directly as Approved — see BuildEntry). BuildEntry always derives
        // Tenant from TenantContext or a validated request.Tenant, so this guard cannot trip
        // under normal operation. It exists to catch any future code path that constructs an
        // ExpertiseEntry with an unvalidated tenant bypassing that assertion.
        var callerTenant = RequireTenant(ctx);
        var isSharedByApprover = string.Equals(entry.Tenant, "shared", StringComparison.Ordinal)
                              && ctx.Scopes.Contains(AuthConstants.WriteApproveScope);
        if (!string.Equals(entry.Tenant, callerTenant, StringComparison.Ordinal) && !isSharedByApprover)
            throw new InvalidOperationException(
                $"Entry tenant '{entry.Tenant}' does not match caller tenant '{callerTenant}'.");

        // Generate the Id client-side so the audit row's EntryId points at a real GUID.
        // The DB-level gen_random_uuid() default still applies if Id is left empty by other
        // callers, but EF cannot wire the audit FK across two pending inserts in the same
        // SaveChanges without a navigation property — so we assign here to be explicit.
        if (entry.Id == Guid.Empty)
            entry.Id = Guid.NewGuid();

        entry.CreatedAt = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;
        entry.IntegrityHash = IntegrityHashService.Compute(entry);

        db.ExpertiseEntries.Add(entry);
        db.ExpertiseAuditLogs.Add(BuildAuditRow(AuditAction.Created, entry, ctx, beforeHash: null, afterHash: entry.IntegrityHash));
        await db.SaveChangesAsync(ct);

        return entry;
    }

    public async Task<(WriteOutcome Outcome, ExpertiseEntry? Entry)> UpdateAsync(Guid id, TenantContext ctx, Func<ExpertiseEntry, Task> applyUpdates, CancellationToken ct)
    {
        var entry = await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.Id == id)
            .Where(e => e.DeprecatedAt == null)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
            return (WriteOutcome.NotFound, null);

        var beforeHash = entry.IntegrityHash ?? IntegrityHashService.Compute(entry);
        var beforeVisibility = entry.Visibility;

        await applyUpdates(entry);

        // ADR-003 scope escalation: changing Visibility (Private <-> Shared) is the
        // symmetric inverse of /approve's Visibility selection and must require the
        // same scope (expertise.write.approve). Verified value-based (post-mutation
        // compared against pre-mutation snapshot) so a no-op PATCH that includes the
        // current Visibility value does not escalate. See issue #66.
        //
        // Interaction with the state-regression block below: this check fires BEFORE
        // the regression block, so a denied request never reaches the Approved->Draft
        // demotion path. A draft-only caller PATCHing an Approved+Shared entry with
        // Visibility=Shared (no-op) plus a title change DOES regress to Draft+Shared;
        // the Shared flag on a Draft entry is schema-permitted but semantically dormant
        // (Draft entries are invisible to cross-tenant reads under ApplyApprovedReviewFilter)
        // and the next /approve unconditionally overwrites Visibility from the request body.
        if (entry.Visibility != beforeVisibility
            && !ctx.Scopes.Contains(AuthConstants.WriteApproveScope))
        {
            return (WriteOutcome.InsufficientScope, null);
        }

        entry.UpdatedAt = DateTime.UtcNow;
        entry.IntegrityHash = IntegrityHashService.Compute(entry);

        // ADR-003 state-regression rule: a write.draft-only caller editing an Approved
        // or Rejected entry resets it to Draft (forces re-review); write.approve callers
        // preserve the source state. Without this, the approval workflow does not
        // actually mitigate ASI06 — content can change post-approval without re-review,
        // and a Rejected entry could not be resubmitted at all.
        if ((entry.ReviewState == ReviewState.Approved || entry.ReviewState == ReviewState.Rejected)
            && !ctx.Scopes.Contains(AuthConstants.WriteApproveScope))
        {
            entry.ReviewState = ReviewState.Draft;
            entry.ReviewedBy = null;
            entry.ReviewedAt = null;
            entry.RejectionReason = null;
        }

        db.ExpertiseAuditLogs.Add(BuildAuditRow(AuditAction.Updated, entry, ctx, beforeHash, entry.IntegrityHash));

        try
        {
            await db.SaveChangesAsync(ct);
            return (WriteOutcome.Success, entry);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (WriteOutcome.ConcurrentConflict, null);
        }
    }

    public async Task<WriteOutcome> SoftDeleteAsync(Guid id, TenantContext ctx, CancellationToken ct)
    {
        var entry = await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.Id == id)
            .Where(e => e.DeprecatedAt == null)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
            return WriteOutcome.NotFound;

        // ADR-003: soft-delete on shared entries requires expertise.write.approve.
        // 403 (not 404) is correct here because the caller already knows the entry exists
        // — they could read it under the same TenantContext.
        if (entry.Tenant == "shared" && !ctx.Scopes.Contains(AuthConstants.WriteApproveScope))
            return WriteOutcome.InsufficientScope;

        var hash = entry.IntegrityHash ?? IntegrityHashService.Compute(entry);
        entry.DeprecatedAt = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;

        db.ExpertiseAuditLogs.Add(BuildAuditRow(AuditAction.Deleted, entry, ctx, hash, hash));
        await db.SaveChangesAsync(ct);
        return WriteOutcome.Success;
    }

    public async Task<(WriteOutcome Outcome, ExpertiseEntry? Entry)> ApproveAsync(
        Guid id, TenantContext ctx, Visibility visibility, CancellationToken ct)
    {
        var entry = await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.Id == id)
            .Where(e => e.DeprecatedAt == null)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
            return (WriteOutcome.NotFound, null);

        if (entry.ReviewState != ReviewState.Draft)
            return (WriteOutcome.InvalidState, null);

        var reviewer = ctx.Principal.FindFirst("sub")?.Value
                    ?? ctx.Principal.Identity?.Name
                    ?? "system";

        var hash = entry.IntegrityHash ?? IntegrityHashService.Compute(entry);
        entry.ReviewState = ReviewState.Approved;
        entry.Visibility = visibility;
        entry.ReviewedBy = reviewer;
        entry.ReviewedAt = DateTime.UtcNow;
        entry.RejectionReason = null;
        entry.UpdatedAt = DateTime.UtcNow;

        db.ExpertiseAuditLogs.Add(BuildAuditRow(AuditAction.Approved, entry, ctx, hash, hash));

        try
        {
            await db.SaveChangesAsync(ct);
            return (WriteOutcome.Success, entry);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (WriteOutcome.ConcurrentConflict, null);
        }
    }

    public async Task<(WriteOutcome Outcome, ExpertiseEntry? Entry)> RejectAsync(
        Guid id, TenantContext ctx, string rejectionReason, CancellationToken ct)
    {
        var entry = await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.Id == id)
            .Where(e => e.DeprecatedAt == null)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
            return (WriteOutcome.NotFound, null);

        if (entry.ReviewState != ReviewState.Draft)
            return (WriteOutcome.InvalidState, null);

        var reviewer = ctx.Principal.FindFirst("sub")?.Value
                    ?? ctx.Principal.Identity?.Name
                    ?? "system";

        var hash = entry.IntegrityHash ?? IntegrityHashService.Compute(entry);
        entry.ReviewState = ReviewState.Rejected;
        entry.ReviewedBy = reviewer;
        entry.ReviewedAt = DateTime.UtcNow;
        entry.RejectionReason = rejectionReason;
        entry.UpdatedAt = DateTime.UtcNow;

        db.ExpertiseAuditLogs.Add(BuildAuditRow(AuditAction.Rejected, entry, ctx, hash, hash));

        try
        {
            await db.SaveChangesAsync(ct);
            return (WriteOutcome.Success, entry);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (WriteOutcome.ConcurrentConflict, null);
        }
    }

    public async Task<List<ExpertiseAuditLog>> ListAuditAsync(AuditLogFilter filter, CancellationToken ct)
    {
        var query = db.ExpertiseAuditLogs.AsQueryable();

        if (filter.EntryId is { } entryId)
            query = query.Where(a => a.EntryId == entryId);
        if (filter.Principal is { Length: > 0 } principal)
            query = query.Where(a => a.Principal == principal);
        if (filter.Action is { } action)
            query = query.Where(a => a.Action == action);
        if (filter.ActorClass is { } actorClass)
            query = query.Where(a => a.ActorClass == actorClass);
        if (filter.From is { } from)
            query = query.Where(a => a.Timestamp >= from);
        if (filter.To is { } to)
            query = query.Where(a => a.Timestamp <= to);

        // Cursor: keyset pagination over (Timestamp DESC, Id) — strictly less than the
        // cursor row keeps result pages deterministic across inserts.
        if (filter.AfterTimestamp is { } cursorTs && filter.AfterId is { } cursorId)
        {
            query = query.Where(a =>
                a.Timestamp < cursorTs ||
                (a.Timestamp == cursorTs && a.Id.CompareTo(cursorId) < 0));
        }

        var limit = Math.Clamp(filter.Limit, 1, 200);
        return await query
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public Task<ExpertiseAuditLog?> GetAuditByIdAsync(Guid id, CancellationToken ct)
    {
        // Admin-only forensic accessor (gated at the endpoint). No tenant/actor-class
        // filter — the caller has expertise.admin scope and the use case is incident
        // triage of the row identified by id.
        return db.ExpertiseAuditLogs
            .Where(a => a.Id == id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<ExpertiseEntry>> KeywordSearchAsync(string query, TenantContext ctx, bool includeDeprecated, int limit, CancellationToken ct)
    {
        // Tenant + ReviewState + DeprecatedAt filters live inside the raw SQL alongside
        // ORDER BY because composing LINQ Where on top of FromSqlInterpolated wraps the
        // original query in a subquery — the planner may then drop the inner ORDER BY
        // since the subquery has no LIMIT, leaving result order undefined.
        //
        // websearch_to_tsquery over plainto_tsquery: adds phrase quoting, OR, and
        // -negation while never throwing on malformed input. ts_rank_cd over ts_rank:
        // cover-density ranking rewards term proximity, which performs better on the
        // short few-word queries agent callers issue (#424).
        var tenant = RequireTenant(ctx);
        var approvedState = nameof(ReviewState.Approved);

        if (includeDeprecated)
            return await db.ExpertiseEntries.FromSqlInterpolated($"""
                SELECT *, xmin FROM "ExpertiseEntries"
                WHERE "SearchVector" @@ websearch_to_tsquery('english', {query})
                  AND ("Tenant" = {tenant} OR "Tenant" = 'shared')
                  AND "ReviewState" = {approvedState}
                ORDER BY ts_rank_cd("SearchVector", websearch_to_tsquery('english', {query})) DESC
                LIMIT {limit}
                """).ToListAsync(ct);

        return await db.ExpertiseEntries.FromSqlInterpolated($"""
            SELECT *, xmin FROM "ExpertiseEntries"
            WHERE "SearchVector" @@ websearch_to_tsquery('english', {query})
              AND ("Tenant" = {tenant} OR "Tenant" = 'shared')
              AND "ReviewState" = {approvedState}
              AND "DeprecatedAt" IS NULL
            ORDER BY ts_rank_cd("SearchVector", websearch_to_tsquery('english', {query})) DESC
            LIMIT {limit}
            """).ToListAsync(ct);
    }

    public async Task<List<ExpertiseEntry>> SemanticSearchAsync(Vector queryVector, TenantContext ctx, int limit, bool includeDeprecated, CancellationToken ct)
    {
        var query = ApplyApprovedReviewFilter(ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx))
            .Where(e => e.Embedding != null);

        if (!includeDeprecated)
            query = query.Where(e => e.DeprecatedAt == null);

        return await query
            .OrderBy(e => e.Embedding!.CosineDistance(queryVector))
            .Take(limit)
            .ToListAsync(ct);
    }

    [SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification = "e.Title.ToLower() in this LINQ expression translates to PostgreSQL's LOWER() function and never executes on the .NET runtime; specifying a CultureInfo would not affect the SQL translation. The input parameter is normalized with ToLowerInvariant() above to keep the C#-side value culture-stable.")]
    [SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version",
        Justification = "e.Title.ToLower() in this LINQ expression translates to PostgreSQL's LOWER() function; specifying a culture has no effect on the SQL output.")]
    [SuppressMessage("Performance", "CA1862:Use the StringComparison overload of String.Equals",
        Justification = "EF Core does not consistently translate StringComparison overloads to SQL; the .ToLower() pattern matches the dedicated LOWER(title) index introduced by AddTitleLowerIndex.")]
    public async Task<ExpertiseEntry?> FindExactMatchAsync(string domain, string title, TenantContext ctx, CancellationToken ct)
    {
        // Normalize the input parameter once with InvariantCulture so the
        // captured value is culture-stable. The server-side `e.Title.ToLower()`
        // translates to SQL LOWER(), hitting the AddTitleLowerIndex; both sides
        // are now deterministic and locale-independent on the .NET runtime.
        var lowerTitle = title.ToLowerInvariant();
        return await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.ReviewState != ReviewState.Rejected)
            .Where(e => e.Domain == domain)
            .Where(e => e.Title.ToLower() == lowerTitle)
            .FirstOrDefaultAsync(ct);
    }

    [SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification = "e.Title.ToLower() in this LINQ expression translates to PostgreSQL's LOWER() function and never executes on the .NET runtime; the input list is normalized with ToLowerInvariant() above. Same rationale as FindExactMatchAsync.")]
    [SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version",
        Justification = "e.Title.ToLower() translates to SQL LOWER(); culture has no effect on the SQL output.")]
    public async Task<List<ExpertiseEntry>> FindExactMatchesAsync(string domain, IReadOnlyList<string> titles, TenantContext ctx, CancellationToken ct)
    {
        var lowerTitles = titles.Select(t => t.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // e.Title.ToLower() (NOT ToLowerInvariant): EF Core translates ToLower() to SQL
        // LOWER() but cannot translate ToLowerInvariant(), which made this method throw
        // at runtime — failing EVERY valid /expertise/batch item at the dedup phase.
        // Latent since the batch endpoint landed; surfaced by the first integration test
        // to exercise /expertise/batch end-to-end (SyncOriginAttributionTests, ADR-013).
        return await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.ReviewState != ReviewState.Rejected)
            .Where(e => e.Domain == domain)
            .Where(e => lowerTitles.Contains(e.Title.ToLower()))
            .ToListAsync(ct);
    }

    public async Task<ExpertiseEntry?> FindNearestInDomainAsync(string domain, Vector queryVector, double maxDistance, TenantContext ctx, CancellationToken ct)
    {
        var candidate = await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.ReviewState != ReviewState.Rejected)
            .Where(e => e.Domain == domain)
            .Where(e => e.Embedding != null)
            .OrderBy(e => e.Embedding!.CosineDistance(queryVector))
            .FirstOrDefaultAsync(ct);

        if (candidate?.Embedding is null)
            return null;

        // Threshold check in memory on the single returned vector to avoid double SQL evaluation
        var a = candidate.Embedding.ToArray();
        var b = queryVector.ToArray();
        var distance = CosineDistance(a, b);

        if (distance is null)
        {
            logger.LogWarning(
                "Embedding dimension mismatch in domain {Domain}: stored {StoredDim}, query {QueryDim}. Run 'reembed' to regenerate stored embeddings",
                domain, a.Length, b.Length);
            return null;
        }

        return distance.Value <= maxDistance ? candidate : null;
    }

    public async Task<List<ExpertiseEntry>> FindAllEmbeddingsInDomainAsync(string domain, TenantContext ctx, CancellationToken ct)
    {
        return await ApplyTenantFilter(db.ExpertiseEntries.AsQueryable(), ctx)
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.ReviewState != ReviewState.Rejected)
            .Where(e => e.Domain == domain)
            .Where(e => e.Embedding != null)
            .ToListAsync(ct);
    }

    public async Task<List<ExpertiseEntry>> ListSharedApprovedUpdatedAfterAsync(
        DateTime afterUpdatedAt, Guid afterId, int limit, CancellationToken ct)
    {
        // No TenantContext by design (see interface doc): scope is hard-coded to the
        // shared namespace. Runs from a background-service scope where the EF global
        // tenant filter's accessor returns null (short-circuits) — this explicit WHERE
        // is the correctness driver, same posture as every other method here.
        return await db.ExpertiseEntries
            .Where(e => e.Tenant == "shared")
            .Where(e => e.ReviewState == ReviewState.Approved)
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.UpdatedAt > afterUpdatedAt
                        || (e.UpdatedAt == afterUpdatedAt && e.Id > afterId))
            .OrderBy(e => e.UpdatedAt).ThenBy(e => e.Id)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Computes cosine distance between two vectors. Returns null if dimensions differ.
    /// </summary>
    internal static double? CosineDistance(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return null;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }
        return 1.0 - dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
