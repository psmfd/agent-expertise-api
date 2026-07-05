using System.Text.Json.Serialization;

namespace ExpertiseApi.Cli;

/// <summary>
/// NDJSON line shape for one <see cref="Models.ExpertiseEntry"/> in a backup artifact
/// (ADR-012). Enums travel as their string names so the artifact stays readable and
/// forward-compatible; <c>SearchVector</c> (server-generated) and the <c>xmin</c>
/// concurrency token are deliberately absent. <see cref="Embedding"/> is included for
/// restore speed but sits OUTSIDE the trust boundary — it is not covered by
/// <see cref="RecordHash"/> (regenerable via <c>reembed</c>).
/// </summary>
internal sealed record BackupEntryRecord
{
    public required Guid Id { get; init; }
    public required string Domain { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string EntryType { get; init; }
    public required string Severity { get; init; }
    public required string Source { get; init; }
    public string? SourceVersion { get; init; }
    public IReadOnlyList<float>? Embedding { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public DateTime? DeprecatedAt { get; init; }
    public required string Tenant { get; init; }
    public required string Visibility { get; init; }
    public required string AuthorPrincipal { get; init; }
    public string? AuthorAgent { get; init; }
    public string? IntegrityHash { get; init; }
    public required string ReviewState { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public string? RejectionReason { get; init; }

    /// <summary>
    /// <see cref="Services.BackupRecordHash"/> over this record's canonical fields.
    /// Doubles as the record's Merkle leaf input; lets restore localize a tampered
    /// record (quarantine) instead of only detecting whole-file mismatch.
    /// </summary>
    public required string RecordHash { get; init; }
}

/// <summary>
/// NDJSON line shape for one <see cref="Models.ExpertiseAuditLog"/> row. Rows are
/// exported and re-imported verbatim — restore never rewrites source history.
/// </summary>
internal sealed record BackupAuditRecord
{
    public required Guid Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Action { get; init; }
    public required Guid EntryId { get; init; }
    public required string Tenant { get; init; }
    public required string Principal { get; init; }
    public string? Agent { get; init; }
    public string? BeforeHash { get; init; }
    public string? AfterHash { get; init; }
    public string? IpAddress { get; init; }
    public required string ActorClass { get; init; }
    public string? AuthMethod { get; init; }
    public string? ActorClassHeader { get; init; }

    /// <inheritdoc cref="BackupEntryRecord.RecordHash"/>
    public required string RecordHash { get; init; }
}

internal sealed record BackupEmbeddingModel
{
    public required string Name { get; init; }
    public required int Dims { get; init; }
}

/// <summary>
/// Cleartext manifest signed by the operator tooling (`cosign sign-blob --key`, ADR-012).
/// <see cref="PayloadSha256"/> is null when emitted by the CLI verb; the apictl wrapper
/// injects it after age-encrypting the payload, before signing.
/// </summary>
internal sealed record BackupManifest
{
    public required int SchemaVersion { get; init; }
    public required string InstanceId { get; init; }
    public required DateTime ExportedAt { get; init; }
    public required int EntryCount { get; init; }
    public required int AuditCount { get; init; }
    public required string EntriesMerkleRoot { get; init; }
    public required string AuditMerkleRoot { get; init; }
    public string? DbSchemaVersion { get; init; }
    public BackupEmbeddingModel? EmbeddingModel { get; init; }
    public string? PayloadSha256 { get; init; }
}

/// <summary>
/// Source-generated serializer context for the backup artifact DTOs. First
/// source-generated JSON context in the codebase — keeps `AnalysisMode=All`
/// clean (SYSLIB1045-family) and the CLI path trim-friendly.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BackupEntryRecord))]
[JsonSerializable(typeof(BackupAuditRecord))]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
