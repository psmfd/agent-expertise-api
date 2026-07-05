namespace ExpertiseApi.Models;

/// <summary>
/// Spoke-side up-sync cursor (ADR-013): the high-water mark of what has been pushed to
/// the hub. Singleton-row table following the <see cref="EmbeddingMetadata"/> pattern —
/// get-or-create at the call site, no schema-level singleton guard. The cursor is a
/// keyset pair over <c>(UpdatedAt, Id)</c> so re-approved edits (which bump
/// <c>UpdatedAt</c>) are re-synced and absorbed by the hub's dedup as
/// <c>Duplicate</c> (at-least-once delivery; ADR-010 batch exclusion).
/// </summary>
internal class SyncState
{
    public int Id { get; set; }

    /// <summary>UpdatedAt of the last entry acknowledged by the hub.</summary>
    public DateTime LastSyncedUpdatedAt { get; set; }

    /// <summary>Tie-breaker Id of the last entry acknowledged by the hub.</summary>
    public Guid LastSyncedId { get; set; }

    /// <summary>Wall-clock of the last fully-successful sync cycle (observability only).</summary>
    public DateTime? LastSuccessAt { get; set; }
}
