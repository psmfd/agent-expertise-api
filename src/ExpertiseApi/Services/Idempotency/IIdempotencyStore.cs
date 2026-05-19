namespace ExpertiseApi.Services.Idempotency;

/// <summary>
/// Lookup outcome from <see cref="IIdempotencyStore.TryReserveAsync"/>. Drives
/// the three-way branch in <see cref="ExpertiseApi.Endpoints.Filters.IdempotencyEndpointFilter"/>:
/// reserve → execute handler; hit-match → replay buffered response;
/// hit-mismatch → 409 ProblemDetails.
/// </summary>
internal enum IdempotencyLookupOutcome
{
    /// <summary>No existing row; reservation row inserted; handler should run.</summary>
    Reserved,

    /// <summary>Existing row with matching <c>request_hash</c>; replay the cached response.</summary>
    HitMatch,

    /// <summary>Existing row with different <c>request_hash</c>; return 409 (key reuse with different body).</summary>
    HitMismatch,
}

/// <summary>
/// Cached response payload as returned to replay callers. Headers are stored
/// as a flat dictionary (single value per header — multi-value headers are
/// joined on persist and split on replay; acceptable for the JSON responses
/// the three POSTs emit). <see cref="Body"/> is null when the original
/// response exceeded the size cap; the replayer emits an empty body with
/// the original status code plus a <c>Warning</c> header in that case.
/// </summary>
internal sealed record IdempotencyReplayPayload(
    int StatusCode,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Headers,
    byte[]? Body,
    bool BodyOmittedDueToSize);

/// <summary>
/// Singleton store backing the Part D C3 idempotency control (ADR-010).
/// Operates over a dedicated <c>NpgsqlDataSource</c> with raw SQL — no
/// <see cref="ExpertiseApi.Data.ExpertiseDbContext"/> involvement — so the
/// hot path stays out of EF's change tracker and per-request scope.
/// <para>
/// PgBouncer-safety: every method opens exactly one connection and runs
/// exactly one transaction, satisfying the transaction-pooling contract.
/// </para>
/// </summary>
internal interface IIdempotencyStore
{
    /// <summary>
    /// Attempt to reserve <c>(tenant, key)</c> with the supplied
    /// <paramref name="requestHash"/>. Returns
    /// <see cref="IdempotencyLookupOutcome.Reserved"/> when no prior row
    /// existed (the row is inserted as a placeholder for
    /// <see cref="PersistAsync"/> to update on completion);
    /// <see cref="IdempotencyLookupOutcome.HitMatch"/> with the populated
    /// replay payload when a prior row matches; or
    /// <see cref="IdempotencyLookupOutcome.HitMismatch"/> when a prior row
    /// exists but its <c>request_hash</c> differs.
    /// <para>
    /// On <see cref="IdempotencyLookupOutcome.HitMatch"/> with a placeholder
    /// row (handler still running on another request), the caller should
    /// treat the response as "still in-flight" — surfaced today as
    /// <see cref="IdempotencyLookupOutcome.HitMatch"/> with a null
    /// <paramref name="payload"/>; the filter responds 409 Conflict
    /// (concurrent in-flight request with same key).
    /// </para>
    /// <para>
    /// Rows older than <paramref name="ttl"/> are treated as not-present
    /// (TTL check is authoritative; the background GC sweep is a footprint
    /// optimisation, not a correctness mechanism). This means a retry whose
    /// original request expired silently re-executes the handler with a
    /// fresh placeholder — the desired behaviour.
    /// </para>
    /// </summary>
    Task<(IdempotencyLookupOutcome Outcome, IdempotencyReplayPayload? Payload)> TryReserveAsync(
        string tenant,
        string key,
        string requestHash,
        TimeSpan ttl,
        CancellationToken ct);

    /// <summary>
    /// Persist the captured response into the reserved row. Called from
    /// <c>HttpContext.Response.OnCompleted</c> in fire-and-forget mode
    /// (failures emit the <c>expertise_idempotency_persist_failed_total</c>
    /// metric — the row remains a placeholder and the next retry
    /// re-executes the handler, which is the intended failure mode per
    /// ADR-010).
    /// </summary>
    Task PersistAsync(
        string tenant,
        string key,
        int statusCode,
        string responseBodyHash,
        byte[]? responseBody,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct);

    /// <summary>
    /// Delete a placeholder reservation that will never be persisted (the
    /// handler emitted a non-cacheable status — 5xx or 429 — or threw an
    /// unhandled exception, in which case <see cref="PersistAsync"/> is not
    /// invoked). Frees the <c>(tenant, key)</c> slot immediately so the
    /// caller's retry can re-execute rather than receiving 409 for up to the
    /// full TTL window. Safe to call on an already-finalized row (it is a
    /// targeted DELETE that scopes by <c>status_code = 0</c>, so finalised
    /// rows are untouched).
    /// </summary>
    Task ReleaseReservationAsync(string tenant, string key, CancellationToken ct);

    /// <summary>
    /// Delete rows older than <paramref name="olderThan"/>. Returns the row
    /// count for the GC metric. Called from
    /// <see cref="IdempotencyGcService"/> on a fixed cadence.
    /// </summary>
    Task<int> SweepExpiredAsync(DateTimeOffset olderThan, CancellationToken ct);
}
