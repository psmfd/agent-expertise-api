namespace ExpertiseApi.Models;

/// <summary>
/// One sealed link of the audit-log checkpoint chain (ADR-020 decision 2B). Each
/// checkpoint commits the audit rows with <c>Seq</c> in <c>[SeqFrom, SeqTo]</c> to an
/// RFC 6962 Merkle root (leaf = the row's <see cref="Services.BackupRecordHash"/>
/// canonical hash, Seq order) and chains to its predecessor via
/// <see cref="PrevCheckpointMac"/>. <see cref="CheckpointMac"/> is a keyed HMAC over
/// the checkpoint's own fields, so a database-only writer can neither rewrite a sealed
/// range (root diverges) nor re-seal it (no key). Sealing and verification both happen
/// in <see cref="Data.IntegrityVerificationService"/> — the request write path never
/// touches this table (no serialization point; the rejected per-row chain is ADR-020
/// option 2A).
/// </summary>
internal class AuditCheckpoint
{
    /// <summary>Chain position — identity column; strictly increasing.</summary>
    public long Id { get; set; }

    /// <summary>First audit <c>Seq</c> covered by this checkpoint (inclusive).</summary>
    public long SeqFrom { get; set; }

    /// <summary>Last audit <c>Seq</c> covered by this checkpoint (inclusive).</summary>
    public long SeqTo { get; set; }

    /// <summary>
    /// Number of audit rows present in the range at seal time. Seq gaps from
    /// rolled-back inserts are normal (sequence values are consumed on rollback), so
    /// this is not <c>SeqTo - SeqFrom + 1</c>; a post-seal deletion changes the leaf
    /// set and diverges <see cref="MerkleRoot"/> regardless.
    /// </summary>
    public int RowCount { get; set; }

    /// <summary>RFC 6962 Merkle Tree Hash (lowercase hex) over the range's row hashes.</summary>
    public required string MerkleRoot { get; set; }

    /// <summary>The previous checkpoint's <see cref="CheckpointMac"/>; null for the first link.</summary>
    public string? PrevCheckpointMac { get; set; }

    /// <summary>
    /// MAC over this checkpoint's canonical fields: <c>{keyId}:{hex}</c> HMAC-SHA256
    /// when keyed, bare 64-hex SHA-256 in the unkeyed soft-require phase (the same
    /// format contract as <c>ExpertiseEntry.IntegrityHash</c>).
    /// </summary>
    public required string CheckpointMac { get; set; }

    public DateTime CreatedAt { get; set; }
}
