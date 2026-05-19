namespace ExpertiseApi.Services.Idempotency;

/// <summary>
/// Configuration for the Part D C3 idempotency-key control. Bound from the
/// <c>Idempotency</c> section of <c>appsettings.json</c> in
/// <see cref="Program"/>.
/// <para>
/// Hard-require is the shipped default since 2026-05-19 (ADR-010
/// Amendment 1): both in-repo callers — the skill caller
/// (<c>api_curl</c> in <c>.agents/skills/expertise-api/scripts/lib/common.sh</c>,
/// shipped in PR #211) and the pi extension caller
/// (<c>apiCall</c> in <c>.pi/extensions/expertise-api/index.ts</c>,
/// shipped in PR #212) — generate <c>Idempotency-Key</c> headers on
/// every POST. Operators can roll back to soft-require by setting
/// <c>Idempotency:RequireKey=false</c> in an environment overlay; the
/// value is read per-request via <c>IOptionsMonitor</c> so the flip
/// takes effect without redeploy.
/// </para>
/// </summary>
internal sealed class IdempotencyOptions
{
    /// <summary>
    /// When <c>true</c> (the shipped default since 2026-05-19), POSTs
    /// attached via <see cref="ExpertiseApi.Endpoints.Filters.IdempotencyEndpointFilterExtensions.RequireIdempotency"/>
    /// reject requests missing the <c>Idempotency-Key</c> header with 400
    /// ProblemDetails. When <c>false</c>, missing-header requests fall
    /// through to the handler unchanged — the documented operator
    /// rollback path. See ADR-010 § "Migration path" and Amendment 1.
    /// </summary>
    public bool RequireKey { get; set; } = true;

    /// <summary>
    /// Hard cap on stored response body bytes. Bodies exceeding the cap have
    /// their <c>response_body</c> column dropped (NULL) but the
    /// <c>response_body_hash</c> column retained, so replays still detect
    /// drift; the replay response carries an HTTP <c>Warning: 199 - "response body not cached due to size"</c>
    /// header per IETF draft §2.5. Default 64 KiB.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Replay window. Records older than this are treated as not-present at
    /// lookup time (the lookup query enforces the TTL authoritatively; the
    /// background GC sweep is a footprint optimisation, not a correctness
    /// mechanism). A retry whose original request expired silently
    /// re-executes the handler with a fresh placeholder. Default 24 hours,
    /// matching the IETF draft's recommended minimum.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Cadence for the background GC sweep
    /// (<see cref="IdempotencyGcService"/>). Default 1 hour.
    /// </summary>
    public TimeSpan GcInterval { get; set; } = TimeSpan.FromHours(1);
}
