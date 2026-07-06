namespace ExpertiseApi.Models;

internal enum AuditAction
{
    Created,
    Updated,
    Approved,
    Rejected,
    Deleted,

    /// <summary>
    /// Written by the <c>restore</c> CLI verb (ADR-012) when a backup record's
    /// recomputed <see cref="ExpertiseApi.Services.BackupRecordHash"/> did not match
    /// the hash stored in the artifact: the entry was imported but forced to
    /// <see cref="ReviewState.Draft"/>. Stored as text (HasConversion&lt;string&gt;),
    /// so adding this member requires no migration.
    /// </summary>
    RestoreQuarantined
}
