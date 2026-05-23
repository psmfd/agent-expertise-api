namespace ExpertiseApi.Models;

internal class ExpertiseAuditLog
{
    public Guid Id { get; set; }

    public DateTime Timestamp { get; set; }

    public AuditAction Action { get; set; }

    public Guid EntryId { get; set; }

    public required string Tenant { get; set; }

    public required string Principal { get; set; }

    public string? Agent { get; set; }

    public string? BeforeHash { get; set; }

    public string? AfterHash { get; set; }

    public string? IpAddress { get; set; }

    // Part D C6 — actor-vs-human audit tag. See docs/security/integration-threat-model.md
    // (Part D C6 row) for the rationale; see ActorClassResolver for the derivation rule.
    // Default Human preserves the semantics of all audit rows written before this column
    // landed; the migration's column-level default backfills existing rows.
    public ActorClass ActorClass { get; set; }

    // Authentication scheme that produced this row's principal (Bearer / ApiKey / LocalDev).
    // Recorded so a "header said agent, scope said nothing" pattern is queryable post-hoc
    // when X-Actor-Class falls back to Human (security-review-expert recommendation on #168).
    public string? AuthMethod { get; set; }

    // Raw X-Actor-Class header value as received, before resolver normalization. NULL when
    // the header was absent. Stored verbatim (truncated to 32 chars at write time) so a
    // mismatch between header and scope is forensically recoverable.
    public string? ActorClassHeader { get; set; }
}
