using ExpertiseApi.Auth;
using ExpertiseApi.Models;
using Pgvector;

namespace ExpertiseApi.Data;

/// <summary>
/// Per ADR-001: every method takes a <see cref="TenantContext"/>. Reads are filtered to
/// <c>Tenant IN (ctx.Tenant, "shared")</c>. Writes scope tenant ownership at the repository
/// layer so a caller in tenant A cannot mutate or soft-delete an entry in tenant B
/// (cross-tenant resolves to 404 via <c>FirstOrDefaultAsync</c> returning null).
/// <para>
/// State-changing methods write a row to <c>ExpertiseAuditLog</c> in the same
/// <c>SaveChangesAsync</c> call as the entry mutation — atomicity is the safeguard
/// against the entry-mutated-but-no-audit-row failure mode.
/// </para>
/// </summary>
internal interface IExpertiseRepository
{
    Task<ExpertiseEntry?> GetByIdAsync(Guid id, TenantContext ctx, CancellationToken ct = default);

    Task<List<ExpertiseEntry>> ListAsync(
        TenantContext ctx,
        string? domain = null,
        List<string>? tags = null,
        EntryType? entryType = null,
        Severity? severity = null,
        bool includeDeprecated = false,
        CancellationToken ct = default);

    /// <summary>
    /// Lists <c>Draft</c> and <c>Rejected</c> entries in the caller's tenant only — drafts
    /// are owned by the writing tenant and are not cross-tenant visible (no <c>shared</c>).
    /// Caller authorization (<c>WriteApproveAccess</c>) is enforced at the endpoint layer.
    /// </summary>
    Task<List<ExpertiseEntry>> ListDraftsAsync(TenantContext ctx, CancellationToken ct = default);

    Task<ExpertiseEntry> CreateAsync(ExpertiseEntry entry, TenantContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Applies the caller-supplied delegate to the entry, then commits. State-regression
    /// rule (ADR-003): when a <c>write.draft</c>-only caller mutates an <c>Approved</c>
    /// or <c>Rejected</c> entry, the entry is reset to <c>Draft</c> and review metadata
    /// (<c>ReviewedBy</c>, <c>ReviewedAt</c>, <c>RejectionReason</c>) is cleared;
    /// <c>write.approve</c> callers preserve the source state. The state regression and
    /// audit row are written atomically. Returns <see cref="WriteOutcome.NotFound"/> when
    /// the entry is missing or in another tenant; <see cref="WriteOutcome.ConcurrentConflict"/>
    /// when an <c>xmin</c> race is lost.
    /// </summary>
    Task<(WriteOutcome Outcome, ExpertiseEntry? Entry)> UpdateAsync(Guid id, TenantContext ctx, Func<ExpertiseEntry, Task> applyUpdates, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes by setting <see cref="ExpertiseEntry.DeprecatedAt"/>. Soft-deleting a
    /// <c>Tenant = "shared"</c> entry requires <c>expertise.write.approve</c> per ADR-003;
    /// without that scope returns <see cref="WriteOutcome.InsufficientScope"/>.
    /// </summary>
    Task<WriteOutcome> SoftDeleteAsync(Guid id, TenantContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Transitions <c>Draft</c> → <c>Approved</c>. Sets <c>ReviewedBy</c>/<c>ReviewedAt</c>,
    /// applies <paramref name="visibility"/> (default <c>Private</c>), clears
    /// <c>RejectionReason</c>. Returns <see cref="WriteOutcome.InvalidState"/> when the
    /// entry is not in <c>Draft</c>; <see cref="WriteOutcome.ConcurrentConflict"/> when an
    /// <c>xmin</c> race is lost.
    /// </summary>
    Task<(WriteOutcome Outcome, ExpertiseEntry? Entry)> ApproveAsync(
        Guid id, TenantContext ctx, Visibility visibility, CancellationToken ct = default);

    Task<(WriteOutcome Outcome, ExpertiseEntry? Entry)> RejectAsync(
        Guid id, TenantContext ctx, string rejectionReason, CancellationToken ct = default);

    Task<List<ExpertiseAuditLog>> ListAuditAsync(
        AuditLogFilter filter,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a single audit row by id WITHOUT any tenant or actor-class filtering.
    /// Backs the admin-only <c>/audit/{id}/raw</c> escape hatch (security-review-expert
    /// recommendation under #168 in place of a <c>?raw=true</c> query flag on the main
    /// read path). Returns null when the row does not exist.
    /// </summary>
    Task<ExpertiseAuditLog?> GetAuditByIdAsync(Guid id, CancellationToken ct = default);

    Task<List<ExpertiseEntry>> KeywordSearchAsync(string query, TenantContext ctx, bool includeDeprecated = false, CancellationToken ct = default);

    Task<List<ExpertiseEntry>> SemanticSearchAsync(Vector queryVector, TenantContext ctx, int limit = 10, bool includeDeprecated = false, CancellationToken ct = default);

    Task<ExpertiseEntry?> FindExactMatchAsync(string domain, string title, TenantContext ctx, CancellationToken ct = default);

    Task<List<ExpertiseEntry>> FindExactMatchesAsync(string domain, IReadOnlyList<string> titles, TenantContext ctx, CancellationToken ct = default);

    Task<ExpertiseEntry?> FindNearestInDomainAsync(string domain, Vector queryVector, double maxDistance, TenantContext ctx, CancellationToken ct = default);

    Task<List<ExpertiseEntry>> FindAllEmbeddingsInDomainAsync(string domain, TenantContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Up-sync feed (ADR-013): Approved, non-deprecated entries in the cross-tenant
    /// <c>shared</c> namespace strictly after the keyset cursor
    /// <c>(afterUpdatedAt, afterId)</c>, ordered <c>(UpdatedAt, Id)</c> ascending.
    /// Deliberately takes NO <see cref="TenantContext"/> — the caller is the
    /// background <c>ExpertiseSyncWorker</c> (no HTTP context) and the scope is
    /// hard-coded to <c>Tenant = "shared"</c>, which is narrower than any
    /// caller-derived filter (every tenant can read shared). Tombstones
    /// (<c>DeprecatedAt</c>) are excluded; propagation is a down-sync concern (#342).
    /// </summary>
    Task<List<ExpertiseEntry>> ListSharedApprovedUpdatedAfterAsync(
        DateTime afterUpdatedAt, Guid afterId, int limit, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a write operation that can fail for reasons other than "not found."
/// </summary>
internal enum WriteOutcome
{
    Success,
    NotFound,
    /// <summary>State machine rejected the transition (e.g., approve on already-approved).</summary>
    InvalidState,
    /// <summary>Caller is missing a scope required for this specific entry (e.g., shared-entry mutation).</summary>
    InsufficientScope,
    /// <summary>Optimistic concurrency conflict on <c>xmin</c>.</summary>
    ConcurrentConflict
}

/// <summary>
/// Cursor-paginated audit log query. Cursor is <c>(AfterTimestamp, AfterId)</c> for
/// keyset pagination ordered by <c>(Timestamp DESC, Id)</c>.
/// </summary>
internal record AuditLogFilter(
    Guid? EntryId = null,
    string? Principal = null,
    AuditAction? Action = null,
    ActorClass? ActorClass = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = 50,
    DateTime? AfterTimestamp = null,
    Guid? AfterId = null);
